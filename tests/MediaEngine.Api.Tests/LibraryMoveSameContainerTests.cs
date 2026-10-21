using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Contracts.Library;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Matching an item within the place it already sits in (Details overlay D11 and D13): an item added anyway is matched to an entry
/// later, and a numbered item is renumbered by matching it to a different entry. Nothing leaves, nothing is cleaned up, and the
/// taken-slot rules still apply.
/// </summary>
public sealed class LibraryMoveSameContainerTests : IDisposable
{
    private const string Manifest = """
        {"tracks":[
          {"title":"Airbag","track_number":1,"disc_number":1},
          {"title":"Paranoid Android","track_number":2,"disc_number":1},
          {"title":"Hidden Bonus Jam","track_number":4,"disc_number":1},
          {"title":"Lucky","track_number":5,"disc_number":1}]}
        """;

    private readonly LibraryMoveTestWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task AnUnmatchedTrackMatchedToAFreeEntryInItsOwnAlbum_GetsTheNumberAndLosesTheFlag()
    {
        var (album, song) = AlbumWithAnUnmatchedSong();

        var preview = await Preview([song], album);

        Assert.Empty(preview.Warnings);
        var row = Assert.Single(preview.Rows);
        var entry = preview.Entries.Single(candidate => candidate.Title == "Hidden Bonus Jam");
        Assert.Equal(entry.EntryKey, row.SuggestedEntryKey);
        var result = await Commit(preview, Match(song, entry.EntryKey));

        Assert.True(result.IsSuccess, result.Refusal?.Message);
        Assert.Equal(LibraryMoveOutcomes.Matched, Assert.Single(result.Value!.Rows).Outcome);
        Assert.Empty(result.Value.RemovedContainers);
        var state = _world.Work(song);
        Assert.Equal(album, state.Parent);
        Assert.Equal(4, state.Ordinal);
        Assert.Equal(4d, state.OrdinalSort);
        Assert.Equal(0, state.Unmatched);
        Assert.Equal("4", _world.Canonical(song, "track_number"));
        Assert.True(_world.WorkExists(album));
    }

    [Fact]
    public async Task RenumberingATrackToAFreeEntryInTheSameAlbum_Works_WithoutLeavingOrCleaningUpAnything()
    {
        var album = _world.AddParent("Music", "OK Computer", manifestJson: Manifest);
        var (song, _, _) = _world.AddChild(album, "Music", "Untitled demo", 3);

        var preview = await Preview([song], album);

        Assert.Empty(preview.Warnings);
        Assert.DoesNotContain(preview.Entries, entry => entry.TakenByItemId == song && entry.Title == "Lucky");
        var free = preview.Entries.Single(entry => entry.Title == "Lucky");
        var result = await Commit(preview, Match(song, free.EntryKey));

        Assert.True(result.IsSuccess, result.Refusal?.Message);
        Assert.Empty(result.Value!.RemovedContainers);
        var state = _world.Work(song);
        Assert.Equal(album, state.Parent);
        Assert.Equal(5, state.Ordinal);
        Assert.Equal("5", _world.Canonical(song, "track_number"));
    }

    [Fact]
    public async Task RenumberingToATakenEntry_IsRefusedUnlessItIsAddedAsAnotherVersion()
    {
        var album = _world.AddParent("Music", "OK Computer", manifestJson: Manifest);
        var (taken, takenEdition, takenAsset) = _world.AddChild(album, "Music", "Paranoid Android", 2);
        var (song, _, songAsset) = _world.AddChild(album, "Music", "Paranoid Android (live)", 3);

        var preview = await Preview([song], album);
        var entry = preview.Entries.Single(candidate => candidate.TakenByItemId == taken);

        var refused = await Commit(preview, Match(song, entry.EntryKey));
        Assert.Equal(LibraryMoveRefusalKind.Invalid, refused.Refusal!.Kind);
        Assert.Contains("already has a file", refused.Refusal.Message, StringComparison.Ordinal);
        Assert.True(_world.WorkExists(song));

        var added = await Commit(preview, AsVersion(song, entry.EntryKey));

        Assert.True(added.IsSuccess, added.Refusal?.Message);
        Assert.Equal(LibraryMoveOutcomes.AddedAsVersion, Assert.Single(added.Value!.Rows).Outcome);
        Assert.Equal(taken, _world.WorkOfAsset(songAsset!.Value));
        Assert.Equal(taken, _world.WorkOfAsset(takenAsset!.Value));
        Assert.Equal(2, _world.EditionCount(taken));
        Assert.False(_world.WorkExists(song));
        Assert.True(_world.WorkExists(album));
        _ = takenEdition;
    }

