using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services.View;

/// <summary>Why a Trash question was turned away before any plan was made.</summary>
public enum ViewTrashPlanStatus
{
    Ok,
    Unauthenticated,
    Forbidden,
    NotFound,
}

/// <summary>What Empty Trash would do with one file of an item that is going.</summary>
public enum ViewTrashFileAction
{
    /// <summary>The file is removed from disk.</summary>
    Delete,

    /// <summary>Another item still uses this same path, so the file stays on disk and its size is not counted (D6).</summary>
    KeepShared,

    /// <summary>Nothing is at this path any more, so there is nothing to remove.</summary>
    AlreadyGone,
}

/// <summary>
/// One file of an item in the plan. <see cref="StoredPath"/> is what the data store holds; <see cref="ResolvedPath"/> is the
/// real path after every link was followed and is the only path that may be handed to the safety gate or deleted. Neither
/// ever leaves the Engine.
/// </summary>
public sealed record ViewTrashPlannedFile(
    Guid FileId,
    string Role,
    Guid SourceRowId,
    Guid SourceId,
    string StoredPath,
    string ResolvedPath,
    ViewTrashFileAction Action,
    long Bytes,
    FileSourceMutationPolicy Policy);

/// <summary>
/// An item in Trash and what Empty Trash would do with it. <see cref="Files"/> is filled only for <see cref="ViewTrashOutcomes.Delete"/>
/// items and may name the same path more than once (one file used in two roles); remove it once. <see cref="IsCompound"/> is true
/// for a Live Photo, a RAW set or an original with an edited version: one item with files in more than one role.
/// </summary>
public sealed record ViewTrashPlannedItem(
    Guid ItemId,
    string MediaKind,
    string Outcome,
    string? Reason,
    bool IsCompound,
    IReadOnlyList<ViewTrashPlannedFile> Files);

/// <summary>Everything Empty Trash needs, worked out once. The empty action recomputes the plan and compares <see cref="Preview"/>.Fingerprint.</summary>
public sealed record ViewTrashPlan(
    Guid PersonalSpaceId,
    Guid OwnerProfileId,
    IReadOnlyList<ViewTrashPlannedItem> Items,
    ViewTrashEmptyPreviewDto Preview);

/// <summary>Either a plan or the reason there is none.</summary>
public sealed record ViewTrashPlanResult(ViewTrashPlanStatus Status, ViewTrashPlan? Plan)
{
    public static ViewTrashPlanResult Refused(ViewTrashPlanStatus status) => new(status, null);
}

