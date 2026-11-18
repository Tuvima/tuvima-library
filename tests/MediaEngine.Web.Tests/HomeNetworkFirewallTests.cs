using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Configuration;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Home network spec D8: the one Windows firewall rule. It opens the Dashboard port to Private (home) networks only, never Public or Domain
/// ones, only a server administrator at the computer that runs Library may ask for it, and every other system does nothing at all.
/// </summary>
public sealed class HomeNetworkFirewallTests
{
    private static readonly HomeNetworkFirewallCaller Owner = new(true, ClientIngressValues.ThisComputer);

    [Fact]
    public void TheRuleCommand_OpensTheDashboardPortOnPrivateNetworksOnly()
    {
        var command = HomeNetworkFirewallCommands.AddRuleArguments(5016);

        Assert.Equal(
            "advfirewall firewall add rule name=\"Library Dashboard (home network)\" dir=in action=allow protocol=TCP localport=5016 profile=private enable=yes",
            command);
        Assert.Contains("profile=private", command, StringComparison.Ordinal);
        Assert.Equal(1, command.Split("profile=", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("public", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profile=any", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remoteip", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("61495", command, StringComparison.Ordinal);
    }

    [Fact]
    public void TheManualCommandShownInSettings_IsTheSameOneAndNamesTheProgram()
    {
        Assert.Equal($"netsh {HomeNetworkFirewallCommands.AddRuleArguments(5016)}", HomeNetworkFirewallCommands.ManualCommand(5016));
    }

    [Fact]
    public async Task Allow_RunsTheAddCommandOnceAskingForAdministratorRights_ThenChecksTheRuleIsThere()
    {
        var runner = new SpyRunner { RuleExists = false };
        runner.OnRun = (arguments, _) =>
        {
            if (arguments.Contains(" add rule ", StringComparison.Ordinal))
            {
                runner.RuleExists = true;
                return 0;
            }

            return runner.RuleExists ? 0 : 1;
        };
        var firewall = new HomeNetworkFirewall(runner);

        var outcome = await firewall.AllowAsync(5016, Owner);

        Assert.Equal(HomeNetworkFirewallOutcome.Allowed, outcome);
        var add = Assert.Single(runner.Runs, run => run.Arguments.Contains(" add rule ", StringComparison.Ordinal));
        Assert.Equal(HomeNetworkFirewallCommands.AddRuleArguments(5016), add.Arguments);
        Assert.Equal("netsh", add.Program);
        Assert.True(add.NeedsAdministrator);
        Assert.All(runner.Runs.Where(run => run != add), run => Assert.False(run.NeedsAdministrator));
    }

    [Fact]
    public async Task Allow_WhenWindowsSaysNo_ChangesNothingAndReportsNotAllowed()
    {
        var runner = new SpyRunner { OnRun = (_, _) => null };
        var firewall = new HomeNetworkFirewall(runner);

        var outcome = await firewall.AllowAsync(5016, Owner);

        Assert.Equal(HomeNetworkFirewallOutcome.NotAllowed, outcome);
        Assert.Single(runner.Runs);
    }

    [Fact]
    public async Task Allow_WhenTheCommandSucceedsButTheRuleIsNotThere_IsNotAllowed()
    {
        var runner = new SpyRunner { OnRun = (arguments, _) => arguments.Contains(" add rule ", StringComparison.Ordinal) ? 0 : 1 };
        var firewall = new HomeNetworkFirewall(runner);

        Assert.Equal(HomeNetworkFirewallOutcome.NotAllowed, await firewall.AllowAsync(5016, Owner));
    }

    public static TheoryData<bool, string?> CallersWhoMayNotChangeTheFirewall => new()
    {
        { false, ClientIngressValues.ThisComputer },
        { true, ClientIngressValues.HomeNetwork },
        { true, ClientIngressValues.Remote },
        { true, null },
        { true, "" },
        { false, null },
    };

    [Theory]
    [MemberData(nameof(CallersWhoMayNotChangeTheFirewall))]
    public async Task Allow_RunsNothing_ForAnyoneButAnAdministratorOnThisComputer(bool administrator, string? ingress)
    {
        var runner = new SpyRunner { OnRun = (_, _) => 0 };
        var firewall = new HomeNetworkFirewall(runner);

        var outcome = await firewall.AllowAsync(5016, new HomeNetworkFirewallCaller(administrator, ingress));

        Assert.Equal(HomeNetworkFirewallOutcome.NotPermitted, outcome);
        Assert.Empty(runner.Runs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task Allow_RefusesAPortThatIsNotAPort(int port)
    {
        var runner = new SpyRunner { OnRun = (_, _) => 0 };

        Assert.Equal(HomeNetworkFirewallOutcome.NotPermitted, await new HomeNetworkFirewall(runner).AllowAsync(port, Owner));
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task OnEverySystemButWindows_NothingIsAskedOrRun()
    {
        var runner = new SpyRunner { IsWindows = false, OnRun = (_, _) => 0 };
        var firewall = new HomeNetworkFirewall(runner);

        Assert.Equal(HomeNetworkFirewallState.NotApplicable, await firewall.GetStateAsync());
        Assert.Equal(HomeNetworkFirewallOutcome.NotApplicable, await firewall.AllowAsync(5016, Owner));
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task TheNoOpRunnerUsedOffWindows_IsNotWindowsAndRunsNothing()
    {
        var runner = new NoFirewallCommandRunner();

        Assert.False(runner.IsWindows);
        Assert.Null(await runner.RunAsync("netsh", "anything", true, CancellationToken.None));
        Assert.Equal(HomeNetworkFirewallState.NotApplicable, await new HomeNetworkFirewall(runner).GetStateAsync());
    }

    [Theory]
    [InlineData(0, HomeNetworkFirewallState.RuleInPlace)]
    [InlineData(1, HomeNetworkFirewallState.RuleMissing)]
    [InlineData(null, HomeNetworkFirewallState.RuleMissing)]
    public async Task State_ReadsTheRuleWithoutAskingForAdministratorRights(int? exitCode, HomeNetworkFirewallState expected)
    {
        var runner = new SpyRunner { OnRun = (_, _) => exitCode };

        Assert.Equal(expected, await new HomeNetworkFirewall(runner).GetStateAsync());
        var run = Assert.Single(runner.Runs);
        Assert.Equal(HomeNetworkFirewallCommands.ShowRuleArguments(), run.Arguments);
        Assert.False(run.NeedsAdministrator);
    }

    [Fact]
    public void TheDashboardRegistersTheNoOpRunnerOffWindowsAndTheWindowsOneOnWindows()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "MediaEngine.Web", "Program.cs"));

        Assert.Contains("OperatingSystem.IsWindows()", program, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<IFirewallCommandRunner, WindowsFirewallCommandRunner>()", program, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<IFirewallCommandRunner, NoFirewallCommandRunner>()", program, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    /// <summary>Records every command instead of touching a computer. Returns the exit code the test chooses, or null for "Windows refused".</summary>
    internal sealed class SpyRunner : IFirewallCommandRunner
    {
        public bool IsWindows { get; set; } = true;
        public bool RuleExists { get; set; }
        public Func<string, bool, int?> OnRun { get; set; } = (_, _) => 0;
        public List<Run> Runs { get; } = [];

        public Task<int?> RunAsync(string program, string arguments, bool needsAdministrator, CancellationToken ct)
        {
            Runs.Add(new Run(program, arguments, needsAdministrator));
            return Task.FromResult(OnRun(arguments, needsAdministrator));
        }

        public sealed record Run(string Program, string Arguments, bool NeedsAdministrator);
    }
}
