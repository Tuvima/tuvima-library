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

        Assert.StartsWith("{\"preview_only\":true,\"changes\":[{\"id\":\"c1\",\"kind\":\"details\",\"entity_id\":\"" + item.ToString("D") + "\",\"fields\":[{\"key\":\"title\",\"value\":\"Orbital\"}],\"target_id\":null,\"slot\":null,\"token\":null}", json, StringComparison.Ordinal);
        Assert.True(roundTrip.PreviewOnly);
        Assert.Equal(2, roundTrip.Changes.Count);
        Assert.Equal("t", roundTrip.Changes[1].Token);
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
