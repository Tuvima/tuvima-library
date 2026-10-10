using System.Globalization;
using System.Text;
using Dapper;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.View;

public sealed class ViewScopePersistenceService(
    IProfileRepository profiles,
    IViewProfileRepository policies,
    IViewPersonalSpaceRepository spaces,
    IViewSharedLibraryRepository sharedLibraries,
    IDatabaseConnection database) : IViewScopeStore
{
    public async Task<ViewScopeStoreEntry?> FindProfileAsync(Guid profileId, CancellationToken ct = default)
    {
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        if (profile is null)
        {
            return null;
        }

        return new ViewScopeStoreEntry(
            await policies.GetPolicyAsync(profileId, ct).ConfigureAwait(false),
            await spaces.GetByOwnerAsync(profileId, ct).ConfigureAwait(false),
            profile.DisplayName,
            profile.AvatarColor,
            profile.AvatarImagePath is null ? null : $"/profiles/{profile.Id:D}/avatar",
            profile.HouseholdId);
    }

    public async Task<IReadOnlyList<ViewScopeStoreEntry>> GetProfilesAsync(CancellationToken ct = default)
    {
        var result = new List<ViewScopeStoreEntry>();
        foreach (var profile in await profiles.GetAllAsync(ct).ConfigureAwait(false))
        {
            result.Add(new ViewScopeStoreEntry(
                await policies.GetPolicyAsync(profile.Id, ct).ConfigureAwait(false),
                await spaces.GetByOwnerAsync(profile.Id, ct).ConfigureAwait(false),
                profile.DisplayName,
                profile.AvatarColor,
                profile.AvatarImagePath is null ? null : $"/profiles/{profile.Id:D}/avatar",
                profile.HouseholdId));
        }
        return result;
    }

    public async Task<Guid?> GetSharedLibraryIdAsync(CancellationToken ct = default) =>
        await GetSharedLibraryIdAsync(null, ct).ConfigureAwait(false);

    public async Task<Guid?> GetSharedLibraryIdAsync(Guid? profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (profileId is not { } id)
        {
            // An application acting for the server uses the server's own Shared library (none until a household exists).
            using var connection = database.CreateConnection();
            return connection.ExecuteScalar<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM households;", cancellationToken: ct)) == 0
                ? null
                : (await sharedLibraries.GetAsync(ct).ConfigureAwait(false)).LibraryId;
        }

        // A person only ever sees their own household's Shared library, created the first time it is needed.
        // A profile with no household (the setup profile before any account exists) uses the server's own.
        var household = (await profiles.GetByIdAsync(id, ct).ConfigureAwait(false))?.HouseholdId;
        return household is { } householdId
            ? (await sharedLibraries.EnsureForHouseholdAsync(householdId, ct).ConfigureAwait(false)).LibraryId
            : (await sharedLibraries.GetAsync(ct).ConfigureAwait(false)).LibraryId;
    }
}

public sealed class ViewResourcePersistenceService(
    ILocalAssetRepository assets,
    IViewGalleryRepository galleries,
    IViewPersonalSpaceRepository spaces,
    IDatabaseConnection database) : IViewResourceStore
{
    public async Task<ViewResourceDescriptor?> FindAsync(
        ViewResourceKind kind,
        Guid resourceId,
        Guid requestingProfileId,
        CancellationToken ct = default)
    {
        if (kind == ViewResourceKind.Gallery)
        {
            var gallery = await galleries.GetAsync(resourceId, ct).ConfigureAwait(false);
            if (gallery is null)
            {
                return null;
            }

            var space = await spaces.GetByOwnerAsync(gallery.OwnerProfileId, ct).ConfigureAwait(false);
            var shares = await galleries.GetSharesAsync(resourceId, ct).ConfigureAwait(false);
            return new ViewResourceDescriptor(
                kind,
                resourceId,
                gallery.OwnerProfileId,
                space?.LibraryId,
                shares.Select(share => share.ProfileId).ToHashSet(),
                shares.Where(share => share.Permission == ViewGallerySharePermission.Contribute)
                    .Select(share => share.ProfileId).ToHashSet());
        }

        var item = assets.Find(resourceId, ct);
        if (item is null)
        {
            return null;
        }

        var explicitProfiles = requestingProfileId == Guid.Empty
            ? new HashSet<Guid>()
            : await GetExplicitAssetRecipientsAsync(item.Id, requestingProfileId, ct).ConfigureAwait(false);
        using var connection = database.CreateConnection();
        var isSharedLibraryAsset = connection.ExecuteScalar<int>(new Dapper.CommandDefinition(
            "SELECT COUNT(*) FROM view_shared_assets WHERE item_id = @itemId;",
            new { itemId = item.Id }, cancellationToken: ct)) > 0;
        return new ViewResourceDescriptor(
            kind,
            item.Id,
            item.OwnerProfileId,
            item.LibraryId,
            explicitProfiles,
            IsSharedLibraryAsset: isSharedLibraryAsset,
            Hidden: item.Hidden);
    }

    private async Task<IReadOnlySet<Guid>> GetExplicitAssetRecipientsAsync(
        Guid itemId,
        Guid requestingProfileId,
        CancellationToken ct)
    {
        return await galleries.IsItemSharedWithProfileAsync(itemId, requestingProfileId, ct)
            .ConfigureAwait(false)
            ? new HashSet<Guid> { requestingProfileId }
            : new HashSet<Guid>();
    }

    public Task<LocalAssetContentLocation?> ResolveContentAsync(Guid itemId, string role,
        ResolvedViewScope scope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<AuthorizedContentRow>(new CommandDefinition("""
            SELECT li.id AS ItemId, lf.id AS FileId, li.library_id AS LibraryId,
                   li.owner_profile_id AS OwnerProfileId, lfs.source_id AS SourceId,
                   lfs.device_id AS DeviceId, lfs.file_path AS FilePath,
                   lf.mime_type AS MimeType, lf.byte_size AS ByteSize,
                   lf.content_hash AS ContentHash, lif.role AS Role,
                   lif.derivative_kind AS DerivativeKind
              FROM local_items li
              JOIN local_item_files lif ON lif.item_id=li.id AND lif.role=@role
              JOIN local_files lf ON lf.id=lif.file_id
              JOIN local_file_sources lfs ON lfs.file_id=lf.id AND lfs.library_id=li.library_id
              JOIN view_sources vs ON vs.id=lfs.source_id AND vs.library_id=li.library_id
             WHERE li.id=@itemId AND li.library_id=@libraryId
               AND ((li.scope_kind='shared' AND vs.scope_kind='shared' AND vs.personal_space_id IS NULL)
                 OR (li.scope_kind='personal' AND vs.scope_kind='personal'
                     AND vs.personal_space_id=li.personal_space_id))
             ORDER BY lfs.indexed_at DESC, lfs.id LIMIT 1;
            """, new { itemId, role, libraryId = scope.LibraryIds.Single() }, cancellationToken: ct));
        return Task.FromResult(row is null ? null : new LocalAssetContentLocation(
            row.ItemId, row.FileId, row.LibraryId, row.OwnerProfileId,
            row.SourceId, row.DeviceId, row.FilePath, row.MimeType, row.ByteSize,
            row.ContentHash, row.Role, row.DerivativeKind));
    }

    private sealed class AuthorizedContentRow
    {
        public Guid ItemId { get; init; }
        public Guid FileId { get; init; }
        public Guid LibraryId { get; init; }
        public Guid? OwnerProfileId { get; init; }
        public Guid? SourceId { get; init; }
        public Guid? DeviceId { get; init; }
        public string FilePath { get; init; } = "";
        public string MimeType { get; init; } = "";
        public long ByteSize { get; init; }
        public string ContentHash { get; init; } = "";
        public string Role { get; init; } = "";
        public string? DerivativeKind { get; init; }
    }
}

