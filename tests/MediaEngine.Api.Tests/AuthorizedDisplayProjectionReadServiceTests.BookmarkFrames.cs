using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using MediaEngine.Storage.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

// The still frame of a video bookmark (Unified player v2 D21a) is served by exactly the rule of the bookmark list: a profile that may
// not see the bookmark, or may not play its file (Vault, Kid limit), is told "not found" and learns nothing. A frame goes with its
// bookmark, and saved, imported and promoted bookmarks queue theirs the same way.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private readonly string _bookmarkFrameFolder = Path.Combine(Path.GetTempPath(), $"tuvima_frames_{Guid.NewGuid():N}");

    private void DisposeBookmarkFrameFolder()
    {
        try { Directory.Delete(_bookmarkFrameFolder, recursive: true); }
        catch (DirectoryNotFoundException) { /* No test in this run made a frame. */ }
        catch (IOException) { /* The OS temp sweep removes what is still open. */ }
    }

    private BookmarkFrameCache FrameCache() => new(new AssetPathService(Path.Combine(_bookmarkFrameFolder, "library"), dataRoot: Path.Combine(_bookmarkFrameFolder, "data")));

    private BookmarkFrameService FrameService() => new(
        new MediaOperationRepository(_database), new MediaAssetRepository(_database), new MediaTypeExtensionCatalog(),
        new AudiobookBookmarkRepository(_database), new VaultLibraryRepository(_database), FrameCache(), NullLogger<BookmarkFrameService>.Instance);

    private (PlayerService Player, PlayerCatalogueScope Scope) FramePlayer(Guid account, Guid? session)
    {
        var context = VaultContext(account, session);
        var services = new ServiceCollection()
            .AddSingleton<IRequestAuthorityResolver>(new RequestAuthorityResolver(_accounts, _applications))
            .AddSingleton(CreateService(context, RealReaders()))
            .BuildServiceProvider();
        context.RequestServices = services;
        var scope = new PlayerCatalogueScope(new HttpContextAccessor { HttpContext = context });
        var player = new PlayerService(null!, null!, null!, null!, null!, null!, null!,
            new AudiobookBookmarkRepository(_database), null!, scope, null!, null!, frames: FrameService());
        return (player, scope);
    }

    /// <summary>The seeded files are books; a video bookmark needs a video file.</summary>
    private void MakeVideo(Guid assetId)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("UPDATE media_assets SET file_path_root = @path WHERE id = @assetId;", new { path = $"C:/library/{assetId:N}.mkv", assetId });
    }

    private Guid AddOtherProfile()
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@id,'Someone else','#7C4DFF','StandardUser',@at);",
            new { id, at = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    private Task<AudiobookBookmarkDto> BookmarkOf(Guid profile, Guid workId, Guid assetId, double at, bool global = false) =>
        new AudiobookBookmarkRepository(_database).CreateAsync(
            profile, workId, new CreateAudiobookBookmarkRequestDto { AssetId = assetId, PositionSeconds = at, DurationSeconds = 5400, IsGlobal = global });

    [Fact]
    public async Task BookmarkFrames_AProfileSeesItsOwnAndGlobalFrames_ButNotAnotherProfilesPersonalOne()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        var other = AddOtherProfile();
        var own = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 120);
        var shared = await BookmarkOf(other, shape.Normal.WorkId, shape.Normal.AssetId, 240, global: true);
        var theirs = await BookmarkOf(other, shape.Normal.WorkId, shape.Normal.AssetId, 360);
        var cache = FrameCache();
        foreach (var bookmark in new[] { own, shared, theirs })
        {
            cache.Write(shape.Normal.AssetId, bookmark.Id, [0xFF, 0xD8, 0xFF, 0xD9]);
        }

        var (player, _) = FramePlayer(shape.Account, NewVaultSession(shape.Account));

        Assert.Equal(cache.PathFor(shape.Normal.AssetId, own.Id), (await player.GetBookmarkFramePathAsync(null, own.Id))!.Path);
        Assert.Equal(cache.PathFor(shape.Normal.AssetId, shared.Id), (await player.GetBookmarkFramePathAsync(null, shared.Id))!.Path);
        Assert.False((await player.GetBookmarkFramePathAsync(null, own.Id))!.InVault);
        Assert.Null(await player.GetBookmarkFramePathAsync(null, theirs.Id));
        Assert.Null(await player.GetBookmarkFramePathAsync(null, Guid.NewGuid()));
        // The list and the frame agree: the same two bookmarks are listed, each with its frame address.
        var listed = await player.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId);
        Assert.Equal(new[] { own.Id, shared.Id }.Order(), listed.Select(item => item.Id).Order());
        Assert.All(listed, item => Assert.StartsWith($"/api/v1/player/bookmarks/{item.Id:D}/frame?v=", item.FrameUrl, StringComparison.Ordinal));
    }

    [Fact]
    public async Task BookmarkFrames_AnAdministratorCanFetchAnyProfilesFrame()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        var theirs = await BookmarkOf(AddOtherProfile(), shape.Normal.WorkId, shape.Normal.AssetId, 360);
        FrameCache().Write(shape.Normal.AssetId, theirs.Id, [0xFF, 0xD8, 0xFF, 0xD9]);
        var (player, _) = FramePlayer(shape.Account, NewVaultSession(shape.Account));

        Assert.NotNull(await player.GetBookmarkFramePathAsync(null, theirs.Id));
    }

    [Fact]
    public async Task BookmarkFrames_AVaultVideosFrame_IsNotFoundWhileLocked_AndServedOnceUnlocked()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Secret.AssetId);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var (unlocked, _) = FramePlayer(shape.Account, session);
        var saved = await BookmarkOf(VaultProfile, shape.Secret.WorkId, shape.Secret.AssetId, 90);
        FrameCache().Write(shape.Secret.AssetId, saved.Id, [0xFF, 0xD8, 0xFF, 0xD9]);

        // Served, and marked so the route sends it uncacheable like everything else about a Vault file.
        Assert.True((await unlocked.GetBookmarkFramePathAsync(null, saved.Id))!.InVault);

        var (locked, _) = FramePlayer(shape.Account, NewVaultSession(shape.Account));
        Assert.Null(await locked.GetBookmarkFramePathAsync(null, saved.Id));
        Assert.Empty(await locked.GetAudiobookBookmarksAsync(null, shape.Secret.WorkId));
    }

    [Theory]
    [InlineData("R", false)]
    [InlineData("PG", true)]
    public async Task BookmarkFrames_AKidProfilesLimit_DecidesWhetherTheFrameIsServed(string rating, bool served)
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'content_rating', @rating, CURRENT_TIMESTAMP);",
                new { workId = shape.Normal.WorkId, rating });
        }

        var saved = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 60);
        FrameCache().Write(shape.Normal.AssetId, saved.Id, [0xFF, 0xD8, 0xFF, 0xD9]);
        KidsLimitTestSupport.SetLimit(_database, "PG");
        var (player, _) = FramePlayer(shape.Account, NewVaultSession(shape.Account));

        Assert.Equal(served, await player.GetBookmarkFramePathAsync(null, saved.Id) is not null);
    }

    [Fact]
    public async Task BookmarkFrames_SavingOnAVideoQueuesOneJobInstantly_AndAnAudiobookQueuesNone()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        var (player, _) = FramePlayer(shape.Account, NewVaultSession(shape.Account));
        var operations = new MediaOperationRepository(_database);

        var video = await player.CreateAudiobookBookmarkAsync(null, shape.Normal.WorkId, BookmarkAt(shape.Normal.AssetId, 750));
        var audioShape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var audio = await new AudiobookBookmarkRepository(_database).CreateAsync(
            VaultProfile, audioShape.Normal.WorkId, BookmarkAt(audioShape.Normal.AssetId, 30));

        var job = Assert.Single(await operations.GetByEntityAsync(video.Id));
        Assert.Equal(MediaEngine.Domain.Entities.MediaOperationType.PlaybackBookmarkFrame, job.OperationType);
        Assert.Null(video.FrameUrl);
        Assert.Empty(await operations.GetByEntityAsync(audio.Id));
        Assert.Null((await player.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId)).Single().FrameUrl);
    }

    [Fact]
    public async Task BookmarkFrames_DeletingTheBookmarkDeletesItsFrame()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        var (player, _) = FramePlayer(shape.Account, NewVaultSession(shape.Account));
        var kept = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 100);
        var removed = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 200);
        var cache = FrameCache();
        cache.Write(shape.Normal.AssetId, kept.Id, [1]);
        cache.Write(shape.Normal.AssetId, removed.Id, [1]);

        Assert.True(await player.DeleteAudiobookBookmarkAsync(null, removed.Id));

        Assert.False(cache.Exists(shape.Normal.AssetId, removed.Id));
        Assert.True(cache.Exists(shape.Normal.AssetId, kept.Id));
        Assert.Null(await player.GetBookmarkFramePathAsync(null, removed.Id));
    }

    // ----- The frame route, run for real: route table, filters, a real data store and cache -----

    private sealed record FrameResponse(int Status, string? ContentType, string? CacheControl, string? NoSniff, byte[] Body);

    private RouteEndpoint MapFrameRoute()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Services.AddAuthorization();
        // Only so mapping can tell which parameters come from services; each request brings its own.
        builder.Services.AddSingleton<PlayerService>(_ => throw new InvalidOperationException("Not used by mapping."));
        var app = builder.Build();
        app.MapBookmarkFrameEndpoints();
        _routeApps.Add(app);
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().Single();
    }

    private async Task<FrameResponse> GetFrameAsync(RouteEndpoint endpoint, Guid account, Guid session, Guid bookmarkId)
    {
        var context = VaultContext(account, session);
        PlayerService? player = null;
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IRequestAuthorityResolver>(new RequestAuthorityResolver(_accounts, _applications))
            .AddSingleton(CreateService(context, RealReaders()))
            .AddSingleton(_ => player!)
            .BuildServiceProvider();
        var scope = new PlayerCatalogueScope(new HttpContextAccessor { HttpContext = context });
        player = new PlayerService(null!, null!, null!, null!, null!, null!, null!,
            new AudiobookBookmarkRepository(_database), null!, scope, null!, null!, frames: FrameService());
        context.Request.Method = HttpMethods.Get;
        context.Request.RouteValues["bookmarkId"] = bookmarkId.ToString("D");
        // The address the list hands out carries a version; the route must not care about it.
        context.Request.QueryString = new QueryString("?v=638000000000000000");
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);

        return new FrameResponse(
            context.Response.StatusCode,
            context.Response.ContentType,
            context.Response.Headers.TryGetValue("Cache-Control", out var cache) ? cache.ToString() : null,
            context.Response.Headers.TryGetValue("X-Content-Type-Options", out var sniff) ? sniff.ToString() : null,
            ((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task BookmarkFrameRoute_ServesTheJpegToTheProfileThatMaySeeIt_WithTheScopeAndCacheHeaders()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        var saved = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 75);
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0xFF, 0xD9];
        FrameCache().Write(shape.Normal.AssetId, saved.Id, jpeg);
        var endpoint = MapFrameRoute();

        var response = await GetFrameAsync(endpoint, shape.Account, NewVaultSession(shape.Account), saved.Id);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("image/jpeg", response.ContentType);
        Assert.Equal("nosniff", response.NoSniff);
        Assert.Equal("private, max-age=3600", response.CacheControl);
        Assert.Equal(jpeg, response.Body);
        Assert.Equal("/api/v1/player/bookmarks/{bookmarkId:guid}/frame", endpoint.RoutePattern.RawText);
        Assert.Contains(endpoint.Metadata.OfType<ClientScopeRequirementMetadata>(), metadata => metadata.Scope == MediaEngine.Contracts.Authentication.ClientApiScopes.ProgressRead);
    }

    [Fact]
    public async Task BookmarkFrameRoute_AnswersNotFound_ForADeniedAnUnknownAndANotYetMadeFrame_AllAlike()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        var theirs = await BookmarkOf(AddOtherProfile(), shape.Normal.WorkId, shape.Normal.AssetId, 360);
        var notMadeYet = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 90);
        FrameCache().Write(shape.Normal.AssetId, theirs.Id, [0xFF, 0xD8, 0xFF, 0xD9]);
        var endpoint = MapFrameRoute();
        var session = NewVaultSession(shape.Account);

        var denied = await GetFrameAsync(endpoint, shape.Account, session, theirs.Id);
        var unknown = await GetFrameAsync(endpoint, shape.Account, session, Guid.NewGuid());
        var pending = await GetFrameAsync(endpoint, shape.Account, session, notMadeYet.Id);

        foreach (var response in new[] { denied, unknown, pending })
        {
            Assert.Equal(StatusCodes.Status404NotFound, response.Status);
            Assert.NotEqual("image/jpeg", response.ContentType);
        }

        // Nothing tells the denied one from the others: same status, same body.
        Assert.Equal(unknown.Body, denied.Body);
        Assert.Equal(unknown.Body, pending.Body);
    }

    [Fact]
    public async Task BookmarkFrameRoute_AVaultFrame_IsNotFoundWhileLocked_AndNeverCachedOnceUnlocked()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Secret.AssetId);
        var saved = await BookmarkOf(VaultProfile, shape.Secret.WorkId, shape.Secret.AssetId, 90);
        FrameCache().Write(shape.Secret.AssetId, saved.Id, [0xFF, 0xD8, 0xFF, 0xD9]);
        var endpoint = MapFrameRoute();

        var locked = await GetFrameAsync(endpoint, shape.Account, NewVaultSession(shape.Account), saved.Id);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var unlocked = await GetFrameAsync(endpoint, shape.Account, session, saved.Id);

        Assert.Equal(StatusCodes.Status404NotFound, locked.Status);
        Assert.Equal(StatusCodes.Status200OK, unlocked.Status);
        Assert.Equal("image/jpeg", unlocked.ContentType);
        Assert.Equal("private, no-store", unlocked.CacheControl);
        Assert.Equal("nosniff", unlocked.NoSniff);
    }
}
