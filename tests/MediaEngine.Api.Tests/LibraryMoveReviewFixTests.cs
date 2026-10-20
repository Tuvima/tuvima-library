using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Wave 4 review of moves: a refusal that is not a change is a plain refusal (never "something changed"), an item's own place is
/// offered for matching and renumbering, a committed row names the Work it ended up in, matching permission shows in the preview, and
/// an item added anyway loses only its own list's numbers.
/// </summary>
public sealed class LibraryMoveReviewFixTests : IDisposable
{
    private const string LuckyManifest = """{"tracks":[{"title":"Lucky","track_number":5,"disc_number":1}]}""";

    private readonly LibraryMoveTestWorld _world = new();

    public void Dispose() => _world.Dispose();

    // ----- details the person set ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASongWhoseDetailsThePersonSet_IsToldInThePreviewThatItCannotMergeIntoAnExistingEntry_ButCanTakeAnEmptySlot()
    {
        var (bestOf, song, okComputer) = RenamedSongAndAlbumWithAnExistingEntry();

        var preview = await Preview([song], okComputer);

        var row = Assert.Single(preview.Rows);
        Assert.Equal(LibraryMoveReasons.UserManagedMerge, row.MergeBlockedReason);
        Assert.Null(row.BlockedReason);
        Assert.Null(row.SuggestedEntryKey);
        var existing = preview.Entries.Single(entry => entry.Title == "Paranoid Android");
        var slot = preview.Entries.Single(entry => entry.Title == "Lucky");
        Assert.True(existing.JoinsExistingTitle);
        Assert.False(slot.JoinsExistingTitle);

        var slotMove = await Commit(preview, Match(song, slot.EntryKey));
        Assert.True(slotMove.IsSuccess, slotMove.Refusal?.Message);
        Assert.NotEqual(bestOf, _world.Work(song).Parent);
    }

    [Fact]
    public async Task MatchingASongWhoseDetailsThePersonSetOntoAnExistingEntry_IsAPlainRefusalWithTheReason_NotASomethingChangedAnswer()
    {
        var (bestOf, song, okComputer) = RenamedSongAndAlbumWithAnExistingEntry();
        var preview = await Preview([song], okComputer);
        var existing = preview.Entries.Single(entry => entry.Title == "Paranoid Android");

        var refused = await Commit(preview, Match(song, existing.EntryKey));

        Assert.Equal(LibraryMoveRefusalKind.Invalid, refused.Refusal!.Kind);
        Assert.Equal(LibraryMoveReasons.UserManagedMerge, refused.Refusal.Message);
        Assert.Null(refused.Refusal.FreshPreview);
        Assert.Equal(bestOf, _world.Work(song).Parent);

        // Adding it anyway still works, from the very same review.
        var anyway = await Commit(preview, AddAnyway(song));
        Assert.True(anyway.IsSuccess, anyway.Refusal?.Message);
    }

    [Fact]
    public async Task TheMoveTransactionItself_RefusesToMergeASongWhoseDetailsThePersonSet_AsARefusalNotAChange()
    {
        var (_, song, okComputer) = RenamedSongAndAlbumWithAnExistingEntry();
        var destination = (await _world.Repository.GetDestinationAsync(okComputer))!;
        var item = (await _world.Repository.GetItemsAsync([song])).Single();
        var entryWork = _world.Scalar<Guid>(
            "SELECT id FROM works WHERE parent_work_id = @okComputer AND is_catalog_only = 1;", new { okComputer });
        var plan = new LibraryMovePlan(
            destination.Id,
            destination.Kind,
            [new LibraryMovePlanRow(song, LibraryMoveMode.Match, new LibraryMoveTarget(ExistingWorkId: entryWork), item.ParentWorkId, item.IdentityRevision)],
            "Shy");

        var outcome = await _world.Repository.CommitAsync(plan, null);

        Assert.False(outcome.Committed);
        Assert.True(outcome.IsRefusal);
        Assert.Equal(LibraryMoveReasons.UserManagedMerge, outcome.ConflictReason);
        Assert.True(_world.WorkExists(song));
    }

    [Fact]
    public async Task ASongWhoseDetailsThePersonDidNotSet_HasNoMergeBlock()
    {
        var bestOf = _world.AddParent("Music", "The Best Of");
        var (song, _, _) = _world.AddChild(bestOf, "Music", "Paranoid Android", 3);
        var okComputer = _world.AddParent("Music", "OK Computer");
        _world.AddChild(okComputer, "Music", "Paranoid Android", 2, hasFile: false, catalogOnly: true);

        var preview = await Preview([song], okComputer);

        Assert.Null(Assert.Single(preview.Rows).MergeBlockedReason);
    }

