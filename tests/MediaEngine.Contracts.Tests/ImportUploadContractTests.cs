using System.Text.Json;
using MediaEngine.Contracts.Imports;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of resumable uploads into the Import folder (Library setup spec D44, WP12a).</summary>
public sealed class ImportUploadContractTests
{
    [Fact]
    public void StartRequest_UsesSnakeCaseNames_AndTheLibraryIsOptional()
    {
        var json = JsonSerializer.Serialize(new ImportUploadStartRequest { KindKey = "movies", FileName = "Film.mkv", TotalBytes = 5_000_000_000 });
        var roundTrip = JsonSerializer.Deserialize<ImportUploadStartRequest>(json)!;

        Assert.Equal(["kind_key", "library_id", "file_name", "total_bytes"], PropertyNames(json));
        Assert.Contains("\"library_id\":null", json, StringComparison.Ordinal);
        Assert.Equal("movies", roundTrip.KindKey);
        Assert.Null(roundTrip.LibraryId);
        Assert.Equal("Film.mkv", roundTrip.FileName);
        Assert.Equal(5_000_000_000, roundTrip.TotalBytes);
    }

    [Fact]
    public void StartResponse_CarriesTheUploadIdAndPieceSize_WithTheDocumentedNames()
    {
        var id = Guid.NewGuid();

        var json = JsonSerializer.Serialize(new ImportUploadStartResponse { UploadId = id, PieceBytes = 67_108_864, PieceCount = 75, TotalBytes = 5_000_000_000 });

        Assert.Equal(["upload_id", "piece_bytes", "piece_count", "total_bytes"], PropertyNames(json));
        Assert.Contains($"\"upload_id\":\"{id:D}\"", json, StringComparison.Ordinal);
        Assert.Equal(67_108_864, JsonSerializer.Deserialize<ImportUploadStartResponse>(json)!.PieceBytes);
    }

    [Fact]
    public void Status_ListsTheReceivedPieces_AndStartsOpen()
    {
        var status = new ImportUploadStatusResponse { UploadId = Guid.NewGuid(), FileName = "Film.mkv", ReceivedPieces = [0, 1, 3] };

        var json = JsonSerializer.Serialize(status);
        var roundTrip = JsonSerializer.Deserialize<ImportUploadStatusResponse>(json)!;

        Assert.Equal(["upload_id", "state", "file_name", "total_bytes", "piece_bytes", "piece_count", "received_pieces", "received_bytes"], PropertyNames(json));
        Assert.Contains("\"state\":\"open\"", json, StringComparison.Ordinal);
        Assert.Contains("\"received_pieces\":[0,1,3]", json, StringComparison.Ordinal);
        Assert.Equal([0, 1, 3], roundTrip.ReceivedPieces);
    }

    [Fact]
    public void CompleteResponse_CarriesTheSavedNameWhetherItWasRenamed_AndWhereItLanded()
    {
        var json = JsonSerializer.Serialize(new ImportUploadCompleteResponse
        {
            UploadId = Guid.NewGuid(),
            FileName = "Film (1).mkv",
            Renamed = true,
            DestinationPath = "/import/Movies/Film (1).mkv",
        });

        Assert.Equal(["upload_id", "file_name", "renamed", "destination_path"], PropertyNames(json));
        Assert.Contains("\"renamed\":true", json, StringComparison.Ordinal);
        Assert.Equal("/import/Movies/Film (1).mkv", JsonSerializer.Deserialize<ImportUploadCompleteResponse>(json)!.DestinationPath);
    }

    [Fact]
    public void TheStatesAndRefusalCodes_AreTheStableWordsTheDashboardReads()
    {
        Assert.Equal(["open", "completed", "cancelled"], new[] { ImportUploadStates.Open, ImportUploadStates.Completed, ImportUploadStates.Cancelled });
        Assert.Equal("no_import_folder", ImportUploadRefusalCodes.NoImportFolder);
        Assert.Equal("no_managed_destination", ImportUploadRefusalCodes.NoManagedDestination);
        Assert.Equal("vault_not_available", ImportUploadRefusalCodes.VaultNotAvailable);
        Assert.Equal("destination_unavailable", ImportUploadRefusalCodes.DestinationUnavailable);
        Assert.Equal("not_enough_space", ImportUploadRefusalCodes.NotEnoughSpace);
        Assert.Equal("too_large", ImportUploadRefusalCodes.TooLarge);
        Assert.Equal("drag_and_drop_off", ImportUploadRefusalCodes.DragAndDropOff);
        Assert.Equal("unknown_kind", ImportUploadRefusalCodes.UnknownKind);
        Assert.Equal("unknown_library", ImportUploadRefusalCodes.UnknownLibrary);
        Assert.Equal("invalid_file_name", ImportUploadRefusalCodes.InvalidFileName);
        Assert.Equal("invalid_size", ImportUploadRefusalCodes.InvalidSize);
        Assert.Equal("invalid_piece", ImportUploadRefusalCodes.InvalidPiece);
        Assert.Equal("upload_not_found", ImportUploadRefusalCodes.UploadNotFound);
        Assert.Equal("upload_closed", ImportUploadRefusalCodes.UploadClosed);
        Assert.Equal("pieces_missing", ImportUploadRefusalCodes.PiecesMissing);
        Assert.Equal("size_mismatch", ImportUploadRefusalCodes.SizeMismatch);
    }

    private static string[] PropertyNames(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateObject().Select(property => property.Name)];
    }
}
