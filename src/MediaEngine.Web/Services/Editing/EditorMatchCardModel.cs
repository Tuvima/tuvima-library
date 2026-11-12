using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Resources;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Editing;

/// <summary>The shape of the picture on the card: portrait for books and films, 16:9 for episodes, square for music (universal editor v6, D7).</summary>
public enum EditorMatchCardArt
{
    Portrait,
    Wide,
    Square,
}

/// <summary>
/// One row of the card's compare table: the field, what the file says, what the selected result says, and the Engine's outcome
/// (<see cref="EditorMatchEvidenceStates"/>: agrees is a tick, differs a cross, not checked a dash). The Dashboard never compares anything itself.
/// <see cref="Fact"/> is the Engine's key for the row, kept so the basis note can word it.
/// </summary>
public sealed record EditorMatchCardRow(string Field, string? YourFile, string? Result, string Outcome, string Fact);

/// <summary>
/// Everything the Match card shows for one result or for the current match, already worded. <see cref="Verdict"/> is null when the Engine sent
/// none (a Wikidata result has an evidence table but no verdict); the card then says nothing about one.
/// </summary>
public sealed record EditorMatchCardModel(
    string Title,
    string? SeriesLine,
    string? Byline,
    string? ImageUrl,
    EditorMatchCardArt Art,
    string ProviderName,
    string? SourceUrl,
    string? Description,
    string? Verdict,
    IReadOnlyList<EditorMatchCardRow> Rows,
    string BasisNote,
    IReadOnlyList<string> Notes);

/// <summary>Builds the card's model from the Engine's search result or its read of the current match, and words the small pieces the list rows share.</summary>
public static class EditorMatchCardModels
{
    /// <summary>The card for a search result. <paramref name="shown"/> is the chosen edition when there is one, else the work itself.</summary>
    public static EditorMatchCardModel ForResult(
        EditorMatchResultDto shown, EditorMatchResultDto work, string? kind, IStringLocalizer<SharedStrings> l)
    {
        var rows = shown.Reasons.Select(reason => new EditorMatchCardRow(
            FieldLabel(reason.Fact, kind, l),
            Value(reason.Fact, reason.YourFile, yours: true, l),
            Value(reason.Fact, reason.Result, yours: false, l),
            reason.State,
            reason.Fact)).ToList();
        return new EditorMatchCardModel(
            shown.Title,
            SeriesLine(shown.SeriesName ?? work.SeriesName, shown.SeriesNumber ?? work.SeriesNumber, kind, " · ", l),
            Join(shown.Creator, shown.Year),
            shown.ImageUrl ?? work.ImageUrl,
            ArtFor(shown.KindText ?? work.KindText, kind),
            shown.ProviderName,
            shown.SourceUrl ?? work.SourceUrl,
            shown.Description ?? work.Description,
            shown.Verdict,
            rows,
            BasisNote(rows, kind, l),
            []);
    }

    /// <summary>
    /// The card for a record the person picked in the TV or music flow and the Engine judged: the same card as a search result, with the provider's
    /// own name (not its id) and, where the judged answer carries no picture or description, the ones the picked record itself brought.
    /// </summary>
    public static EditorMatchCardModel ForPick(
        EditorMatchResultDto judged, ItemCanonicalRetailCandidateDto candidate, string? kind, string providerName, IStringLocalizer<SharedStrings> l)
    {
        var model = ForResult(judged, judged, kind, l);
        return model with
        {
            ImageUrl = model.ImageUrl ?? candidate.CoverUrl,
            Description = model.Description ?? candidate.Description,
            ProviderName = providerName,
        };
    }

