using MediaEngine.Contracts.Items;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>Which group a person asked to delete, as the Dashboard sends it.</summary>
/// <param name="GroupId">The show's, season's, album's or Series' own id.</param>
/// <param name="FallbackKind">One of show, season, album or series; used only when the Engine cannot find the group by id.</param>
/// <param name="FallbackTitle">The group's name as the page shows it; used only with <paramref name="FallbackWorkIds"/>.</param>
/// <param name="FallbackWorkIds">The titles the page lists; used only when the Engine cannot find the group by id.</param>
public sealed record LibraryItemGroupRequest(
    Guid GroupId,
    string? FallbackKind,
    string? FallbackTitle,
    IReadOnlyList<Guid> FallbackWorkIds);

// Delete a show, season, album or Series: the same plan, safety gate, fingerprint and clean-up as a single title, for every owned title in the group.
public sealed partial class LibraryItemDeletionService
{
    private const string GroupNotFound = "This is no longer in your library.";
    private const string GroupEmpty = "There is nothing in your library to delete here.";
    private static readonly string[] GroupKinds = ["show", "season", "album", "series"];

    public async Task<LibraryItemDeletionResult<LibraryItemDeletionPreview>> PreviewGroupAsync(
        LibraryItemGroupRequest request,
        CancellationToken ct = default)
    {
        var planned = await BuildGroupPlanAsync(request, ct).ConfigureAwait(false);
        return planned.IsSuccess
            ? LibraryItemDeletionResult<LibraryItemDeletionPreview>.Ok(planned.Value!.Preview)
            : LibraryItemDeletionResult<LibraryItemDeletionPreview>.Refused(planned.Refusal!);
    }

    /// <summary>
    /// Deletes the whole group the person confirmed, as one action with one fingerprint and one activity entry. When the plan
    /// has changed since the preview, nothing is touched and the refusal carries the fresh preview.
    /// </summary>
    public async Task<LibraryItemDeletionResult<LibraryItemDeletionResultDto>> DeleteGroupAsync(
        LibraryItemGroupRequest request,
        string? fingerprint,
        LibraryItemDeletionActor actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        await _deleteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var planned = await BuildGroupPlanAsync(request, ct).ConfigureAwait(false);
            if (!planned.IsSuccess)
            {
                return LibraryItemDeletionResult<LibraryItemDeletionResultDto>.Refused(planned.Refusal!);
            }

            var plan = planned.Value!;
            if (!string.Equals(fingerprint, plan.Preview.Fingerprint, StringComparison.Ordinal))
            {
                return LibraryItemDeletionResult<LibraryItemDeletionResultDto>.Refused(new LibraryItemDeletionRefusal(
                    LibraryItemDeletionRefusalKind.Changed, ChangedMessage, [], plan.Preview));
            }

            var result = await ExecuteAsync(plan, actor).ConfigureAwait(false);
            return LibraryItemDeletionResult<LibraryItemDeletionResultDto>.Ok(result);
        }
        finally
        {
            _deleteLock.Release();
        }
    }

    private async Task<LibraryItemDeletionResult<LibraryItemDeletionPlan>> BuildGroupPlanAsync(
        LibraryItemGroupRequest request,
        CancellationToken ct)
    {
        LibraryItemDeletionGroupContext context;
        IReadOnlyList<Guid> workIds;

        var resolved = request.GroupId == Guid.Empty
            ? null
            : await repository.ResolveGroupAsync(request.GroupId, ct).ConfigureAwait(false);
        if (resolved is not null)
        {
            workIds = resolved.OwnedWorkIds;
            context = new LibraryItemDeletionGroupContext(
                resolved.GroupId,
                resolved.Kind,
                resolved.Title,
                resolved.NotOwnedCount,
                resolved.SubGroups,
                resolved.ParentTitle,
                resolved.ParentGroupId,
                RemovesParent: string.Equals(resolved.Kind, "season", StringComparison.Ordinal)
                    && resolved.OwnedWorkIds.Count > 0
                    && resolved.ParentOwnedCount <= resolved.OwnedWorkIds.Count);
        }
        else if ((request.FallbackWorkIds ?? []).Count > 0
            && request.FallbackKind is { } kind
            && GroupKinds.Contains(kind, StringComparer.Ordinal))
        {
            // A grouping the page builds from its own titles has no id the Engine can follow; the titles are still checked one by one.
            workIds = [.. (request.FallbackWorkIds ?? []).Distinct()];
            var title = string.IsNullOrWhiteSpace(request.FallbackTitle) ? "this group" : request.FallbackTitle.Trim();
            context = new LibraryItemDeletionGroupContext(
                request.GroupId, kind, title.Length > 200 ? title[..200] : title, 0, [], null, null, false);
        }
        else
        {
            return Refused(LibraryItemDeletionRefusalKind.NotFound, GroupNotFound, []);
        }

        if (workIds.Count == 0)
        {
            return Refused(LibraryItemDeletionRefusalKind.Validation, GroupEmpty, []);
        }

        return await BuildPlanAsync(workIds, ct, vaultItems: false, group: context).ConfigureAwait(false);
    }

    private LibraryItemDeletionGroupDto DescribeGroup(
        LibraryItemDeletionGroupContext group,
        int titles,
        IReadOnlyList<LibraryItemDeletionPlannedFile> planned,
        long bytesFreed,
        bool goesToRecycleBin)
    {
        var settings = configuration?.LoadMaintenance() ?? new MaintenanceSettings();
        var filesDeleted = planned.Count(file => file.Outcome == LibraryItemDeletionOutcomes.Delete);

        // Typing the name is asked only when files will be destroyed for good (not with the Recycle bin on, not when every file stays on disk)
        // and the delete is a whole show, a whole Series, a big group or a big space (spec D4). The limits are settings.
        var big = group.Kind is "show" or "series"
            || titles > settings.DeleteTypeToConfirmTitles
            || bytesFreed > settings.DeleteTypeToConfirmGigabytes * 1_073_741_824L;
        var typed = filesDeleted > 0 && !goesToRecycleBin && big;

        return new LibraryItemDeletionGroupDto
        {
            Kind = group.Kind,
            Title = group.Title,
            ConfirmName = typed ? group.Title : string.Empty,
            RequiresTypedConfirmation = typed,
            TitlesOwned = titles,
            TitlesNotOwned = group.NotOwnedCount,
            SubGroups = [.. group.SubGroups.Select(part => new LibraryItemDeletionSubGroupDto
            {
                Title = part.Title,
                Owned = part.OwnedCount,
                Total = part.TotalCount,
            })],
            RemovesParent = group.RemovesParent,
            ParentTitle = group.ParentTitle,
        };
    }

    /// <summary>The group in the words of the activity line: the show "The Expanse", the album "OK Computer".</summary>
    private static string DescribeGroupForActivity(LibraryItemDeletionGroupContext group, LibraryItemDeletionPlan plan)
    {
        var noun = group.Kind switch
        {
            "show" => "show",
            "season" => "season",
            "album" => "album",
            _ => "Series",
        };
        var of = group.Kind == "season" && !string.IsNullOrWhiteSpace(group.ParentTitle) ? $" of \"{group.ParentTitle}\"" : string.Empty;
        return $"the {noun} \"{group.Title}\"{of} ({DescribeTitles(plan)})";
    }

    private static IReadOnlyList<Guid> GroupIdsToAnnounce(LibraryItemDeletionGroupContext? group)
    {
        if (group is null || group.GroupId == Guid.Empty)
        {
            return [];
        }

        return group.RemovesParent && group.ParentGroupId is { } parent ? [group.GroupId, parent] : [group.GroupId];
    }
}
