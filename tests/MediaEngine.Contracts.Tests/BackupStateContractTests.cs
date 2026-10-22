using System.Text.Json;
using MediaEngine.Contracts.Setup;
using MediaEngine.Contracts.System;
using MediaEngine.Domain.Services;

namespace MediaEngine.Contracts.Tests;

public sealed class BackupStateContractTests
{
    [Fact]
    public void BackupState_UsesTheAgreedSnakeCaseNames_AndRoundTrips()
    {
        var restoredAt = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
        var state = new BackupStateDto(4, 90, 7, new LatestRestoreDto(restoredAt, "tuvima-backup-1.zip", 20, 12, 1, 0));

        var json = JsonSerializer.Serialize(state, MediaEngineJson.Web);
        var roundTrip = JsonSerializer.Deserialize<BackupStateDto>(json, MediaEngineJson.Web);

        Assert.Equal(
            "{\"file_only_titles\":4,\"waiting_period_days\":90,\"still_waiting\":7,\"latest_restore\":" +
            "{\"restored_at\":\"2026-10-22T08:00:00+00:00\",\"backup_file_name\":\"tuvima-backup-1.zip\",\"applied\":20,\"attached\":12,\"yielded\":1,\"purged\":0}}",
            json);
        Assert.Equal(state, roundTrip);
    }

    [Fact]
    public void BackupState_WithNoRestore_WritesANullLatestRestore()
    {
        var json = JsonSerializer.Serialize(new BackupStateDto(0, 90, 0, null), MediaEngineJson.Web);

        Assert.Contains("\"latest_restore\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitingPeriodRequest_IsASingleDaysMember()
    {
        Assert.Equal("{\"days\":180}", JsonSerializer.Serialize(new UpdateWaitingPeriodRequest(180), MediaEngineJson.Web));
    }

    [Fact]
    public void BackupContents_AppearOnTheRestoreTestAndOnTheFirstRunInspection_UnderTheSameNames()
    {
        var contents = new BackupContentsDto(2, 31, 120, 80, 5);
        var drill = JsonSerializer.Serialize(
            new RestoreValidationResultDto(true, "a.zip", DateTimeOffset.UnixEpoch, "ok", contents), MediaEngineJson.Web);
        var inspection = JsonSerializer.Serialize(
            new SetupBackupInspectionDto(Guid.Empty, "a.zip", null, "3.0", "epoch", 1, 2, 3, 4, "warning", contents), MediaEngineJson.Web);

        const string expected = "\"contents\":{\"profiles\":2,\"configuration_rows\":31,\"personal_items\":120,\"titles\":80,\"file_only_titles\":5}";
        Assert.Contains(expected, drill, StringComparison.Ordinal);
        Assert.Contains(expected, inspection, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderAnswerWithoutContents_StillReadsWithNullContents()
    {
        var drill = JsonSerializer.Deserialize<RestoreValidationResultDto>(
            "{\"valid\":true,\"file_name\":\"a.zip\",\"validated_at\":\"2026-10-22T00:00:00+00:00\",\"message\":\"ok\"}", MediaEngineJson.Web);

        Assert.Null(Assert.IsType<RestoreValidationResultDto>(drill).Contents);
    }
}
