using MediaEngine.Application.Services;
using MediaEngine.Contracts.Activity;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

public sealed class ActivityLogReadService : IActivityLogReadService
{
    private const int DefaultLimit = 50;

    private readonly ISystemActivityRepository _activity;

    public ActivityLogReadService(ISystemActivityRepository activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        _activity = activity;
    }

    public async Task<PagedResponse<ActivityLogEntryDto>> GetLogAsync(
        string? category,
        string? search,
        int offset,
        int limit,
        string? mediaType = null,
        CancellationToken ct = default)
    {
        var page = PagedRequest.From(offset, limit, DefaultLimit);
        var (entries, total) = await _activity
            .QueryAsync(ActivityLogCategories.ActionTypesFor(category), search, page.Offset, page.Limit, mediaType, ct)
            .ConfigureAwait(false);

        var items = new List<ActivityLogEntryDto>(entries.Count);
        foreach (var entry in entries)
        {
            if (ActivityLogPresenter.Present(entry) is { } dto)
            {
                items.Add(dto);
            }
        }

        var hasMore = page.Offset + entries.Count < total;
        return new PagedResponse<ActivityLogEntryDto>(
            items,
            page.Offset,
            page.Limit,
            hasMore,
            total,
            hasMore ? (page.Offset + entries.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }
}
