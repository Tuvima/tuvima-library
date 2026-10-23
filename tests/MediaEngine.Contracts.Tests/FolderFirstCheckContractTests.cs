using System.Text.Json;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Settings;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shape of a new folder's first check (Library setup spec D35): an additive field on each source, and the administrator's list.</summary>
public sealed class FolderFirstCheckContractTests
{
    [Fact]
    public void ASourceWithNoCheck_LeavesFirstCheckOutOfItsJson_SoOlderReadersSeeNothingNew()
    {
        var json = JsonSerializer.Serialize(new LibrarySourceDto { Id = "source-a", Path = "D:/Media/Movies" });

        Assert.DoesNotContain("first_check", json, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<LibrarySourceDto>(json)!.FirstCheck);
    }

    [Fact]
    public void ASourceWithAPausedCheck_CarriesTheStateTheCountsAndTheTotal_InSnakeCase()
    {
        var source = new LibrarySourceDto
        {
            Id = "source-a",
            FirstCheck = new FolderFirstCheckDto { State = "paused", Tried = 50, Unmatched = 30, TotalFiles = 300 },
        };

        var json = JsonSerializer.Serialize(source);
        var roundTrip = JsonSerializer.Deserialize<LibrarySourceDto>(json)!.FirstCheck!;

        Assert.Contains("\"first_check\":{\"state\":\"paused\",\"tried\":50,\"unmatched\":30,\"total_files\":300}", json, StringComparison.Ordinal);
        Assert.Equal(("paused", 50, 30, 300), (roundTrip.State, roundTrip.Tried, roundTrip.Unmatched, roundTrip.TotalFiles));
    }

    [Fact]
    public void TheAdministratorsList_NamesTheLibraryTheFolderThePathTheStateAndTheCounts()
    {
        var json = JsonSerializer.Serialize(new FolderFirstChecksDto
        {
            Items =
            [
                new FolderFirstCheckItemDto
                {
                    LibraryId = "movies", SourceId = "source-a", Path = "D:/Media/Movies", State = "checking", Tried = 12, Unmatched = 3, TotalFiles = 40,
                },
            ],
        });

        Assert.Equal(
            "{\"items\":[{\"library_id\":\"movies\",\"source_id\":\"source-a\",\"path\":\"D:/Media/Movies\",\"state\":\"checking\",\"tried\":12,\"unmatched\":3,\"total_files\":40}]}",
            json);
    }
}
