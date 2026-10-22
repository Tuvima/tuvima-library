using MediaEngine.Api.Services.Settings;

namespace MediaEngine.Api.Tests;

public sealed class ProviderEndpointPinningTests
{
    private static readonly Dictionary<string, string> Saved = new() { ["api"] = "https://stashdb.org/graphql" };

    [Theory]
    [InlineData("https://evil.example/graphql")]
    [InlineData("http://127.0.0.1:9999/graphql")]
    [InlineData("")]
    public void ChangingTheStashBoxAddress_IsRefused_SoTheSavedKeyNeverMovesToAnotherHost(string address) =>
        Assert.True(ProviderEndpointPinning.ChangesPinnedEndpoint("stashbox", Saved, new Dictionary<string, string> { ["api"] = address }));

    [Fact]
    public void AddingAnEndpointTheProviderDoesNotHave_IsAlsoAChange() =>
        Assert.True(ProviderEndpointPinning.ChangesPinnedEndpoint("StashBox", Saved, new Dictionary<string, string> { ["images"] = "https://evil.example" }));

    [Theory]
    [InlineData("https://stashdb.org/graphql")]
    [InlineData("HTTPS://STASHDB.ORG/graphql/")]
    public void ResendingTheSavedAddress_IsNotAChange(string address) =>
        Assert.False(ProviderEndpointPinning.ChangesPinnedEndpoint("stashbox", Saved, new Dictionary<string, string> { ["api"] = address }));

    [Fact]
    public void LeavingTheEndpointsOut_IsNotAChange() =>
        Assert.False(ProviderEndpointPinning.ChangesPinnedEndpoint("stashbox", Saved, null));

    [Fact]
    public void OtherProvidersKeepEditingTheirEndpoints() =>
        Assert.False(ProviderEndpointPinning.ChangesPinnedEndpoint("tmdb", Saved, new Dictionary<string, string> { ["api"] = "https://example.org" }));

    [Fact]
    public void TheProviderConfigRoute_ChecksThePinBeforeItChangesAnything()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "Endpoints", "SettingsEndpoints.cs"));
        var routeStart = source.IndexOf("grp.MapPut(\"/providers/{name}/config\"", StringComparison.Ordinal);
        var check = source.IndexOf("ProviderEndpointPinning.ChangesPinnedEndpoint(", routeStart, StringComparison.Ordinal);
        var firstChange = source.IndexOf("existing.Enabled = request.Enabled.Value;", routeStart, StringComparison.Ordinal);

        Assert.True(routeStart >= 0 && check > routeStart && check < firstChange);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
