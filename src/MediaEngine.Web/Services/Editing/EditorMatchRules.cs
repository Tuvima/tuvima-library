using System.Globalization;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain;

namespace MediaEngine.Web.Services.Editing;

/// <summary>What a result says about itself, in the words a result row and the review use.</summary>
public sealed record EditorMatchCandidateFacts(
    string Title,
    string? Creator,
    string? Parent,
    string? Year,
    string? Edition,
    string Provider);

/// <summary>
/// The Match tool's rules that need no screen (universal editor spec D5, D6, D11): what a result says about itself and the request
/// staged for the Engine. Kept apart so they can be tested directly.
/// </summary>
public static class EditorMatchRules
{
    private static readonly string[] EditionKeys = ["edition", "version", "format", "release_type"];

    /// <summary>The kind of record a group is, as the search asks for it.</summary>
    public static string TargetKindOf(string group) => group.ToLowerInvariant() switch
    {
        "artist" or "narrator" => "person",
        "album" or "series" or "show" => "container",
        _ => "item",
    };

    /// <summary>The words a result row shows.</summary>
    public static EditorMatchCandidateFacts FactsOf(ItemCanonicalRetailCandidateDto candidate)
    {
        var creator = FirstNonEmpty(candidate.Author, candidate.Director, Field(candidate, MetadataFieldConstants.Artist));
        return new EditorMatchCandidateFacts(
            candidate.Title,
            creator,
            ParentOf(candidate),
            FirstNonEmpty(candidate.Year, Field(candidate, MetadataFieldConstants.Year)),
            EditionOf(candidate),
            candidate.ProviderName);
    }

    /// <summary>The album, show or series a result sits in.</summary>
    public static string? ParentOf(ItemCanonicalRetailCandidateDto candidate) => FirstNonEmpty(
        Field(candidate, MetadataFieldConstants.Album),
        Field(candidate, MetadataFieldConstants.ShowName),
        Field(candidate, MetadataFieldConstants.Series));

    private static string? EditionOf(ItemCanonicalRetailCandidateDto candidate)
    {
        foreach (var key in EditionKeys)
        {
            if (candidate.ExtraFields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>What the Engine is asked to save for a chosen result (the Identity change only; the move it brings is staged separately, spec D6 and D8).</summary>
    public static ReplaceRetailMatchRequestDto RequestFor(ItemCanonicalRetailCandidateDto candidate, EditorMatchSubject subject, string group) => new()
    {
        TargetKind = TargetKindOf(group),
        TargetFieldGroup = group,
        TargetScopeId = subject.ScopeId,
        ProviderId = candidate.ProviderId,
        ProviderName = candidate.ProviderName,
        ProviderItemId = candidate.ProviderItemId ?? string.Empty,
        CoverUrl = candidate.CoverUrl,
        RequiredFields = new Dictionary<string, string>(candidate.RequiredFields, StringComparer.OrdinalIgnoreCase),
        SuggestedFields = new Dictionary<string, string>(candidate.SuggestedFields, StringComparer.OrdinalIgnoreCase),
        BridgeIds = new Dictionary<string, string>(candidate.BridgeIds, StringComparer.OrdinalIgnoreCase),
    };

    private static string? Field(ItemCanonicalRetailCandidateDto candidate, string key)
    {
        if (candidate.RequiredFields.TryGetValue(key, out var required) && !string.IsNullOrWhiteSpace(required))
        {
            return required.Trim();
        }

        return candidate.SuggestedFields.TryGetValue(key, out var suggested) && !string.IsNullOrWhiteSpace(suggested)
            ? suggested.Trim()
            : null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
