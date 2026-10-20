using System.Text.Json;
using MediaEngine.Contracts.Activity;
using MediaEngine.Contracts.Ingestion;

namespace MediaEngine.Contracts.Tests;

public sealed class OperationsIngestionContractTests
{
    [Fact]
    public void ActivityContracts_DoNotLeakDashboardRouteOrRelativeTimeHelpers()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var entryJson = JsonSerializer.Serialize(
            new ActivityEntryResponse
            {
                OccurredAt = "2025-01-02T03:04:05Z",
                ActionType = "MediaAdded",
            },
            options);

        Assert.DoesNotContain("relativeTime", entryJson, StringComparison.Ordinal);
    }

    [Fact]
    public void IngestionContracts_CarryNoBatchOrRunIdentifier()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var groupJson = JsonSerializer.Serialize(new IngestionMediaGroupDto { GroupId = Guid.NewGuid() }, options);
        var snapshotJson = JsonSerializer.Serialize(new IngestionPresentationSnapshotDto(), options);
        var entryJson = JsonSerializer.Serialize(new ActivityEntryResponse(), options);

        Assert.DoesNotContain("batch", groupJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("batch", snapshotJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("run_id", entryJson, StringComparison.OrdinalIgnoreCase);
    }
}
