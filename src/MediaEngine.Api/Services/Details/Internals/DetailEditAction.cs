using MediaEngine.Contracts.Details;

namespace MediaEngine.Api.Services.Details.Internals;

/// <summary>
/// The "..." menu's edit entry (details overlay spec D2). There is no standing Edit button, so the entry
/// names what is being edited: "Edit this show", "Edit this album".
/// </summary>
internal static class DetailEditAction
{
    public const string Key = "edit";

    public const string UpdatingReason = "Updating details. Editing is available when ingestion finishes.";

    public static string LabelFor(DetailEntityType entityType) => entityType switch
    {
        DetailEntityType.Work => "Edit this title",
        DetailEntityType.Movie => "Edit this movie",
        DetailEntityType.MovieSeries or DetailEntityType.BookSeries or DetailEntityType.ComicSeries => "Edit this series",
        DetailEntityType.TvShow => "Edit this show",
        DetailEntityType.TvSeason => "Edit this season",
        DetailEntityType.TvEpisode => "Edit this episode",
        DetailEntityType.Book => "Edit this book",
        DetailEntityType.Audiobook => "Edit this audiobook",
        DetailEntityType.ComicIssue => "Edit this comic",
        DetailEntityType.MusicAlbum => "Edit this album",
        DetailEntityType.Person => "Edit this person",
        DetailEntityType.Universe => "Edit this universe",
        DetailEntityType.Collection => "Edit this collection",
        // A type added later still gets a working entry; the label test lists every current type.
        _ => "Edit",
    };

    /// <summary>Disabled while the item is still processing; the reason travels with it so the menu can say why.</summary>
    public static DetailAction Build(DetailEntityType entityType, bool updatingDetails) => new()
    {
        Key = Key,
        Label = LabelFor(entityType),
        Icon = "edit",
        IsDisabled = updatingDetails,
        Tooltip = updatingDetails ? UpdatingReason : null,
    };
}
