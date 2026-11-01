using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.Collections.Packs;

/// <summary>Why a pack request was refused. The endpoint turns it into the matching answer.</summary>
public enum CollectionPackFailure
{
    BadRequest,
    Forbidden,
    NotFound,
}

/// <summary>A pack request that cannot be carried out, with a sentence for the person.</summary>
public sealed class CollectionPackException(CollectionPackFailure failure, string message) : Exception(message)
{
    public CollectionPackFailure Failure { get; } = failure;
}

/// <summary>
/// The pack file (Collections and lists spec D14): reading one safely and writing one from a set. A file holds names,
/// rules, windows and artwork hints only. It is read as data: every field is checked before anything is created, and a
/// rule the Engine cannot evaluate is refused rather than silently widening a collection.
/// </summary>
public static partial class CollectionPackFile
{
    /// <summary>Largest pack file read, in characters. A real pack is a few kilobytes.</summary>
    public const int MostCharacters = 1_048_576;

    public const string FileId = "pack-file";

    private const string CouldNotRead =
        "Couldn't read this file. A pack file is a Library collection pack (a .json file exported from a set).";

    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal)
    {
        "eq", "neq", "in", "any_of", "contains", "not_contains", "like", "gt", "gte", "lt", "lte", "between",
        "known", "unknown", "is_known", "is_unknown", "has_any_value", "has_no_known_value",
        "within_days", "not_within_days", "in_last_days", "in_last_months", "in_last_years",
    };

    private static readonly HashSet<string> ValueFreeOperators = new(StringComparer.Ordinal)
    {
        "known", "unknown", "is_known", "is_unknown", "has_any_value", "has_no_known_value",
    };

    private static readonly HashSet<string> SortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "creator", "year", "provider_rating", "rating", "added_at", "media_type", "random", "release_date",
    };

    // These read this one Library (a folder path, a Library id, another collection), so a pack made from them cannot
    // mean the same thing anywhere else. A set that uses one is exported without that collection.
    private static readonly HashSet<string> LocalOnlyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "library", "file_location", "in_collection",
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex FieldName();

    /// <summary>A cheap look at the text: does it announce itself as a pack file? Used by the shared Import to choose a reader.</summary>
    public static bool Recognises(string? content) =>
        !string.IsNullOrWhiteSpace(content)
        && content.Length <= MostCharacters
        && content.Contains(CollectionPackFileDto.FormatName, StringComparison.Ordinal)
        && content.TrimStart().StartsWith('{');

    /// <summary>Reads and checks a pack file. Throws <see cref="CollectionPackException"/> with a plain sentence when it is not one.</summary>
    public static PackBlueprint Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw Bad(CouldNotRead);
        }

        if (content.Length > MostCharacters)
        {
            throw Bad("This pack file is too large to read.");
        }

        CollectionPackFileDto? file;
        try
        {
            // The file must say so itself: the DTO's default would otherwise let any JSON object pass as a pack.
            using (var document = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("format", out var format)
                    || format.ValueKind != JsonValueKind.String
                    || !string.Equals(format.GetString(), CollectionPackFileDto.FormatName, StringComparison.Ordinal))
                {
                    throw Bad(CouldNotRead);
                }
            }

            file = JsonSerializer.Deserialize<CollectionPackFileDto>(content, ReadOptions);
        }
        catch (JsonException)
        {
            // Not valid JSON, or the wrong shape: the person is told it is not a pack file; there is no data to log.
            throw Bad(CouldNotRead);
        }

        if (file is null || !string.Equals(file.Format, CollectionPackFileDto.FormatName, StringComparison.Ordinal))
        {
            throw Bad(CouldNotRead);
        }

        if (file.Version < 1 || file.Version > CollectionPackFileDto.CurrentVersion)
        {
            throw Bad("This pack was made by a newer version of Library. Update Library to use it.");
        }

        if (file.Collections is not { Count: > 0 })
        {
            throw Bad("This pack has no collections in it.");
        }

        if (file.Collections.Count > CollectionPackCatalog.MostFileEntries)
        {
            throw Bad($"This pack has more than {CollectionPackCatalog.MostFileEntries} collections, which is more than Library will create from one file.");
        }

        var entries = file.Collections.Select(ToEntry).ToList();
        var name = Clean(file.Name, 120) ?? "Imported pack";
        return new PackBlueprint(FileId, name, Clean(file.Description, 500), file.CreatesSet && entries.Count > 0, entries);
    }

    private static PackEntry ToEntry(CollectionPackFileEntryDto source)
    {
        var name = Clean(source.Name, 120) ?? throw Bad("A collection in this pack has no name.");
        var rules = ToRules(source.RuleDefinition, name);
        if (!CollectionSeasonWindow.TryCreate(source.SeasonFrom, source.SeasonTo, out _))
        {
            throw Bad($"The seasonal window of “{name}” is not a pair of days like 10-01 and 11-01.");
        }

        if (source.MinItems is < 0 or > 1000 || source.MaxItems is < 0 or > 10000)
        {
            throw Bad($"The minimum or maximum number of titles of “{name}” is out of range.");
        }

        var sortField = SortFields.Contains(source.SortField ?? string.Empty) ? source.SortField!.Trim().ToLowerInvariant() : "title";
        var secondary = SortFields.Contains(source.SecondarySortField ?? string.Empty) && source.SecondarySortField != "random"
            ? source.SecondarySortField!.Trim().ToLowerInvariant()
            : null;
        return new PackEntry(
            name,
            Clean(source.Description, 500),
            rules,
            sortField,
            string.Equals(source.SortDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc",
            Clean(source.IconName, 64),
            secondary,
            secondary is null ? null : string.Equals(source.SecondarySortDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc",
            source.MinItems,
            source.MaxItems,
            Clean(source.SeasonFrom, 5),
            Clean(source.SeasonTo, 5),
            source.PrimaryArea is "Watch" or "Read" or "Listen" ? source.PrimaryArea : "Mixed",
            CollectionPackCatalog.StackArtworkHint);
    }

    private static CollectionRuleDefinition ToRules(CollectionRuleDefinitionDto? source, string collectionName)
    {
        if (source is null || source.Version != 1 || source.Groups is not { Count: > 0 and <= 12 })
        {
            throw Bad($"The rules of “{collectionName}” are missing or not in a shape Library understands.");
        }

        var total = 0;
        foreach (var group in source.Groups)
        {
            if (group.Conditions is not { Count: > 0 and <= 30 })
            {
                throw Bad($"A rule group of “{collectionName}” is empty or has too many rules.");
            }

            total += group.Conditions.Count;
            foreach (var condition in group.Conditions)
            {
                CheckRule(condition, collectionName);
            }
        }

        if (total > 100)
        {
            throw Bad($"“{collectionName}” has more rules than Library will read from a file.");
        }

        return source.ToDomain();
    }

    private static void CheckRule(CollectionRulePredicateDto rule, string collectionName)
    {
        var field = rule.Field?.Trim() ?? string.Empty;
        var op = rule.Op?.Trim() ?? string.Empty;
        if (!FieldName().IsMatch(field) || !Operators.Contains(op))
        {
            throw Bad($"A rule of “{collectionName}” uses a field or condition Library does not know.");
        }

        var values = rule.Values is { Length: > 0 } ? rule.Values : rule.Value is null ? [] : [rule.Value];
        if (!ValueFreeOperators.Contains(op) && (values.Length == 0 || values.Length > 100 || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 200)))
        {
            throw Bad($"A rule of “{collectionName}” has no value, or a value that is too long.");
        }
    }

    private static string? Clean(string? text, int most)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > most ? trimmed[..most] : trimmed;
    }

    private static CollectionPackException Bad(string message) => new(CollectionPackFailure.BadRequest, message);

    // ── Writing ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pack file for a set: its name and, for each collection still in it, the rules, order and window. Collections
    /// that cannot travel (they read this Library's folders or other collections, or no longer use rules) are named in
    /// <paramref name="omitted"/> instead.
    /// </summary>
    public static CollectionPackFileDto ToFile(Collection set, IEnumerable<Collection> children, List<string> omitted)
    {
        var entries = new List<CollectionPackFileEntryDto>();
        foreach (var child in children.OrderBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var name = child.DisplayName ?? "Untitled";
            CollectionRuleDefinition rules;
            try
            {
                rules = CollectionRuleEvaluator.ParseDefinition(child.RuleJson);
            }
            catch (FormatException)
            {
                // A hand-edited or old rule text that cannot be read cannot be written out either; say so in the answer.
                omitted.Add(name);
                continue;
            }

            if (rules.AllConditions.Count == 0 || rules.AllConditions.Any(rule => LocalOnlyFields.Contains(rule.Field)))
            {
                omitted.Add(name);
                continue;
            }

            entries.Add(new CollectionPackFileEntryDto
            {
                Name = name,
                Description = child.Description,
                IconName = child.IconName,
                PrimaryArea = child.PrimaryArea.ToString(),
                RuleDefinition = ToDto(rules),
                SortField = child.SortField,
                SortDirection = child.SortDirection.ToStorageValue(),
                SecondarySortField = child.SecondarySortField,
                SecondarySortDirection = child.SecondarySortDirection?.ToStorageValue(),
                MinItems = child.MinItems,
                MaxItems = child.MaxItems ?? 0,
                SeasonFrom = child.SeasonFrom,
                SeasonTo = child.SeasonTo,
            });
        }

        return new CollectionPackFileDto
        {
            Name = set.DisplayName ?? "Pack",
            Description = set.Description,
            CreatesSet = true,
            Collections = entries,
        };
    }

    /// <summary>The text a person saves as the pack file.</summary>
    public static string Serialize(CollectionPackFileDto file) => JsonSerializer.Serialize(file, WriteOptions);

    private static CollectionRuleDefinitionDto ToDto(CollectionRuleDefinition source) => new()
    {
        Version = source.Version,
        Groups = source.Groups.Select(group => new CollectionRuleGroupDto
        {
            Id = group.Id,
            JoinWithPrevious = group.JoinWithPrevious,
            MatchMode = group.MatchMode,
            Conditions = group.Conditions.Select(rule => new CollectionRulePredicateDto
            {
                Field = rule.Field,
                Op = rule.Op,
                Value = rule.Value,
                DisplayValue = rule.DisplayValue,
                Values = rule.Values,
                DisplayValues = rule.DisplayValues,
            }).ToList(),
        }).ToList(),
    };

    /// <summary>The rule-set a collection stores, as JSON text.</summary>
    public static string RuleJson(CollectionRuleDefinition rules) => JsonSerializer.Serialize(rules);
}
