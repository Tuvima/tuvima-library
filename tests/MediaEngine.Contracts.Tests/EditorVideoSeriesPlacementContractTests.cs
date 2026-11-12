using System.Text.Json;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Items;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire names of the series-move preview and of the series state a video's chooser reads (universal editor v6, D17 and D24).</summary>
public sealed class EditorVideoSeriesPlacementContractTests
{
    [Fact]
    public void Placement_UsesSnakeCaseNames_AndLeavesOutWhatIsNotSet()
    {
        var moves = new EditorVideoSeriesPlacementDto { Managed = true, FromPath = "/v/a.mp4", ToPath = "/v/Series/a.mp4" };
        var stays = new EditorVideoSeriesPlacementDto { Reason = "That series has no folder of its own yet, so the file stays where it is." };

        var movesJson = JsonSerializer.Serialize(moves);
        var staysJson = JsonSerializer.Serialize(stays);

        Assert.Contains("\"managed\":true", movesJson, StringComparison.Ordinal);
        Assert.Contains("\"from_path\":\"/v/a.mp4\"", movesJson, StringComparison.Ordinal);
        Assert.Contains("\"to_path\":\"/v/Series/a.mp4\"", movesJson, StringComparison.Ordinal);
        Assert.DoesNotContain("reason", movesJson, StringComparison.Ordinal);
        Assert.Contains("\"managed\":false", staysJson, StringComparison.Ordinal);
        Assert.Contains("\"reason\":", staysJson, StringComparison.Ordinal);
        Assert.DoesNotContain("to_path", staysJson, StringComparison.Ordinal);
        Assert.Equal("/v/Series/a.mp4", JsonSerializer.Deserialize<EditorVideoSeriesPlacementDto>(movesJson)!.ToPath);
    }

    [Fact]
    public void PlacementRequest_CarriesTheSameTwoFieldsASeriesChangeDoes()
    {
        var json = JsonSerializer.Serialize(new EditorVideoSeriesPlacementRequestDto { Token = EditSessionVideo.NewSeriesToken("Garden talks") });

        Assert.Equal("{\"token\":\"new:Garden talks\"}", json);
    }

    [Fact]
    public void ASeriesMove_NamesItsSeriesChoice_AndReadsItBack()
    {
        var id = Guid.NewGuid().ToString("D");

        Assert.Equal("with_series:" + id, EditSessionVideo.MovesWithSeries(id, null));
        Assert.Equal("with_series:none", EditSessionVideo.MovesWithSeries(null, EditSessionVideo.NoSeriesToken));
        Assert.Equal("with_series:new:Garden talks", EditSessionVideo.MovesWithSeries(null, EditSessionVideo.NewSeriesToken("Garden talks")));
        Assert.True(EditSessionVideo.IsMovesWithSeries("with_series:none"));
        Assert.False(EditSessionVideo.IsMovesWithSeries("with_series"));
        Assert.False(EditSessionVideo.IsMovesWithSeries(null));
        Assert.True(EditSessionVideo.TryReadMovesWithSeries("with_series:" + id, out var choice));
        Assert.Equal(id, choice);
    }

    [Fact]
    public void Placement_SaysWhenTheChoiceCannotBeSaved_AndLeavesTheFlagOutWhenItIsNot()
    {
        var blocked = JsonSerializer.Serialize(new EditorVideoSeriesPlacementDto { Reason = "x", Blocked = true });
        var fine = JsonSerializer.Serialize(new EditorVideoSeriesPlacementDto { Managed = true });

        Assert.Contains("\"blocked\":true", blocked, StringComparison.Ordinal);
        Assert.DoesNotContain("blocked", fine, StringComparison.Ordinal);
    }

    [Fact]
    public void SeriesState_SaysWhetherTheSeriesIsOnlyWhatTheFolderSays()
    {
        var json = JsonSerializer.Serialize(new VideoSeriesStateResponse { Supported = true, InSeries = true, FromFolder = true });

        Assert.Contains("\"from_folder\":true", json, StringComparison.Ordinal);
        Assert.False(JsonSerializer.Deserialize<VideoSeriesStateResponse>("{\"supported\":true}")!.FromFolder);
    }
}
