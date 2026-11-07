using Dapper;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The editor's header navigation model (universal editor v5, WP3, section 3a): home, the Season or Section dropdown, the item list with
/// codes, stills and states, the supporting files of each level, the cover's colours and the editor's numbers, for every kind of item.
/// </summary>
public sealed class EditorHeaderReadServiceTests : IDisposable
{
    private readonly EditorHeaderTestWorld _world = new("The Expanse");

    [Fact]
    public async Task TvEpisode_GetsHomeASeasonDropdownAndItsSeasonsEpisodesWithCodesAndStills()
    {
        var tv = SeedExpanse();
        var still = Guid.NewGuid();
        using (var connection = _world.Database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, local_image_path_m)
                VALUES (@still, @episode, 'Work', 'EpisodeStill', '/art/still.jpg');
                """,
                new { still, episode = tv.Episode3 });
        }

        var header = await _world.Header().GetHeaderAsync(tv.Episode3, null);

        Assert.NotNull(header);
        Assert.Equal("tv_episode", header.Kind);
        Assert.True(header.ShowHome);
        Assert.Equal(tv.Show, header.HomeId);
        Assert.False(header.HomeIsCurrent);
        Assert.Equal(tv.Show, header.Root.Id);
        Assert.Equal("The Expanse", header.Root.Title);
        Assert.Equal("The Expanse", header.Label);
        Assert.False(header.LabelIsKind);
        Assert.NotNull(header.SecondLevel);
        Assert.Equal("Season", header.SecondLevel.Label);
        Assert.Equal([tv.Season1, tv.Season2], header.SecondLevel.Entries.Select(entry => entry.Id));
        Assert.Equal([1, 2], header.SecondLevel.Entries.Select(entry => entry.Number!.Value));
        Assert.Equal(tv.Season2, header.SecondLevel.CurrentId);
        Assert.True(header.ShowItemDropdown);
        Assert.NotNull(header.Level);
        Assert.Equal(tv.Season2, header.Level.LevelId);
        Assert.Equal(["S02E01", "S02E02", "S02E03"], header.Level.Items.Select(item => item.Code));
        Assert.Equal([1, 2, 3], header.Level.Items.Select(item => item.Number!.Value));
        var current = Assert.Single(header.Level.Items, item => item.IsCurrent);
        Assert.Equal(tv.Episode3, current.Id);
        Assert.Equal($"/stream/artwork/{still:D}", current.StillUrl);
        Assert.Equal("episode", current.Kind);
        Assert.Equal(3, header.Level.Total);
        Assert.False(header.Level.HasMore);
        Assert.Equal("The Expanse", header.FolderButton!.FolderName);
    }

    [Fact]
    public async Task TvEpisode_ListsTheSupportingFilesOfItsSeason_AndCountsTheWholeTitle()
    {
        var tv = SeedExpanse();
        await _world.SupportingFileAsync("Season 02/guide.pdf", ExtraScope.ForSeason(2));
        await _world.SupportingFileAsync("Season 01/recap.pdf", ExtraScope.ForSeason(1));
        await _world.SupportingFileAsync("Series bible.pdf", ExtraScope.Root);

        var header = await _world.Header().GetHeaderAsync(tv.Episode3, null);

        var files = header!.Level!.SupportingFiles!;
        Assert.Equal("season:2", files.ScopeKey);
        Assert.Equal(1, files.Count);
        Assert.Equal(3, files.TotalInRoot);
        Assert.False(files.ShowAll);
        var file = Assert.Single(files.Files);
        Assert.Equal("guide", file.Title);
        Assert.Equal("PDF", file.TypeBadge);
        Assert.Equal("document", file.Kind);
        Assert.Equal(1, header.SecondLevel!.Entries.Single(entry => entry.Id == tv.Season2).SupportingFileCount);
        Assert.Equal(1, header.SecondLevel.Entries.Single(entry => entry.Id == tv.Season1).SupportingFileCount);
    }

    [Fact]
    public async Task ASeasonWithMoreSupportingFilesThanTheThreshold_OffersAllOfThemAndShowsOnlyTheThreshold()
    {
        var tv = SeedExpanse();
        for (var index = 0; index < 10; index++)
        {
            await _world.SupportingFileAsync($"Season 02/sheet-{index:00}.pdf", ExtraScope.ForSeason(2));
        }

        var files = (await _world.Header().GetHeaderAsync(tv.Episode3, null))!.Level!.SupportingFiles!;

        Assert.Equal(10, files.Count);
        Assert.Equal(8, files.Files.Count);
        Assert.True(files.ShowAll);
    }

    [Fact]
    public async Task TheShow_OpensOnItsOwnHome_WithNoSeasonChosenYet()
    {
        var tv = SeedExpanse();

        var header = await _world.Header().GetHeaderAsync(tv.Show, null);

        Assert.Equal("tv_show", header!.Kind);
        Assert.True(header.HomeIsCurrent);
        Assert.True(header.LabelIsKind);
        Assert.Equal("Show", header.Label);
        Assert.Null(header.SecondLevel!.CurrentId);
        Assert.Empty(header.Level!.Items);
    }

    [Fact]
    public async Task ALongSeason_IsPagedFiftyAtATime_AndCanBeSearched()
    {
        var show = _world.Work("TV", "parent", "Long Runner");
        var season = _world.Work("TV", "parent", "Season 1", show, 1);
        var episodes = new List<Guid>();
        for (var number = 1; number <= 120; number++)
        {
            var episode = _world.Work("TV", "child", number == 77 ? "The Needle" : $"Episode {number}", season, number);
            _world.File(episode, $"Season 01/e{number:000}.mkv");
            episodes.Add(episode);
        }

        var service = _world.Header();
        var first = (await service.GetHeaderAsync(episodes[0], null))!.Level!;
        var third = await service.GetLevelAsync(episodes[0], season, null, 100, null);
        var searched = await service.GetLevelAsync(episodes[0], season, "needle", 0, null);

        Assert.Equal(50, first.Items.Count);
        Assert.Equal(120, first.Total);
        Assert.Equal(50, first.PageSize);
        Assert.True(first.HasMore);
        Assert.Equal(20, third!.Items.Count);
        Assert.False(third.HasMore);
        Assert.Equal("S01E101", third.Items[0].Code);
        var found = Assert.Single(searched!.Items);
        Assert.Equal("The Needle", found.Title);
        Assert.Equal("S01E77", found.Code);
        Assert.Equal(1, searched.Total);
    }

    [Fact]
    public async Task AnItemNotMatchedYet_ReadsAsNotMatched()
    {
        var tv = SeedExpanse();
        _world.Canonical(tv.Show, "tvdb_id", "280619");

        var matched = await _world.Header().GetLevelAsync(tv.Episode3, tv.Season2, null, 0, null);

        Assert.All(matched!.Items, item => Assert.Equal(EditorNavItemStates.Matched, item.State));

        using var connection = _world.Database.CreateConnection();
        connection.Execute("DELETE FROM canonical_values WHERE key = 'tvdb_id';");
        var unmatched = await _world.Header().GetLevelAsync(tv.Episode3, tv.Season2, null, 0, null);

        Assert.All(unmatched!.Items, item => Assert.Equal(EditorNavItemStates.NotMatched, item.State));
    }

    [Fact]
    public async Task AMovieWithNoSupportingFiles_HasNoHomeNoSecondDropdownAndNoItemList()
    {
        var movie = _world.Work("Movies", "standalone", "Arrival");
        _world.File(movie, "Arrival (2016).mkv");

        var header = await _world.Header().GetHeaderAsync(movie, null);

        Assert.Equal("movie", header!.Kind);
        Assert.False(header.ShowHome);
        Assert.Null(header.HomeId);
        Assert.Null(header.SecondLevel);
        Assert.False(header.ShowItemDropdown);
        Assert.Equal("Movie", header.Label);
        Assert.True(header.LabelIsKind);
        Assert.Equal(0, header.Level!.SupportingFiles!.Count);
    }

    [Fact]
    public async Task AMovieWithSupportingFiles_GetsTheItemDropdown_ListingThem()
    {
        var movie = _world.Work("Movies", "standalone", "Arrival");
        _world.File(movie, "Arrival (2016).mkv");
        await _world.SupportingFileAsync("Production notes.pdf", ExtraScope.Root);

        var header = await _world.Header().GetHeaderAsync(movie, null);

        Assert.False(header!.ShowHome);
        Assert.Null(header.SecondLevel);
        Assert.True(header.ShowItemDropdown);
        var files = header.Level!.SupportingFiles!;
        Assert.Equal(1, files.Count);
        Assert.Equal("Production notes", Assert.Single(files.Files).Title);
        Assert.Equal("Arrival", Assert.Single(header.Level.Items).Title);
    }

    [Fact]
    public async Task AStandaloneBook_StillGetsAHeader_WithNoHome()
    {
        var book = _world.Work("Books", "standalone", "Dune");
        _world.File(book, "Dune.epub");

        var header = await _world.Header().GetHeaderAsync(book, null);

        Assert.Equal("book", header!.Kind);
        Assert.False(header.ShowHome);
        Assert.Null(header.SecondLevel);
        Assert.Equal("Dune", header.Root.Title);
    }

    [Fact]
    public async Task AVideosLesson_GetsASectionDropdown_NumbersAndLengthsNeverEpisodeCodes()
    {
        var course = _world.Work("Videos", "parent", "Steph Curry MasterClass");
        var section = _world.Work("Videos", "parent", "Ball Handling", course, 1);
        var warmUp = _world.Work("Videos", "child", "Warm Up", section, 1);
        var drills = _world.Work("Videos", "child", "Drills", section, 2);
        var intro = _world.Work("Videos", "child", "Introduction", course, 2);
        var warmUpAsset = _world.File(warmUp, "Ball Handling/01 Warm Up.mkv", 754);
        _world.File(drills, "Ball Handling/02 Drills.mkv", 1320);
        _world.File(intro, "00 Introduction.mkv", 95);
        var profile = Guid.NewGuid();
        using (var connection = _world.Database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO user_states (user_id, asset_id, progress_pct) VALUES (@profile, @warmUpAsset, 100);",
                new { profile, warmUpAsset });
        }

        var header = await _world.Header().GetHeaderAsync(warmUp, profile);

        Assert.Equal("video", header!.Kind);
        Assert.True(header.ShowHome);
        Assert.Equal(course, header.HomeId);
        Assert.Equal("Section", header.SecondLevel!.Label);
        Assert.Equal([section], header.SecondLevel.Entries.Select(entry => entry.Id));
        Assert.Equal(section, header.SecondLevel.CurrentId);
        Assert.Equal(["Warm Up", "Drills"], header.Level!.Items.Select(item => item.Title));
        Assert.Equal([1, 2], header.Level.Items.Select(item => item.Number!.Value));
        Assert.Equal([754d, 1320d], header.Level.Items.Select(item => item.DurationSeconds!.Value));
        Assert.All(header.Level.Items, item =>
        {
            Assert.Equal("video", item.Kind);
            Assert.DoesNotMatch(@"S\d+E\d+", item.Code ?? string.Empty);
        });
        Assert.Equal(EditorNavItemStates.Watched, header.Level.Items[0].State);
        Assert.Equal(EditorNavItemStates.Unwatched, header.Level.Items[1].State);
        Assert.Contains("Section", header.SecondLevel.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVideoStraightUnderTheCourse_ListsTheCoursesLooseVideosWithNoSectionChosen()
    {
        var course = _world.Work("Videos", "parent", "Steph Curry MasterClass");
        var section = _world.Work("Videos", "parent", "Ball Handling", course, 1);
        var warmUp = _world.Work("Videos", "child", "Warm Up", section, 1);
        var intro = _world.Work("Videos", "child", "Introduction", course, 2);
        _world.File(warmUp, "Ball Handling/01 Warm Up.mkv");
        _world.File(intro, "00 Introduction.mkv", 95);

        var header = await _world.Header().GetHeaderAsync(intro, null);

        Assert.Equal("video", header!.Kind);
        Assert.Null(header.SecondLevel!.CurrentId);
        Assert.Equal(course, header.Level!.LevelId);
        Assert.Equal(["Introduction"], header.Level.Items.Select(item => item.Title));
    }

    [Fact]
    public async Task ACourseAndItsSection_AreKindsOfTheirOwn()
    {
        var course = _world.Work("Videos", "parent", "Steph Curry MasterClass");
        var section = _world.Work("Videos", "parent", "Ball Handling", course, 1);
        var warmUp = _world.Work("Videos", "child", "Warm Up", section, 1);
        _world.File(warmUp, "Ball Handling/01 Warm Up.mkv");

        var service = _world.Header();

        Assert.Equal("course", (await service.GetHeaderAsync(course, null))!.Kind);
        Assert.Equal("course_section", (await service.GetHeaderAsync(section, null))!.Kind);
    }

    [Fact]
    public async Task AnAlbumTrack_GetsHomeAsTheAlbum_NoSecondDropdown_AndTheTracks()
    {
        var album = _world.Work("Music", "parent", "Discovery");
        var one = _world.Work("Music", "child", "One More Time", album, 1);
        var two = _world.Work("Music", "child", "Aerodynamic", album, 2);
        _world.File(one, "01 One More Time.flac", 320);
        _world.File(two, "02 Aerodynamic.flac", 212);

        var header = await _world.Header().GetHeaderAsync(two, null);

        Assert.Equal("song", header!.Kind);
        Assert.True(header.ShowHome);
        Assert.Equal(album, header.HomeId);
        Assert.Null(header.SecondLevel);
        Assert.Equal(["One More Time", "Aerodynamic"], header.Level!.Items.Select(item => item.Title));
        Assert.Equal(["01", "02"], header.Level.Items.Select(item => item.Code));
        Assert.Equal([320d, 212d], header.Level.Items.Select(item => item.DurationSeconds!.Value));
        Assert.Equal(two, Assert.Single(header.Level.Items, item => item.IsCurrent).Id);
        Assert.Equal("track", header.Level.Items[0].Kind);
        Assert.Equal("album", (await _world.Header().GetHeaderAsync(album, null))!.Kind);
    }

    [Fact]
    public async Task TheCoversStoredColours_AreCarriedOnTheHeader()
    {
        var tv = SeedExpanse();
        var artwork = Guid.NewGuid();
        using (var connection = _world.Database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO artwork_assets (id, content_hash, primary_hex, secondary_hex, accent_hex) VALUES (@artwork, 'cover-hash', '#112233', '#445566', '#778899');
                INSERT INTO entity_artwork_links (id, entity_id, entity_type, artwork_asset_id, role, is_preferred)
                VALUES (@link, @show, 'Work', @artwork, 'Primary', 1);
                """,
                new { artwork, link = Guid.NewGuid(), show = tv.Show });
        }

        var header = await _world.Header().GetHeaderAsync(tv.Episode3, null);

        Assert.Equal("#112233", header!.Colors!.PrimaryHex);
        Assert.Equal("#445566", header.Colors.SecondaryHex);
        Assert.Equal("#778899", header.Colors.AccentHex);
        Assert.Equal($"/stream/artwork/{artwork:D}", header.Root.CoverUrl);
    }

    [Fact]
    public async Task ACoverWithNoStoredColours_LeavesTheColoursOut()
    {
        var tv = SeedExpanse();

        var header = await _world.Header().GetHeaderAsync(tv.Episode3, null);

        Assert.Null(header!.Colors);
    }

    [Fact]
    public async Task TheEditorsNumbersAndPerKindRules_AreCarriedFromTheConfigFile()
    {
        var tv = SeedExpanse();
        var movie = _world.Work("Movies", "standalone", "Arrival");
        _world.File(movie, "Arrival (2016).mkv");
        var service = _world.Header();

        var episode = (await service.GetHeaderAsync(tv.Episode3, null))!.Config;
        var film = (await service.GetHeaderAsync(movie, null))!.Config;

        Assert.Equal(8, episode.InlineSupportingFiles);
        Assert.Equal(12, episode.CardsPerGroup);
        Assert.Equal(25, episode.RowsPerPage);
        Assert.Equal(50, episode.ItemListPageSize);
        Assert.Equal(["details", "artwork", "match", "history"], episode.Tabs);
        Assert.Equal(["show", "year"], episode.MatchFields);
        Assert.Equal(["episode_number", "runtime", "air_date", "match_state"], episode.FactsLine);
        Assert.Contains("file_quality", episode.QuickFacts);
        Assert.Equal(["title", "year"], film.MatchFields);
    }

    [Fact]
    public async Task AFileFactsRead_CarriesTheFileForQuickFacts_AndMarksAMissingFileAndAReadOnlyFolder()
    {
        using var readOnly = new EditorHeaderTestWorld("Arrival", LibrarySourceAccessModes.ReadOnly);
        var movie = readOnly.Work("Movies", "standalone", "Arrival");
        readOnly.File(movie, "Arrival (2016).mkv");

        var missing = (await readOnly.Header().GetHeaderAsync(movie, null))!.FileFacts!;

        Assert.Equal("Arrival (2016).mkv", missing.FileName);
        Assert.Equal(readOnly.TitleFolder, missing.Folder);
        Assert.True(missing.IsMissing);
        Assert.True(missing.ReadOnly);

        readOnly.Write("Arrival (2016).mkv");
        var present = (await readOnly.Header().GetHeaderAsync(movie, null))!.FileFacts!;

        Assert.False(present.IsMissing);
        Assert.True(present.ReadOnly);

        var writable = _world.Work("Movies", "standalone", "Heat");
        _world.File(writable, "Heat.mkv");
        Assert.False((await _world.Header().GetHeaderAsync(writable, null))!.FileFacts!.ReadOnly);
    }

    [Fact]
    public async Task AnUnknownItem_HasNoHeader()
    {
        Assert.Null(await _world.Header().GetHeaderAsync(Guid.NewGuid(), null));
        Assert.Null(await _world.Header().GetLevelAsync(Guid.NewGuid(), null, null, 0, null));
    }

    [Fact]
    public async Task ALevelThatIsNotInTheFamily_IsNotFound()
    {
        var tv = SeedExpanse();

        Assert.Null(await _world.Header().GetLevelAsync(tv.Episode3, Guid.NewGuid(), null, 0, null));
    }

    private sealed record Expanse(Guid Show, Guid Season1, Guid Season2, Guid Episode1, Guid Episode3);

    // A show with two seasons: one episode in the first, three in the second.
    private Expanse SeedExpanse()
    {
        var show = _world.Work("TV", "parent", "The Expanse");
        var season1 = _world.Work("TV", "parent", "Season 1", show, 1);
        var season2 = _world.Work("TV", "parent", "Season 2", show, 2);
        var episode1 = _world.Work("TV", "child", "Dulcinea", season1, 1);
        _world.File(episode1, "Season 01/The Expanse - S01E01.mkv");
        _world.File(_world.Work("TV", "child", "Safe", season2, 1), "Season 02/The Expanse - S02E01.mkv");
        _world.File(_world.Work("TV", "child", "Doors & Corners", season2, 2), "Season 02/The Expanse - S02E02.mkv");
        var episode3 = _world.Work("TV", "child", "Static", season2, 3);
        _world.File(episode3, "Season 02/The Expanse - S02E03.mkv", 2580);
        return new Expanse(show, season1, season2, episode1, episode3);
    }

    public void Dispose() => _world.Dispose();
}