/// <summary>
/// Works out what emptying a person's Trash in View would do, and changes nothing. It looks at the caller's own Personal Space
/// only, so another person's Trash can never be reached. Whether a file may be removed is decided only by the shared source-file
/// safety gate, after the file's path has been followed through every link and shown to lie inside the owner's own profile
/// folder; a file in a linked (read-only) folder, or a missing source, is never handed to the gate as deletable.
/// </summary>
public sealed class ViewTrashDeletionPlanner(
    IViewTrashRepository trash,
    IViewPersonalSpaceRepository spaces,
    IViewProfileRepository profiles,
    ViewStorageService storage,
    ISourceMutationPolicyGate gate,
    IViewResourceAuthorizationService authorization,
    ILogger<ViewTrashDeletionPlanner> logger)
{
    public const string ReadOnlyReason = "In a read-only folder.";
    public const string ContributionPendingReason = "Waiting to be added to the Shared Library; cancel that first.";
    public const string SourceMissingReason = "Its folder is no longer set up in View, so the file was left where it is.";
    public const string StorageUnavailableReason = "View storage isn't available right now, so the file was left where it is.";
    public const string StorageReadOnlyReason = "View storage is read-only right now, so the file was left where it is.";
    public const string NotAllowedReason = "Library isn't allowed to change this file.";
    public const string NotAFileReason = "Something other than a file is at this location, so it was left where it is.";

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>What is in the signed-in person's own Trash, for the size line. Reads only the data store.</summary>
    public async Task<(ViewTrashPlanStatus Status, ViewTrashSummaryDto? Summary)> GetSummaryAsync(
        RequestAuthority authority, CancellationToken ct = default)
    {
        var (status, space) = await ResolveMineAsync(authority, ct).ConfigureAwait(false);
        if (space is null)
        {
            return (status, null);
        }

        var documents = (await profiles.GetPolicyAsync(space.OwnerProfileId, ct).ConfigureAwait(false)).DocumentsEnabled;
        var totals = await trash.GetTotalsAsync(space.Id, space.OwnerProfileId, documents, ct).ConfigureAwait(false);
        return (ViewTrashPlanStatus.Ok, new ViewTrashSummaryDto(
            totals.ItemCount, totals.ImageCount, totals.VideoCount, totals.DocumentCount, totals.OtherCount, totals.Bytes));
    }

    /// <summary>Builds the full plan for the signed-in person's own Trash.</summary>
    public async Task<ViewTrashPlanResult> BuildPlanAsync(RequestAuthority authority, CancellationToken ct = default)
    {
        var (status, space) = await ResolveMineAsync(authority, ct).ConfigureAwait(false);
        if (space is null)
        {
            return ViewTrashPlanResult.Refused(status);
        }

        var ownerId = space.OwnerProfileId;
        var documentsEnabled = (await profiles.GetPolicyAsync(ownerId, ct).ConfigureAwait(false)).DocumentsEnabled;
        var listed = (await trash.GetTrashedItemsAsync(space.Id, ownerId, ct).ConfigureAwait(false))
            .Where(item => documentsEnabled
                || !string.Equals(item.MediaKind, LocalAssetMediaKinds.Document, StringComparison.Ordinal))
            .OrderBy(item => item.ItemId)
            .ToList();

        // 1. Only items this person may manage ever appear. An item they cannot manage is simply absent.
        var items = new List<ViewTrashItem>(listed.Count);
        foreach (var item in listed)
        {
            ct.ThrowIfCancellationRequested();
            var decision = await authorization.AuthorizeAsync(authority,
                new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Asset, item.ItemId, ViewResourceAction.Manage), ct)
                .ConfigureAwait(false);
            if (decision.Outcome is ViewAccessOutcome.Forbidden or ViewAccessOutcome.Unauthenticated)
            {
                return ViewTrashPlanResult.Refused(ToStatus(decision.Outcome));
            }

            if (decision.IsAllowed)
            {
                items.Add(item);
            }
        }

        var pending = await trash.GetItemsWithPendingContributionAsync(space.Id, ownerId, ct).ConfigureAwait(false);
        var sources = (await spaces.GetSourcesAsync(space.Id, ct).ConfigureAwait(false)).ToDictionary(source => source.Id);
        var rootProblem = TryGetProfileRoot(space, out var profileRoot);
        var storageWritable = profileRoot is not null && SafeIsWritable();

        // 2. Decide each item on its own: all of its files go, or none do.
        var decided = new List<Decision>(items.Count);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            decided.Add(Decide(space, item, sources, pending, profileRoot, rootProblem, storageWritable));
        }

        var deleting = decided.Where(decision => decision.Outcome == ViewTrashOutcomes.Delete)
            .Select(decision => decision.Item.ItemId).ToHashSet();

        // 3. A path another item that is staying still uses is kept. Matching content on a different path is not sharing.
        var uses = await trash.GetPathUsesByOtherItemsAsync(space.Id, ownerId, ct).ConfigureAwait(false);
        var staysShared = new HashSet<(Guid ItemId, string Path)>();
        foreach (var use in uses)
        {
            if (!deleting.Contains(use.OtherItemId))
            {
                staysShared.Add((use.ItemId, use.FilePath.ToLowerInvariant()));
            }
        }

        var plannedItems = new List<ViewTrashPlannedItem>(decided.Count);
        var counted = new HashSet<string>(PathComparer);
        long bytesFreed = 0;
        foreach (var decision in decided)
        {
            if (decision.Outcome != ViewTrashOutcomes.Delete)
            {
                plannedItems.Add(new ViewTrashPlannedItem(
                    decision.Item.ItemId, decision.Item.MediaKind, decision.Outcome, decision.Reason, decision.IsCompound, []));
                continue;
            }

            var files = new List<ViewTrashPlannedFile>(decision.Files.Count);
            foreach (var file in decision.Files)
            {
                var action = file.Action;
                if (action == ViewTrashFileAction.Delete && staysShared.Contains((decision.Item.ItemId, file.StoredPath.ToLowerInvariant())))
                {
                    action = ViewTrashFileAction.KeepShared;
                }

                var bytes = action == ViewTrashFileAction.Delete && counted.Add(file.ResolvedPath) ? file.Bytes : 0;
                bytesFreed += bytes;
                files.Add(file with { Action = action, Bytes = bytes });
            }

            plannedItems.Add(new ViewTrashPlannedItem(
                decision.Item.ItemId, decision.Item.MediaKind, ViewTrashOutcomes.Delete, null, decision.IsCompound, files));
        }

        // 4. Manual Galleries that lose items, counts by kind, and the fingerprint the person confirms.
        var memberships = (await trash.GetManualGalleryMembershipsAsync(space.Id, ownerId, ct).ConfigureAwait(false))
            .Where(membership => deleting.Contains(membership.ItemId))
            .GroupBy(membership => membership.GalleryId)
            .Select(group => new ViewTrashPreviewGalleryDto(
                group.Key, group.First().Name, group.Select(membership => membership.ItemId).Distinct().Count()))
            .OrderBy(gallery => gallery.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(gallery => gallery.Name, StringComparer.Ordinal)
            .ThenBy(gallery => gallery.GalleryId)
            .ToList();

        var deleted = plannedItems.Where(item => item.Outcome == ViewTrashOutcomes.Delete).ToList();
        var skipped = plannedItems.Where(item => item.Outcome != ViewTrashOutcomes.Delete)
            .GroupBy(item => (Code: item.Outcome, Reason: item.Reason ?? string.Empty))
            .Select(group => new ViewTrashPreviewSkippedDto(group.Key.Code, group.Key.Reason, group.Count()))
            .OrderBy(entry => entry.ReasonCode, StringComparer.Ordinal)
            .ThenBy(entry => entry.Reason, StringComparer.Ordinal)
            .ToList();

        var preview = new ViewTrashEmptyPreviewDto(
            plannedItems.Select(item => new ViewTrashPreviewItemDto(item.ItemId, item.MediaKind, item.Outcome, item.Reason)).ToList(),
            memberships,
            deleted.Count,
            deleted.Count(item => item.MediaKind == LocalAssetMediaKinds.Image),
            deleted.Count(item => item.MediaKind == LocalAssetMediaKinds.Video),
            deleted.Count(item => item.MediaKind == LocalAssetMediaKinds.Document),
            deleted.Count(item => item.MediaKind is not (LocalAssetMediaKinds.Image or LocalAssetMediaKinds.Video or LocalAssetMediaKinds.Document)),
            skipped,
            bytesFreed,
            deleted.Any(item => item.IsCompound),
            ComputeFingerprint(ownerId, plannedItems, memberships));

        return new ViewTrashPlanResult(ViewTrashPlanStatus.Ok, new ViewTrashPlan(space.Id, ownerId, plannedItems, preview));
    }

    /// <summary>Authorizes the Mine scope and finds the caller's own Personal Space; nobody else's can be named.</summary>
    private async Task<(ViewTrashPlanStatus Status, ViewPersonalSpace? Space)> ResolveMineAsync(
        RequestAuthority authority, CancellationToken ct)
    {
        if (!authority.IsAuthenticated || authority.ActiveProfileId is not { } profileId)
        {
            return (ViewTrashPlanStatus.Unauthenticated, null);
        }

        // A person signed in as themselves only. A paired app's photo permissions are for viewing, and a service application
        // is not a person, so neither is ever given a Trash to look at (the route refuses them too; this keeps the planner safe to reuse).
        if (authority.PrincipalKind != PrincipalKind.Human)
        {
            return (ViewTrashPlanStatus.Forbidden, null);
        }

        var access = await authorization.AuthorizeAsync(authority,
            new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Preference, null), ct).ConfigureAwait(false);
        if (!access.IsAllowed)
        {
            return (ToStatus(access.Outcome), null);
        }

        var space = await spaces.GetByOwnerAsync(profileId, ct).ConfigureAwait(false);
        return space is null ? (ViewTrashPlanStatus.NotFound, null) : (ViewTrashPlanStatus.Ok, space);
    }

    private Decision Decide(
        ViewPersonalSpace space,
        ViewTrashItem item,
        IReadOnlyDictionary<Guid, ViewSource> sources,
        IReadOnlySet<Guid> pending,
        string? profileRoot,
        string? rootProblem,
        bool storageWritable)
    {
        // A file in a linked folder is for looking at: the whole item is read-only, even if its other files are not.
        foreach (var source in item.Files.SelectMany(file => file.Sources))
        {
            if (source.SourceId is { } id && sources.TryGetValue(id, out var row) && row.StorageMode == ViewSourceStorageMode.Linked)
            {
                return Skip(item, ViewTrashOutcomes.SkipReadOnly, ReadOnlyReason);
            }
        }

        if (pending.Contains(item.ItemId))
        {
            return Skip(item, ViewTrashOutcomes.SkipContributionPending, ContributionPendingReason);
        }

        var planned = new List<ViewTrashPlannedFile>();
        foreach (var file in item.Files)
        {
            foreach (var place in file.Sources)
            {
                var refusal = CheckPlace(space, file, place, sources, profileRoot, rootProblem, storageWritable, out var plannedFile);
                if (refusal is not null)
                {
                    return Skip(item, ViewTrashOutcomes.SkipRefused, refusal);
                }

                planned.Add(plannedFile!);
            }
        }

        return new Decision(item, ViewTrashOutcomes.Delete, null, planned);
    }

    /// <summary>Returns the reason a file may not go, or null with the planned file when it may.</summary>
    private string? CheckPlace(
        ViewPersonalSpace space,
        ViewTrashFile file,
        ViewTrashFileSource place,
        IReadOnlyDictionary<Guid, ViewSource> sources,
        string? profileRoot,
        string? rootProblem,
        bool storageWritable,
        out ViewTrashPlannedFile? planned)
    {
        planned = null;
        if (place.SourceId is not { } sourceId || !sources.TryGetValue(sourceId, out var source))
        {
            return SourceMissingReason;
        }

        if (profileRoot is null)
        {
            return rootProblem ?? StorageUnavailableReason;
        }

        // Follow every link first, including the file itself; only the real path is ever asked about.
        if (!ViewStorageService.TryResolveContainedPath(profileRoot, place.FilePath, out var resolved, out var reason))
        {
            return reason;
        }

        var policy = ViewSourceMutationPolicyFactory.Create(space, source, resolved.Root, storageWritable);
        var verdict = gate.Evaluate(new SourceMutationRequest
        {
            Source = policy,
            Mutation = SourceMutationKind.Delete,
            Path = resolved.Path,
        });
        if (!verdict.Allowed)
        {
            // The gate's own wording can carry technical detail (even an exception message), so it is logged here and never
            // sent to the person or folded into the fingerprint.
            logger.LogInformation("The source-file safety gate refused to let Empty Trash delete file {FileId} of a Personal Space: {Reason}",
                file.FileId, verdict.Reason);
            return storageWritable ? NotAllowedReason : StorageReadOnlyReason;
        }

        var action = ViewTrashFileAction.Delete;
        long bytes = 0;
        if (Directory.Exists(resolved.Path))
        {
            return NotAFileReason;
        }

        if (!File.Exists(resolved.Path))
        {
            action = ViewTrashFileAction.AlreadyGone;
        }
        else
        {
            bytes = LengthOf(resolved.Path) ?? file.ByteSize;
        }

        planned = new ViewTrashPlannedFile(
            file.FileId, file.Role, place.SourceRowId, sourceId, place.FilePath, resolved.Path, action, bytes, policy);
        return null;
    }

    private string? TryGetProfileRoot(ViewPersonalSpace space, out string? profileRoot)
    {
        try
        {
            // Read-only storage still gets a root: the gate, not this lookup, refuses to change files on it.
            profileRoot = storage.GetProfileRoot(space, requireWrite: false);
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException
            or MediaEngine.Storage.Configuration.ConfigValidationException
            or ArgumentException)
        {
            // Without a verified profile folder nothing can be shown to be inside it, so every file is left alone.
            logger.LogWarning(exception, "View storage could not be resolved while planning Empty Trash for a Personal Space");
            profileRoot = null;
            return StorageUnavailableReason;
        }
    }

    private bool SafeIsWritable()
    {
        try
        {
            return storage.IsStorageWritable();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException
            or MediaEngine.Storage.Configuration.ConfigValidationException)
        {
            // If writability cannot be read it counts as not writable: the gate then refuses, which costs nothing.
            logger.LogWarning(exception, "View storage writability could not be read while planning Empty Trash");
            return false;
        }
    }

    private static Decision Skip(ViewTrashItem item, string outcome, string reason) => new(item, outcome, reason, []);

    private static long? LengthOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file vanished or cannot be read right now; the size recorded when it was indexed stands in.
            _ = exception;
            return null;
        }
    }

    private static ViewTrashPlanStatus ToStatus(ViewAccessOutcome outcome) => outcome switch
    {
        ViewAccessOutcome.Unauthenticated => ViewTrashPlanStatus.Unauthenticated,
        ViewAccessOutcome.Forbidden => ViewTrashPlanStatus.Forbidden,
        _ => ViewTrashPlanStatus.NotFound,
    };

    /// <summary>
    /// Lowercase hex SHA-256 over a canonical, ordinal-sorted description of the plan: the owner, every item with its outcome and
    /// reason, every file with its real path, what happens to it and its size, and every Gallery with its count. Names of
    /// Galleries and timestamps are left out because they change on their own or add nothing.
    /// </summary>
    private static string ComputeFingerprint(
        Guid ownerId,
        IReadOnlyList<ViewTrashPlannedItem> items,
        IReadOnlyList<ViewTrashPreviewGalleryDto> galleries)
    {
        var lines = new List<string> { "owner|" + ownerId.ToString("D", CultureInfo.InvariantCulture) };
        foreach (var item in items)
        {
            lines.Add(string.Join('|', "item", item.ItemId.ToString("D", CultureInfo.InvariantCulture), item.Outcome, item.Reason ?? "-"));
            foreach (var file in item.Files)
            {
                lines.Add(string.Join('|', "file", item.ItemId.ToString("D", CultureInfo.InvariantCulture),
                    file.FileId.ToString("D", CultureInfo.InvariantCulture), file.Role, file.ResolvedPath,
                    file.Action.ToString(), file.Bytes.ToString(CultureInfo.InvariantCulture)));
            }
        }

        foreach (var gallery in galleries)
        {
            lines.Add(string.Join('|', "gallery", gallery.GalleryId.ToString("D", CultureInfo.InvariantCulture),
                gallery.ItemCount.ToString(CultureInfo.InvariantCulture)));
        }

        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    private sealed record Decision(ViewTrashItem Item, string Outcome, string? Reason, IReadOnlyList<ViewTrashPlannedFile> Files)
    {
        public bool IsCompound => Item.Files.Select(file => file.Role).Distinct(StringComparer.Ordinal).Count() > 1;
    }
}
