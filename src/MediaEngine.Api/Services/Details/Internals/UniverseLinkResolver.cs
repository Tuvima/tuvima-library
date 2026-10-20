using MediaEngine.Contracts.Details;

namespace MediaEngine.Api.Services.Details.Internals;

/// <summary>
/// Answers, for one request, whether an item page should link to its Universe: the link when the Universe with this
/// Wikidata QID is one the requesting profile can see and the Library holds at least two Series or two formats in it,
/// otherwise null. The endpoint supplies it per request because the profile-aware Universe read is request-scoped.
/// </summary>
internal delegate Task<UniverseLinkViewModel?> UniverseLinkResolver(string universeQid, CancellationToken ct);
