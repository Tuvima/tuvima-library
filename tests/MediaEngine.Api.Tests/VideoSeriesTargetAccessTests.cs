using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A series choice is checked like any other item the caller edits (universal editor v6, D24): a series in a library the caller may not write
/// to answers the preview exactly as a series that does not exist, and a save naming it is refused with nothing written. The real resource check
/// over a temp SQLite data store; no authorization rule is changed by any of this.
/// </summary>
public sealed class VideoSeriesTargetAccessTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-series-target-access-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly Guid _mine = Guid.NewGuid();
    private readonly Guid _theirs = Guid.NewGuid();
    private readonly ServiceProvider _services;

    public VideoSeriesTargetAccessTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, AccountId: Guid.NewGuid(), ActiveProfileId: MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            ApplicationId: Guid.NewGuid(), DeviceId: Guid.NewGuid(), AccountEnabled: true, ActiveProfileInUserHousehold: true, ApplicationEnabled: true,
            AccountAuthorizationVersion: 1, ProfileAuthorizationVersion: 1, ApplicationAuthorizationVersion: 1);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDatabaseConnection>(_db);
        services.AddScoped<CatalogueResourceAuthorizationService>();
        services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthority(authority));
        services.AddSingleton<IAccountAccessDecisionService>(new OnlyLibrary(_mine));
        services.AddSingleton<IAuthorizationEvaluator, AllowEverything>();
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        try { _db.Dispose(); } catch (Exception) { /* test cleanup: the temp file goes next */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }

    [Fact]
    public async Task Preview_OfASeriesInALibraryTheCallerMayNotWriteTo_IsNotFound_LikeASeriesThatDoesNotExist_AndAsksNobodyWhereFilesGo()
    {
        var mine = AddSeries(_mine);
        var theirs = AddSeries(_theirs);
        var spy = new SpyPlacement();
        using var scope = _services.CreateScope();
        var (http, resources) = Caller(scope);

        var allowed = await Preview(http, resources, spy, mine);
        var refused = await Preview(http, resources, spy, theirs);
        var missing = await Preview(http, resources, spy, Guid.NewGuid());

        Assert.IsType<Ok<EditorVideoSeriesPlacementDto>>(allowed);
        Assert.Equal(refused.GetType(), missing.GetType());
        Assert.IsNotType<Ok<EditorVideoSeriesPlacementDto>>(refused);
        Assert.Equal([mine.ToString()], spy.Asked);
    }

    [Fact]
    public async Task Preview_OfANewSeriesOrALeaveWithNoSeries_NeedsNoSeriesAccess()
    {
        var spy = new SpyPlacement();
        using var scope = _services.CreateScope();
        var (http, resources) = Caller(scope);

        var created = await Ask(http, resources, spy, null, EditSessionVideo.NewSeriesToken("Garden talks"));
        var leaving = await Ask(http, resources, spy, null, EditSessionVideo.NoSeriesToken);

        Assert.IsType<Ok<EditorVideoSeriesPlacementDto>>(created);
        Assert.IsType<Ok<EditorVideoSeriesPlacementDto>>(leaving);
    }

    [Fact]
    public async Task Save_NamingASeriesTheCallerMayNotWriteTo_IsRefusedAtTheCheck_AndNothingIsWritten()
    {
        var mine = AddSeries(_mine);
        var theirs = AddSeries(_theirs);
        var video = Guid.NewGuid();
        var handler = new RecordingSeriesHandler();
        var service = new EditSessionService([handler], NullLogger<EditSessionService>.Instance);
        using var scope = _services.CreateScope();
        var (http, resources) = Caller(scope);
        List<EditSessionChange> refusedChanges = [new() { Id = "c1", Kind = EditSessionChangeKinds.VideoSeries, EntityId = video, TargetId = theirs.ToString() }];
        List<EditSessionChange> allowedChanges = [new() { Id = "c1", Kind = EditSessionChangeKinds.VideoSeries, EntityId = video, TargetId = mine.ToString() }];

        var refusals = await VideoSeriesTargetAccess.AddRefusalsAsync(http, resources, refusedChanges, new Dictionary<Guid, string>());
        var refused = await service.RunAsync(new EditSessionRequest { Changes = refusedChanges }, refusals);
        var writtenAfterRefusal = handler.Applied.Count;
        var allowedRefusals = await VideoSeriesTargetAccess.AddRefusalsAsync(http, resources, allowedChanges, new Dictionary<Guid, string>());
        var saved = await service.RunAsync(new EditSessionRequest { Changes = allowedChanges }, allowedRefusals);

        Assert.False(refused.Valid);
        Assert.Equal(EditSessionReasons.SeriesGone, refused.Changes.Single().Reason);
        Assert.Equal(0, writtenAfterRefusal);
        Assert.True(saved.Valid);
        Assert.Equal(["c1"], handler.Applied);
    }

    [Fact]
    public async Task Save_OfAMadeUpSeriesId_LooksTheSameAsASeriesTheCallerMayNotWriteTo()
    {
        var theirs = AddSeries(_theirs);
        using var scope = _services.CreateScope();
        var (http, resources) = Caller(scope);
        var video = Guid.NewGuid();

        var refused = await VideoSeriesTargetAccess.AddRefusalsAsync(http, resources, [Change(video, theirs)], new Dictionary<Guid, string>());
        var missing = await VideoSeriesTargetAccess.AddRefusalsAsync(http, resources, [Change(video, Guid.NewGuid())], new Dictionary<Guid, string>());

        Assert.Equal(refused[video], missing[video]);
    }

    [Fact]
    public async Task Save_OfAChangeNamingAnAllowedSeriesAndANewSeriesName_IsRefused_SoNoPartOfItIsActedOn()
    {
        var mine = AddSeries(_mine);
        using var scope = _services.CreateScope();
        var (http, resources) = Caller(scope);
        var video = Guid.NewGuid();
        List<EditSessionChange> forged =
        [
            new() { Id = "c1", Kind = EditSessionChangeKinds.VideoSeries, EntityId = video, TargetId = mine.ToString(), Token = EditSessionVideo.NewSeriesToken("x") },
        ];

        var refusals = await VideoSeriesTargetAccess.AddRefusalsAsync(http, resources, forged, new Dictionary<Guid, string>());
        var preview = await Ask(http, resources, new SpyPlacement(), mine.ToString(), EditSessionVideo.NewSeriesToken("x"));

        Assert.Equal(EditSessionReasons.SeriesGone, refusals[video]);
        Assert.IsNotType<Ok<EditorVideoSeriesPlacementDto>>(preview);
    }

    private static EditSessionChange Change(Guid video, Guid target) =>
        new() { Id = "c1", Kind = EditSessionChangeKinds.VideoSeries, EntityId = video, TargetId = target.ToString() };

    private static Task<IResult> Preview(HttpContext http, CatalogueResourceAuthorizationService resources, SpyPlacement spy, Guid target) =>
        Ask(http, resources, spy, target.ToString(), null);

    private static Task<IResult> Ask(
        HttpContext http, CatalogueResourceAuthorizationService resources, SpyPlacement spy, string? targetId, string? token) =>
        MetadataEndpoints.GetVideoSeriesPlacementAsync(
            Guid.NewGuid(), new EditorVideoSeriesPlacementRequestDto { TargetId = targetId, Token = token }, http, resources, spy, CancellationToken.None);

    private static (HttpContext Http, CatalogueResourceAuthorizationService Resources) Caller(IServiceScope scope) =>
        (new DefaultHttpContext { RequestServices = scope.ServiceProvider }, scope.ServiceProvider.GetRequiredService<CatalogueResourceAuthorizationService>());

    // A series (a parent work) with one video in it, in the given library: the library is how a series is scoped to a caller.
    private Guid AddSeries(Guid library)
    {
        var series = Guid.NewGuid();
        var work = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO works (id, media_type, work_kind) VALUES (@series, 'Videos', 'parent');
            INSERT INTO works (id, parent_work_id, media_type, work_kind, ordinal) VALUES (@work, @series, 'Videos', 'child', 1);
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id) VALUES (@asset, @edition, @hash, @path, @library);
            """,
            new
            {
                series = GuidSql.ToBlob(series),
                work = GuidSql.ToBlob(work),
                edition = GuidSql.ToBlob(Guid.NewGuid()),
                asset = GuidSql.ToBlob(asset),
                hash = $"hash-{asset:N}",
                path = $"C:/library/videos/{asset:N}.mp4",
                library = library.ToString("D"),
            });
        return series;
    }

    private sealed class SpyPlacement : IVideoSeriesPlacement
    {
        public List<string?> Asked { get; } = [];

        public Task<VideoSeriesFilePlan?> PlanAsync(Guid entityId, string? targetId, string? token, CancellationToken ct = default)
        {
            Asked.Add(targetId);
            return Task.FromResult<VideoSeriesFilePlan?>(VideoSeriesFilePlan.Stays(entityId));
        }
    }

    private sealed class RecordingSeriesHandler : IEditSessionChangeHandler
    {
        public List<string> Applied { get; } = [];

        public string Kind => EditSessionChangeKinds.VideoSeries;

        public Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
        {
            Applied.Add(change.Id);
            return Task.FromResult<Func<CancellationToken, Task>?>(null);
        }
    }

    private sealed class FixedAuthority(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) => ValueTask.FromResult(authority);
    }

    private sealed class OnlyLibrary(Guid libraryId) : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid requestedLibraryId, CancellationToken cancellationToken = default) => ValueTask.FromResult(requestedLibraryId == libraryId ? AuthorizationDecision.Allow() : AuthorizationDecision.Deny(AuthorizationDenialReason.LibraryNotVisible));
        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class AllowEverything : IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority, AuthorizationRequirement requirement, ResourceAuthorizationContext? resource, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }
}
