using MediaEngine.Api.Services.Networking;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class TailscaleRemoteConnectivityProviderTests
{
    [Fact]
    public async Task ConnectedServeReportsPrivateHttpsAddress()
    {
        var runner = new FakeRunner(
            new CommandResult(0, """
                {"BackendState":"Running","Self":{"DNSName":"tuvima.example.ts.net."}}
                """, string.Empty),
            new CommandResult(0, "{\"Web\":{\"https://tuvima.example.ts.net:443\":{}}}", string.Empty));
        var provider = Create(runner);

        var result = await provider.GetStateAsync(CancellationToken.None);

        Assert.Equal(RemoteProviderState.Connected, result.State);
        Assert.True(result.SecureHttps);
        Assert.Equal("https://tuvima.example.ts.net", result.PublicAddress);
        Assert.Equal(["status --json", "serve status --json"], runner.Calls);
    }

    [Fact]
    public async Task ConnectedWithoutServeIsDegraded()
    {
        var provider = Create(new FakeRunner(
            new CommandResult(0, "{\"BackendState\":\"Running\",\"Self\":{\"DNSName\":\"tuvima.example.ts.net.\"}}", string.Empty),
            new CommandResult(0, "{}", string.Empty)));

        var result = await provider.TestAsync(CancellationToken.None);

        Assert.Equal(RemoteProviderState.Degraded, result.State);
        Assert.False(result.SecureHttps);
        Assert.Contains("not active", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingClientIsReportedWithoutTreatingItAsAProviderFailure()
    {
        var provider = Create(new MissingRunner());

        var result = await provider.GetStateAsync(CancellationToken.None);

        Assert.Equal(RemoteProviderState.Unconfigured, result.State);
        Assert.True(result.NotInstalled);
        Assert.Contains("not installed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignedOutClientIsUnconfiguredButNotReportedAsMissing()
    {
        var provider = Create(new FakeRunner(
            new CommandResult(0, "{\"BackendState\":\"NeedsLogin\"}", string.Empty)));

        var result = await provider.GetStateAsync(CancellationToken.None);

        Assert.Equal(RemoteProviderState.Unconfigured, result.State);
        Assert.False(result.NotInstalled);
    }

    private static TailscaleRemoteConnectivityProvider Create(ICommandRunner runner) => new(
        runner,
        new HttpClient(new NeverHandler()),
        NullLogger<TailscaleRemoteConnectivityProvider>.Instance);

    private sealed class FakeRunner(params CommandResult[] results) : ICommandRunner
    {
        private int _index;
        public List<string> Calls { get; } = [];

        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add(string.Join(' ', arguments));
            return Task.FromResult(results[_index++]);
        }
    }

    private sealed class MissingRunner : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct) =>
            throw new FileNotFoundException();
    }

    private sealed class NeverHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No HTTP request was expected.");
    }
}
