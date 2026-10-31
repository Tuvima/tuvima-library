using MediaEngine.Contracts.Items;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>What the caller may do and know about the Recycle bin (spec Delete a show, season, album or Series, D12 to D14).</summary>
/// <param name="CanManage">An administrator: sees everything and may Restore or Empty.</param>
/// <param name="CanListItems">May see the names of the items. A restricted (child) profile may not, because the bin keeps no age rating for an item; it sees totals only.</param>
/// <param name="Requester">The caller's name, to mark the items they already asked to keep.</param>
public sealed record RecycleBinViewer(bool CanManage, bool CanListItems, string Requester);

/// <summary>Turns the bin's entries into the view one profile is allowed to have. Pure, so the privacy rules are testable.</summary>
public static class RecycleBinSummaryBuilder
{
    public static RecycleBinSummaryDto Build(
        IReadOnlyList<RecycleBinEntry> entries,
        DateTimeOffset now,
        Func<string?, int> retentionDaysFor,
        int warningDays,
        string? mediaType,
        RecycleBinViewer viewer)
    {
        var inScope = string.IsNullOrWhiteSpace(mediaType)
            ? entries
            : [.. entries.Where(entry => string.Equals(entry.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))];

        var listed = new List<RecycleBinListItemDto>();
        var otherItems = 0;
        long otherBytes = 0;
        foreach (var entry in inScope.OrderBy(entry => entry.DeletedAt))
        {
            if (!viewer.CanListItems)
            {
                otherItems++;
                otherBytes += entry.SizeBytes;
                continue;
            }

            var requests = entry.KeepRequestedBy ?? [];
            listed.Add(new RecycleBinListItemDto
            {
                Id = entry.Id,
                Title = string.IsNullOrWhiteSpace(entry.Title) ? entry.FileName : entry.Title,
                MediaType = entry.MediaType,
                SizeBytes = entry.SizeBytes,
                DeletedAt = entry.DeletedAt,
                DeletedBy = entry.DeletedBy,
                LeavesInDays = DaysLeft(entry, now, retentionDaysFor(entry.LibraryId)),
                Requested = requests.Contains(viewer.Requester, StringComparer.OrdinalIgnoreCase),
                RequestedBy = viewer.CanManage ? requests : [],
            });
        }

        var soon = viewer.CanManage
            ? inScope.Where(entry => DaysLeft(entry, now, retentionDaysFor(entry.LibraryId)) is { } left && left <= warningDays).ToList()
            : [];
        return new RecycleBinSummaryDto
        {
            TotalItems = inScope.Count,
            TotalBytes = inScope.Sum(entry => entry.SizeBytes),
            CanManage = viewer.CanManage,
            Items = listed,
            OtherItems = otherItems,
            OtherBytes = otherBytes,
            LeavingSoonItems = soon.Count,
            LeavingSoonBytes = soon.Sum(entry => entry.SizeBytes),
            LeavingSoonWindowDays = warningDays,
            LeavingSoonDays = soon.Count == 0 ? null : soon.Min(entry => DaysLeft(entry, now, retentionDaysFor(entry.LibraryId))),
        };
    }

    /// <summary>Each library's own retention days; a library the settings no longer know keeps the 90-day default.</summary>
    public static Func<string?, int> RetentionFrom(MediaEngine.Domain.Configuration.LibrariesConfiguration libraries)
    {
        var days = libraries.Libraries
            .Where(library => library.RecycleBin)
            .ToDictionary(library => library.Id, library => library.RecycleBinDays, StringComparer.Ordinal);
        return id => id is not null && days.TryGetValue(id, out var value) ? value : 90;
    }

    /// <summary>Whole days before the automatic removal (0 = today); null when the bin never empties itself.</summary>
    public static int? DaysLeft(RecycleBinEntry entry, DateTimeOffset now, int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return null;
        }

        var left = entry.DeletedAt.AddDays(retentionDays) - now;
        return Math.Max(0, (int)Math.Ceiling(left.TotalDays));
    }
}
