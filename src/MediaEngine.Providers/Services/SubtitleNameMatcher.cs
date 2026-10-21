using System.Text.RegularExpressions;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Plain name matching for subtitle text: when a Universe entity's name or alias appears in a line, that line's time is part
/// of a tag window for the entity. No AI and no network, so every tag is deterministic and can be checked by reading the line.
/// <para>
/// Names come from the entity's label and from any aliases supplied with it. A character's given name or surname (a word of
/// four letters or more, written with a capital letter in the label, such as "Frodo" for "Frodo Baggins") is an alias too, unless
/// two entities would share it. A name two entities share is not matched at all, rather than guessed.
/// </para>
/// </summary>
public sealed class SubtitleNameMatcher
{
    /// <summary>Changes whenever the matching rules change, so stored tags are made again.</summary>
    public const string RulesVersion = "name-match-v1";

    /// <summary>Mentions further apart than this start a new window (30 seconds).</summary>
    public const long DefaultWindowGapMs = 30_000;

    private const int MinimumNameLength = 3;
    private const int MinimumPartLength = 4;

    // Words that are titles or link words rather than names; never an alias of their own.
    private static readonly HashSet<string> NonNameWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "of", "von", "van", "der", "den", "del", "della", "bin", "ibn", "el", "al", "lord", "lady", "king", "queen",
        "prince", "princess", "sir", "dame", "captain", "doctor", "master", "mister", "miss", "house", "clan", "dark", "great",
        "young", "old", "little", "high", "grand", "saint", "general", "commander", "admiral", "baron", "count", "duke",
        "emperor", "empress", "chief",
    };

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex TrailingQualifier = new(@"\s*\([^)]*\)\s*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly Regex? _pattern;
    private readonly Dictionary<string, Guid> _entityByName;
    private readonly long _windowGapMs;

    public SubtitleNameMatcher(IEnumerable<SubtitleEntityName> entities, long windowGapMs = DefaultWindowGapMs)
    {
        ArgumentNullException.ThrowIfNull(entities);
        _windowGapMs = windowGapMs;
        _entityByName = BuildNameTable(entities);
        if (_entityByName.Count > 0)
        {
            var alternatives = _entityByName.Keys
                .OrderByDescending(name => name.Length)
                .ThenBy(name => name, StringComparer.Ordinal)
                .Select(name => Regex.Escape(name).Replace("\\ ", @"\s+", StringComparison.Ordinal));
            _pattern = new Regex(
                @"(?<![\p{L}\p{N}])(?:" + string.Join('|', alternatives) + @")(?![\p{L}\p{N}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>How many distinct names the matcher looks for.</summary>
    public int NameCount => _entityByName.Count;

    /// <summary>A fingerprint of the entities and the rules; it changes when the Universe graph or the rules change.</summary>
    public static string Signature(IEnumerable<SubtitleEntityName> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var lines = entities
            .OrderBy(entity => entity.EntityId)
            .Select(entity => string.Join('|', entity.EntityId.ToString("N"), entity.EntitySubType, entity.Label, string.Join('~', entity.Aliases.Order(StringComparer.Ordinal))));
        return Hashing.Sha256Hex(RulesVersion + "\n" + string.Join('\n', lines));
    }

    /// <summary>Finds the time windows in which each entity is named.</summary>
    public IReadOnlyList<SubtitleNameTag> Tag(IReadOnlyList<SubtitleLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_pattern is null || lines.Count == 0)
        {
            return [];
        }

        var open = new Dictionary<Guid, Window>();
        var done = new List<SubtitleNameTag>();

        foreach (var line in lines.OrderBy(l => l.StartMs).ThenBy(l => l.Index))
        {
            var mentioned = new Dictionary<Guid, string>();
            foreach (Match match in _pattern.Matches(NormalizeText(line.Text)))
            {
                if (_entityByName.TryGetValue(NormalizeName(match.Value), out var entityId))
                {
                    mentioned.TryAdd(entityId, match.Value);
                }
            }

            foreach (var (entityId, surface) in mentioned)
            {
                if (open.TryGetValue(entityId, out var window) && line.StartMs - window.EndMs <= _windowGapMs)
                {
                    window.EndMs = Math.Max(window.EndMs, line.EndMs);
                    window.LastLine = line.Index;
                    window.Mentions++;
                    continue;
                }

                if (window is not null)
                {
                    done.Add(window.ToTag(entityId));
                }

                open[entityId] = new Window(line.StartMs, line.EndMs, line.Index, surface);
            }
        }

        done.AddRange(open.Select(pair => pair.Value.ToTag(pair.Key)));
        return done
            .OrderBy(tag => tag.WindowStartMs)
            .ThenBy(tag => tag.EntityId)
            .ToList();
    }

    private static Dictionary<string, Guid> BuildNameTable(IEnumerable<SubtitleEntityName> entities)
    {
        // name -> every entity that claims it; a name claimed by two different entities is dropped.
        var full = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        var parts = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);

        foreach (var entity in entities)
        {
            var label = TrailingQualifier.Replace(entity.Label ?? string.Empty, string.Empty).Trim();
            foreach (var name in FullNames(label).Concat(entity.Aliases.SelectMany(alias => FullNames(alias.Trim()))))
            {
                Claim(full, name, entity.EntityId);
            }

            if (string.Equals(entity.EntitySubType, "Character", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in NameParts(label))
                {
                    Claim(parts, part, entity.EntityId);
                }
            }
        }

        var table = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (name, owners) in full)
        {
            if (owners.Count == 1)
            {
                table[name] = owners.First();
            }
        }

        foreach (var (name, owners) in parts)
        {
            // A part never overrides a whole name, and a part two entities share is not used.
            if (owners.Count == 1 && !full.ContainsKey(name))
            {
                table[name] = owners.First();
            }
        }

        return table;
    }

    private static IEnumerable<string> FullNames(string label)
    {
        var name = NormalizeName(label);
        if (name.Length >= MinimumNameLength)
        {
            yield return name;
        }

        if (name.StartsWith("the ", StringComparison.Ordinal))
        {
            var withoutArticle = name[4..];
            if (withoutArticle.Length >= MinimumPartLength)
            {
                yield return withoutArticle;
            }
        }
    }

    private static IEnumerable<string> NameParts(string label)
    {
        var words = label.Split([' ', '\t', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            yield break;
        }

        foreach (var raw in words)
        {
            var word = raw.Trim('.', ',', '\'', '"', '(', ')');
            if (word.Length >= MinimumPartLength
                && char.IsUpper(word[0])
                && word.All(char.IsLetter)
                && !NonNameWords.Contains(word))
            {
                yield return NormalizeName(word);
            }
        }
    }

    private static void Claim(Dictionary<string, HashSet<Guid>> table, string name, Guid entityId)
    {
        if (!table.TryGetValue(name, out var owners))
        {
            owners = [];
            table[name] = owners;
        }

        owners.Add(entityId);
    }

    private static string NormalizeText(string value) =>
        value.Replace('’', '\'').Replace('‘', '\'');

    private static string NormalizeName(string value) =>
        Spaces.Replace(NormalizeText(value).Trim(), " ").ToLowerInvariant();

    private sealed class Window(long startMs, long endMs, int line, string matchedText)
    {
        public long StartMs { get; } = startMs;
        public long EndMs { get; set; } = endMs;
        public int FirstLine { get; } = line;
        public int LastLine { get; set; } = line;
        public int Mentions { get; set; } = 1;
        public string MatchedText { get; } = matchedText;

        public SubtitleNameTag ToTag(Guid entityId) =>
            new(entityId, StartMs, EndMs, FirstLine, LastLine, Mentions, MatchedText);
    }
}
