using Dapper;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Realtime;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Deleting a whole show, season, album or Series over a real data store and real temp folders: the Engine finds the owned titles
/// itself, plans them as one action with one fingerprint, and applies the same safety rules as a single title.
/// </summary>
public sealed class LibraryGroupDeletionTests : IDisposable
{
    private readonly LibraryItemDeletionTestWorld _world = new();

    public void Dispose() => _world.Dispose();

    private (Guid Show, Guid Season1, Guid Season2, List<Guid> S1, List<Guid> S2) AddShow(int seasonOneEpisodes = 3, int seasonTwoEpisodes = 3)
    {
        var show = _world.AddGroup("The Expanse", "TV");
        var season1 = _world.AddGroup("The Expanse S1", "TV", show, 1);
        var season2 = _world.AddGroup("The Expanse S2", "TV", show, 2);
        return (show, season1, season2, AddEpisodes(season1, 1, seasonOneEpisodes), AddEpisodes(season2, 2, seasonTwoEpisodes));
    }

    private List<Guid> AddEpisodes(Guid season, int seasonNumber, int count)
    {
        var ids = new List<Guid>();
        for (var episode = 1; episode <= count; episode++)
        {
            var file = _world.WriteFile(_world.Managed, Path.Combine("The Expanse", $"Season {seasonNumber}", $"S{seasonNumber}E{episode}.mkv"));
            ids.Add(_world.AddWork($"Episode {seasonNumber}-{episode}", [file], "TV", "child", parentWorkId: season, ordinal: episode).WorkId);
        }

        return ids;
    }

    private static LibraryItemGroupRequest Group(Guid id) => new(id, null, null, []);

    private int Count(string sql) => _world.Database.CreateConnection().ExecuteScalar<int>(sql);

    [Fact]
    public async Task PreviewingASeason_ListsOnlyItsOwnedEpisodes_UnderOneFingerprint()
    {
        var show = AddShow();
        _world.AddWork("Missing episode", [], "TV", "child", catalogOnly: true, parentWorkId: show.Season2, ordinal: 9);

        var preview = (await _world.Service.PreviewGroupAsync(Group(show.Season2))).Value!;

        Assert.Equal(3, preview.Items.Count);
        Assert.Equal("season", preview.Group!.Kind);
        Assert.Equal("Season 2", preview.Group.Title);
        Assert.Equal(3, preview.Group.TitlesOwned);
        Assert.Equal(1, preview.Group.TitlesNotOwned);
        Assert.False(preview.Group.RemovesParent);
        Assert.Equal(3, preview.Files.Count);
        Assert.False(string.IsNullOrEmpty(preview.Fingerprint));
    }

