using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.LocalAssets.Filing;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// What emptying a person's Trash in View would do, worked out by the real planner and the real routes over a real data store
/// and real folders. Nothing here deletes anything: personal files cannot be downloaded again, so every test also checks the
/// files are still on disk.
/// </summary>
public sealed partial class ViewTrashDeletionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-view-trash-{Guid.NewGuid():N}");
    private readonly ActorBox _actor = new();
    private readonly List<string> _links = [];
    private DatabaseConnection _database = null!;
    private LocalAssetRepository _assets = null!;
    private ViewPersonalSpaceRepository _spaces = null!;
    private ViewProfileRepository _policies = null!;
    private ViewStorageService _storage = null!;
    private ConfigurationDirectoryLoader _configuration = null!;
    private AssetPathService _assetPaths = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _assets = new LocalAssetRepository(_database);
        _spaces = new ViewPersonalSpaceRepository(_database);
        _policies = new ViewProfileRepository(_database);

        var configuration = _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        configuration.SaveLibraries(new LibrariesConfiguration
        {
            StorageLocations = [new ServerStorageLocationConfig { Id = "view", Label = "View", Path = Path.Combine(_root, "storage"), AllowWrite = true }],
            ViewStorage = new ViewStorageConfig { StorageLocationId = "view", RelativeRoot = "View" },
        });
        _storage = new ViewStorageService(configuration, _spaces, new ViewSharedLibraryRepository(_database));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton<IAuthorizationHandler, AllowHandler>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<ILocalAssetRepository>(_assets);
        builder.Services.AddSingleton<IViewCatalogueRepository, ViewCatalogueRepository>();
        builder.Services.AddSingleton<IDocumentFilingRepository, DocumentFilingRepository>();
        builder.Services.AddSingleton<IDocumentRuleRepository, DocumentRuleRepository>();
        builder.Services.AddSingleton<IDocumentBulkRepository, DocumentBulkRepository>();
        builder.Services.AddScoped<ViewDocumentBulkService>();
        builder.Services.AddSingleton<IMediaOperationRepository, MediaOperationRepository>();
        builder.Services.AddSingleton<DocumentRulesQueue>();
        builder.Services.AddSingleton<ViewDocumentRuleService>();
        builder.Services.AddSingleton<IViewProfileRepository>(_policies);
        builder.Services.AddSingleton<IViewPersonalSpaceRepository>(_spaces);
        builder.Services.AddSingleton<IViewGalleryRepository, ViewGalleryRepository>();
        builder.Services.AddSingleton<IViewSharedLibraryRepository, ViewSharedLibraryRepository>();
        builder.Services.AddSingleton<IProfileRepository, ProfileRepository>();
        builder.Services.AddSingleton<IViewScopeStore, ViewScopePersistenceService>();
        builder.Services.AddSingleton<IViewScopeResolver, ViewScopeResolver>();
        builder.Services.AddSingleton<IViewResourceStore, ViewResourcePersistenceService>();
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowEvaluator>();
        builder.Services.AddScoped<IViewResourceAuthorizationService, ViewResourceAuthorizationService>();
        builder.Services.AddScoped<IViewRequestProfileContext, HttpViewRequestProfileContext>();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new SwitchableAuthorityResolver(_actor));
        builder.Services.AddSingleton<IViewAssetQueryBackend, ViewAssetQueryService>();
        builder.Services.AddSingleton<IViewSmartGalleryQueryService, ViewSmartGalleryQueryService>();
        builder.Services.AddScoped<IViewQueryOrchestrator, ViewQueryOrchestrator>();
        _assetPaths = new AssetPathService(Path.Combine(_root, "thumbnails"));
        builder.Services.AddSingleton(new ViewThumbnailService(_assetPaths, NullLogger<ViewThumbnailService>.Instance));
        builder.Services.AddSingleton(_assetPaths);
        builder.Services.AddSingleton<IViewTrashPurgeRepository, ViewTrashPurgeRepository>();
        builder.Services.AddSingleton<ISystemActivityRepository, SystemActivityRepository>();
        builder.Services.AddSingleton<ViewTrashRecoveryService>();
        builder.Services.AddScoped<ViewTrashDeletionService>();
        builder.Services.AddScoped<ViewFolderService>();
        builder.Services.AddSingleton<IViewTrashRepository, ViewTrashRepository>();
        builder.Services.AddSingleton<ISourceMutationPolicyGate, SourceMutationPolicyGate>();
        builder.Services.AddSingleton(_storage);
        builder.Services.AddScoped<ViewTrashDeletionPlanner>();
        // The other View routes name more services than these tests use; building a request builds every route, so each has to
        // exist. These stand-ins throw if a request these tests make ever reaches them.
        foreach (var stub in StandIns)
        {
            builder.Services.AddSingleton(stub, stub.IsInterface
                ? typeof(DispatchProxy).GetMethods()
                    .Single(method => method.Name == nameof(DispatchProxy.Create) && method.IsGenericMethodDefinition)
                    .MakeGenericMethod(stub, typeof(UnexpectedUse)).Invoke(null, null)!
                : RuntimeHelpers.GetUninitializedObject(stub));
        }

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapViewEndpoints();
        await _app.StartAsync();
        var address = new Uri(_app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _client = new HttpClient { BaseAddress = address };
    }

    private static readonly Type[] StandIns =
    [
        typeof(IAccountAccessDecisionService),
        typeof(IConfigurationLoader),
        typeof(MediaEngine.Identity.Contracts.IProfileService),
        typeof(IViewOtherPeopleService),
        typeof(ViewDiscoveryService),
        typeof(ViewLibraryService),
        typeof(ViewSharedContributionService),
        typeof(ViewSharedSourceService),
        typeof(ViewSourceIndexingHostedService),
        typeof(MediaEngine.Api.Services.Settings.ServerFolderBrowserService),
    ];

    public class UnexpectedUse : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"{targetMethod?.DeclaringType?.Name}.{targetMethod?.Name} was not expected in this test.");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_root, "library.db")}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }

        // A link is removed on its own first, so deleting the temp folder can never follow it into a target.
        foreach (var link in _links)
        {
            TestLinks.Remove(link);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---------------------------------------------------------------- planner: what leaves, what stays

    [Fact]
    public async Task AManagedUploadInTrash_IsPlannedForDeletion_AndNothingIsDeleted()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "beach", size: 2048);

        var plan = await PlanAsync(alice);

        var item = Assert.Single(plan.Items);
        Assert.Equal(photo.ItemId, item.ItemId);
        Assert.Equal(ViewTrashOutcomes.Delete, item.Outcome);
        Assert.Null(item.Reason);
        var file = Assert.Single(item.Files);
        Assert.Equal(ViewTrashFileAction.Delete, file.Action);
        Assert.Equal(2048, file.Bytes);
        Assert.Equal(photo.Paths[0], file.StoredPath);
        Assert.Equal(FullPath(_storage.GetProfileRoot(alice.Space)), FullPath(file.Policy.RootPath));
        Assert.Equal(1, plan.Preview.DeleteCount);
        Assert.Equal(1, plan.Preview.PhotoCount);
        Assert.Equal(2048, plan.Preview.BytesFreed);
        Assert.Empty(plan.Preview.Skipped);
        Assert.False(plan.Preview.HasCompound);
        Assert.All(photo.Paths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task ALinkedItemAlreadyInTrash_IsSkippedAsReadOnly_AndItsFileStays()
    {
        var alice = await CreatePerson();
        var linked = await AddLinkedSource(alice);
        var item = await AddPhoto(alice, "holiday", source: linked, folder: linked.ExternalPath!);

        var plan = await PlanAsync(alice);

        var planned = Assert.Single(plan.Items);
        Assert.Equal(ViewTrashOutcomes.SkipReadOnly, planned.Outcome);
        Assert.Equal(ViewTrashDeletionPlanner.ReadOnlyReason, planned.Reason);
        Assert.Empty(planned.Files);
        Assert.Equal(0, plan.Preview.DeleteCount);
        Assert.Equal(0, plan.Preview.BytesFreed);
        var skipped = Assert.Single(plan.Preview.Skipped);
        Assert.Equal((ViewTrashOutcomes.SkipReadOnly, 1), (skipped.ReasonCode, skipped.Count));
        Assert.True(File.Exists(item.Paths[0]));
    }

    [Fact]
    public async Task AnItemWithOneFileInALinkedFolder_IsReadOnlyEvenWhenItsOtherFileIsManaged()
    {
        var alice = await CreatePerson();
        var linked = await AddLinkedSource(alice);
        var livePhoto = await AddItem(alice, LocalAssetMediaKinds.Image, "live",
            Part(alice, "live.heic", 100, LocalAssetFileRoles.Primary, alice.Upload),
            Part(alice, "live.mov", 300, LocalAssetFileRoles.LivePhotoVideo, linked, linked.ExternalPath));

        var plan = await PlanAsync(alice);

        Assert.Equal(ViewTrashOutcomes.SkipReadOnly, Assert.Single(plan.Items).Outcome);
        Assert.All(livePhoto.Paths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task APathOutsideTheOwnersProfileFolder_IsRefused()
    {
        var alice = await CreatePerson();
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        var item = await AddPhoto(alice, "stray", folder: elsewhere);

        var plan = await PlanAsync(alice);

        var planned = Assert.Single(plan.Items);
        Assert.Equal(ViewTrashOutcomes.SkipRefused, planned.Outcome);
        Assert.Contains("outside", planned.Reason, StringComparison.Ordinal);
        Assert.Equal(0, plan.Preview.DeleteCount);
        Assert.True(File.Exists(item.Paths[0]));
    }

    [Fact]
    public async Task APathInAnotherPersonsProfileFolder_IsRefused()
    {
        var alice = await CreatePerson();
        var bob = await CreatePerson();
        var bobsFolder = Directory.CreateDirectory(bob.UploadFolder).FullName;
        var item = await AddPhoto(alice, "wrong-folder", folder: bobsFolder);

        var planned = Assert.Single((await PlanAsync(alice)).Items);

        Assert.Equal(ViewTrashOutcomes.SkipRefused, planned.Outcome);
        Assert.True(File.Exists(item.Paths[0]));
    }

    [Fact]
    public async Task APathThroughADirectoryLinkThatLeavesTheProfileFolder_IsRefused()
    {
        var alice = await CreatePerson();
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside-dir")).FullName;
        Directory.CreateDirectory(alice.UploadFolder);
        var link = Path.Combine(alice.UploadFolder, "shortcut");
        Assert.True(TestLinks.TryCreateDirectoryLink(link, outside), "This machine could not create a directory link.");
        _links.Add(link);
        // The data store says the photo lives under the link, which is inside the profile folder as far as the text goes.
        var item = await AddPhoto(alice, "through-link", folder: link, realFolder: outside);

        var plan = await PlanAsync(alice);

        var planned = Assert.Single(plan.Items);
        Assert.Equal(ViewTrashOutcomes.SkipRefused, planned.Outcome);
        Assert.Contains("link", planned.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(outside, Path.GetFileName(item.Paths[0]))));
    }

    [Fact]
    public async Task APathWhoseLastStepIsALinkLeavingTheProfileFolder_IsRefused()
    {
        var alice = await CreatePerson();
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside-leaf")).FullName;
        Directory.CreateDirectory(alice.UploadFolder);
        var leaf = Path.Combine(alice.UploadFolder, "last-step");
        Assert.True(TestLinks.TryCreateDirectoryLink(leaf, outside), "This machine could not create a directory link.");
        _links.Add(leaf);
        await AddItem(alice, LocalAssetMediaKinds.Image, "last-step",
            new FilePart(leaf, "last-step", 0, LocalAssetFileRoles.Primary, alice.Upload));

        var planned = Assert.Single((await PlanAsync(alice)).Items);

        Assert.Equal(ViewTrashOutcomes.SkipRefused, planned.Outcome);
        Assert.Contains("leads outside", planned.Reason, StringComparison.Ordinal);
        Assert.True(Directory.Exists(outside));
    }

    [SymbolicLinkFact]
    public async Task AFileThatIsASymbolicLinkToSomewhereOutside_IsRefused()
    {
        var alice = await CreatePerson();
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside-file")).FullName;
        var real = Path.Combine(outside, "precious.jpg");
        await File.WriteAllBytesAsync(real, new byte[64]);
        Directory.CreateDirectory(alice.UploadFolder);
        var leaf = Path.Combine(alice.UploadFolder, "precious.jpg");
        File.CreateSymbolicLink(leaf, real);
        _links.Add(leaf);
        var item = await AddItem(alice, LocalAssetMediaKinds.Image, "precious",
            new FilePart(leaf, "precious.jpg", 64, LocalAssetFileRoles.Primary, alice.Upload));

        var planned = Assert.Single((await PlanAsync(alice)).Items);

        Assert.Equal(ViewTrashOutcomes.SkipRefused, planned.Outcome);
        Assert.Contains("link", planned.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(real));
        Assert.NotEmpty(item.Paths);
    }

    [SymbolicLinkFact]
    public async Task AFileThatIsASymbolicLinkToAnotherFileInsideTheProfileFolder_IsAlsoRefused()
    {
        var alice = await CreatePerson();
        Directory.CreateDirectory(alice.UploadFolder);
        var real = Path.Combine(alice.UploadFolder, "real.jpg");
        await File.WriteAllBytesAsync(real, new byte[64]);
        var alias = Path.Combine(alice.UploadFolder, "alias.jpg");
        File.CreateSymbolicLink(alias, real);
        _links.Add(alias);
        await AddItem(alice, LocalAssetMediaKinds.Image, "alias",
            new FilePart(alias, "alias.jpg", 64, LocalAssetFileRoles.Primary, alice.Upload));

        var planned = Assert.Single((await PlanAsync(alice)).Items);

        Assert.Equal(ViewTrashOutcomes.SkipRefused, planned.Outcome);
        Assert.True(File.Exists(real));
    }

    [Fact]
    public async Task AFileWhoseSourceIsNoLongerSetUp_IsRefused()
    {
        var alice = await CreatePerson();
        var item = await AddPhoto(alice, "orphan");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE local_file_sources SET source_id = NULL;");
        }

        var planned = Assert.Single((await PlanAsync(alice)).Items);

        Assert.Equal(ViewTrashOutcomes.SkipRefused, planned.Outcome);
        Assert.Equal(ViewTrashDeletionPlanner.SourceMissingReason, planned.Reason);
        Assert.True(File.Exists(item.Paths[0]));
    }

    [Fact]
    public async Task ALivePhoto_IsOneItemWithBothFilesGoingTogether()
    {
        var alice = await CreatePerson();
        var live = await AddItem(alice, LocalAssetMediaKinds.Image, "live",
            Part(alice, "IMG_1.heic", 1000, LocalAssetFileRoles.Primary, alice.Upload),
            Part(alice, "IMG_1.mov", 3000, LocalAssetFileRoles.LivePhotoVideo, alice.Upload));

        var plan = await PlanAsync(alice);

        var item = Assert.Single(plan.Items);
        Assert.Equal(ViewTrashOutcomes.Delete, item.Outcome);
        Assert.Equal(2, item.Files.Count);
        Assert.Equal(["live_photo_video", "primary"], item.Files.Select(file => file.Role).Order().ToArray());
        Assert.All(item.Files, file => Assert.Equal(ViewTrashFileAction.Delete, file.Action));
        Assert.Equal(1, plan.Preview.DeleteCount);
        Assert.Equal(1, plan.Preview.PhotoCount);
        Assert.Equal(4000, plan.Preview.BytesFreed);
        Assert.True(plan.Preview.HasCompound);
        Assert.All(live.Paths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task AFileAnotherLiveItemStillUses_IsKeptAndItsBytesAreNotCounted()
    {
        var alice = await CreatePerson();
        var video = Part(alice, "clip.mov", 5000, LocalAssetFileRoles.LivePhotoVideo, alice.Upload);
        var live = await AddItem(alice, LocalAssetMediaKinds.Image, "live",
            Part(alice, "clip.heic", 700, LocalAssetFileRoles.Primary, alice.Upload), video);
        // The same file on disk is also a video of its own that stays in the library.
        var own = await AddItem(alice, LocalAssetMediaKinds.Video, "clip", video with { Role = LocalAssetFileRoles.Primary }, trash: false);

        var plan = await PlanAsync(alice);

        var item = Assert.Single(plan.Items);
        Assert.Equal(live.ItemId, item.ItemId);
        Assert.Equal(ViewTrashOutcomes.Delete, item.Outcome);
        Assert.Equal(ViewTrashFileAction.KeepShared, item.Files.Single(file => file.Role == LocalAssetFileRoles.LivePhotoVideo).Action);
        Assert.Equal(ViewTrashFileAction.Delete, item.Files.Single(file => file.Role == LocalAssetFileRoles.Primary).Action);
        Assert.Equal(700, plan.Preview.BytesFreed);
        Assert.True(File.Exists(own.Paths[0]));
    }

    [Fact]
    public async Task AFileTwoTrashedItemsShare_GoesWithBothAndIsCountedOnce()
    {
        var alice = await CreatePerson();
        var video = Part(alice, "clip.mov", 5000, LocalAssetFileRoles.LivePhotoVideo, alice.Upload);
        await AddItem(alice, LocalAssetMediaKinds.Image, "live",
            Part(alice, "clip.heic", 700, LocalAssetFileRoles.Primary, alice.Upload), video);
        await AddItem(alice, LocalAssetMediaKinds.Video, "clip", video with { Role = LocalAssetFileRoles.Primary });

        var plan = await PlanAsync(alice);

        Assert.Equal(2, plan.Preview.DeleteCount);
        Assert.All(plan.Items.SelectMany(item => item.Files), file => Assert.Equal(ViewTrashFileAction.Delete, file.Action));
        Assert.Equal(5700, plan.Preview.BytesFreed);
    }

    [Fact]
    public async Task AFileSharedWithAnItemThatIsSkipped_IsKept()
    {
        var alice = await CreatePerson();
        var video = Part(alice, "clip.mov", 5000, LocalAssetFileRoles.LivePhotoVideo, alice.Upload);
        var live = await AddItem(alice, LocalAssetMediaKinds.Image, "live",
            Part(alice, "clip.heic", 700, LocalAssetFileRoles.Primary, alice.Upload), video);
        var own = await AddItem(alice, LocalAssetMediaKinds.Video, "clip", video with { Role = LocalAssetFileRoles.Primary });
        InsertContribution(alice, own.ItemId, "pending", "waiting");

        var plan = await PlanAsync(alice);

        Assert.Equal(ViewTrashOutcomes.SkipContributionPending, plan.Items.Single(item => item.ItemId == own.ItemId).Outcome);
        var going = plan.Items.Single(item => item.ItemId == live.ItemId);
        Assert.Equal(ViewTrashFileAction.KeepShared, going.Files.Single(file => file.Role == LocalAssetFileRoles.LivePhotoVideo).Action);
        Assert.Equal(700, plan.Preview.BytesFreed);
    }

    [Fact]
    public async Task TheSamePhotoInAnotherPersonsSpace_IsNotSharingAndTheirFileIsNeverListed()
    {
        var alice = await CreatePerson();
        var bob = await CreatePerson();
        var hash = NewHash();
        var mine = await AddItem(alice, LocalAssetMediaKinds.Image, "same", Part(alice, "same.jpg", 900, LocalAssetFileRoles.Primary, alice.Upload) with { Hash = hash });
        var theirs = await AddItem(bob, LocalAssetMediaKinds.Image, "same", Part(bob, "same.jpg", 900, LocalAssetFileRoles.Primary, bob.Upload) with { Hash = hash }, trash: false);

        var plan = await PlanAsync(alice);

        var item = Assert.Single(plan.Items);
        Assert.Equal(mine.ItemId, item.ItemId);
        Assert.Equal(ViewTrashOutcomes.Delete, item.Outcome);
        var file = Assert.Single(item.Files);
        Assert.Equal(ViewTrashFileAction.Delete, file.Action);
        Assert.StartsWith(FullPath(_storage.GetProfileRoot(alice.Space)), FullPath(file.ResolvedPath), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(900, plan.Preview.BytesFreed);
        Assert.True(File.Exists(theirs.Paths[0]));
    }

    [Fact]
    public async Task OnlyInFlightSharedLibraryWork_ProtectsAnItem()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "shared-soon");
        var contribution = InsertContribution(alice, photo.ItemId, "pending", "waiting");

        var waiting = Assert.Single((await PlanAsync(alice)).Items);
        Assert.Equal(ViewTrashOutcomes.SkipContributionPending, waiting.Outcome);
        Assert.Equal(ViewTrashDeletionPlanner.ContributionPendingReason, waiting.Reason);

        // Accepted and still waiting, being copied, or cleaning up the original: protected.
        foreach (var state in new[] { "waiting", "transferring", "cleanup_pending" })
        {
            SetContribution(contribution, "accepted", state);
            Assert.Equal(ViewTrashOutcomes.SkipContributionPending, Assert.Single((await PlanAsync(alice)).Items).Outcome);
        }

        // Failed, needs attention, completed, cancelled or declined: the person's own copy may go.
        foreach (var (status, state) in new[]
        {
            ("accepted", "failed"), ("accepted", "needs_attention"), ("accepted", "completed"),
            ("cancelled", "waiting"), ("declined", "waiting"),
        })
        {
            SetContribution(contribution, status, state);
            Assert.Equal(ViewTrashOutcomes.Delete, Assert.Single((await PlanAsync(alice)).Items).Outcome);
        }

        Assert.True(File.Exists(photo.Paths[0]));
    }

    [Fact]
    public async Task OnlyAnInFlightTransfer_ProtectsAnItem()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "moving");
        InsertTransfer(photo.ItemId, "planned");

        foreach (var state in new[] { "planned", "transferring", "cleanup_pending" })
        {
            SetTransfer(photo.ItemId, state);
            Assert.Equal(ViewTrashOutcomes.SkipContributionPending, Assert.Single((await PlanAsync(alice)).Items).Outcome);
        }

        foreach (var state in new[] { "failed", "completed" })
        {
            SetTransfer(photo.ItemId, state);
            Assert.Equal(ViewTrashOutcomes.Delete, Assert.Single((await PlanAsync(alice)).Items).Outcome);
        }

        Assert.True(File.Exists(photo.Paths[0]));
    }

    [Fact]
    public async Task AFileMissingFromDisk_DoesNotBlockTheItemAndFreesNothing()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "gone", size: 400);
        File.Delete(photo.Paths[0]);

        var plan = await PlanAsync(alice);

        var item = Assert.Single(plan.Items);
        Assert.Equal(ViewTrashOutcomes.Delete, item.Outcome);
        Assert.Equal(ViewTrashFileAction.AlreadyGone, Assert.Single(item.Files).Action);
        Assert.Equal(0, plan.Preview.BytesFreed);
    }

    [Fact]
    public async Task AnotherProfilesTrash_NeverAppearsInThisPersonsPlan()
    {
        var alice = await CreatePerson();
        var bob = await CreatePerson();
        var mine = await AddPhoto(alice, "mine");
        var theirs = await AddPhoto(bob, "theirs");

        var alicePlan = await PlanAsync(alice);
        var bobPlan = await PlanAsync(bob);

        Assert.Equal([mine.ItemId], alicePlan.Items.Select(item => item.ItemId).ToArray());
        Assert.Equal([theirs.ItemId], bobPlan.Items.Select(item => item.ItemId).ToArray());
        Assert.NotEqual(alicePlan.Preview.Fingerprint, bobPlan.Preview.Fingerprint);
    }

    [Fact]
    public async Task ItemsThatAreNotInTrash_AreNeverListed()
    {
        var alice = await CreatePerson();
        await AddPhoto(alice, "kept", trash: false);
        var gone = await AddPhoto(alice, "binned");

        var plan = await PlanAsync(alice);

        Assert.Equal([gone.ItemId], plan.Items.Select(item => item.ItemId).ToArray());
    }

    [Fact]
    public async Task SharedLibraryItems_NeverAppear()
    {
        var alice = await CreatePerson(household: Guid.NewGuid());
        var shared = await new ViewSharedLibraryRepository(_database).EnsureForHouseholdAsync(alice.Authority.AccountHouseholdId!.Value);
        var sharedFolder = Directory.CreateDirectory(Path.Combine(_root, "shared-files")).FullName;
        var path = Path.Combine(sharedFolder, "family.jpg");
        await File.WriteAllBytesAsync(path, new byte[32]);
        var sourceId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO view_sources
                    (id, scope_kind, personal_space_id, library_id, source_type, name, source_key, storage_mode,
                     relative_path, external_path, include_subdirectories, enabled, created_at, updated_at)
                VALUES (@sourceId, 'shared', NULL, @libraryId, 'folder', 'Shared folder', @sourceKey, 'managed',
                        'Shared/Folders/test', NULL, 1, 1, @now, @now);
                """, new { sourceId, libraryId = shared.LibraryId, sourceKey = $"shared:{sourceId:N}", now = DateTimeOffset.UtcNow });
        }

        var sharedItem = (await _assets.UpsertAsync(new LocalAssetRegistration(
            shared.LibraryId, null, null, LocalAssetMediaKinds.Image, "family", null,
            [new LocalAssetFileRegistration(path, NewHash(), "family.jpg", "image/jpeg", 32, DateTimeOffset.UtcNow) with { SourceId = sourceId }],
            ScopeKind: LocalAssetScopeKinds.Shared))).ItemId;
        await _assets.SetLifecycleStateAsync(sharedItem, LocalAssetLifecycleState.Trashed);

        var plan = await PlanAsync(alice);

        Assert.Empty(plan.Items);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task AKidsProfile_GetsItsOwnPreview()
    {
        var kid = await CreatePerson(role: "RestrictedProfile", household: Guid.NewGuid());
        var photo = await AddPhoto(kid, "drawing");
        _actor.Current = kid.Authority;

        using var response = await _client.PostAsync("/view/trash/empty-preview", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ViewTrashEmptyPreviewDto>())!;

        Assert.Equal(photo.ItemId, Assert.Single(body.Items).ItemId);
        Assert.Equal(1, body.DeleteCount);
    }

    [Fact]
    public async Task AProfileWithoutDocuments_NeverSeesDocumentsInItsTrash()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "pic");
        await AddItem(alice, LocalAssetMediaKinds.Document, "lease", Part(alice, "lease.pdf", 800, LocalAssetFileRoles.Primary, alice.Upload));
        await _policies.SavePolicyAsync(ViewProfilePolicy.Default(alice.ProfileId) with { DocumentsEnabled = false });

        var plan = await PlanAsync(alice);
        _actor.Current = alice.Authority;
        var summary = await _client.GetFromJsonAsync<ViewTrashSummaryDto>("/view/trash/summary");
        using var response = await _client.PostAsync("/view/trash/empty-preview", null);
        var preview = await response.Content.ReadFromJsonAsync<ViewTrashEmptyPreviewDto>();

        Assert.Equal([photo.ItemId], plan.Items.Select(item => item.ItemId).ToArray());
        Assert.Equal([photo.ItemId], preview!.Items.Select(item => item.ItemId).ToArray());
        Assert.Equal(0, preview.DocumentCount);
        Assert.Equal(1, summary!.ItemCount);
        Assert.Equal(0, summary.DocumentCount);
    }

    [Fact]
    public async Task AProfileWithoutPhotos_NeverSeesPhotosInItsTrash_AndKeepsItsVideos()
    {
        var alice = await CreatePerson();
        await AddPhoto(alice, "pic");
        var video = await AddItem(alice, LocalAssetMediaKinds.Video, "clip", Part(alice, "clip.mp4", 800, LocalAssetFileRoles.Primary, alice.Upload));
        await _policies.SavePolicyAsync(ViewProfilePolicy.Default(alice.ProfileId) with { PhotosEnabled = false });

        var plan = await PlanAsync(alice);
        _actor.Current = alice.Authority;
        var summary = await _client.GetFromJsonAsync<ViewTrashSummaryDto>("/view/trash/summary");

        Assert.Equal([video.ItemId], plan.Items.Select(item => item.ItemId).ToArray());
        Assert.Equal(1, summary!.ItemCount);
        Assert.Equal(0, summary.PhotoCount);
        Assert.Equal(1, summary.VideoCount);
    }

    [Fact]
    public async Task ManualGalleriesThatLoseItems_AreListedAToZWithHowManyOfTheirItemsGo()
    {
        var alice = await CreatePerson();
        var one = await AddPhoto(alice, "one");
        var two = await AddPhoto(alice, "two");
        var linked = await AddLinkedSource(alice);
        var staying = await AddPhoto(alice, "read-only", source: linked, folder: linked.ExternalPath!);
        var galleries = new ViewGalleryRepository(_database);
        var zeta = await galleries.CreateAsync(new CreateViewGalleryCommand(alice.ProfileId, alice.Space.Id, "Zeta", ViewGalleryKind.Manual));
        var alpha = await galleries.CreateAsync(new CreateViewGalleryCommand(alice.ProfileId, alice.Space.Id, "alpha", ViewGalleryKind.Manual));
        var untouched = await galleries.CreateAsync(new CreateViewGalleryCommand(alice.ProfileId, alice.Space.Id, "Untouched", ViewGalleryKind.Manual));
        await galleries.AddItemsAsync(zeta.Id, [one.ItemId, two.ItemId, staying.ItemId]);
        await galleries.AddItemsAsync(alpha.Id, [one.ItemId]);
        await galleries.AddItemsAsync(untouched.Id, [staying.ItemId]);

        var plan = await PlanAsync(alice);

        Assert.Equal([("alpha", 1), ("Zeta", 2)], plan.Preview.Galleries.Select(gallery => (gallery.Name, gallery.ItemCount)).ToArray());
    }

    [Fact]
    public async Task AnItemInTwoGalleries_CountsOnceInHowManyDeletedItemsWereInAGallery()
    {
        var alice = await CreatePerson();
        var both = await AddPhoto(alice, "both");
        var onlyOne = await AddPhoto(alice, "only-one");
        await AddPhoto(alice, "no-gallery");
        var galleries = new ViewGalleryRepository(_database);
        var first = await galleries.CreateAsync(new CreateViewGalleryCommand(alice.ProfileId, alice.Space.Id, "First", ViewGalleryKind.Manual));
        var second = await galleries.CreateAsync(new CreateViewGalleryCommand(alice.ProfileId, alice.Space.Id, "Second", ViewGalleryKind.Manual));
        await galleries.AddItemsAsync(first.Id, [both.ItemId, onlyOne.ItemId]);
        await galleries.AddItemsAsync(second.Id, [both.ItemId]);

        var plan = await PlanAsync(alice);

        Assert.Equal(2, plan.Preview.GalleryItemCount);
        Assert.Equal(3, plan.Preview.Galleries.Sum(gallery => gallery.ItemCount));
        Assert.Equal(2, plan.Preview.Galleries.Count);
    }

    [Fact]
    public async Task TheFingerprint_IsStable_AndChangesWhenAnItemIsRestoredOrNewlyTrashed()
    {
        var alice = await CreatePerson();
        var first = await AddPhoto(alice, "first");
        await AddPhoto(alice, "second");

        var before = (await PlanAsync(alice)).Preview.Fingerprint;
        Assert.Equal(before, (await PlanAsync(alice)).Preview.Fingerprint);
        Assert.Matches("^[0-9a-f]{64}$", before);

        await _assets.SetLifecycleStateAsync(first.ItemId, LocalAssetLifecycleState.Active);
        var restored = (await PlanAsync(alice)).Preview.Fingerprint;
        Assert.NotEqual(before, restored);

        var third = await AddPhoto(alice, "third");
        var added = (await PlanAsync(alice)).Preview.Fingerprint;
        Assert.NotEqual(restored, added);
        Assert.NotEqual(before, added);

        // Putting it back exactly as it was gives exactly the same summary.
        await _assets.SetLifecycleStateAsync(third.ItemId, LocalAssetLifecycleState.Active);
        await _assets.SetLifecycleStateAsync(first.ItemId, LocalAssetLifecycleState.Trashed);
        Assert.Equal(before, (await PlanAsync(alice)).Preview.Fingerprint);
    }

    [Fact]
    public async Task TheFingerprint_ChangesWhenAFileChangesOnDisk()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "edited", size: 100);
        var before = (await PlanAsync(alice)).Preview.Fingerprint;

        await File.WriteAllBytesAsync(photo.Paths[0], new byte[500]);

        Assert.NotEqual(before, (await PlanAsync(alice)).Preview.Fingerprint);
    }

    [Fact]
    public async Task AnotherPersonsLinkedCopyOfTheSameBytes_NeverMakesMyManagedUploadReadOnly()
    {
        var alice = await CreatePerson();
        var bob = await CreatePerson();
        var bobsFolder = await AddLinkedSource(bob);
        var photoHash = NewHash();
        var documentHash = NewHash();
        var theirPhoto = await AddItem(bob, LocalAssetMediaKinds.Image, "same", Part(bob, "same.jpg", 100, LocalAssetFileRoles.Primary, bobsFolder, bobsFolder.ExternalPath) with { Hash = photoHash }, trash: false);
        var theirDocument = await AddItem(bob, LocalAssetMediaKinds.Document, "same", Part(bob, "same.pdf", 100, LocalAssetFileRoles.Primary, bobsFolder, bobsFolder.ExternalPath) with { Hash = documentHash }, trash: false);
        var myPhoto = await AddItem(alice, LocalAssetMediaKinds.Image, "same", Part(alice, "same.jpg", 100, LocalAssetFileRoles.Primary, alice.Upload) with { Hash = photoHash }, trash: false);
        var myDocument = await AddItem(alice, LocalAssetMediaKinds.Document, "same", Part(alice, "same.pdf", 100, LocalAssetFileRoles.Primary, alice.Upload) with { Hash = documentHash }, trash: false);

        // Bob's own copies are read-only; Alice's are not, and nothing about Bob's folder reaches her.
        Assert.Equal(
            new HashSet<Guid> { theirPhoto.ItemId, theirDocument.ItemId },
            _assets.GetReadOnlyItemIds([myPhoto.ItemId, myDocument.ItemId, theirPhoto.ItemId, theirDocument.ItemId]));
        Assert.True(_assets.Find(myPhoto.ItemId)!.CanTrash);
        Assert.False(_assets.Find(theirPhoto.ItemId)!.CanTrash);
        var many = _assets.FindMany([theirPhoto.ItemId, Guid.NewGuid(), myPhoto.ItemId, myDocument.ItemId]);
        Assert.Equal([theirPhoto.ItemId, myPhoto.ItemId, myDocument.ItemId], many.Select(item => item.Id).ToArray());
        Assert.Equal([false, true, true], many.Select(item => item.CanTrash).ToArray());

        _actor.Current = alice.Authority;
        using var single = await _client.PostAsync($"/view/items/{myPhoto.ItemId:D}/trash", null);
        Assert.Equal(HttpStatusCode.NoContent, single.StatusCode);
        Assert.NotNull(_assets.Find(myPhoto.ItemId)!.TrashedAt);
        using var bulk = await _client.PostAsJsonAsync("/view/documents/bulk",
            new ViewDocumentBulkRequest([myDocument.ItemId], ViewDocumentBulkOperations.MoveToTrash));
        var result = (await bulk.Content.ReadFromJsonAsync<ViewDocumentBulkResultDto>())!;
        Assert.Equal((1, 0), (result.Done, result.Skipped));
        Assert.NotNull(_assets.Find(myDocument.ItemId)!.TrashedAt);
    }

    [Fact]
    public async Task OnReadOnlyStorage_NothingIsPlannedForDeletion()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "pic");
        // The settings screen refuses to save read-only View storage, so the file is edited by hand, as a person could, and the
        // loader is given a moment to notice.
        _configuration.StartWatching();
        var file = Path.Combine(_root, "config", "libraries.json");
        var text = File.ReadAllText(file);
        var edited = text.Replace("\"allow_write\": true", "\"allow_write\": false", StringComparison.Ordinal);
        Assert.NotEqual(text, edited);
        File.WriteAllText(file, edited);
        // Settings that say the store is read-only no longer load at all (they fail validation), so there is no folder to check.
        for (var wait = 0; wait < 100 && StorageSettingsLoad(); wait++)
        {
            await Task.Delay(100);
        }

        Assert.False(StorageSettingsLoad(), "The loader did not pick up the edited settings.");
        var plan = await PlanAsync(alice);

        var item = Assert.Single(plan.Items);
        Assert.Equal(ViewTrashOutcomes.SkipRefused, item.Outcome);
        Assert.Equal(ViewTrashDeletionPlanner.StorageUnavailableReason, item.Reason);
        Assert.Equal(0, plan.Preview.DeleteCount);
        Assert.True(File.Exists(photo.Paths[0]));
    }

    [Fact]
    public async Task AGateRefusal_NeverPutsItsOwnWordingOnTheWireOrInTheFingerprint()
    {
        var alice = await CreatePerson();
        await AddPhoto(alice, "pic");
        using var scope = _app.Services.CreateScope();
        var planner = new ViewTrashDeletionPlanner(
            scope.ServiceProvider.GetRequiredService<IViewTrashRepository>(), _spaces, _policies, _storage,
            new RefusingGate("secret detail: the ledger failed"),
            scope.ServiceProvider.GetRequiredService<IViewResourceAuthorizationService>(),
            NullLogger<ViewTrashDeletionPlanner>.Instance);

        var plan = (await planner.BuildPlanAsync(alice.Authority)).Plan!;

        var item = Assert.Single(plan.Items);
        Assert.Equal(ViewTrashOutcomes.SkipRefused, item.Outcome);
        Assert.Equal(ViewTrashDeletionPlanner.NotAllowedReason, item.Reason);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(plan.Preview), StringComparison.Ordinal);
        Assert.NotEqual((await PlanAsync(alice)).Preview.Fingerprint, plan.Preview.Fingerprint);
    }

    private bool StorageSettingsLoad()
    {
        try
        {
            _ = _storage.IsStorageWritable();
            return true;
        }
        catch (MediaEngine.Storage.Configuration.ConfigValidationException)
        {
            return false;
        }
    }

    private sealed class RefusingGate(string reason) : ISourceMutationPolicyGate
    {
        public MediaEngine.Ingestion.Models.SourceMutationDecision Evaluate(MediaEngine.Ingestion.Models.SourceMutationRequest request) =>
            MediaEngine.Ingestion.Models.SourceMutationDecision.Deny(reason);
    }

    // ---------------------------------------------------------------- routes

    [Fact]
    public async Task TheSummary_CountsTheOwnersTrashByKindAndSize()
    {
        var alice = await CreatePerson();
        await AddPhoto(alice, "p1", size: 100);
        await AddPhoto(alice, "p2", size: 200);
        await AddItem(alice, LocalAssetMediaKinds.Video, "v", Part(alice, "v.mp4", 1000, LocalAssetFileRoles.Primary, alice.Upload));
        await AddItem(alice, LocalAssetMediaKinds.Document, "d", Part(alice, "d.pdf", 50, LocalAssetFileRoles.Primary, alice.Upload));
        await AddItem(alice, LocalAssetMediaKinds.Audio, "a", Part(alice, "a.mp3", 10, LocalAssetFileRoles.Primary, alice.Upload));
        await AddPhoto(alice, "not-trashed", trash: false);
        _actor.Current = alice.Authority;

        var summary = await _client.GetFromJsonAsync<ViewTrashSummaryDto>("/view/trash/summary");

        Assert.Equal(new ViewTrashSummaryDto(5, 2, 1, 1, 1, 1360), summary);
    }

    [Fact]
    public async Task TheEmptyPreview_ReturnsTheSameFactsAsThePlan_WithNoDiskPaths()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "wire");
        var plan = await PlanAsync(alice);
        _actor.Current = alice.Authority;

        using var response = await _client.PostAsync("/view/trash/empty-preview", null);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = System.Text.Json.JsonSerializer.Deserialize<ViewTrashEmptyPreviewDto>(json)!;
        Assert.Equal(plan.Preview.Fingerprint, preview.Fingerprint);
        Assert.Equal(photo.ItemId, Assert.Single(preview.Items).ItemId);
        Assert.Contains("\"fingerprint\"", json, StringComparison.Ordinal);
        Assert.Contains("\"bytes_freed\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Profiles", json, StringComparison.Ordinal);
        Assert.DoesNotContain(".jpg", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APairedAppAndAServiceApplication_AreRefusedBothTrashRoutes()
    {
        var alice = await CreatePerson();
        var photo = await AddPhoto(alice, "private");
        var pairedApp = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, AccountId: alice.Authority.AccountId, ActiveProfileId: alice.ProfileId,
            ApplicationId: Guid.NewGuid(), AccountEnabled: true, GrantEnabled: true, ApplicationEnabled: true);
        var service = new RequestAuthority(
            PrincipalKind.ServiceApplication, true, ApplicationId: Guid.NewGuid(), ApplicationEnabled: true,
            ApplicationIsAdministrator: true);

        foreach (var caller in new[] { pairedApp, service })
        {
            _actor.Current = caller;
            using var summary = await _client.GetAsync("/view/trash/summary");
            using var preview = await _client.PostAsync("/view/trash/empty-preview", null);
            Assert.Equal(HttpStatusCode.Forbidden, summary.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        }

        _actor.Current = new RequestAuthority(PrincipalKind.Anonymous, false);
        using var anonymous = await _client.PostAsync("/view/trash/empty-preview", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.True(File.Exists(photo.Paths[0]));
    }

    [Fact]
    public async Task ThePlannerItself_RefusesAPairedAppAndAServiceApplication()
    {
        var alice = await CreatePerson();
        await AddPhoto(alice, "private");
        var pairedApp = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, AccountId: alice.Authority.AccountId, ActiveProfileId: alice.ProfileId,
            ApplicationId: Guid.NewGuid(), AccountEnabled: true, GrantEnabled: true, ApplicationEnabled: true);
        var service = new RequestAuthority(
            PrincipalKind.ServiceApplication, true, ApplicationId: Guid.NewGuid(), ApplicationEnabled: true);
        using var scope = _app.Services.CreateScope();
        var planner = scope.ServiceProvider.GetRequiredService<ViewTrashDeletionPlanner>();

        foreach (var caller in new[] { pairedApp, service })
        {
            Assert.NotEqual(ViewTrashPlanStatus.Ok, (await planner.BuildPlanAsync(caller)).Status);
            Assert.NotEqual(ViewTrashPlanStatus.Ok, (await planner.GetSummaryAsync(caller)).Status);
        }

        Assert.Equal(ViewTrashPlanStatus.Forbidden, (await planner.BuildPlanAsync(pairedApp)).Status);
        Assert.Equal(ViewTrashPlanStatus.Forbidden, (await planner.GetSummaryAsync(pairedApp)).Status);
    }

    [Fact]
    public async Task ViewersAndAdministratorsOfOtherPeople_SeeNothingOfSomeoneElsesTrash()
    {
        var household = Guid.NewGuid();
        var owner = await CreatePerson(household: household);
        var sharedViewer = await CreatePerson(household: household);
        var galleryViewer = await CreatePerson(household: household);
        var householdAdmin = await CreatePerson(household: household, householdAdmin: true);
        var otherHousehold = await CreatePerson(household: Guid.NewGuid(), serverAdmin: true);
        var photo = await AddPhoto(owner, "family-secret");
        var galleries = new ViewGalleryRepository(_database);
        var gallery = await galleries.CreateAsync(new CreateViewGalleryCommand(owner.ProfileId, owner.Space.Id, "Trip", ViewGalleryKind.Manual));
        await galleries.AddItemsAsync(gallery.Id, [photo.ItemId]);
        await galleries.ReplaceSharesAsync(gallery.Id, [(galleryViewer.ProfileId, ViewGallerySharePermission.View)]);

        foreach (var caller in new[] { sharedViewer, galleryViewer, householdAdmin, otherHousehold })
        {
            _actor.Current = caller.Authority;
            var summary = await _client.GetFromJsonAsync<ViewTrashSummaryDto>("/view/trash/summary");
            using var response = await _client.PostAsync("/view/trash/empty-preview", null);
            var preview = await response.Content.ReadFromJsonAsync<ViewTrashEmptyPreviewDto>();

            Assert.Equal(0, summary!.ItemCount);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(preview!.Items);
            Assert.Empty(preview.Galleries);
        }

        _actor.Current = owner.Authority;
        Assert.Equal(1, (await _client.GetFromJsonAsync<ViewTrashSummaryDto>("/view/trash/summary"))!.ItemCount);
    }

    [Fact]
    public async Task APersonWhoseViewIsSwitchedOff_GetsNothingFromEitherRoute()
    {
        var alice = await CreatePerson();
        await AddPhoto(alice, "hidden-away");
        await _policies.SavePolicyAsync(ViewProfilePolicy.Default(alice.ProfileId) with { ViewEnabled = false });
        _actor.Current = alice.Authority;

        using var summary = await _client.GetAsync("/view/trash/summary");
        using var preview = await _client.PostAsync("/view/trash/empty-preview", null);

        Assert.False(summary.IsSuccessStatusCode);
        Assert.False(preview.IsSuccessStatusCode);
    }

    // ---------------------------------------------------------------- the containment check on its own

    [Fact]
    public async Task TheContainmentCheck_AcceptsARealFileInsideTheFolder_EvenOneThatIsNotThereYet()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "profile")).FullName;
        var real = Path.Combine(folder, "a", "b.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        await File.WriteAllBytesAsync(real, new byte[8]);

        Assert.True(ViewStorageService.TryResolveContainedPath(folder, real, out var resolved, out _));
        Assert.Equal(FullPath(real), FullPath(resolved.Path));
        Assert.True(ViewStorageService.TryResolveContainedPath(folder, Path.Combine(folder, "a", "missing.jpg"), out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("-sibling")]
    public void TheContainmentCheck_RefusesTheFolderItselfAndAnythingBesideOrAboveIt(string shape)
    {
        var folder = Path.Combine(_root, "profile");
        var path = shape switch
        {
            "" => folder,
            ".." => Path.Combine(folder, "..", "other", "a.jpg"),
            _ => folder + shape + Path.DirectorySeparatorChar + "a.jpg",
        };

        Assert.False(ViewStorageService.TryResolveContainedPath(folder, path, out _, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.DoesNotContain(_root, reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<ViewTrashPlan> PlanAsync(Person person)
    {
        using var scope = _app.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ViewTrashDeletionPlanner>().BuildPlanAsync(person.Authority);
        Assert.Equal(ViewTrashPlanStatus.Ok, result.Status);
        return result.Plan!;
    }

    private static string FullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string NewHash() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    private sealed record Person(
        Guid ProfileId, ViewPersonalSpace Space, ViewSource Upload, string UploadFolder, RequestAuthority Authority);

    private sealed record FilePart(
        string Path, string FileName, long Size, string Role, ViewSource? Source, string? Hash = null);

    private sealed record Added(Guid ItemId, IReadOnlyList<string> Paths);

    private async Task<Person> CreatePerson(
        string role = "StandardUser", Guid? household = null, bool householdAdmin = false, bool serverAdmin = false)
    {
        var profileId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            if (household is { } householdId)
            {
                connection.Execute("INSERT OR IGNORE INTO households (id, name, created_at) VALUES (@householdId, 'Home', @now);",
                    new { householdId, now = DateTimeOffset.UtcNow });
            }

            connection.Execute("""
                INSERT INTO profiles (id, display_name, avatar_color, role, created_at, household_id)
                VALUES (@profileId, @name, '#7C4DFF', @role, @now, @household);
                """, new { profileId, name = $"Person {profileId:N}", role, now = DateTimeOffset.UtcNow, household });
        }

        var space = await _storage.EnsurePersonalSpaceAsync(profileId);
        var upload = (await _spaces.GetSourcesAsync(space.Id)).Single(source => source.SourceKey == "builtin:browser-uploads");
        var authority = new RequestAuthority(
            PrincipalKind.Human, true, AccountId: Guid.NewGuid(), ActiveProfileId: profileId, SessionId: Guid.NewGuid(),
            AccountEnabled: true, GrantEnabled: true,
            AccountIsAdministrator: serverAdmin, GrantAdminEnabled: serverAdmin,
            ActiveProfileIsRestricted: role == "RestrictedProfile",
            AccountHouseholdId: household, AccountIsHouseholdAdmin: householdAdmin);
        return new Person(profileId, space, upload, _storage.GetSourcePath(space, upload), authority);
    }

    private async Task<ViewSource> AddLinkedSource(Person person)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, $"linked-{Guid.NewGuid():N}")).FullName;
        var now = DateTimeOffset.UtcNow;
        return await _spaces.UpsertSourceAsync(new ViewSource(
            Guid.NewGuid(), person.Space.Id, ViewSourceType.Folder, "Photos on my NAS", $"linked:{Guid.NewGuid():N}",
            null, now, now, ViewSourceStorageMode.Linked, RelativePath: null, ExternalPath: folder,
            IncludeSubdirectories: true, Enabled: true, IncludeInTimeline: false));
    }

    private FilePart Part(Person person, string name, long size, string role, ViewSource source, string? folder = null) =>
        new(Path.Combine(folder ?? person.UploadFolder, name), name, size, role, source);

    /// <summary>A photo in Trash by default, written to disk where the data store says it is.</summary>
    private async Task<Added> AddPhoto(
        Person person, string name, long size = 256, bool trash = true, ViewSource? source = null,
        string? folder = null, string? realFolder = null)
    {
        var file = $"{name}.jpg";
        var part = new FilePart(Path.Combine(folder ?? person.UploadFolder, file), file, size, LocalAssetFileRoles.Primary,
            source ?? person.Upload);
        return await AddItem(person, LocalAssetMediaKinds.Image, name, trash, realFolder, part);
    }

    private Task<Added> AddItem(Person person, string kind, string title, params FilePart[] parts) =>
        AddItem(person, kind, title, true, null, parts);

    private Task<Added> AddItem(Person person, string kind, string title, FilePart part, bool trash) =>
        AddItem(person, kind, title, trash, null, part);

    private async Task<Added> AddItem(
        Person person, string kind, string title, bool trash, string? realFolder, params FilePart[] parts)
    {
        var registrations = new List<LocalAssetFileRegistration>();
        foreach (var part in parts)
        {
            var onDisk = realFolder is null ? part.Path : Path.Combine(realFolder, part.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(onDisk)!);
            if (!File.Exists(onDisk) && !Directory.Exists(onDisk))
            {
                await File.WriteAllBytesAsync(onDisk, new byte[part.Size]);
            }

            registrations.Add(new LocalAssetFileRegistration(
                part.Path, part.Hash ?? Hash(part), part.FileName, MimeFor(part.FileName), part.Size, DateTimeOffset.UtcNow,
                part.Role, SourceId: part.Source?.Id));
        }

        var result = await _assets.UpsertAsync(new LocalAssetRegistration(
            person.Space.LibraryId, person.Space.Id, person.ProfileId, kind, title, null, registrations));
        if (trash)
        {
            await _assets.SetLifecycleStateAsync(result.ItemId, LocalAssetLifecycleState.Trashed);
        }

        return new Added(result.ItemId, parts.Select(part => part.Path).ToList());
    }

    // The same file on disk keeps the same content hash however many items use it.
    private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);

    private string Hash(FilePart part)
    {
        if (!_hashes.TryGetValue(part.Path, out var hash))
        {
            _hashes[part.Path] = hash = NewHash();
        }

        return hash;
    }

    private static string MimeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".heic" => "image/heic",
        ".mov" => "video/quicktime",
        ".mp4" => "video/mp4",
        ".mp3" => "audio/mpeg",
        ".pdf" => "application/pdf",
        _ => "image/jpeg",
    };

    private Guid InsertContribution(Person person, Guid itemId, string status, string state)
    {
        var contributionId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO view_shared_contributions
                (id, submitted_by_profile_id, submitted_by_name, status, destination_kind, idempotency_key, submitted_at, updated_at)
            VALUES (@contributionId, @profileId, 'Person', @status, 'timeline', @key, @now, @now);
            INSERT INTO view_shared_contribution_items
                (id, contribution_id, item_id, original_profile_id, original_profile_name, position, operation,
                 source_manifest_json, execution_state, updated_at)
            VALUES (@rowId, @contributionId, @itemId, @profileId, 'Person', 0, 'copy', '[]', @state, @now);
            """, new
        {
            contributionId,
            itemId,
            status,
            state,
            profileId = person.ProfileId,
            key = Guid.NewGuid().ToString("N"),
            rowId = Guid.NewGuid(),
            now = DateTimeOffset.UtcNow,
        });
        return contributionId;
    }

    private void InsertTransfer(Guid itemId, string state)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO view_shared_transfers
                (id, item_id, operation, state, source_manifest_json, created_at, updated_at)
            VALUES (@id, @itemId, 'copy', @state, '[]', @now, @now);
            """, new { id = Guid.NewGuid(), itemId, state, now = DateTimeOffset.UtcNow });
    }

    private void SetTransfer(Guid itemId, string state)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("UPDATE view_shared_transfers SET state = @state WHERE item_id = @itemId;", new { itemId, state });
    }

    private void SetContribution(Guid contributionId, string status, string state)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("""
            UPDATE view_shared_contributions SET status = @status WHERE id = @contributionId;
            UPDATE view_shared_contribution_items SET execution_state = @state WHERE contribution_id = @contributionId;
            """, new { contributionId, status, state });
    }

    private sealed class ActorBox
    {
        public RequestAuthority Current { get; set; } = new(PrincipalKind.Anonymous, false);
    }

    private sealed class SwitchableAuthorityResolver(ActorBox actor) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(actor.Current);
    }

    private sealed class AllowEvaluator : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken ct = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class AllowHandler : AuthorizationHandler<AdministratorOrApplicationRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
            AdministratorOrApplicationRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "view-trash-test")], Scheme.Name)),
                Scheme.Name)));
    }
}

/// <summary>Runs only where the operating system lets this account create a symbolic link; otherwise it is skipped with the reason.</summary>
public sealed class SymbolicLinkFactAttribute : FactAttribute
{
    public SymbolicLinkFactAttribute()
    {
        if (!TestLinks.CanCreateFileSymbolicLink())
        {
            Skip = "This account cannot create symbolic links here (on Windows turn on Developer Mode or run elevated).";
        }
    }
}

internal static class TestLinks
{
    public static bool CanCreateFileSymbolicLink()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"tuvima-link-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, "target");
            File.WriteAllText(target, "x");
            File.CreateSymbolicLink(Path.Combine(folder, "link"), target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _ = ex;
            return false;
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Remove(Path.Combine(folder, "link"));
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>A junction on Windows (no special rights needed), a symbolic link elsewhere.</summary>
    public static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                });
                process!.WaitForExit();
                return Directory.Exists(link);
            }

            Directory.CreateSymbolicLink(link, target);
            return Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            _ = ex;
            return false;
        }
    }

    /// <summary>Removes a link without touching what it points at.</summary>
    public static void Remove(string link)
    {
        try
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
            else if (File.Exists(link) || new FileInfo(link).LinkTarget is not null)
            {
                File.Delete(link);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort in a temp folder: a link that cannot be removed here is cleaned up with the temp folder.
            _ = ex;
        }
    }
}