    /// <summary>The card for the item's current match: the same card, filled from the Engine's read of the matched record and its "why" rows.</summary>
    public static EditorMatchCardModel ForCurrent(EditorCurrentMatchDto current, string? kind, IStringLocalizer<SharedStrings> l)
    {
        var record = current.Record!;
        var rows = current.Why.Select(row => new EditorMatchCardRow(
            FieldLabel(row.Field, kind, l),
            Value(row.Field, row.YourFile, yours: true, l),
            Value(row.Field, row.MatchedRecord, yours: false, l),
            row.State,
            row.Field)).ToList();
        var notes = new List<string>();
        if (current.Pinned)
        {
            notes.Add(l["EditorMatchPanel_ChosenByYou"].Value);
        }

        if (!string.IsNullOrWhiteSpace(current.Note))
        {
            notes.Add(current.Note);
        }

        return new EditorMatchCardModel(
            record.Title,
            null,
            Join(record.Creator, record.Year),
            record.ImageUrl,
            ArtFor(record.Kind, kind),
            record.ProviderName,
            record.SourceUrl,
            record.Description,
            current.Verdict ?? EditorMatchVerdicts.Possible,
            rows,
            BasisNote(rows, kind, l),
            notes);
    }

    /// <summary>"Percy Jackson and the Olympians · Book 2" (a list row joins the same two parts with a comma); null when the provider gave no series.</summary>
    public static string? SeriesLine(string? name, string? number, string? kind, string separator, IStringLocalizer<SharedStrings> l)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(number))
        {
            return name;
        }

        var position = kind switch
        {
            EditorKinds.Comic => l["EditorMatchCard_PositionIssue", number].Value,
            EditorKinds.Book or EditorKinds.Audiobook => l["EditorMatchCard_PositionBook", number].Value,
            _ => l["EditorMatchCard_PositionNumber", number].Value,
        };
        return name + separator + position;
    }

    /// <summary>"Rick Riordan · 2009": the creator, then the year; either may be missing.</summary>
    public static string? Join(params string?[] parts)
    {
        var shown = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return shown.Length == 0 ? null : string.Join(" · ", shown);
    }

    /// <summary>The picture shape: an episode is 16:9, music is square, everything else is a portrait.</summary>
    public static EditorMatchCardArt ArtFor(string? recordKind, string? editorKind) => (editorKind, recordKind) switch
    {
        (EditorKinds.TvEpisode, _) or (_, "Episode") => EditorMatchCardArt.Wide,
        (EditorKinds.Album or EditorKinds.Song, _) or (_, "Album" or "Song") => EditorMatchCardArt.Square,
        _ => EditorMatchCardArt.Portrait,
    };

    /// <summary>The "Links this book to Apple Books." line under Apply match, worded for the kind and the provider.</summary>
    public static string ApplyLine(string? kind, string provider, IStringLocalizer<SharedStrings> l) =>
        l["EditorMatchCard_LinksThis", EditorMatchKindWords.For(kind, l), provider].Value;

    // The field's name, in the kind's own words: the creator is an author, a director or an artist; the identifier is an ISBN for a book.
    private static string FieldLabel(string fact, string? kind, IStringLocalizer<SharedStrings> l) => fact switch
    {
        "title" => l["EditorMatchPanel_FieldTitle"].Value,
        "year" => l["EditorMatchPanel_FieldYear"].Value,
        "runtime" => l["EditorMatchPanel_FieldRuntime"].Value,
        "track_length" => l["EditorMatchCard_FieldLength"].Value,
        "show" => l["EditorMatchCard_FieldShow"].Value,
        "album" => l["EditorMatchCard_FieldAlbum"].Value,
        "creator" => kind switch
        {
            EditorKinds.Movie or EditorKinds.TvShow or EditorKinds.TvSeason or EditorKinds.TvEpisode => l["EditorMatchCard_FieldDirector"].Value,
            EditorKinds.Album or EditorKinds.Song => l["EditorMatchCard_FieldArtist"].Value,
            _ => l["EditorMatchCard_FieldAuthor"].Value,
        },
        "identifier" => kind is EditorKinds.Book or EditorKinds.Audiobook ? l["EditorMatchCard_FieldIsbn"].Value : l["EditorMatchCard_FieldIdentifier"].Value,
        "number" => kind switch
        {
            EditorKinds.Comic => l["EditorMatchCard_FieldIssue"].Value,
            EditorKinds.TvSeason => l["EditorMatchPanel_FieldSeason"].Value,
            EditorKinds.TvEpisode => l["EditorMatchCard_FieldSeasonAndEpisode"].Value,
            _ => l["EditorMatchCard_FieldNumber"].Value,
        },
        "season_number" => l["EditorMatchPanel_FieldSeason"].Value,
        "episode_number" => l["EditorMatchPanel_FieldEpisode"].Value,
        _ => l["EditorMatchCard_FieldOther"].Value,
    };

    // A measured length says so, and so does a provider's rounded one.
    private static string? Value(string fact, string? value, bool yours, IStringLocalizer<SharedStrings> l)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return fact switch
        {
            "runtime" => yours ? l["EditorMatchPanel_Measured", value].Value : l["EditorMatchPanel_Rounded", value].Value,
            "track_length" when yours => l["EditorMatchPanel_Measured", value].Value,
            _ => value,
        };
    }

    // What the verdict rests on: the facts that could be compared (a fact one side did not give is not part of it). The identifier comes first,
    // since it settles most matches, then the rest in the table's order: "Based on the ISBN, title and author."
    internal static string BasisNote(IReadOnlyList<EditorMatchCardRow> rows, string? kind, IStringLocalizer<SharedStrings> l)
    {
        var compared = rows.Where(row => row.Outcome != EditorMatchEvidenceStates.NotChecked).ToList();
        if (compared.Count == 0)
        {
            return l["EditorMatchCard_BasisNothing"].Value;
        }

        var ordered = compared.Where(row => row.Fact == "identifier").Concat(compared.Where(row => row.Fact != "identifier"));
        var words = ordered.Select(row => BasisWords(row.Fact, kind, l)).Distinct().ToList();
        var list = words.Count == 1
            ? words[0]
            : string.Join(", ", words.Take(words.Count - 1)) + " " + l["EditorMatchCard_And"].Value + " " + words[^1];
        return l["EditorMatchCard_BasedOn", list].Value;
    }

    // A fact as it reads in the middle of a sentence.
    private static string BasisWords(string fact, string? kind, IStringLocalizer<SharedStrings> l) => fact switch
    {
        "title" => l["EditorMatchCard_BasisTitle"].Value,
        "year" => l["EditorMatchCard_BasisYear"].Value,
        "runtime" => l["EditorMatchCard_BasisRuntime"].Value,
        "track_length" => l["EditorMatchCard_BasisLength"].Value,
        "show" => l["EditorMatchCard_BasisShow"].Value,
        "album" => l["EditorMatchCard_BasisAlbum"].Value,
        "creator" => kind switch
        {
            EditorKinds.Movie or EditorKinds.TvShow or EditorKinds.TvSeason or EditorKinds.TvEpisode => l["EditorMatchCard_BasisDirector"].Value,
            EditorKinds.Album or EditorKinds.Song => l["EditorMatchCard_BasisArtist"].Value,
            _ => l["EditorMatchCard_BasisAuthor"].Value,
        },
        "identifier" => kind is EditorKinds.Book or EditorKinds.Audiobook ? l["EditorMatchCard_BasisIsbn"].Value : l["EditorMatchCard_BasisIdentifier"].Value,
        "number" => kind switch
        {
            EditorKinds.Comic => l["EditorMatchCard_BasisIssue"].Value,
            EditorKinds.TvSeason => l["EditorMatchCard_BasisSeason"].Value,
            EditorKinds.TvEpisode => l["EditorMatchCard_BasisSeasonAndEpisode"].Value,
            _ => l["EditorMatchCard_BasisNumber"].Value,
        },
        "season_number" => l["EditorMatchCard_BasisSeason"].Value,
        "episode_number" => l["EditorMatchCard_BasisEpisode"].Value,
        _ => l["EditorMatchCard_BasisOther"].Value,
    };
}