    [Fact]
    public async Task DeletingASeason_RemovesItsEpisodesAndFiles_AndLeavesTheShowAndOtherSeasons()
    {
        var show = AddShow();
        var preview = (await _world.Service.PreviewGroupAsync(Group(show.Season2))).Value!;

        var result = await _world.Service.DeleteGroupAsync(Group(show.Season2), preview.Fingerprint, LibraryItemDeletionTestWorld.Actor);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value!.FilesDeleted);
        Assert.Equal(3, result.Value.DeletedWorkIds.Count);
        Assert.Equal(0, Count($"SELECT COUNT(*) FROM works WHERE parent_work_id = X'{Hex(show.Season2)}';"));
        Assert.Equal(3, Count($"SELECT COUNT(*) FROM works WHERE parent_work_id = X'{Hex(show.Season1)}';"));
        Assert.Equal(1, Count($"SELECT COUNT(*) FROM works WHERE id = X'{Hex(show.Show)}';"));
    }

    [Fact]
    public async Task DeletingTheLastSeason_AlsoRemovesTheShow_AndSaysSoBeforehand()
    {
        var show = AddShow();
        var first = (await _world.Service.PreviewGroupAsync(Group(show.Season1))).Value!;
        Assert.False(first.Group!.RemovesParent);
        await _world.Service.DeleteGroupAsync(Group(show.Season1), first.Fingerprint, LibraryItemDeletionTestWorld.Actor);

        var last = (await _world.Service.PreviewGroupAsync(Group(show.Season2))).Value!;
        Assert.True(last.Group!.RemovesParent);
        Assert.Equal("The Expanse", last.Group.ParentTitle);
        await _world.Service.DeleteGroupAsync(Group(show.Season2), last.Fingerprint, LibraryItemDeletionTestWorld.Actor);

        Assert.Equal(0, Count($"SELECT COUNT(*) FROM works WHERE id = X'{Hex(show.Show)}';"));
        var announced = Assert.IsType<LibraryItemsRemovedEvent>(_world.Events.Published.Last().Payload);
        Assert.Contains(show.Show, announced.GroupIds!);
        Assert.Contains(show.Season2, announced.GroupIds!);
    }

    [Fact]
    public async Task DeletingAGroupWritesOneActivityEntryNamingTheGroup()
    {
        var show = AddShow();
        var preview = (await _world.Service.PreviewGroupAsync(Group(show.Show))).Value!;

        await _world.Service.DeleteGroupAsync(Group(show.Show), preview.Fingerprint, LibraryItemDeletionTestWorld.Actor);

        using var connection = _world.Database.CreateConnection();
        var details = connection.Query<string>("SELECT detail FROM system_activity WHERE action_type = 'MediaRemoved';").ToList();
        var detail = Assert.Single(details);
        Assert.Contains("the show \"The Expanse\"", detail);
        Assert.Contains("6 episodes", detail);
    }

    [Fact]
    public async Task APlanThatChangedSincePreview_DeletesNothing_AndReturnsAFreshPreview()
    {
        var show = AddShow();
        var preview = (await _world.Service.PreviewGroupAsync(Group(show.Season1))).Value!;
        AddEpisodes(show.Season1, 1, 1).Clear();
        var extra = _world.WriteFile(_world.Managed, Path.Combine("The Expanse", "Season 1", "Bonus.mkv"));
        _world.AddWork("Bonus", [extra], "TV", "child", parentWorkId: show.Season1, ordinal: 99);

        var result = await _world.Service.DeleteGroupAsync(Group(show.Season1), preview.Fingerprint, LibraryItemDeletionTestWorld.Actor);

        Assert.Equal(LibraryItemDeletionRefusalKind.Changed, result.Refusal!.Kind);
        Assert.True(File.Exists(extra));
        Assert.Equal(5, result.Refusal.FreshPreview!.Items.Count);
    }

    [Fact]
    public async Task AGroupLargerThanASingleDeleteAllows_IsDeleted()
    {
        var season = _world.AddGroup("Long show", "TV");
        AddEpisodes(season, 1, LibraryItemDeletionService.MaxWorks + 20);

        var preview = (await _world.Service.PreviewGroupAsync(Group(season))).Value!;
        var result = await _world.Service.DeleteGroupAsync(Group(season), preview.Fingerprint, LibraryItemDeletionTestWorld.Actor);

        Assert.True(result.IsSuccess);
        Assert.Equal(LibraryItemDeletionService.MaxWorks + 20, result.Value!.FilesDeleted);
    }

    [Fact]
    public async Task AWholeShow_AsksForTheTypedName_ButNotWithTheRecycleBinOn()
    {
        var show = AddShow();
        var destroying = (await _world.Service.PreviewGroupAsync(Group(show.Show))).Value!;
        Assert.True(destroying.Group!.RequiresTypedConfirmation);
        Assert.Equal("The Expanse", destroying.Group.ConfirmName);

        // One small season stays under the limits and needs no typing.
        var smallSeason = (await _world.Service.PreviewGroupAsync(Group(show.Season1))).Value!;
        Assert.False(smallSeason.Group!.RequiresTypedConfirmation);
        Assert.Equal(string.Empty, smallSeason.Group.ConfirmName);

        _world.TurnOnMoviesRecycleBin();
        var inBin = (await _world.Service.PreviewGroupAsync(Group(show.Show))).Value!;
        Assert.True(inBin.GoesToRecycleBin);
        Assert.False(inBin.Group!.RequiresTypedConfirmation);
    }

    [Fact]
    public async Task FilesInAnAsIsFolder_StayOnDisk_AndNoTypingIsAsked()
    {
        var show = _world.AddGroup("Old show", "TV");
        var file = _world.WriteFile(_world.AsIs, "Old.S01E01.mkv");
        _world.AddWork("Pilot", [file], "TV", "child", parentWorkId: show, ordinal: 1);

        var preview = (await _world.Service.PreviewGroupAsync(Group(show))).Value!;
        Assert.False(preview.Group!.RequiresTypedConfirmation);
        var result = await _world.Service.DeleteGroupAsync(Group(show), preview.Fingerprint, LibraryItemDeletionTestWorld.Actor);

        Assert.True(File.Exists(file));
        Assert.Equal(0, result.Value!.FilesDeleted);
        Assert.Equal(1, result.Value.FilesLeftOnDisk);
    }

    [Fact]
    public async Task DeletingAnAlbum_RemovesItsTracks()
    {
        var album = _world.AddGroup("OK Computer", "Music");
        var files = Enumerable.Range(1, 3).Select(n => _world.WriteFile(_world.Managed, Path.Combine("Radiohead", $"{n}.mp3"))).ToList();
        for (var n = 0; n < files.Count; n++)
        {
            _world.AddWork($"Track {n + 1}", [files[n]], "Music", "child", parentWorkId: album, ordinal: n + 1);
        }

        var preview = (await _world.Service.PreviewGroupAsync(Group(album))).Value!;
        Assert.Equal("album", preview.Group!.Kind);
        var result = await _world.Service.DeleteGroupAsync(Group(album), preview.Fingerprint, LibraryItemDeletionTestWorld.Actor);

        Assert.Equal(3, result.Value!.FilesDeleted);
        Assert.Equal(0, Count($"SELECT COUNT(*) FROM works WHERE id = X'{Hex(album)}';"));
    }

    [Fact]
    public async Task DeletingASeries_RemovesTheBooksInIt_ButNotBooksOutsideIt()
    {
        var series = _world.AddSeries("Dune Novels");
        var inside = Enumerable.Range(1, 2).Select(n =>
            _world.AddWork($"Dune {n}", [_world.WriteFile(_world.Managed, $"Dune{n}.epub")], "Books", collectionId: series).WorkId).ToList();
        var outside = _world.AddWork("Other", [_world.WriteFile(_world.Managed, "Other.epub")], "Books").WorkId;

        var preview = (await _world.Service.PreviewGroupAsync(Group(series))).Value!;
        Assert.Equal("series", preview.Group!.Kind);
        Assert.True(preview.Group.RequiresTypedConfirmation);
        await _world.Service.DeleteGroupAsync(Group(series), preview.Fingerprint, LibraryItemDeletionTestWorld.Actor);

        Assert.All(inside, id => Assert.Equal(0, Count($"SELECT COUNT(*) FROM works WHERE id = X'{Hex(id)}';")));
        Assert.Equal(1, Count($"SELECT COUNT(*) FROM works WHERE id = X'{Hex(outside)}';"));
    }

    [Fact]
    public async Task AGroupTheEngineCannotFind_FallsBackToThePagesTitles_CheckedOneByOne()
    {
        var album = Guid.NewGuid();
        var file = _world.WriteFile(_world.Managed, "Song.mp3");
        var track = _world.AddWork("Song", [file], "Music").WorkId;

        var request = new LibraryItemGroupRequest(album, "album", "Greatest Hits", [track]);
        var preview = (await _world.Service.PreviewGroupAsync(request)).Value!;

        Assert.Equal("Greatest Hits", preview.Group!.Title);
        Assert.Single(preview.Items);
    }

    [Fact]
    public async Task AnUnknownGroup_IsNotFound()
    {
        var result = await _world.Service.PreviewGroupAsync(Group(Guid.NewGuid()));

        Assert.Equal(LibraryItemDeletionRefusalKind.NotFound, result.Refusal!.Kind);
    }

    [Fact]
    public async Task ASingleTitleId_IsNotAGroup()
    {
        var file = _world.WriteFile(_world.Managed, "Dune.mkv");
        var (workId, _) = _world.AddWork("Dune", [file]);

        var result = await _world.Service.PreviewGroupAsync(Group(workId));

        Assert.False(result.IsSuccess);
    }

    private static string Hex(Guid id) => Convert.ToHexString(MediaEngine.Storage.GuidSql.ToBlob(id));
}
