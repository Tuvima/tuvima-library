using System.Text.Json;
using MediaEngine.Contracts.System;
using MediaEngine.Domain.Services;

namespace MediaEngine.Contracts.Tests;

public sealed class DevelopmentToolsContractTests
{
    [Fact]
    public void DevelopmentToolsResponse_UsesTheAgreedSnakeCaseNames_AndRoundTrips()
    {
        var response = new DevelopmentToolsResponse(EnrichmentTester: true, DevelopmentData: false);

        var json = JsonSerializer.Serialize(response, MediaEngineJson.Web);
        var roundTrip = JsonSerializer.Deserialize<DevelopmentToolsResponse>(json, MediaEngineJson.Web);

        Assert.Equal("{\"enrichment_tester\":true,\"development_data\":false}", json);
        Assert.Equal(response, roundTrip);
    }

    [Fact]
    public void DevelopmentToolsResponse_IsNotPartOfThePublicSystemStatus()
    {
        var status = JsonSerializer.Serialize(new SystemStatusResponse { Status = "ok", Version = "1" }, MediaEngineJson.Web);

        Assert.DoesNotContain("enrichment_tester", status, StringComparison.Ordinal);
        Assert.DoesNotContain("development_data", status, StringComparison.Ordinal);
    }
}
