using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using MediaEngine.Storage.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
            new AudiobookBookmarkRepository(_database), null!, scope, null!, null!, closeness: null, frames: FrameService());
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
        Assert.All(listed, item => Assert.Equal($"/api/v1/player/bookmarks/{item.Id:D}/frame", item.FrameUrl));
    }

    [Fact]
    public async Task BookmarkFrames_AnAdministratorSeesEveryonesFrames_AsInTheEveryonesList()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        var theirs = await BookmarkOf(AddOtherProfile(), shape.Normal.WorkId, shape.Normal.AssetId, 360);
        FrameCache().Write(shape.Normal.AssetId, theirs.Id, [0xFF, 0xD8, 0xFF, 0xD9]);
        var (player, _) = FramePlayer(shape.Account, NewVaultSession(shape.Account));

        Assert.NotNull(await player.GetBookmarkFramePathAsync(null, theirs.Id));
        var everyones = Assert.Single(await player.GetEveryonesBookmarksAsync(null, shape.Normal.WorkId));
        Assert.NotNull(everyones.FrameUrl);
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

    [Fact]
    public async Task BookmarkFrames_AnImportedSetQueuesAFrameForEachNewBookmark_AndTheReplacedOnesLoseTheirs()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        var (_, scope) = FramePlayer(shape.Account, NewVaultSession(shape.Account));
        var bookmarks = new AudiobookBookmarkRepository(_database);
        var sets = new BookmarkSetService(scope, bookmarks, new BookmarkRecognitionRepository(_database),
            new VaultLibraryRepository(_database), new BookmarkClosenessProvider(() => null), frames: FrameService());
        var replaced = await OwnBookmarkAsync(bookmarks, shape.Normal.WorkId, shape.Normal.AssetId, 761);
        FrameCache().Write(shape.Normal.AssetId, replaced.Id, [1]);
        var file = FileOf(ImdbOfTheFilm, 7200, Item(763), Item(6330));

        await sets.ApplyAsync(null, shape.Normal.WorkId, new BookmarkSetApplyRequestDto { Set = SetOf(file, shape.Normal.AssetId) });

        var after = await bookmarks.GetByWorkAsync(VaultProfile, shape.Normal.WorkId);
        var operations = new MediaOperationRepository(_database);
        Assert.Equal(2, after.Count);
        Assert.All(after, item => Assert.Single(operations.GetByEntityAsync(item.Id).Result));
        Assert.False(FrameCache().Exists(shape.Normal.AssetId, replaced.Id));
    }

    [Fact]
    public async Task BookmarkFrames_PromotingOverADuplicate_RemovesTheDuplicatesFrame_AndKeepsThePromotedOnes()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        var (player, _) = FramePlayer(shape.Account, NewVaultSession(shape.Account));
        var promoted = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 500);
        var duplicate = await BookmarkOf(AddOtherProfile(), shape.Normal.WorkId, shape.Normal.AssetId, 501);
        var cache = FrameCache();
        cache.Write(shape.Normal.AssetId, promoted.Id, [1]);
        cache.Write(shape.Normal.AssetId, duplicate.Id, [1]);

        var result = await player.PromoteAudiobookBookmarkAsync(null, promoted.Id, null);

        Assert.True(result!.IsGlobal);
        Assert.NotNull(result.FrameUrl);
        Assert.True(cache.Exists(shape.Normal.AssetId, promoted.Id));
        Assert.False(cache.Exists(shape.Normal.AssetId, duplicate.Id));
    }
}
