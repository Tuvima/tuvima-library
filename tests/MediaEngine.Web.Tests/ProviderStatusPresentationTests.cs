using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

public sealed class ProviderStatusPresentationTests
{
    [Fact]
    public void SuccessfulConnectionCheckSupersedesOlderRuntimeFailure()
    {
        var status = new ProviderStatusDto
        {
            Enabled = true,
            HealthStatus = "Down",
            LastFailureAt = "2026-09-29T12:00:00Z",
            ConnectionStatus = "valid",
            ConnectionCheckedAt = "2026-09-29T13:00:00Z",
        };

        Assert.Equal("Connected", MetadataSettingsStateService.HealthLabel(status));
    }

    [Fact]
    public void NewerRuntimeFailureStillShowsUnavailable()
    {
        var status = new ProviderStatusDto
        {
            Enabled = true,
            HealthStatus = "Down",
            LastFailureAt = "2026-09-29T14:00:00Z",
            ConnectionStatus = "valid",
            ConnectionCheckedAt = "2026-09-29T13:00:00Z",
        };

        Assert.Equal("Unavailable", MetadataSettingsStateService.HealthLabel(status));
    }
}
