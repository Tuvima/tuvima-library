using Dapper;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class ViewSharedTransferServiceTests
{
    [Fact]
    public async Task Contribution_DoesNotMoveUntilCuratorAccepts_AndIsIdempotent()
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, true, true, true, null));
        var original = fixture.WriteManaged("keeper.jpg", [11, 12, 13]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        Assert.NotNull(indexed);
        var preview = await fixture.Contributions.PreviewAsync(fixture.Authority,
            new ViewSharedContributionPreviewRequest([indexed!.ItemId]));
        var request = new ViewSharedContributionSubmitRequest([indexed.ItemId], "timeline", null,
            "Worth keeping", preview.PreviewRevision, "stable-request");

        var pending = await fixture.Contributions.SubmitAsync(fixture.Authority, request);
        var repeated = await fixture.Contributions.SubmitAsync(fixture.Authority, request);

        Assert.Equal(pending.Id, repeated.Id);
        Assert.Equal("pending", pending.Status);
        Assert.True(File.Exists(original));

        await fixture.Contributions.DecideAsync(fixture.Authority, pending.Id,
            new ViewSharedContributionDecisionRequest("accepted", pending.Revision));
        await fixture.Contributions.ProcessAsync(pending.Id);
        var accepted = await fixture.Contributions.GetRequiredAsync(fixture.Authority, pending.Id, true);

        Assert.Equal("accepted", accepted.Status);
        Assert.Equal("completed", Assert.Single(accepted.Items).ExecutionState);
        Assert.Contains(accepted.Events, value => value.EventType == "submitted");
        Assert.Contains(accepted.Events, value => value.EventType == "accepted");
        Assert.Contains(accepted.Events, value => value.EventType == "transfer_completed");
        Assert.False(File.Exists(original));
    }

    [Fact]
    public async Task CuratorCanAddOwnedItemDirectlyWithoutSubmitGrant()
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, false, true, true, null));
        var original = fixture.WriteManaged("direct.jpg", [31, 32]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);

        var accepted = await fixture.Contributions.AddDirectAsync(fixture.Authority,
            new ViewSharedDirectAddRequest([indexed!.ItemId], IdempotencyKey: "direct-request"));

        Assert.Equal("accepted", accepted.Status);
        Assert.True(File.Exists(original));
        await fixture.Contributions.ProcessAsync(accepted.Id);
        Assert.False(File.Exists(original));
    }

    [Fact]
    public async Task CuratorPolicyCannotReplaceEffectiveAccountAdministratorAuthority()
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, false, true, true, null));
        var original = fixture.WriteManaged("denied-direct.jpg", [41, 42]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        var nonAdministrator = fixture.Authority with { AccountIsAdministrator = false };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Contributions.AddDirectAsync(
            nonAdministrator, new ViewSharedDirectAddRequest([indexed!.ItemId])));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public async Task CuratorActionRequiresAdministratorSurfaceUnlock()
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, false, true, true, null));
        var original = fixture.WriteManaged("unlock-direct.jpg", [61, 62]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);

        await fixture.Contributions.AddDirectAsync(fixture.Authority,
            new ViewSharedDirectAddRequest([indexed!.ItemId]));

        Assert.True(fixture.Authorization.LastRequirement!.RequiresAdministrator);
        Assert.True(fixture.Authorization.LastRequirement.RequiresAdministratorSurfaceUnlock);
    }

    [Fact]
    public async Task ContributionCannotProbeAnotherProfilesItem()
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, true, true, true, null));
        var original = fixture.WriteManaged("private-item.jpg", [71, 72]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        var otherProfileId = Guid.NewGuid();
        await fixture.InsertProfileAsync(new Profile
        {
            Id = otherProfileId,
            DisplayName = "Other profile",
            Role = ProfileRole.StandardUser,
        });
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            otherProfileId, true, true, true, false, false, null));
        var otherAuthority = fixture.Authority with { ActiveProfileId = otherProfileId };

        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Contributions.PreviewAsync(
            otherAuthority, new ViewSharedContributionPreviewRequest([indexed!.ItemId])));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public async Task DisabledAccountCannotSubmitDespiteProfilePolicy()
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, true, true, true, null));
        var original = fixture.WriteManaged("disabled-submit.jpg", [51, 52]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Contributions.PreviewAsync(
            fixture.Authority with { AccountEnabled = false },
            new ViewSharedContributionPreviewRequest([indexed!.ItemId])));
        Assert.True(File.Exists(original));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DisabledGrantOrDelegatedApplicationCannotSubmit(
        bool grantEnabled,
        bool applicationEnabled)
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, true, true, true, null));
        var original = fixture.WriteManaged("disabled-binding.jpg", [53, 54]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        var authority = fixture.Authority with
        {
            PrincipalKind = applicationEnabled ? PrincipalKind.Human : PrincipalKind.DelegatedUserClient,
            GrantEnabled = grantEnabled,
            ApplicationId = applicationEnabled ? null : Guid.NewGuid(),
            ApplicationEnabled = applicationEnabled,
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Contributions.PreviewAsync(
            authority, new ViewSharedContributionPreviewRequest([indexed!.ItemId])));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public async Task DeclinedContribution_LeavesOriginalUntouched()
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, true, true, true, null));
        var original = fixture.WriteManaged("declined.jpg", [21, 22]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        var preview = await fixture.Contributions.PreviewAsync(fixture.Authority,
            new ViewSharedContributionPreviewRequest([indexed!.ItemId]));
        var pending = await fixture.Contributions.SubmitAsync(fixture.Authority,
            new([indexed.ItemId], "timeline", null, null, preview.PreviewRevision, "decline-request"));

        var declined = await fixture.Contributions.DecideAsync(fixture.Authority, pending.Id,
            new("declined", pending.Revision, "Not for the shared collection"));

        Assert.Equal("declined", declined.Status);
        Assert.Equal("waiting", Assert.Single(declined.Items).ExecutionState);
        Assert.True(File.Exists(original));
    }

    [Fact]
    public async Task ManagedItem_IsVerifiedMovedAndBrowsableUnderSharedRoot()
    {
        using var fixture = new Fixture();
        var original = fixture.WriteManaged("shared-moment.jpg", [1, 2, 3, 4, 5]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        Assert.NotNull(indexed);

        var preview = fixture.Transfers.Preview(indexed!.ItemId, "timeline", null);
        var result = await fixture.Transfers.ExecuteAsync(indexed.ItemId, fixture.ProfileId, "timeline", null);
        var repeated = await fixture.Transfers.ExecuteAsync(indexed.ItemId, fixture.ProfileId, "timeline", null);

        Assert.Equal("move", preview.Operation);
        Assert.Equal("completed", result.State);
        Assert.False(File.Exists(original));
        var destination = Assert.Single(result.DestinationPaths);
        Assert.StartsWith(fixture.Storage.GetSharedRoot(), destination, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([1, 2, 3, 4, 5], await File.ReadAllBytesAsync(destination));
        Assert.Equal(result.DestinationPaths, repeated.DestinationPaths);

        var sharedLibraryId = fixture.SharedLibraryId();
        var sharedSourceId = fixture.SharedSourceId("shared:timeline");
        var root = await fixture.Folders.QueryAsync(
            fixture.ProfileId,
            new ResolvedViewScope(ViewScopeKind.Shared, null, new HashSet<Guid> { sharedLibraryId }),
            null, null, false, null, 0, 100);
        Assert.Contains(root.Sources, source => source.SourceId == sharedSourceId);
        var shared = await fixture.Folders.QueryAsync(
            fixture.ProfileId,
            new ResolvedViewScope(ViewScopeKind.Shared, null, new HashSet<Guid> { sharedLibraryId }),
            sharedSourceId, null, true, null, 0, 100);
        Assert.Equal("Timeline", Assert.Single(shared.Breadcrumbs).Label);
        var sharedItem = Assert.Single(shared.Items);
        Assert.Equal("shared", sharedItem.ScopeKind);
        Assert.Null(sharedItem.OwnerProfileId);
        Assert.Null(sharedItem.PersonalSpaceId);
        Assert.NotEqual(indexed.ItemId, sharedItem.Id);
        Assert.Equal(indexed.ItemId, fixture.OriginItemId(sharedItem.Id));
    }

    [Fact]
    public async Task LinkedItem_IsCopiedAndExternalOriginalRemains()
    {
        using var fixture = new Fixture();
        var linkedRoot = Directory.CreateDirectory(Path.Combine(fixture.Root, "linked"));
        await fixture.Storage.AddLinkedSourceAsync(fixture.Space, "Home Movies", linkedRoot.FullName, true);
        var original = Path.Combine(linkedRoot.FullName, "home-movie.mp4");
        await File.WriteAllBytesAsync(original, [8, 9, 10]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        Assert.NotNull(indexed);

        var preview = fixture.Transfers.Preview(indexed!.ItemId, "folder", "Home Movies");
        var result = await fixture.Transfers.ExecuteAsync(
            indexed.ItemId, fixture.ProfileId, "folder", "Home Movies");

        Assert.Equal("copy", preview.Operation);
        Assert.Equal("completed", result.State);
        Assert.True(File.Exists(original));
        var destination = Assert.Single(result.DestinationPaths);
        Assert.Equal([8, 9, 10], await File.ReadAllBytesAsync(destination));
        var sharedId = fixture.SharedItemId(indexed.ItemId);
        var resolved = await fixture.ResolveSharedContentAsync(sharedId);
        Assert.NotNull(resolved);
        Assert.Equal(destination, resolved!.FilePath);
        Assert.NotEqual(original, resolved.FilePath);
        Assert.Null(resolved.OwnerProfileId);
        Assert.Equal(fixture.FileIdForPath(original), fixture.FileIdForPath(destination));
    }

    [Fact]
    public async Task FolderPinsAndTimelinePolicies_AreScopedAndInherited()
    {
        using var fixture = new Fixture();
        var source = Assert.Single(await fixture.Sources.GetSourcesAsync(fixture.Space.Id));
        var scope = new ResolvedViewScope(ViewScopeKind.Mine, fixture.ProfileId,
            new HashSet<Guid> { fixture.Space.LibraryId });

        await fixture.Folders.SetPinAsync(fixture.ProfileId, scope, source.Id, "Timeline", true);
        await fixture.Folders.SetTimelinePolicyAsync(fixture.ProfileId, true, scope,
            source.Id, string.Empty, false);
        await fixture.Folders.SetTimelinePolicyAsync(fixture.ProfileId, true, scope,
            source.Id, "Timeline", true);

        var root = await fixture.Folders.QueryAsync(fixture.ProfileId, scope,
            null, null, false, null, 0, 100);
        var nested = await fixture.Folders.QueryAsync(fixture.ProfileId, scope,
            source.Id, Path.Combine("Timeline", "2024"), false, null, 0, 100);

        Assert.Contains(root.PinnedFolders, pin => pin.SourceId == source.Id && pin.RelativePath == "Timeline");
        Assert.True(nested.EffectiveIncludeInTimeline);
        Assert.Null(nested.IncludeInTimelineOverride);
    }

    [Fact]
    public async Task FolderCounts_CountLogicalItemsRatherThanDuplicateFilePaths()
    {
        using var fixture = new Fixture();
        var first = fixture.WriteManaged(Path.Combine("Trip", "photo.jpg"), [1, 2, 3, 4]);
        var duplicate = fixture.WriteManaged(Path.Combine("Trip", "copy.jpg"), [1, 2, 3, 4]);
        await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, first);
        await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, duplicate);
        var source = Assert.Single(await fixture.Sources.GetSourcesAsync(fixture.Space.Id));
        var scope = new ResolvedViewScope(ViewScopeKind.Mine, fixture.ProfileId, new HashSet<Guid> { fixture.Space.LibraryId });
        var root = await fixture.Folders.QueryAsync(fixture.ProfileId, scope, null, null, false, null, 0, 100);
        var folders = await fixture.Folders.QueryAsync(fixture.ProfileId, scope, source.Id, null, false, null, 0, 100);
        var items = await fixture.Folders.QueryAsync(fixture.ProfileId, scope, source.Id, "Trip", false, null, 0, 100);
        Assert.Equal(1, Assert.Single(root.Sources).ItemCount);
        Assert.Equal(1, Assert.Single(folders.Folders).ItemCount);
        Assert.Single(items.Items);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ConfigurationDirectoryLoader _configuration;
        private readonly DatabaseConnection _database;
        private readonly ViewPersonalSpaceRepository _spaces;
        private readonly LocalAssetRepository _assets;

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"tuvima-shared-transfer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            _configuration = new ConfigurationDirectoryLoader(Path.Combine(Root, "config"));
            _configuration.SaveLibraries(new LibrariesConfiguration
            {
                SchemaVersion = "6.0",
                StorageLocations =
                [
                    new ServerStorageLocationConfig
                    {
                        Id = "view",
                        Label = "View",
                        Path = Path.Combine(Root, "storage"),
                        AllowWrite = true,
                    },
                ],
                ViewStorage = new ViewStorageConfig
                {
                    StorageLocationId = "view",
                    RelativeRoot = "View",
                },
            });
            _database = new DatabaseConnection(Path.Combine(Root, "view.db"));
            _database.InitializeSchema();
            _spaces = new ViewPersonalSpaceRepository(_database);
            _assets = new LocalAssetRepository(_database);
            Profiles = new ProfileRepository(_database);
            ProfileId = Guid.NewGuid();
            ProfileTestData.InsertAsync(_database, new Profile
            {
                Id = ProfileId,
                DisplayName = "Shared curator",
                Role = ProfileRole.Administrator,
            }).GetAwaiter().GetResult();
            Authority = new RequestAuthority(PrincipalKind.Human, true, Guid.NewGuid(), ProfileId,
                AccountEnabled: true, GrantEnabled: true,
                AccountIsAdministrator: true, GrantAdminEnabled: true);
            Storage = new ViewStorageService(_configuration, _spaces, new ViewSharedLibraryRepository(_database));
            Space = Storage.EnsurePersonalSpaceAsync(ProfileId).GetAwaiter().GetResult();
            Library = new ViewLibraryService(_assets, _configuration, _spaces, Storage,
                NullLogger<ViewLibraryService>.Instance);
            Transfers = new ViewSharedTransferService(_database, _assets, Storage);
            Policies = new ViewProfileRepository(_database);
            Authorization = new TestAllowAuthorizationEvaluator();
            Contributions = new ViewSharedContributionService(_database, _assets, Policies, Transfers,
                Authorization, new ViewSharedContributionQueue());
            Folders = new ViewFolderService(_database, _spaces, Profiles, _assets, Storage);
        }

        public string Root { get; }
        public Guid ProfileId { get; }
        public RequestAuthority Authority { get; }
        public ViewPersonalSpace Space { get; }
        public ViewPersonalSpaceRepository Sources => _spaces;
        public ProfileRepository Profiles { get; }

        public Task InsertProfileAsync(Profile profile) => ProfileTestData.InsertAsync(_database, profile);
        public ViewStorageService Storage { get; }
        public ViewLibraryService Library { get; }
        public ViewSharedTransferService Transfers { get; }
        public TestAllowAuthorizationEvaluator Authorization { get; }
        public ViewProfileRepository Policies { get; }
        public ViewSharedContributionService Contributions { get; }
        public ViewFolderService Folders { get; }

        public Guid SharedLibraryId()
        {
            return new ViewSharedLibraryRepository(_database).GetAsync().GetAwaiter().GetResult().LibraryId;
        }

        public Guid SharedSourceId(string sourceKey)
        {
            using var connection = _database.CreateConnection();
            return connection.QuerySingle<Guid>(
                "SELECT id FROM view_sources WHERE scope_kind='shared' AND source_key=@sourceKey;",
                new { sourceKey });
        }

        public Guid? OriginItemId(Guid sharedItemId)
        {
            using var connection = _database.CreateConnection();
            return connection.QuerySingle<Guid?>(
                "SELECT origin_item_id FROM view_shared_assets WHERE item_id=@sharedItemId;",
                new { sharedItemId });
        }

        public Guid SharedItemId(Guid originItemId)
        {
            using var connection = _database.CreateConnection();
            return connection.QuerySingle<Guid>(
                "SELECT item_id FROM view_shared_assets WHERE origin_item_id=@originItemId;",
                new { originItemId });
        }

        public Guid FileIdForPath(string path)
        {
            using var connection = _database.CreateConnection();
            return connection.QuerySingle<Guid>(
                "SELECT file_id FROM local_file_sources WHERE file_path=@path;",
                new { path = Path.GetFullPath(path) });
        }

        public Task<MediaEngine.Storage.Contracts.LocalAssetContentLocation?> ResolveSharedContentAsync(Guid itemId)
        {
            var resources = new ViewResourcePersistenceService(
                _assets, new ViewGalleryRepository(_database), _spaces, _database);
            return resources.ResolveContentAsync(itemId, MediaEngine.Storage.Contracts.LocalAssetFileRoles.Primary,
                new ResolvedViewScope(ViewScopeKind.Shared, null,
                    new HashSet<Guid> { SharedLibraryId() }));
        }

        public string WriteManaged(string name, byte[] bytes)
        {
            var source = _spaces.GetSourcesAsync(Space.Id).GetAwaiter().GetResult()
                .Single(value => value.SourceType == ViewSourceType.BrowserUpload);
            var path = Path.Combine(Storage.GetSourcePath(Space, source), name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            _configuration.Dispose();
            _database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