    [Fact]
    public async Task AnItemIsNeverOfferedItsOwnEntry_AndAddingAnywayIntoItsOwnPlaceIsRefusedWithAReason()
    {
        var album = _world.AddParent("Music", "OK Computer", manifestJson: Manifest);
        var (song, _, _) = _world.AddChild(album, "Music", "Paranoid Android", 2);

        var preview = await Preview([song], album);
        var own = preview.Entries.Single(entry => entry.TakenByItemId == song);

        Assert.NotEqual(own.EntryKey, Assert.Single(preview.Rows).SuggestedEntryKey);
        var ownEntry = await Commit(preview, Match(song, own.EntryKey));
        var anyway = await Commit(preview, AddAnyway(song));

        Assert.Equal(LibraryMoveRefusalKind.Invalid, ownEntry.Refusal!.Kind);
        Assert.Contains("already is", ownEntry.Refusal.Message, StringComparison.Ordinal);
        Assert.Equal(LibraryMoveRefusalKind.Invalid, anyway.Refusal!.Kind);
        Assert.Contains("would change nothing", anyway.Refusal.Message, StringComparison.Ordinal);
        Assert.Equal(2, _world.Work(song).Ordinal);
    }

    [Fact]
    public async Task MatchingAnUnmatchedTrackWithinItsAlbum_DoesNotWarnThatTheShowOrAlbumWillBeRemoved_EvenWhenItIsTheOnlyOne()
    {
        var album = _world.AddParent("Music", "OK Computer", manifestJson: Manifest);
        var (song, _, _) = _world.AddChild(album, "Music", "Hidden Bonus Jam", null, ordinalSort: null);
        _world.Execute("UPDATE works SET placement_unmatched = 1 WHERE id = @song;", new { song });

        var preview = await Preview([song], album);
        var result = await Commit(preview, Match(song, preview.Entries.Single(entry => entry.Title == "Hidden Bonus Jam").EntryKey));

        Assert.Empty(preview.Warnings);
        Assert.True(result.IsSuccess, result.Refusal?.Message);
        Assert.Empty(result.Value!.RemovedContainers);
        Assert.True(_world.WorkExists(album));
        Assert.Equal(4, _world.Work(song).Ordinal);
    }

    [Fact]
    public async Task AnUnmatchedAudiobookPartMatchedWithinItsOwnAudiobook_GetsThePartNumber_EvenWhenTheAudiobookIsRenamed()
    {
        var (audiobook, parts) = _world.AddAudiobook("Dune (audiobook)", [(1, "Part one")], partCount: 3);
        _world.Execute("UPDATE works SET display_overrides_json = '{\"title\":\"My own title\"}' WHERE id = @audiobook;", new { audiobook });
        var (loose, _) = (_world.AddPart(audiobook, null, "Loose chapter"), 0);
        _world.Execute("UPDATE editions SET placement_unmatched = 1 WHERE id = @edition;", new { edition = loose.EditionId });

        var preview = await Preview([loose.EditionId], audiobook);

        Assert.Empty(preview.Warnings);
        Assert.Null(Assert.Single(preview.Rows).BlockedReason);
        var free = preview.Entries.Single(entry => entry.Number == "2");
        var result = await Commit(preview, Match(loose.EditionId, free.EntryKey));

        Assert.True(result.IsSuccess, result.Refusal?.Message);
        Assert.Equal(audiobook, _world.WorkOfAsset(loose.AssetId));
        Assert.Equal("2", _world.Canonical(loose.AssetId, "audiobook_part_number"));
        Assert.Equal(0, _world.Scalar<long>("SELECT placement_unmatched FROM editions WHERE id = @edition;", new { edition = loose.EditionId }));
        Assert.True(_world.WorkExists(audiobook));
        Assert.Single(parts);
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private (Guid Album, Guid Song) AlbumWithAnUnmatchedSong()
    {
        var album = _world.AddParent("Music", "OK Computer", manifestJson: Manifest);
        _world.AddChild(album, "Music", "Airbag", 1);
        var (song, _, _) = _world.AddChild(album, "Music", "Hidden Bonus Jam", null, ordinalSort: null);
        _world.Execute("UPDATE works SET placement_unmatched = 1 WHERE id = @song;", new { song });
        return (album, song);
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

    private static LibraryMoveCommitRow AsVersion(Guid item, string entryKey) =>
        new() { ItemId = item, Mode = LibraryMoveModes.AddAsVersion, EntryKey = entryKey };

    private static LibraryMoveCommitRow AddAnyway(Guid item) =>
        new() { ItemId = item, Mode = LibraryMoveModes.AddAnyway };
}
