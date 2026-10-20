using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Contracts.Library;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Match step and moves (Details overlay D12 to D17) over a real temp data store: what each kind of destination lists, the best
/// guess, the gate that keeps an item out of a list that lacks it, Add anyway, taken entries, and the preview-then-confirm re-check.
/// </summary>
public sealed class LibraryMoveServiceTests : IDisposable
{
    private const string OkComputerManifest = """
        {"tracks":[
          {"title":"Airbag","track_number":1,"disc_number":1,"duration_seconds":284},
          {"title":"Paranoid Android","track_number":2,"disc_number":1,"duration_seconds":387},
          {"title":"Subterranean Homesick Alien","track_number":3,"disc_number":1,"duration_seconds":267}]}
        """;

    private readonly LibraryMoveTestWorld _world = new();

    public void Dispose() => _world.Dispose();

    // ----- albums: tracks ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASongMovedIntoAnAlbumThatLacksIt_GoesInOnlyByMatchingARealTrack()
    {
        var (song, _) = SongInOwnAlbum("Paranoid Android", "The Best Of");
        var okComputer = AlbumWithAirbag();

        var preview = await PreviewAsync([song], okComputer);

        Assert.Equal(LibraryMoveListKinds.Tracks, preview.ListKind);
        Assert.Equal(["1", "2", "3"], preview.Entries.Select(entry => entry.Number));
        Assert.Equal("Airbag", preview.Entries[0].TakenByTitle);
        Assert.Null(preview.Entries[1].TakenByItemId);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(preview.Entries[1].EntryKey, row.SuggestedEntryKey);
        Assert.Equal("Same title", row.SuggestionReason);

        // A plain drop is not possible: a match with no entry chosen is refused and nothing moves.
        var plain = await CommitAsync(preview, new LibraryMoveCommitRow { ItemId = song, Mode = LibraryMoveModes.Match });
        Assert.Equal(LibraryMoveRefusalKind.Invalid, plain.Refusal!.Kind);
        Assert.NotEqual(okComputer, _world.Work(song).Parent);

        var matched = await CommitAsync(preview, Match(song, row.SuggestedEntryKey!));

        Assert.True(matched.IsSuccess);
        Assert.Equal(1, matched.Value!.MovedCount);
        Assert.Equal(LibraryMoveOutcomes.Matched, Assert.Single(matched.Value.Rows).Outcome);
        var state = _world.Work(song);
        Assert.Equal(okComputer, state.Parent);
        Assert.Equal(2, state.Ordinal);
        Assert.Equal(2d, state.OrdinalSort);
        Assert.Equal(0, state.Unmatched);
        Assert.Equal("2", _world.Canonical(song, "track_number"));
    }

    [Fact]
    public async Task AddAnyway_PutsTheSongInTheAlbumWithNoNumber_FlagsItNotMatched_AndBorrowsNothingFromTheAlbum()
    {
        var (song, _) = SongInOwnAlbum("Hidden Bonus Jam", "The Best Of");
        _world.Put(song, "track_number", "9");
        var okComputer = AlbumWithAirbag();

        var preview = await PreviewAsync([song], okComputer);
        Assert.Null(Assert.Single(preview.Rows).SuggestedEntryKey);
        var result = await CommitAsync(preview, AddAnyway(song));

        Assert.True(result.IsSuccess);
        Assert.Equal(LibraryMoveOutcomes.AddedUnmatched, Assert.Single(result.Value!.Rows).Outcome);
        var state = _world.Work(song);
        Assert.Equal(okComputer, state.Parent);
        Assert.Null(state.Ordinal);
        Assert.Null(state.OrdinalSort);
        Assert.Equal(1, state.Unmatched);
        Assert.Null(_world.Canonical(song, "track_number"));
        Assert.Equal("Hidden Bonus Jam", _world.Canonical(song, "title"));
        // Nothing the album knows about itself, or about any of its entries, was copied onto the song.
        Assert.Null(_world.Canonical(song, "album"));
        Assert.Null(_world.Canonical(song, "child_entities_json"));
    }

    [Fact]
    public async Task ASongThatMatchesNoTrackByTitle_IsSuggestedTheTrackClosestInLength_WithThatReason()
    {
        var (song, _) = SongInOwnAlbum("Untitled demo", "The Best Of", duration: "388");
        var okComputer = AlbumWithAirbag();

        var preview = await PreviewAsync([song], okComputer);

        var row = Assert.Single(preview.Rows);
        Assert.Equal(preview.Entries.Single(entry => entry.Title == "Paranoid Android").EntryKey, row.SuggestedEntryKey);
        Assert.Equal("Closest length", row.SuggestionReason);
    }

