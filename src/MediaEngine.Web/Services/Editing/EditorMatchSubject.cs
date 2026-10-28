namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What the Match tool needs to know about the item being matched (universal editor spec D11): who it is, what kind of record it is
/// looked up as, the details it holds now (the search starts from them) and which of its details the person has overridden.
/// </summary>
/// <param name="EntityId">The item the match is staged for.</param>
/// <param name="MediaType">The editor's media type: Books, Movies, TV, Music, Comics and so on.</param>
/// <param name="ScopeId">The kind of item inside that type: track, album, episode, season, series, movie, book.</param>
/// <param name="TargetGroup">The group of record searched for first (for a song, track).</param>
/// <param name="Title">What the item is called now.</param>
/// <param name="Creator">Its artist, author or director, when it has one.</param>
/// <param name="ParentLabel">The album, show or series it sits in, when it sits in one.</param>
/// <param name="Year">Its year, when it has one.</param>
/// <param name="Draft">Its details as the search sends them (key and text, only the ones with a value).</param>
/// <param name="OverriddenKeys">The keys of the details the person has overridden, as the editor stages them.</param>
public sealed record EditorMatchSubject(
    Guid EntityId,
    string MediaType,
    string ScopeId,
    string TargetGroup,
    string Title,
    string? Creator,
    string? ParentLabel,
    string? Year,
    IReadOnlyDictionary<string, string> Draft,
    IReadOnlyList<string> OverriddenKeys);
