using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Models;

namespace MediaEngine.Ingestion.Extras;

/// <summary>
/// <c>config/extras.json</c> compiled for fast use: the extension sets, folder-word sets and the regular expressions built from the
/// word lists. One book per configuration instance, kept while that instance lives. There are no word lists in code: everything here is
/// built from the configuration (universal editor v5 D16, D34, section 3b).
/// </summary>
internal sealed class ExtrasRuleBook
{
    private static readonly ConditionalWeakTable<ExtrasConfiguration, ExtrasRuleBook> Books = new();

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private ExtrasRuleBook(ExtrasConfiguration config)
    {
        Config = config;
        Documents = Set(config.AcceptedTypes.Documents);
        Pictures = Set(config.AcceptedTypes.Pictures);
        VideoClips = Set(config.AcceptedTypes.Videos);
        AudioClips = Set(config.AcceptedTypes.Audio);
        HiddenExtensions = Set(config.HiddenTypes.Extensions);
        HintFolders = Set(config.HintFolders);
        GroupParentFolders = Set(config.GroupParentFolders);
        IgnoredFolders = Set(config.IgnoredFolders);
        TransientNames = Set(config.TransientFiles.Names);
        FolderRoles = new Dictionary<string, string>(config.FolderRoles, StringComparer.OrdinalIgnoreCase);
        TitleSeparators = [.. config.TitleSeparators.Where(text => text.Length == 1).Select(text => text[0])];
        SkipWords = new HashSet<string>(
            config.RoleWords.SelectMany(entry => entry.Words)
                .Concat(config.DocumentHintWords)
                .SelectMany(word => word.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            StringComparer.OrdinalIgnoreCase);

        RoleWords = [.. config.RoleWords
            .Where(entry => entry.Words.Count > 0)
            .Select(entry => (entry.Role, WholeWord(entry.Words)))];

        PictureSidecarName = string.IsNullOrWhiteSpace(config.HiddenTypes.PictureSidecarNamePattern)
            ? null
            : new Regex(config.HiddenTypes.PictureSidecarNamePattern, Options, TimeSpan.FromSeconds(1));

        DocumentHint = WholeWord(config.DocumentHintWords);
        ClipSuffix = new Regex($@"(?<sep>^|[-_.\s])(?:{Alternation(config.ClipSuffixWords)})\d*$", Options, TimeSpan.FromSeconds(1));
        SeasonWord = new Regex(
            $@"\b(?:{Alternation(config.SeasonWords)})\s*(?<season>\d{{1,2}})\b|\bS(?<season2>\d{{1,2}})\b", Options, TimeSpan.FromSeconds(1));
        Specials = WholeWord(config.SpecialsWords);
        SeasonFolder = new Regex(
            $@"^(?:{Alternation(config.SeasonWords)})[-_. ]*(?<season>\d{{1,3}})$|^(?<specials>{Alternation(config.SpecialsWords)})$", Options, TimeSpan.FromSeconds(1));
        EpisodeWord = new Regex($@"\b(?:{Alternation(config.EpisodeWords)})\s*(?<episode>\d{{1,4}})\b", Options, TimeSpan.FromSeconds(1));
        LeadingNumber = new Regex(
            $@"^[^\p{{L}}\p{{N}}]*(?:(?:{Alternation(config.EpisodeWords)})[-_. ]*)?(?<n>\d{{1,3}})(?!\d)", Options, TimeSpan.FromSeconds(1));
        EpisodeMarker = new Regex(
            $@"(?:\bS\d{{1,3}}\s*E\d{{1,4}}\b|\b\d{{1,2}}x\d{{2,3}}\b|\b(?:{Alternation(config.EpisodeWords)})[-_. ]*\d{{1,4}}\b|^\d{{1,3}}\s*[-_. )]|\b\d{{4}}[-_.]\d{{2}}[-_.]\d{{2}}\b)",
            Options,
            TimeSpan.FromSeconds(1));
    }

    public static ExtrasRuleBook For(ExtrasConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Books.GetValue(config, static c => new ExtrasRuleBook(c));
    }

    public ExtrasConfiguration Config { get; }

    public HashSet<string> Documents { get; }

    public HashSet<string> Pictures { get; }

    public HashSet<string> VideoClips { get; }

    public HashSet<string> AudioClips { get; }

    public HashSet<string> HiddenExtensions { get; }

    public HashSet<string> HintFolders { get; }

    public HashSet<string> GroupParentFolders { get; }

    public HashSet<string> IgnoredFolders { get; }

    public HashSet<string> TransientNames { get; }

    public Dictionary<string, string> FolderRoles { get; }

    public char[] TitleSeparators { get; }

    /// <summary>The words that only say what a file is (role words and document-hint words), left out when comparing names.</summary>
    public HashSet<string> SkipWords { get; }

    /// <summary>The file-name words per role, in the order they are tried.</summary>
    public IReadOnlyList<(string Role, Regex Words)> RoleWords { get; }

    public Regex? PictureSidecarName { get; }

    public Regex DocumentHint { get; }

    /// <summary>Matches a trailing extra word; the <c>sep</c> group is the character in front of it ("" at the start of the name).</summary>
    public Regex ClipSuffix { get; }

    public Regex SeasonWord { get; }

    public Regex Specials { get; }

    /// <summary>Matches the whole name of a season or section-like folder: "Season 02", "Specials".</summary>
    public Regex SeasonFolder { get; }

    public Regex EpisodeWord { get; }

    /// <summary>The number a name starts with, after an optional episode word: "02 Components", "Lesson 2 - Components".</summary>
    public Regex LeadingNumber { get; }

    /// <summary>A name that carries an episode or lesson number: S01E02, 1x02, Episode 4, Lesson 07, "07 - Title", 2024-03-05.</summary>
    public Regex EpisodeMarker { get; }

    public bool IsAccepted(string extension) =>
        Documents.Contains(extension) || Pictures.Contains(extension) || VideoClips.Contains(extension) || AudioClips.Contains(extension);

    public string KindOf(string extension) =>
        Documents.Contains(extension) ? ItemExtraKinds.Document
        : Pictures.Contains(extension) ? ItemExtraKinds.Picture
        : ItemExtraKinds.Clip;

    /// <summary>The first role (in the configured order) whose words appear in <paramref name="spacedName"/> and that the file kind allows.</summary>
    public string? RoleByWords(string spacedName, string fileKind)
    {
        foreach (var (role, words) in RoleWords)
        {
            if (Config.RoleFits(fileKind, role) && words.IsMatch(spacedName))
            {
                return role;
            }
        }

        return null;
    }

    /// <summary>A file name with separators turned into spaces, for word matching.</summary>
    public static string Spaced(string text) => text.Replace('_', ' ').Replace('.', ' ').Replace('-', ' ');

    private static HashSet<string> Set(IEnumerable<string> values) => new(values, StringComparer.OrdinalIgnoreCase);

    private static Regex WholeWord(IEnumerable<string> words) =>
        new($@"(?<![\p{{L}}\p{{N}}])(?:{Alternation(words)})(?![\p{{L}}\p{{N}}])", Options, TimeSpan.FromSeconds(1));

    /// <summary>Longest words first, each escaped, spaces matching any run of whitespace. An empty list matches nothing.</summary>
    private static string Alternation(IEnumerable<string> words)
    {
        var builder = new StringBuilder();
        foreach (var word in words.Where(text => !string.IsNullOrWhiteSpace(text)).OrderByDescending(text => text.Length))
        {
            if (builder.Length > 0)
            {
                builder.Append('|');
            }

            builder.Append(Regex.Escape(word.Trim()).Replace("\\ ", @"\s+", StringComparison.Ordinal));
        }

        return builder.Length == 0 ? "(?!)" : builder.ToString();
    }
}