    [Fact]
    public async Task AnEpisode_IsNeverOfferedAnAlbum_AndCannotBeMovedIntoOne()
    {
        var show = _world.AddParent("TV", "Northwatch");
        var season = _world.AddParent("TV", string.Empty, show, ordinal: 1);
        var (episode, _, _) = _world.AddChild(season, "TV", "Pilot", 1);
        var album = AlbumWithAirbag();
        var otherSeason = _world.AddParent("TV", string.Empty, show, ordinal: 2);
        _world.AddChild(otherSeason, "TV", "Return", 1);

        var destinations = await _world.Service.GetDestinationsAsync(episode, null, LibraryMoveTestWorld.Everything);
        var refused = await _world.Service.PreviewAsync(
            new LibraryMovePreviewRequest { ItemIds = [episode], DestinationId = album }, LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything);

        Assert.DoesNotContain(destinations.Value!.Destinations, destination => destination.Kind == LibraryMoveDestinationKinds.Album);
        Assert.Contains(destinations.Value.Destinations, destination => destination.Id == otherSeason && destination.Label == "Season 2");
        Assert.DoesNotContain(destinations.Value.Destinations, destination => destination.Id == season);
        Assert.Equal(LibraryMoveRefusalKind.Invalid, refused.Refusal!.Kind);
    }

    [Fact]
    public async Task ASongIsOfferedAlbumsAndCollections_ButNeverSeasonsOrSeries()
    {
        var (song, _) = SongInOwnAlbum("Airbag", "The Best Of");
        var okComputer = AlbumWithAirbag();
        var show = _world.AddParent("TV", "Northwatch");
        _world.AddParent("TV", string.Empty, show, ordinal: 1);
        var dune = _world.AddParent("Books", "Dune");
        _world.AddChild(dune, "Books", "Dune", 1);
        var mine = _world.AddCollection("Road trip");

        var destinations = (await _world.Service.GetDestinationsAsync(song, null, LibraryMoveTestWorld.Everything)).Value!.Destinations;

        Assert.Equal(
            [LibraryMoveDestinationKinds.Album, LibraryMoveDestinationKinds.Collection],
            destinations.Select(destination => destination.Kind).Distinct().Order());
        var album = destinations.Single(destination => destination.Id == okComputer);
        Assert.True(album.HasList);
        Assert.False(destinations.Single(destination => destination.Id == mine).HasList);
        Assert.Equal(
            okComputer,
            (await _world.Service.GetDestinationsAsync(song, "ok comp", LibraryMoveTestWorld.Everything)).Value!.Destinations.Single(destination => destination.Kind == LibraryMoveDestinationKinds.Album).Id);
    }

    [Fact]
    public async Task ATakenEntry_RefusesMatch_AndAddAsAVersionAttachesTheFileToItAndKeepsTheOriginal()
    {
        var (song, bestOf) = SongInOwnAlbum("Paranoid Android (Live)", "The Best Of");
        var songAsset = _world.Scalar<Guid>("SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @song;", new { song });
        var okComputer = _world.AddParent("Music", "OK Computer", manifestJson: OkComputerManifest);
        var (taken, _, takenAsset) = _world.AddChild(okComputer, "Music", "Paranoid Android", 2);

        var preview = await PreviewAsync([song], okComputer);

        var entry = preview.Entries.Single(candidate => candidate.TakenByItemId == taken);
        Assert.Equal(entry.EntryKey, Assert.Single(preview.Rows).SuggestedEntryKey);
        Assert.Contains("The Best Of will be empty and removed", preview.Warnings);

        var refused = await CommitAsync(preview, Match(song, entry.EntryKey));
        Assert.Equal(LibraryMoveRefusalKind.Invalid, refused.Refusal!.Kind);
        Assert.Contains("already has a file", refused.Refusal.Message, StringComparison.Ordinal);
        Assert.True(_world.WorkExists(song));

        var added = await CommitAsync(preview, AsVersion(song, entry.EntryKey));

        Assert.True(added.IsSuccess);
        Assert.Equal(LibraryMoveOutcomes.AddedAsVersion, Assert.Single(added.Value!.Rows).Outcome);
        Assert.Equal(2, _world.EditionCount(taken));
        Assert.Equal(taken, _world.WorkOfAsset(takenAsset!.Value));
        Assert.Equal(taken, _world.WorkOfAsset(songAsset));
        Assert.False(_world.WorkExists(song));
        Assert.False(_world.WorkExists(bestOf));
        Assert.Contains("The Best Of", added.Value.RemovedContainers);
    }

