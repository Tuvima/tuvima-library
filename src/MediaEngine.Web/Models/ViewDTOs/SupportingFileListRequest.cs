namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// What the supporting-files browser asks the Engine for (universal editor v5 screen E): search text, the Where, type and group filters, the
/// sort and the page. It names no page size: the Engine reads rows per page from <c>config/editor.json</c>, so the Dashboard holds no number.
/// </summary>
/// <param name="Query">Words to find in file names, titles and descriptions; empty for no search.</param>
/// <param name="Where">The <c>where</c> value (<c>all</c>, <c>root</c> or a scope key).</param>
/// <param name="Type">The type filter, or null for every type.</param>
/// <param name="Group">The group filter (<c>all</c>, <c>ungrouped</c> or a group id).</param>
/// <param name="Sort">One of the <c>SupportingFileListDefaults.Sort*</c> values.</param>
/// <param name="Descending">True to reverse the sort.</param>
/// <param name="Page">The page, from 1.</param>
public sealed record SupportingFileListRequest(
    string? Query = null,
    string? Where = null,
    string? Type = null,
    string? Group = null,
    string? Sort = null,
    bool Descending = false,
    int Page = 1);
