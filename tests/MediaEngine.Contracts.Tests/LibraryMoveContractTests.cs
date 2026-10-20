using System.Text.Json;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Library;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of moving items into another album, season, series, audiobook or collection (Details overlay, WP5).</summary>
public sealed class LibraryMoveContractTests
{
    [Fact]
    public void PreviewRequest_UsesSnakeCaseNames_AndTheSourceCollectionIsOptional()
    {
        var item = Guid.NewGuid();
        var destination = Guid.NewGuid();

        var json = JsonSerializer.Serialize(new LibraryMovePreviewRequest { ItemIds = [item], DestinationId = destination });
        var roundTrip = JsonSerializer.Deserialize<LibraryMovePreviewRequest>(json)!;

        Assert.Equal(["item_ids", "destination_id", "source_collection_id"], PropertyNames(json));
        Assert.Equal([item], roundTrip.ItemIds);
        Assert.Equal(destination, roundTrip.DestinationId);
        Assert.Null(roundTrip.SourceCollectionId);
        Assert.Contains("\"source_collection_id\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_CarriesTheDestinationListEntriesRowsWarningsAndToken_WithTheDocumentedNames()
    {
        var preview = new LibraryMovePreview
        {
            Destination = new LibraryMoveDestination { Id = Guid.NewGuid(), Kind = LibraryMoveDestinationKinds.Album, Label = "OK Computer", ParentLabel = "Radiohead", HasList = true },
            ListKind = LibraryMoveListKinds.Tracks,
            Entries = [new LibraryMoveEntry { EntryKey = "track:1:2", Number = "2", Title = "Paranoid Android", LengthSeconds = 387, TakenByItemId = null, TakenByTitle = null }],
            Rows = [new LibraryMovePreviewRow { ItemId = Guid.NewGuid(), Title = "Paranoid Android", CurrentPlacementLabel = "The Best Of, track 3", SuggestedEntryKey = "track:1:2", SuggestionReason = "Same title" }],
            Warnings = ["Leaves The Best Of"],
            PreviewToken = "abc",
        };

        var json = JsonSerializer.Serialize(preview);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(["destination", "list_kind", "entries", "rows", "warnings", "preview_token"], PropertyNames(json));
        Assert.Equal(["id", "kind", "label", "parent_label", "has_list"], root.GetProperty("destination").EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["entry_key", "number", "title", "length_seconds", "taken_by_item_id", "taken_by_title"],
            root.GetProperty("entries")[0].EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["item_id", "title", "current_placement_label", "suggested_entry_key", "suggestion_reason", "file_change", "file_note", "blocked_reason"],
            root.GetProperty("rows")[0].EnumerateObject().Select(property => property.Name));
        // The file change and note are filled in when moves also rename and move files; until then they are null, never absent.
        Assert.Equal(JsonValueKind.Null, root.GetProperty("rows")[0].GetProperty("file_change").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("rows")[0].GetProperty("file_note").ValueKind);
        Assert.Equal("tracks", root.GetProperty("list_kind").GetString());
    }

    [Fact]
    public void FileChange_IsAFromAndToPair()
    {
        var json = JsonSerializer.Serialize(new LibraryMoveFileChange { From = "01 - Old.flac", To = "02 - Paranoid Android.flac" });

        Assert.Equal(["from", "to"], PropertyNames(json));
    }

    [Fact]
    public void CommitRequestAndResult_RoundTrip_WithTheDocumentedNames()
    {
        var item = Guid.NewGuid();
        var request = new LibraryMoveCommitRequest
        {
            PreviewToken = "abc",
            Rows = [new LibraryMoveCommitRow { ItemId = item, Mode = LibraryMoveModes.AddAsVersion, EntryKey = "work:1" }],
        };
        var result = new LibraryMoveCommitResult
        {
            MovedCount = 1,
            Rows = [new LibraryMoveRowResult { ItemId = item, Outcome = LibraryMoveOutcomes.AddedAsVersion }],
            RemovedContainers = ["Season 2"],
        };

        var requestJson = JsonSerializer.Serialize(request);
        var resultJson = JsonSerializer.Serialize(result);
        var roundTrip = JsonSerializer.Deserialize<LibraryMoveCommitRequest>(requestJson)!;

        Assert.Equal(["preview_token", "rows"], PropertyNames(requestJson));
        Assert.Equal(["item_id", "mode", "entry_key"], PropertyNames(JsonSerializer.Serialize(request.Rows[0])));
        Assert.Equal("add_as_version", roundTrip.Rows[0].Mode);
        Assert.Equal("work:1", roundTrip.Rows[0].EntryKey);
        Assert.Equal(["moved_count", "rows", "removed_containers"], PropertyNames(resultJson));
        Assert.Equal(["item_id", "outcome"], PropertyNames(JsonSerializer.Serialize(result.Rows[0])));
    }

    [Fact]
    public void TheConflictBody_CarriesTheFreshPreview()
    {
        var json = JsonSerializer.Serialize(new LibraryMoveConflictDto { Message = "Something changed", Preview = new LibraryMovePreview { PreviewToken = "fresh" } });

        Assert.Equal(["message", "preview"], PropertyNames(json));
    }

    [Fact]
    public void DestinationsResponse_IsAListOfPlacesWrappedByName()
    {
        var json = JsonSerializer.Serialize(new LibraryMoveDestinationsResponse
        {
            Destinations = [new LibraryMoveDestination { Id = Guid.NewGuid(), Kind = LibraryMoveDestinationKinds.Collection, Label = "Road trip", HasList = false }],
        });

        Assert.Equal(["destinations"], PropertyNames(json));
    }

    [Fact]
    public void TheWireStrings_AreTheDocumentedOnes()
    {
        Assert.Equal(["album", "season", "series", "audiobook", "collection"],
            [LibraryMoveDestinationKinds.Album, LibraryMoveDestinationKinds.Season, LibraryMoveDestinationKinds.Series, LibraryMoveDestinationKinds.Audiobook, LibraryMoveDestinationKinds.Collection]);
        Assert.Equal(["tracks", "episodes", "positions", "parts", "none"],
            [LibraryMoveListKinds.Tracks, LibraryMoveListKinds.Episodes, LibraryMoveListKinds.Positions, LibraryMoveListKinds.Parts, LibraryMoveListKinds.None]);
        Assert.Equal(["match", "add_anyway", "add_as_version"],
            [LibraryMoveModes.Match, LibraryMoveModes.AddAnyway, LibraryMoveModes.AddAsVersion]);
        Assert.Equal(["matched", "added_as_version", "added_unmatched", "moved"],
            [LibraryMoveOutcomes.Matched, LibraryMoveOutcomes.AddedAsVersion, LibraryMoveOutcomes.AddedUnmatched, LibraryMoveOutcomes.Moved]);
    }

    [Fact]
    public void TheDetailPageAndItsChildRows_CarryTheNotMatchedFlag_FalseByDefault()
    {
        Assert.False(new DetailPageViewModel().IsUnmatched);
        Assert.False(new SequenceItemViewModel().IsUnmatched);
        Assert.False(new MediaGroupingItemViewModel().IsUnmatched);

        var json = JsonSerializer.Serialize(new SequenceItemViewModel { IsUnmatched = true });

        Assert.Contains("\"IsUnmatched\":true", json, StringComparison.Ordinal);
    }

    private static List<string> PropertyNames(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateObject().Select(property => property.Name)];
    }
}
