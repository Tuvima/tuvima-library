using MediaEngine.Contracts.Details;

namespace MediaEngine.Api.Services.Details.Internals;

/// <summary>
/// The "Companion" entry in an item page's "..." menu: opens the phone companion for this title so a person can pick the
/// episode or chapter they are at by hand. It is a link (it carries a route), so the Dashboard needs no handler for it.
/// Only titles that have a position to follow get one: a film, an episode, a book, an audiobook or a comic issue.
/// </summary>
internal static class DetailCompanionAction
{
    public const string Key = "companion";

    public static bool AppliesTo(DetailEntityType entityType) => entityType is
        DetailEntityType.Work or DetailEntityType.Movie or DetailEntityType.TvEpisode or
        DetailEntityType.Book or DetailEntityType.Audiobook or DetailEntityType.ComicIssue;

    public static DetailAction Build(Guid workId) => new()
    {
        Key = Key,
        Label = "Companion",
        Subtitle = "Who is here and where you are",
        Icon = "groups",
        Route = $"/companion/{workId:D}",
        Tooltip = "Open the companion for this title",
    };
}
