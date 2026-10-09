using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Services.Display;

internal sealed class DisplayRecentComposerService(RecentCatalogueReadService catalogue, IViewQueryOrchestrator view)
{
    public async Task<DisplayRecentPageDto> LoadAsync(string? type, string? cursor, int? limit, Guid profileId, CancellationToken ct)
    {
        var scope = DisplayRecentCursor.NormalizeType(type);
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("An active profile is required.");
        }
        if (limit is < 1)
        {
            throw new ArgumentException("Recent limit must be positive.");
        }
        var take = Math.Min(limit ?? 18, 100);
        var boundary = DisplayRecentCursor.Decode(cursor, scope, profileId);
        var items = new List<DisplayRecentItemDto>();
        if (scope != "view")
        {
            items.AddRange(await catalogue.LoadAsync(scope, profileId, boundary, take + 1, ct));
        }
        if (scope is "all" or "view")
        {
            var result = await view.QueryAsync(new ViewAssetQueryRequest(new ViewScopeRequest(ViewScopeKind.Mine),
                Limit: take + 1, SortByAddedAt: true, AddedBefore: boundary?.AddedAt, AddedAfterKey: boundary?.Key), ct);
            if (result.Page is { } page)
            {
                foreach (var asset in page.Items)
                {
                    var key = DisplayRecentCursor.ViewKey(asset.Id);
                    if (!DisplayRecentCursor.IsAfter(asset.CreatedAt, key, boundary))
                    {
                        continue;
                    }
                    items.Add(new(key, asset.CreatedAt, null, new(asset.Id, asset.LibraryId, asset.Title ?? asset.FileName,
                        asset.FileName, asset.MediaKind, asset.Width, asset.Height, asset.DurationSeconds, asset.CreatedAt)));
                }
            }
        }
        return DisplayRecentCursor.Page(scope, profileId, items, take);
    }
}