    // ----- the item's own place --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANotMatchedSong_IsOfferedItsOwnAlbumAsTheCurrentDestination_ButNeverInThePickersList()
    {
        var album = _world.AddParent("Music", "OK Computer", manifestJson: LuckyManifest);
        _world.AddChild(album, "Music", "Airbag", 1);
        var (song, _, _) = _world.AddChild(album, "Music", "Hidden Bonus Jam", null, ordinalSort: null);
        _world.Execute("UPDATE works SET placement_unmatched = 1 WHERE id = @song;", new { song });
        var elsewhere = _world.AddParent("Music", "Another Album");
        _world.AddChild(elsewhere, "Music", "Opener", 1);

        var result = await _world.Service.GetDestinationsAsync(song, null, LibraryMoveTestWorld.Everything);

        Assert.True(result.IsSuccess, result.Refusal?.Message);
        var current = result.Value!.CurrentDestination!;
        Assert.Equal(album, current.Id);
        Assert.Equal(LibraryMoveDestinationKinds.Album, current.Kind);
        Assert.True(current.HasList);
        Assert.DoesNotContain(result.Value.Destinations, destination => destination.Id == album);
        Assert.Contains(result.Value.Destinations, destination => destination.Id == elsewhere);
    }

    [Fact]
    public async Task AnEpisodeAnAudiobookPartAndASongOnItsOwn_GetTheirCurrentPlaceOrNone()
    {
        var show = _world.AddParent("TV", "Northwatch");
        var season = _world.AddParent("TV", string.Empty, show, ordinal: 1);
        var (episode, _, _) = _world.AddChild(season, "TV", "Pilot", 1);
        var (audiobook, parts) = _world.AddAudiobook("Dune (audiobook)", [(1, "Part one")]);
        var (loose, _, _) = _world.AddChild(_world.AddParent("Music", "Loose"), "Music", "Lucky", 1);
        _world.Execute("UPDATE works SET parent_work_id = NULL, work_kind = 'standalone' WHERE id = @loose;", new { loose });

        var episodeResult = await _world.Service.GetDestinationsAsync(episode, null, LibraryMoveTestWorld.Everything);
        var partResult = await _world.Service.GetDestinationsAsync(parts[0].EditionId, null, LibraryMoveTestWorld.Everything);
        var looseResult = await _world.Service.GetDestinationsAsync(loose, null, LibraryMoveTestWorld.Everything);

        Assert.Equal(season, episodeResult.Value!.CurrentDestination!.Id);
        Assert.Equal(audiobook, partResult.Value!.CurrentDestination!.Id);
        Assert.Null(looseResult.Value!.CurrentDestination);
    }

    [Fact]
    public async Task ThePlaceThePersonMayNotEdit_IsNotOfferedAsTheCurrentDestination()
    {
        var album = _world.AddParent("Music", "OK Computer", manifestJson: LuckyManifest);
        var (song, _, _) = _world.AddChild(album, "Music", "Airbag", 1);
        LibraryMoveAccess everythingButTheAlbum = (id, _, _) => ValueTask.FromResult(id != album);

        var result = await _world.Service.GetDestinationsAsync(song, null, everythingButTheAlbum);

        Assert.Null(result.Value!.CurrentDestination);
    }

    // ----- which Work each row ended up in ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task EachCommittedRow_NamesTheWorkTheItemNowLivesInOrAs()
    {
        // Merged into an existing entry: the entry's Work survives, the song's own is gone.
        var bestOf = _world.AddParent("Music", "The Best Of");
        var (merged, _, _) = _world.AddChild(bestOf, "Music", "Paranoid Android", 3);
        var okComputer = _world.AddParent("Music", "OK Computer");
        var (entry, _, _) = _world.AddChild(okComputer, "Music", "Paranoid Android", 2, hasFile: false, catalogOnly: true);
        var mergedPreview = await Preview([merged], okComputer);
        var mergedResult = await Commit(mergedPreview, Match(merged, mergedPreview.Entries.Single().EntryKey));
        var mergedRow = Assert.Single(mergedResult.Value!.Rows);
        Assert.Equal(entry, mergedRow.ResultEntityId);
        Assert.False(_world.WorkExists(merged));

        // Added anyway: the song stays its own Work.
        var (stray, _, _) = _world.AddChild(_world.AddParent("Music", "Singles"), "Music", "Lucky", 1);
        var anywayPreview = await Preview([stray], okComputer);
        var anywayResult = await Commit(anywayPreview, AddAnyway(stray));
        Assert.Equal(stray, Assert.Single(anywayResult.Value!.Rows).ResultEntityId);

        // An audiobook part: the audiobook it joined.
        var (target, _) = _world.AddAudiobook("Dune (audiobook)", [(1, "Part one")], partCount: 3);
        var (source, parts) = _world.AddAudiobook("Dune disc three", [(1, "Chapter three")]);
        var partPreview = await Preview([parts[0].EditionId], target);
        var partResult = await Commit(partPreview, Match(parts[0].EditionId, partPreview.Entries.Single(candidate => candidate.Number == "2").EntryKey));
        Assert.Equal(target, Assert.Single(partResult.Value!.Rows).ResultEntityId);
        Assert.False(_world.WorkExists(source));
    }

