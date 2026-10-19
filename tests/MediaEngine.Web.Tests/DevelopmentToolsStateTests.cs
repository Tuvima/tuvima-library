using MediaEngine.Contracts.System;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Dashboard asks the Engine once which development-only Settings pages it offers, keeps a clear answer, and treats
/// anything else as "not available" while still asking again next time.
/// </summary>
public sealed class DevelopmentToolsStateTests
{
    [Fact]
    public void BeforeTheEngineHasAnswered_NeitherPageIsOffered()
    {
        var (state, _) = Create(new DevelopmentToolsResponse(true, true));

        Assert.False(state.EnrichmentTester);
        Assert.False(state.DevelopmentData);
    }

    [Fact]
    public async Task ADevelopmentEnginesAnswer_IsKept_AndAskedForOnlyOnce()
    {
        var (state, calls) = Create(new DevelopmentToolsResponse(EnrichmentTester: true, DevelopmentData: true));

        await state.EnsureLoadedAsync();
        await state.EnsureLoadedAsync();

        Assert.True(state.EnrichmentTester);
        Assert.True(state.DevelopmentData);
        Assert.Single(calls);
    }

    [Fact]
    public async Task AReleaseEnginesAnswer_OffersNeitherPage()
    {
        var (state, _) = Create(new DevelopmentToolsResponse(EnrichmentTester: false, DevelopmentData: false));

        await state.EnsureLoadedAsync();

        Assert.False(state.EnrichmentTester);
        Assert.False(state.DevelopmentData);
    }

    [Fact]
    public async Task AnEngineThatCannotBeAsked_OffersNeitherPage_AndIsAskedAgainNextTime()
    {
        DevelopmentToolsResponse? answer = null;
        var (state, calls) = Create(() => answer);

        await state.EnsureLoadedAsync();
        Assert.False(state.EnrichmentTester);
        Assert.False(state.DevelopmentData);

        answer = new DevelopmentToolsResponse(EnrichmentTester: true, DevelopmentData: false);
        await state.EnsureLoadedAsync();

        Assert.Equal(2, calls.Count);
        Assert.True(state.EnrichmentTester);
        Assert.False(state.DevelopmentData);
    }

    [Fact]
    public async Task AnEngineThatThrows_OffersNeitherPage_InsteadOfFailingTheSettingsPage()
    {
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetDevelopmentToolsAsync),
                _ => throw new HttpRequestException("The Engine is unreachable.")));
        var state = new DevelopmentToolsState(api);

        await state.EnsureLoadedAsync();

        Assert.False(state.EnrichmentTester);
        Assert.False(state.DevelopmentData);
    }

    private static (DevelopmentToolsState State, List<string> Calls) Create(DevelopmentToolsResponse? answer) =>
        Create(() => answer);

    private static (DevelopmentToolsState State, List<string> Calls) Create(Func<DevelopmentToolsResponse?> answer)
    {
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetDevelopmentToolsAsync), _ => Task.FromResult(answer())));
        return (new DevelopmentToolsState(api), ((EngineApiClientStub)(object)api).Calls);
    }
}
