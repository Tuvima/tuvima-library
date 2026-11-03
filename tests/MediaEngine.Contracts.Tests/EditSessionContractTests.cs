using System.Text.Json;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of the universal editor's edit session: the pending list, the check and the per-change results.</summary>
public sealed class EditSessionContractTests
{
    [Fact]
    public void Request_UsesSnakeCaseNames_AndRoundTripsAMixedList()
    {
        var item = Guid.NewGuid();
        var request = new EditSessionRequest
        {
            PreviewOnly = true,
            Changes =
            [
                new EditSessionChange
                {
                    Id = "c1",
                    Kind = EditSessionChangeKinds.Details,
                    EntityId = item,
                    Fields = [new EditSessionFieldChange { Key = "title", Value = "Orbital" }],
                },
                new EditSessionChange { Id = "c2", Kind = EditSessionChangeKinds.FileMove, EntityId = item, Token = "t" },
            ],
        };

        var json = JsonSerializer.Serialize(request);
        var roundTrip = JsonSerializer.Deserialize<EditSessionRequest>(json)!;

        Assert.StartsWith("{\"preview_only\":true,\"changes\":[{\"id\":\"c1\",\"kind\":\"details\",\"entity_id\":\"" + item.ToString("D") + "\",\"fields\":[{\"key\":\"title\",\"value\":\"Orbital\"}],\"target_id\":null,\"slot\":null,\"token\":null,\"ordered_ids\":[]}", json, StringComparison.Ordinal);
        Assert.True(roundTrip.PreviewOnly);
        Assert.Equal(2, roundTrip.Changes.Count);
        Assert.Equal("t", roundTrip.Changes[1].Token);
    }

    [Fact]
    public void VideoOrderChange_CarriesTheOrderedIdsByTheirWireName_AndTheNewKindsAreKnown()
    {
        var parent = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var change = new EditSessionChange { Id = "o1", Kind = EditSessionChangeKinds.VideoOrder, EntityId = parent, OrderedIds = [second, first] };

        var json = JsonSerializer.Serialize(change);
        var roundTrip = JsonSerializer.Deserialize<EditSessionChange>(json)!;

        Assert.Contains("\"kind\":\"video_order\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ordered_ids\":[\"" + second.ToString("D") + "\",\"" + first.ToString("D") + "\"]", json, StringComparison.Ordinal);
        Assert.Equal([second, first], roundTrip.OrderedIds);
        Assert.All(
            new[] { "video_series", "video_order", "video_rename" },
            kind => Assert.True(EditSessionChangeKinds.IsKnown(kind)));
    }

    [Fact]
    public void VideoSeriesTargets_AreReadFromTheTokenOrTheTargetId()
    {
        Assert.Equal("new:Garden talks", EditSessionVideo.NewSeriesToken("  Garden talks "));
        Assert.True(EditSessionVideo.TryParseNewSeries("new:Garden talks", out var name));
        Assert.Equal("Garden talks", name);
        Assert.False(EditSessionVideo.TryParseNewSeries("new:  ", out _));
        Assert.False(EditSessionVideo.TryParseNewSeries("none", out _));
        Assert.True(EditSessionVideo.IsNoSeries(EditSessionVideo.NoSeriesToken));
    }

    [Fact]
    public void Result_UsesSnakeCaseNames()
    {
        var item = Guid.NewGuid();
        var result = new EditSessionResult
        {
            Valid = true,
            DataSaved = true,
            Changes = [new EditSessionChangeResult { Id = "c1", Kind = EditSessionChangeKinds.Details, EntityId = item, Status = EditSessionChangeStatuses.Ok }],
        };

        var json = JsonSerializer.Serialize(result);

        Assert.Equal(
            "{\"preview_only\":false,\"valid\":true,\"data_saved\":true,\"changes\":[{\"id\":\"c1\",\"kind\":\"details\",\"entity_id\":\"" + item.ToString("D") + "\",\"status\":\"ok\",\"reason\":null}]}",
            json);
    }

    [Fact]
    public void Kinds_SeparateDataChangesFromFileMoves()
    {
        Assert.All(
            new[] { EditSessionChangeKinds.Details, EditSessionChangeKinds.Identity, EditSessionChangeKinds.Relationship, EditSessionChangeKinds.Artwork },
            kind => Assert.True(EditSessionChangeKinds.IsDataChange(kind)));
        Assert.False(EditSessionChangeKinds.IsDataChange(EditSessionChangeKinds.FileMove));
        Assert.False(EditSessionChangeKinds.IsKnown("delete"));
    }
}
