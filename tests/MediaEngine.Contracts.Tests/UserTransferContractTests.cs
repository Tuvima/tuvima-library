using System.Text.Json;
using MediaEngine.Contracts.Backups;
using MediaEngine.Domain.Services;

namespace MediaEngine.Contracts.Tests;

public sealed class UserTransferContractTests
{
    [Fact]
    public void UserImportPreview_UsesTheAgreedSnakeCaseNames_AndRoundTrips()
    {
        var preview = new UserImportPreviewDto(
            Guid.Parse("11111111-2222-3333-4444-555555555555"), "maya@example.com", "The Khans", ["Maya", "Maya (kids)"], 12, null);

        var json = JsonSerializer.Serialize(preview, MediaEngineJson.Web);
        var roundTrip = JsonSerializer.Deserialize<UserImportPreviewDto>(json, MediaEngineJson.Web);

        Assert.Equal(
            "{\"operation_id\":\"11111111-2222-3333-4444-555555555555\",\"email\":\"maya@example.com\",\"household_name\":\"The Khans\"," +
            "\"profiles\":[\"Maya\",\"Maya (kids)\"],\"personal_items\":12,\"blocked_reason\":null}",
            json);
        Assert.Equal(preview.Email, roundTrip!.Email);
        Assert.Equal(preview.Profiles, roundTrip.Profiles);
        Assert.Null(roundTrip.BlockedReason);
    }

    [Fact]
    public void UserImportPreview_ThatIsBlocked_CarriesOneSentenceAndNoHousehold()
    {
        var json = JsonSerializer.Serialize(
            new UserImportPreviewDto(Guid.Empty, "maya@example.com", null, ["Maya"], 0, "Someone in this Library already signs in with maya@example.com. Nothing was imported."),
            MediaEngineJson.Web);

        Assert.Contains("\"household_name\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"blocked_reason\":\"Someone in this Library already signs in with maya@example.com. Nothing was imported.\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void UserImportResult_UsesTheAgreedSnakeCaseNames_AndRoundTrips()
    {
        var result = new UserImportResultDto(Guid.Parse("11111111-2222-3333-4444-555555555555"), "maya@example.com", ["Maya"], 4);

        var json = JsonSerializer.Serialize(result, MediaEngineJson.Web);
        var roundTrip = JsonSerializer.Deserialize<UserImportResultDto>(json, MediaEngineJson.Web);

        Assert.Equal(
            "{\"account_id\":\"11111111-2222-3333-4444-555555555555\",\"display_name\":\"maya@example.com\",\"profiles\":[\"Maya\"],\"waiting_items\":4}",
            json);
        Assert.Equal(result.AccountId, roundTrip!.AccountId);
        Assert.Equal(4, roundTrip.WaitingItems);
    }
}