    [Fact]
    public async Task AddAsAVersion_IsRefusedForAnEntryThatHasNoFileYet()
    {
        var (song, _) = SongInOwnAlbum("Paranoid Android", "The Best Of");
        var okComputer = AlbumWithAirbag();

        var preview = await PreviewAsync([song], okComputer);
        var free = preview.Entries.Single(entry => entry.Title == "Paranoid Android");
        var refused = await CommitAsync(preview, AsVersion(song, free.EntryKey));

        Assert.Equal(LibraryMoveRefusalKind.Invalid, refused.Refusal!.Kind);
        Assert.Contains("match", refused.Refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MatchingAnEntryThatIsOnlyAPlaceholderRow_AttachesTheFileToThatEntry()
    {
        var (song, _) = SongInOwnAlbum("Paranoid Android", "The Best Of");
        var okComputer = _world.AddParent("Music", "OK Computer");
        var (placeholder, _, _) = _world.AddChild(okComputer, "Music", "Paranoid Android", 2, hasFile: false, catalogOnly: true);

        var preview = await PreviewAsync([song], okComputer);
        var entry = Assert.Single(preview.Entries);
        Assert.Null(entry.TakenByItemId);
        var result = await CommitAsync(preview, Match(song, entry.EntryKey));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _world.EditionCount(placeholder) - 1);
        Assert.False(_world.WorkExists(song));
        Assert.Equal("Owned", _world.Scalar<string>("SELECT ownership FROM works WHERE id = @placeholder;", new { placeholder }));
        Assert.Equal("child", _world.Scalar<string>("SELECT work_kind FROM works WHERE id = @placeholder;", new { placeholder }));
    }

    // ----- stale previews ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADestinationEntryTakenAfterThePreview_GetsAFreshPreviewAndMovesNothing()
    {
        var (song, _) = SongInOwnAlbum("Paranoid Android", "The Best Of");
        var okComputer = AlbumWithAirbag();
        var preview = await PreviewAsync([song], okComputer);
        var entry = preview.Entries.Single(candidate => candidate.Title == "Paranoid Android");
        _world.AddChild(okComputer, "Music", "Paranoid Android", 2);

        var result = await CommitAsync(preview, Match(song, entry.EntryKey));

        Assert.Equal(LibraryMoveRefusalKind.Changed, result.Refusal!.Kind);
        var fresh = result.Refusal.FreshPreview!;
        Assert.NotEmpty(fresh.PreviewToken);
        Assert.NotEqual(preview.PreviewToken, fresh.PreviewToken);
        Assert.Contains(fresh.Entries, candidate => candidate.Title == "Paranoid Android" && candidate.TakenByItemId.HasValue);
        Assert.NotEqual(okComputer, _world.Work(song).Parent);
    }

    [Fact]
    public async Task AnItemWhoseIdentityChangedAfterThePreview_GetsAFreshPreviewAndMovesNothing()
    {
        var (song, bestOf) = SongInOwnAlbum("Paranoid Android", "The Best Of");
        var okComputer = AlbumWithAirbag();
        var preview = await PreviewAsync([song], okComputer);
        _world.Put(song, "identity_revision", Guid.NewGuid().ToString("N"));

        var result = await CommitAsync(preview, Match(song, preview.Entries[1].EntryKey));

        Assert.Equal(LibraryMoveRefusalKind.Changed, result.Refusal!.Kind);
        Assert.Equal(bestOf, _world.Work(song).Parent);
    }

    [Fact]
    public async Task AnItemDeletedAfterThePreview_IsAChange_AndTheOthersAreNotMoved()
    {
        var (first, bestOf) = SongInOwnAlbum("Paranoid Android", "The Best Of");
        var (second, _, _) = _world.AddChild(bestOf, "Music", "Airbag", 4);
        var okComputer = _world.AddParent("Music", "OK Computer", manifestJson: OkComputerManifest);
        var preview = await PreviewAsync([first, second], okComputer);
        var keys = preview.Rows.ToDictionary(row => row.ItemId, row => row.SuggestedEntryKey!);
        _world.Scalar<long>("DELETE FROM works WHERE id = @second; SELECT 0;", new { second });

        var result = await CommitAsync(preview, Match(first, keys[first]), Match(second, keys[second]));

        Assert.Equal(LibraryMoveRefusalKind.Changed, result.Refusal!.Kind);
        Assert.Equal(bestOf, _world.Work(first).Parent);
        Assert.Single(result.Refusal.FreshPreview!.Rows);
    }

    [Fact]
    public async Task AnItemThatStartedProcessingAfterThePreview_IsAChange()
    {
        var (song, bestOf) = SongInOwnAlbum("Paranoid Android", "The Best Of");
        var okComputer = AlbumWithAirbag();
        var preview = await PreviewAsync([song], okComputer);
        _world.MarkProcessing(AssetOf(song));

        var result = await CommitAsync(preview, Match(song, preview.Entries[1].EntryKey));

        Assert.Equal(LibraryMoveRefusalKind.Changed, result.Refusal!.Kind);
        Assert.Equal("Still being processed", Assert.Single(result.Refusal.FreshPreview!.Rows).BlockedReason);
        Assert.Equal(bestOf, _world.Work(song).Parent);
    }

    [Fact]
    public async Task APreviewTokenThatIsUnknownOrBelongsToSomeoneElse_IsRefused()
    {
        var (song, _) = SongInOwnAlbum("Paranoid Android", "The Best Of");
        var okComputer = AlbumWithAirbag();
        var preview = await PreviewAsync([song], okComputer);
        var someoneElse = LibraryMoveTestWorld.Actor with { Authority = LibraryMoveTestWorld.Actor.Authority with { SessionId = Guid.NewGuid() } };
        var request = new LibraryMoveCommitRequest { PreviewToken = preview.PreviewToken, Rows = [Match(song, preview.Entries[1].EntryKey)] };

        var unknown = await _world.Service.CommitAsync(
            new LibraryMoveCommitRequest { PreviewToken = new string('a', 48), Rows = request.Rows }, LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything);
        var other = await _world.Service.CommitAsync(request, someoneElse, LibraryMoveTestWorld.Everything);

        Assert.Equal(LibraryMoveRefusalKind.Expired, unknown.Refusal!.Kind);
        Assert.Equal(LibraryMoveRefusalKind.Expired, other.Refusal!.Kind);
    }

    [Fact]
    public async Task ACallerWhoMayNotEditAnItem_IsRefusedBeforeAnythingIsLookedAt()
    {
        var (song, _) = SongInOwnAlbum("Paranoid Android", "The Best Of");
        var okComputer = AlbumWithAirbag();

        var preview = await _world.Service.PreviewAsync(
            new LibraryMovePreviewRequest { ItemIds = [song], DestinationId = okComputer }, LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Nothing);
        var destinations = await _world.Service.GetDestinationsAsync(song, null, LibraryMoveTestWorld.Nothing);

        Assert.Equal(LibraryMoveRefusalKind.Unavailable, preview.Refusal!.Kind);
        Assert.Equal(LibraryMoveRefusalKind.Unavailable, destinations.Refusal!.Kind);
    }

    // ----- warnings and emptied places -----------------------------------------------------------------------------------------

    [Fact]
    public async Task MovingTheOnlySongOutOfAnAlbum_WarnsThatItWillBeEmptyAndRemoved_AndRemovesIt()
    {
        var (song, bestOf) = SongInOwnAlbum("Paranoid Android", "The Best Of");
        var okComputer = AlbumWithAirbag();

        var preview = await PreviewAsync([song], okComputer);
        var result = await CommitAsync(preview, Match(song, preview.Entries[1].EntryKey));

        Assert.Contains("The Best Of will be empty and removed", preview.Warnings);
        Assert.DoesNotContain("Leaves The Best Of", preview.Warnings);
        Assert.Equal(["The Best Of"], result.Value!.RemovedContainers);
        Assert.False(_world.WorkExists(bestOf));
    }

    [Fact]
    public async Task MovingOneOfSeveralSongsOutOfAnAlbum_OnlyWarnsThatItLeaves()
    {
        var (song, bestOf) = SongInOwnAlbum("Paranoid Android", "The Best Of");
        _world.AddChild(bestOf, "Music", "Another one", 4);
        var okComputer = AlbumWithAirbag();

        var preview = await PreviewAsync([song], okComputer);
        var result = await CommitAsync(preview, Match(song, preview.Entries[1].EntryKey));

        Assert.Equal(["Leaves The Best Of"], preview.Warnings);
        Assert.Empty(result.Value!.RemovedContainers);
        Assert.True(_world.WorkExists(bestOf));
    }

    [Fact]
    public async Task AnEmptiedHandPickedCollection_IsRemovedWithAWarningInThePreview()
    {
        var (song, _) = SongInOwnAlbum("Paranoid Android", "The Best Of");
        var roadTrip = _world.AddCollection("Road trip", song);
        var favourites = _world.AddCollection("Favourites");

        var preview = await _world.Service.PreviewAsync(
            new LibraryMovePreviewRequest { ItemIds = [song], DestinationId = favourites, SourceCollectionId = roadTrip },
            LibraryMoveTestWorld.Actor,
            LibraryMoveTestWorld.Everything);

        Assert.Contains("Road trip will be empty and removed", preview.Value!.Warnings);
        var result = await CommitAsync(preview.Value, Free(song));

        Assert.True(result.IsSuccess);
        Assert.Contains("Road trip", result.Value!.RemovedContainers);
        Assert.Equal(0, _world.Scalar<long>("SELECT COUNT(*) FROM collections WHERE id = @roadTrip;", new { roadTrip }));
        Assert.Equal(1, _world.Scalar<long>("SELECT COUNT(*) FROM collection_items WHERE collection_id = @favourites AND work_id = @song;", new { favourites, song }));
    }

    [Fact]
    public async Task ACollectionThatKeepsOtherItems_OnlyWarnsThatTheItemLeavesIt()
    {
        var (song, bestOf) = SongInOwnAlbum("Paranoid Android", "The Best Of");
        var (other, _, _) = _world.AddChild(bestOf, "Music", "Another one", 4);
        var roadTrip = _world.AddCollection("Road trip", song, other);
        var favourites = _world.AddCollection("Favourites");

        var preview = await _world.Service.PreviewAsync(
            new LibraryMovePreviewRequest { ItemIds = [song], DestinationId = favourites, SourceCollectionId = roadTrip },
            LibraryMoveTestWorld.Actor,
            LibraryMoveTestWorld.Everything);
        await CommitAsync(preview.Value!, Free(song));

        Assert.Contains("Leaves Road trip", preview.Value!.Warnings);
        Assert.Equal(1, _world.Scalar<long>("SELECT COUNT(*) FROM collections WHERE id = @roadTrip;", new { roadTrip }));
        Assert.Equal(0, _world.Scalar<long>("SELECT COUNT(*) FROM collection_items WHERE collection_id = @roadTrip AND work_id = @song;", new { roadTrip, song }));
    }

    // ----- no list -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHandPickedCollection_HasNoListAndNoGate_ItemsJoinAtTheEnd()
    {
        var (first, _) = SongInOwnAlbum("Paranoid Android", "The Best Of");
        var (second, _) = SongInOwnAlbum("Airbag", "Another album");
        var mine = _world.AddCollection("Road trip", first);

        var preview = await PreviewAsync([second], mine);

        Assert.Equal(LibraryMoveListKinds.None, preview.ListKind);
        Assert.False(preview.Destination.HasList);
        Assert.Empty(preview.Entries);
        Assert.Null(Assert.Single(preview.Rows).SuggestedEntryKey);

        var tried = await CommitAsync(preview, AddAnyway(second));
        Assert.Equal(LibraryMoveRefusalKind.Invalid, tried.Refusal!.Kind);

        var result = await CommitAsync(preview, Free(second));
        Assert.Equal(LibraryMoveOutcomes.Moved, Assert.Single(result.Value!.Rows).Outcome);
        Assert.Equal(1, _world.Scalar<long>("SELECT sort_order FROM collection_items WHERE collection_id = @mine AND work_id = @second;", new { mine, second }));
    }

    // ----- TV: episodes, with specials as Season 0 -----------------------------------------------------------------------------

    [Fact]
    public async Task ASeasonsEpisodeList_IncludesSpecialsAsSeasonZero_WithTheEpisodesTheShowKnowsAreMissing()
    {
        var show = _world.AddParent("TV", "Northwatch");
        var specials = _world.AddParent("TV", string.Empty, show, ordinal: 0);
        var (owned, _, _) = _world.AddChild(specials, "TV", "Behind the scenes", 1);
        var collection = _world.AddSeriesCollection("tvdb:77", [
            ("e1", "tvdb:77:season:0", 1, "Behind the scenes", owned),
            ("e3", "tvdb:77:season:0", 3, "The lost reel", null),
            ("s1e1", "tvdb:77:season:1", 1, "Pilot", null)]);
        _world.Scalar<long>("UPDATE works SET collection_id = @collection WHERE id = @show; SELECT 0;", new { collection, show });
        var season = _world.AddParent("TV", string.Empty, show, ordinal: 1);
        var (episode, _, _) = _world.AddChild(season, "TV", "The lost reel", 9);
        _world.AddChild(season, "TV", "Return", 10);

        var preview = await PreviewAsync([episode], specials);

        Assert.Equal(LibraryMoveListKinds.Episodes, preview.ListKind);
        Assert.Equal("Specials", preview.Destination.Label);
        Assert.Equal(["S0 E1", "S0 E3"], preview.Entries.Select(entry => entry.Number));
        var row = Assert.Single(preview.Rows);
        Assert.Equal("Same title", row.SuggestionReason);
        Assert.Equal(preview.Entries[1].EntryKey, row.SuggestedEntryKey);

        var result = await CommitAsync(preview, Match(episode, row.SuggestedEntryKey!));

        Assert.True(result.IsSuccess);
        var state = _world.Work(episode);
        Assert.Equal(specials, state.Parent);
        Assert.Equal(3, state.Ordinal);
        Assert.Equal(collection, state.Collection);
        Assert.Equal("0", _world.Canonical(episode, "season_number"));
        Assert.Equal("3", _world.Canonical(episode, "episode_number"));
        Assert.Equal(episode, _world.Scalar<Guid>("SELECT linked_work_id FROM series_manifest_items WHERE item_qid = 'e3';"));
        Assert.Equal("Owned", _world.Scalar<string>("SELECT ownership_state FROM series_manifest_items WHERE item_qid = 'e3';"));
    }

    [Fact]
    public async Task MovingTheOnlyEpisodeOutOfASeason_WarnsThatTheSeasonWillBeEmptyAndRemoved_AndTheShowStays()
    {
        var show = _world.AddParent("TV", "Northwatch");
        var seasonOne = _world.AddParent("TV", string.Empty, show, ordinal: 1);
        var seasonTwo = _world.AddParent("TV", string.Empty, show, ordinal: 2);
        var (episode, _, _) = _world.AddChild(seasonOne, "TV", "Pilot", 1);
        _world.AddChild(seasonTwo, "TV", "Return", 1);

        var preview = await PreviewAsync([episode], seasonTwo);
        var result = await CommitAsync(preview, AddAnyway(episode));

        Assert.Contains("Season 1 will be empty and removed", preview.Warnings);
        Assert.Equal(["Season 1"], result.Value!.RemovedContainers);
        Assert.False(_world.WorkExists(seasonOne));
        Assert.True(_world.WorkExists(show));
        Assert.Equal(1, _world.Work(episode).Unmatched);
    }

    // ----- series: positions, decimals allowed --------------------------------------------------------------------------------

    [Fact]
    public async Task ASeriesPositionList_AllowsDecimals_AndAMatchToOnePointFiveKeepsTheDecimal()
    {
        var series = _world.AddParent("Books", "The Dune Chronicles");
        var (first, _, _) = _world.AddChild(series, "Books", "Dune", 1);
        var collection = _world.AddSeriesCollection("wd:Q1", [
            ("q1", "wd:Q1", 1, "Dune", first),
            ("q15", "wd:Q1", 1.5, "A Dune Novella", null),
            ("q2", "wd:Q1", 2, "Dune Messiah", null)]);
        _world.Scalar<long>("UPDATE works SET collection_id = @collection WHERE id = @first; SELECT 0;", new { collection, first });
        var elsewhere = _world.AddParent("Books", "Loose books");
        var (novella, _, _) = _world.AddChild(elsewhere, "Books", "A Dune Novella", 7);

        var preview = await PreviewAsync([novella], series);

        Assert.Equal(LibraryMoveListKinds.Positions, preview.ListKind);
        Assert.Equal(["1", "1.5", "2"], preview.Entries.Select(entry => entry.Number));
        var row = Assert.Single(preview.Rows);
        Assert.Equal(preview.Entries[1].EntryKey, row.SuggestedEntryKey);

        var result = await CommitAsync(preview, Match(novella, row.SuggestedEntryKey!));

        Assert.True(result.IsSuccess);
        var state = _world.Work(novella);
        Assert.Equal(series, state.Parent);
        Assert.Equal(1.5d, state.OrdinalSort);
        Assert.Null(state.Ordinal);
        Assert.Equal("1.5", _world.Canonical(novella, "series_position"));
        Assert.Equal(novella, _world.Scalar<Guid>("SELECT linked_work_id FROM series_manifest_items WHERE item_qid = 'q15';"));
    }

    [Fact]
    public async Task AMovieIsNotOfferedABookSeries_AndABookSeriesRefusesAMovie()
    {
        var bookSeries = _world.AddParent("Books", "The Dune Chronicles");
        _world.AddChild(bookSeries, "Books", "Dune", 1);
        var filmSeries = _world.AddParent("Movies", "Dune films");
        _world.AddChild(filmSeries, "Movies", "Dune: Part One", 1);
        var (movie, _, _) = _world.AddChild(_world.AddParent("Movies", "Other films"), "Movies", "Dune: Part Two", 1);

        var destinations = (await _world.Service.GetDestinationsAsync(movie, null, LibraryMoveTestWorld.Everything)).Value!.Destinations;
        var refused = await _world.Service.PreviewAsync(
            new LibraryMovePreviewRequest { ItemIds = [movie], DestinationId = bookSeries }, LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything);

        Assert.Contains(destinations, destination => destination.Id == filmSeries);
        Assert.DoesNotContain(destinations, destination => destination.Id == bookSeries);
        Assert.Equal(LibraryMoveRefusalKind.Invalid, refused.Refusal!.Kind);
    }

    // ----- audiobooks: parts are files ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAudiobooksPartList_ShowsItsFilesAndTheMissingPartNumbers_AndAPartMatchedToAFreeNumberTakesIt()
    {
        var (target, _) = _world.AddAudiobook("Dune (audiobook)", [(1, "Part one"), (2, "Part two")], partCount: 4);
        var (source, parts) = _world.AddAudiobook("Dune disc three", [(1, "Chapter three"), (2, "Another chapter")]);

        var preview = await PreviewAsync([parts[0].EditionId], target);

        Assert.Equal(LibraryMoveListKinds.Parts, preview.ListKind);
        Assert.Equal(["1", "2", "3", "4"], preview.Entries.Select(entry => entry.Number));
        Assert.Equal([true, true, false, false], preview.Entries.Select(entry => entry.TakenByItemId.HasValue));
        Assert.Contains("Leaves Dune disc three", preview.Warnings);

        var free = preview.Entries.Single(entry => entry.Number == "3");
        var result = await CommitAsync(preview, Match(parts[0].EditionId, free.EntryKey));

        Assert.True(result.IsSuccess);
        Assert.Equal(target, _world.WorkOfAsset(parts[0].AssetId));
        Assert.Equal("3", _world.Canonical(parts[0].AssetId, "audiobook_part_number"));
        Assert.Equal(source, _world.WorkOfAsset(parts[1].AssetId));
        Assert.Equal(0, _world.Scalar<long>("SELECT placement_unmatched FROM editions WHERE id = @edition;", new { edition = parts[0].EditionId }));
    }

    [Fact]
    public async Task AnAudiobookPartAddedAnyway_HasNoPartNumber_IsFlaggedNotMatched_AndAnEmptiedAudiobookIsRemoved()
    {
        var (target, _) = _world.AddAudiobook("Dune (audiobook)", [(1, "Part one")], partCount: 3);
        var (source, parts) = _world.AddAudiobook("Dune disc three", [(1, "Chapter three")]);

        var preview = await PreviewAsync([parts[0].EditionId], target);
        var result = await CommitAsync(preview, AddAnyway(parts[0].EditionId));

        Assert.Contains("Dune disc three will be empty and removed", preview.Warnings);
        Assert.True(result.IsSuccess);
        Assert.Equal(target, _world.WorkOfAsset(parts[0].AssetId));
        Assert.Null(_world.Canonical(parts[0].AssetId, "audiobook_part_number"));
        Assert.Equal(1, _world.Scalar<long>("SELECT placement_unmatched FROM editions WHERE id = @edition;", new { edition = parts[0].EditionId }));
        Assert.False(_world.WorkExists(source));
        Assert.Contains("Dune disc three", result.Value!.RemovedContainers);
    }

    [Fact]
    public async Task AFileOfAnEbookOrASong_CannotBeMovedAsAPartOfAnAudiobook()
    {
        var (target, _) = _world.AddAudiobook("Dune (audiobook)", [(1, "Part one")]);
        var (song, _) = SongInOwnAlbum("Airbag", "The Best Of");

        var refused = await _world.Service.PreviewAsync(
            new LibraryMovePreviewRequest { ItemIds = [song], DestinationId = target }, LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything);

        Assert.Equal(LibraryMoveRefusalKind.Invalid, refused.Refusal!.Kind);
    }

    // ----- the request itself ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryItemMustBePlaced_AndTwoItemsCannotTakeTheSameEntry()
    {
        var (first, bestOf) = SongInOwnAlbum("Paranoid Android", "The Best Of");
        var (second, _, _) = _world.AddChild(bestOf, "Music", "Paranoid Android (Live)", 5);
        var okComputer = AlbumWithAirbag();
        var preview = await PreviewAsync([first, second], okComputer);
        var entry = preview.Entries.Single(candidate => candidate.Title == "Paranoid Android");

        var missingOne = await CommitAsync(preview, Match(first, entry.EntryKey));
        var sameEntry = await CommitAsync(preview, Match(first, entry.EntryKey), Match(second, entry.EntryKey));
        var badMode = await CommitAsync(preview, new LibraryMoveCommitRow { ItemId = first, Mode = "swap" }, AddAnyway(second));

        Assert.Equal(LibraryMoveRefusalKind.Invalid, missingOne.Refusal!.Kind);
        Assert.Equal(LibraryMoveRefusalKind.Invalid, sameEntry.Refusal!.Kind);
        Assert.Equal(LibraryMoveRefusalKind.Invalid, badMode.Refusal!.Kind);
        Assert.Equal(bestOf, _world.Work(first).Parent);
    }

    [Fact]
    public async Task ABatchOfSongs_GetsOneGuessEach_WithEachEntrySuggestedOnce()
    {
        var (first, bestOf) = SongInOwnAlbum("Airbag", "The Best Of");
        var (second, _, _) = _world.AddChild(bestOf, "Music", "Paranoid Android", 4);
        var (third, _, _) = _world.AddChild(bestOf, "Music", "Mystery", 5);
        var okComputer = _world.AddParent("Music", "OK Computer", manifestJson: OkComputerManifest);

        var preview = await PreviewAsync([first, second, third], okComputer);

        var byItem = preview.Rows.ToDictionary(row => row.ItemId);
        Assert.Equal("Airbag", preview.Entries.Single(entry => entry.EntryKey == byItem[first].SuggestedEntryKey).Title);
        Assert.Equal("Paranoid Android", preview.Entries.Single(entry => entry.EntryKey == byItem[second].SuggestedEntryKey).Title);
        Assert.Null(byItem[third].SuggestedEntryKey);
        Assert.All(preview.Rows, row => Assert.Null(row.FileChange));
        Assert.All(preview.Rows, row => Assert.Null(row.FileNote));
    }

    [Fact]
    public async Task TheCurrentPlacementIsDescribedInPlainWords()
    {
        var (song, bestOf) = SongInOwnAlbum("Paranoid Android", "The Best Of");
        var okComputer = AlbumWithAirbag();

        var preview = await PreviewAsync([song], okComputer);

        Assert.Equal("The Best Of, track 3", Assert.Single(preview.Rows).CurrentPlacementLabel);
        Assert.Equal(bestOf, _world.Work(song).Parent);
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private (Guid Song, Guid Album) SongInOwnAlbum(string title, string album, string? duration = null)
    {
        var parent = _world.AddParent("Music", album);
        var (work, _, _) = _world.AddChild(parent, "Music", title, 3, duration: duration);
        return (work, parent);
    }

    private Guid AlbumWithAirbag()
    {
        var album = _world.AddParent("Music", "OK Computer", manifestJson: OkComputerManifest);
        _world.AddChild(album, "Music", "Airbag", 1);
        return album;
    }

    private Guid AssetOf(Guid work) =>
        _world.Scalar<Guid>("SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @work LIMIT 1;", new { work });

    private async Task<LibraryMovePreview> PreviewAsync(IReadOnlyList<Guid> items, Guid destination)
    {
        var result = await _world.Service.PreviewAsync(
            new LibraryMovePreviewRequest { ItemIds = items, DestinationId = destination },
            LibraryMoveTestWorld.Actor,
            LibraryMoveTestWorld.Everything);
        Assert.True(result.IsSuccess, result.Refusal?.Message);
        return result.Value!;
    }

    private Task<LibraryMoveResult<LibraryMoveCommitResult>> CommitAsync(LibraryMovePreview preview, params LibraryMoveCommitRow[] rows) =>
        _world.Service.CommitAsync(
            new LibraryMoveCommitRequest { PreviewToken = preview.PreviewToken, Rows = rows },
            LibraryMoveTestWorld.Actor,
            LibraryMoveTestWorld.Everything);

    private static LibraryMoveCommitRow Match(Guid item, string entryKey) =>
        new() { ItemId = item, Mode = LibraryMoveModes.Match, EntryKey = entryKey };

    private static LibraryMoveCommitRow AsVersion(Guid item, string entryKey) =>
        new() { ItemId = item, Mode = LibraryMoveModes.AddAsVersion, EntryKey = entryKey };

    private static LibraryMoveCommitRow AddAnyway(Guid item) =>
        new() { ItemId = item, Mode = LibraryMoveModes.AddAnyway };

    private static LibraryMoveCommitRow Free(Guid item) =>
        new() { ItemId = item, Mode = LibraryMoveModes.Match };
}
