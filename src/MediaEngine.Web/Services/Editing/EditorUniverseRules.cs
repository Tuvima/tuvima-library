using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Small rules for the Universe editor (universal editor v4, WP11): which subjects it covers, what its tabs are called and which of them it
/// offers. A Universe and a character, place or faction of one have Details, Pictures, Match and History, and no Files tab.
/// </summary>
public static class EditorUniverseRules
{
    /// <summary>True for a Universe or one of its things, the subjects the Universe editor covers.</summary>
    public static bool IsUniverseSubject(string? subjectKind) =>
        subjectKind is EditorSubjectKinds.Universe or EditorSubjectKinds.FictionalEntity;

    /// <summary>The name of the pictures tab: "Pictures" for a Universe and its things, "Artwork" everywhere else.</summary>
    public static string ArtworkTabLabel(string? subjectKind) => IsUniverseSubject(subjectKind) ? "Pictures" : "Artwork";

    /// <summary>The name of the match tab (universal editor v5 D3): "Match" for every item; a Universe's is read-only and says so inside.</summary>
    public static string MatchTabLabel(string? subjectKind) => "Match";

    /// <summary>True when the text is a Wikidata item id ("Q42").</summary>
    public static bool LooksLikeQid(string? text)
    {
        var value = text?.Trim();
        return value is { Length: > 1 } && value[0] is 'Q' or 'q' && value.Skip(1).All(char.IsAsciiDigit);
    }

    /// <summary>True when a Wikidata fact says the thing was added by hand (it has no Wikidata record).</summary>
    public static bool IsAddedByUser(string? wikidataFact) =>
        wikidataFact is not null && wikidataFact.Contains("added by you", StringComparison.OrdinalIgnoreCase);

    /// <summary>The kind line of the header for a Universe subject ("Universe", "Faction in Dune").</summary>
    public static string KindLine(string? subjectKind, string? thingKind, string? universeName) => subjectKind switch
    {
        EditorSubjectKinds.Universe => "Universe",
        EditorSubjectKinds.FictionalEntity => string.IsNullOrWhiteSpace(universeName)
            ? thingKind ?? "Character"
            : $"{thingKind ?? "Character"} in {universeName}",
        _ => string.Empty,
    };
}