    // ----- permission shows in the preview ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACallerWhoMayWriteButNotMatch_SeesCanMatchOffInThePreview_ForAPlaceWithAList()
    {
        var bestOf = _world.AddParent("Music", "The Best Of");
        var (song, _, _) = _world.AddChild(bestOf, "Music", "Airbag", 4);
        var okComputer = _world.AddParent("Music", "OK Computer", manifestJson: LuckyManifest);

        var writeOnly = await _world.Service.PreviewAsync(
            new LibraryMovePreviewRequest { ItemIds = [song], DestinationId = okComputer },
            LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.WriteOnly);
        var everything = await Preview([song], okComputer);

        Assert.False(Assert.Single(writeOnly.Value!.Rows).CanMatch);
        Assert.True(Assert.Single(everything.Rows).CanMatch);
    }

    // ----- an item added anyway loses only its own list's numbers ---------------------------------------------------------------------

    [Fact]
    public async Task AnAudiobookAddedAnywayIntoASeries_KeepsThePartNumbersOfItsFiles_AndAReReadStillNumbersItsPartsButNeverItsPlace()
    {
        var (audiobook, parts) = _world.AddAudiobook("Dune (audiobook)", [(1, "Part one"), (2, "Part two")]);
        var series = _world.AddParent("Audiobooks", "Dune series");
        _world.AddChild(series, "Audiobooks", "Dune Messiah (audiobook)", 2);

        var preview = await Preview([audiobook], series);
        var result = await Commit(preview, AddAnyway(audiobook));

        Assert.True(result.IsSuccess, result.Refusal?.Message);
        Assert.Equal(1, _world.Work(audiobook).Unmatched);
        Assert.Equal("1", _world.Canonical(parts[0].AssetId, "audiobook_part_number"));
        Assert.Equal("2", _world.Canonical(parts[1].AssetId, "audiobook_part_number"));

        // A re-read of the files: the part number is still written (the title's mark does not cover it), the title's own place is not.
        var canonicals = new CanonicalValueRepository(_world.Database);
        await canonicals.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = parts[0].AssetId, Key = "audiobook_part_number", Value = "3", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = parts[0].AssetId, Key = "series_position", Value = "7", LastScoredAt = DateTimeOffset.UtcNow },
        ]);
        Assert.Equal("3", _world.Canonical(parts[0].AssetId, "audiobook_part_number"));
        Assert.Null(_world.Canonical(parts[0].AssetId, "series_position"));
    }

    [Fact]
    public async Task AnAudiobookPartAddedAnyway_StaysWithoutAPartNumber_WhileItsFileCanStillCarryAnythingElse()
    {
        var (target, _) = _world.AddAudiobook("Dune (audiobook)", [(1, "Part one")], partCount: 3);
        var (_, parts) = _world.AddAudiobook("Dune disc three", [(1, "Chapter three")]);

        var preview = await Preview([parts[0].EditionId], target);
        var result = await Commit(preview, AddAnyway(parts[0].EditionId));
        Assert.True(result.IsSuccess, result.Refusal?.Message);

        var canonicals = new CanonicalValueRepository(_world.Database);
        await canonicals.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = parts[0].AssetId, Key = "audiobook_part_number", Value = "2", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = parts[0].AssetId, Key = "track_title", Value = "Chapter three", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        Assert.Null(_world.Canonical(parts[0].AssetId, "audiobook_part_number"));
        Assert.Equal("Chapter three", _world.Canonical(parts[0].AssetId, "track_title"));
    }

    // ----- helpers -------------------------------------------------------------------------------------------------------------------

    private (Guid BestOf, Guid Song, Guid OkComputer) RenamedSongAndAlbumWithAnExistingEntry()
    {
        var bestOf = _world.AddParent("Music", "The Best Of");
        var (song, _, _) = _world.AddChild(bestOf, "Music", "Paranoid Android", 3);
        _world.Execute("UPDATE works SET display_overrides_json = '{\"title\":\"My own title\"}' WHERE id = @song;", new { song });
        var okComputer = _world.AddParent("Music", "OK Computer", manifestJson: LuckyManifest);
        _world.AddChild(okComputer, "Music", "Paranoid Android", 2, hasFile: false, catalogOnly: true);
        return (bestOf, song, okComputer);
    }

    private async Task<LibraryMovePreview> Preview(IReadOnlyList<Guid> items, Guid destination)
    {
        var result = await _world.Service.PreviewAsync(
            new LibraryMovePreviewRequest { ItemIds = items, DestinationId = destination },
            LibraryMoveTestWorld.Actor,
            LibraryMoveTestWorld.Everything);
        Assert.True(result.IsSuccess, result.Refusal?.Message);
        return result.Value!;
    }

    private Task<LibraryMoveResult<LibraryMoveCommitResult>> Commit(LibraryMovePreview preview, params LibraryMoveCommitRow[] rows) =>
        _world.Service.CommitAsync(
            new LibraryMoveCommitRequest { PreviewToken = preview.PreviewToken, Rows = rows },
            LibraryMoveTestWorld.Actor,
            LibraryMoveTestWorld.Everything);

    private static LibraryMoveCommitRow Match(Guid item, string entryKey) =>
        new() { ItemId = item, Mode = LibraryMoveModes.Match, EntryKey = entryKey };

    private static LibraryMoveCommitRow AddAnyway(Guid item) =>
        new() { ItemId = item, Mode = LibraryMoveModes.AddAnyway };
}
