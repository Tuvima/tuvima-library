namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// What the Logs page asks the Engine for: a page of technical log lines. It becomes the query string of
/// <c>GET /logs/technical</c>; it is never sent as a body. Level is <c>error</c>, <c>warning</c>, <c>information</c>
/// or <c>all</c>; area is one of the Engine's area names; Search matches the message text.
/// </summary>
public sealed record TechnicalLogRequest(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Level = null,
    string? Area = null,
    string? Search = null,
    string? Cursor = null,
    int Limit = 100);