public sealed class ViewAssetQueryService(ILocalAssetRepository assets) : IViewAssetQueryBackend
{
    public Task<ViewAssetTimelinePageDto> QueryAsync(
        ViewAssetQueryPlan plan,
        CancellationToken ct = default)
    {
        var cursor = ViewTimelineCursorCodec.Decode(plan.Cursor);
        var page = assets.QueryTimeline(new LocalAssetTimelineQuery(
            plan.Scope.LibraryIds,
            plan.Limit,
            cursor?.EffectiveAt,
            cursor?.ItemId,
            plan.Search,
            plan.MediaKinds,
            plan.FavoritesOnly,
            plan.IncludeHidden,
            plan.HiddenOnly,
            plan.GalleryId,
            plan.Lifecycle,
            plan.SmartRule,
            plan.TimelineEligibleOnly,
            plan.IncludeSharedLibraryAssets,
            plan.AnchorBefore, plan.WithoutLocation, plan.PersonKey, plan.From, plan.To, plan.SortByAddedAt, plan.AddedBefore, plan.AddedAfterKey), ct);
        return Task.FromResult(new ViewAssetTimelinePageDto(
            page.Items,
            plan.SortByAddedAt ? null : ViewTimelineCursorCodec.Encode(page.NextCursor),
            page.HasMore));
    }

    public Task<ViewTimelineIndexDto> IndexAsync(
        ViewAssetQueryPlan plan,
        CancellationToken ct = default)
    {
        var buckets = assets.QueryTimelineIndex(new LocalAssetTimelineQuery(
            plan.Scope.LibraryIds,
            Search: plan.Search,
            MediaKinds: plan.MediaKinds,
            FavoritesOnly: plan.FavoritesOnly,
            IncludeHidden: plan.IncludeHidden,
            HiddenOnly: plan.HiddenOnly,
            GalleryId: plan.GalleryId,
            Lifecycle: plan.Lifecycle,
            SmartRule: plan.SmartRule,
            TimelineEligibleOnly: plan.TimelineEligibleOnly,
            IncludeSharedLibraryAssets: plan.IncludeSharedLibraryAssets), ct);
        return Task.FromResult(new ViewTimelineIndexDto(buckets.Select(bucket => new ViewTimelineBucketDto(
            bucket.Year,
            bucket.Month,
            bucket.AssetCount,
            bucket.EarliestAt,
            bucket.LatestAt)).ToList()));
    }
}

public static class ViewTimelineCursorCodec
{
    public static string? Encode(LocalAssetTimelineCursor? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{cursor.EffectiveAt.UtcTicks}:{cursor.ItemId:N}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static LocalAssetTimelineCursor? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            var separator = decoded.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0
                || !long.TryParse(decoded[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !Guid.TryParseExact(decoded[(separator + 1)..], "N", out var itemId))
            {
                throw new FormatException();
            }

            return new LocalAssetTimelineCursor(new DateTimeOffset(ticks, TimeSpan.Zero), itemId);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException)
        {
            throw new ArgumentException("The View timeline cursor is invalid.", nameof(value));
        }
    }
}
