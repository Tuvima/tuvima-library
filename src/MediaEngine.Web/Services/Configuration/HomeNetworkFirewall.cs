using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Web.Services.Configuration;

/// <summary>Whether the Windows firewall lets the home network reach the Dashboard.</summary>
public enum HomeNetworkFirewallState
{
    /// <summary>Not Windows, so there is nothing to ask or change.</summary>
    NotApplicable,

    /// <summary>No Library rule for the Dashboard port exists on this computer.</summary>
    RuleMissing,

    /// <summary>Library's rule for the Dashboard port exists.</summary>
    RuleInPlace,
}

/// <summary>What happened when the rule was asked for.</summary>
public enum HomeNetworkFirewallOutcome
{
    /// <summary>The rule is in place.</summary>
    Allowed,

    /// <summary>Not Windows; nothing was done.</summary>
    NotApplicable,

    /// <summary>The person asking is not a server administrator on the computer that runs Library; nothing was run.</summary>
    NotPermitted,

    /// <summary>Windows asked and the answer was no, or the change did not take; the rule is not in place.</summary>
    NotAllowed,
}

/// <summary>Who is asking, as the Dashboard knows it from the signed-in session.</summary>
public readonly record struct HomeNetworkFirewallCaller(bool IsServerAdministrator, string? Ingress)
{
    /// <summary>A rule on the Windows firewall is changed only by a server administrator sitting at the computer that runs Library.</summary>
    public bool MayChangeFirewall => IsServerAdministrator && Ingress == ClientIngressValues.ThisComputer;
}

/// <summary>
/// The exact commands Library gives Windows. Home networks only: the rule is scoped to the Private profile and never to
/// Public (or Domain) networks, so public Wi-Fi stays blocked.
/// </summary>
public static class HomeNetworkFirewallCommands
{
    public const string Program = "netsh";
    public const string RuleName = "Library Dashboard (home network)";

    /// <summary>Adds the inbound rule for the Dashboard port on the Private profile only.</summary>
    public static string AddRuleArguments(int port) =>
        $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} profile=private enable=yes";

    /// <summary>Looks the rule up by name; the exit code is 0 only when it exists.</summary>
    public static string ShowRuleArguments() =>
        $"advfirewall firewall show rule name=\"{RuleName}\"";

    /// <summary>The command a person can run themselves in an administrator window when they declined the prompt.</summary>
    public static string ManualCommand(int port) => $"{Program} {AddRuleArguments(port)}";
}

/// <summary>Runs one firewall command. A seam so tests can record the command instead of changing a computer.</summary>
public interface IFirewallCommandRunner
{
    /// <summary>True on Windows, the only place the firewall is touched.</summary>
    bool IsWindows { get; }

    /// <summary>Runs <paramref name="program"/> and returns its exit code, or null when Windows refused to run it (for example a declined prompt).</summary>
    Task<int?> RunAsync(string program, string arguments, bool needsAdministrator, CancellationToken ct);
}

/// <summary>Asks Windows for the Dashboard's home-network rule (and reports whether it is there). A no-op everywhere else.</summary>
public interface IHomeNetworkFirewall
{
    Task<HomeNetworkFirewallState> GetStateAsync(CancellationToken ct = default);

    Task<HomeNetworkFirewallOutcome> AllowAsync(int port, HomeNetworkFirewallCaller caller, CancellationToken ct = default);
}

public sealed class HomeNetworkFirewall(IFirewallCommandRunner runner) : IHomeNetworkFirewall
{
    public async Task<HomeNetworkFirewallState> GetStateAsync(CancellationToken ct = default)
    {
        if (!runner.IsWindows)
        {
            return HomeNetworkFirewallState.NotApplicable;
        }

        var exitCode = await runner.RunAsync(
            HomeNetworkFirewallCommands.Program, HomeNetworkFirewallCommands.ShowRuleArguments(), needsAdministrator: false, ct).ConfigureAwait(false);
        return exitCode == 0 ? HomeNetworkFirewallState.RuleInPlace : HomeNetworkFirewallState.RuleMissing;
    }

    public async Task<HomeNetworkFirewallOutcome> AllowAsync(int port, HomeNetworkFirewallCaller caller, CancellationToken ct = default)
    {
        if (!runner.IsWindows)
        {
            return HomeNetworkFirewallOutcome.NotApplicable;
        }

        if (!caller.MayChangeFirewall || port is < 1 or > 65535)
        {
            return HomeNetworkFirewallOutcome.NotPermitted;
        }

        var exitCode = await runner.RunAsync(
            HomeNetworkFirewallCommands.Program, HomeNetworkFirewallCommands.AddRuleArguments(port), needsAdministrator: true, ct).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return HomeNetworkFirewallOutcome.NotAllowed;
        }

        // Trust the firewall, not the exit code alone.
        return await GetStateAsync(ct).ConfigureAwait(false) == HomeNetworkFirewallState.RuleInPlace
            ? HomeNetworkFirewallOutcome.Allowed
            : HomeNetworkFirewallOutcome.NotAllowed;
    }
}

/// <summary>Runs <c>netsh</c> on Windows, asking Windows once for permission when this process is not already an administrator.</summary>
public sealed class WindowsFirewallCommandRunner(ILogger<WindowsFirewallCommandRunner> logger) : IFirewallCommandRunner
{
    private const int UserDeclinedElevation = 1223;

    public bool IsWindows => OperatingSystem.IsWindows();

    public async Task<int?> RunAsync(string program, string arguments, bool needsAdministrator, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var elevate = needsAdministrator && !IsElevated();
        var start = new ProcessStartInfo(program, arguments)
        {
            // The Windows prompt ("runas") needs the shell; a plain run keeps the window hidden and the output unread.
            UseShellExecute = elevate,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (elevate)
        {
            start.Verb = "runas";
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == UserDeclinedElevation)
        {
            logger.LogInformation("The Windows firewall prompt for the home network was declined.");
            return null;
        }
        catch (Win32Exception exception)
        {
            logger.LogWarning(exception, "Windows could not run the firewall command.");
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>Everywhere except Windows: there is no firewall rule to ask for, so nothing is run.</summary>
public sealed class NoFirewallCommandRunner : IFirewallCommandRunner
{
    public bool IsWindows => false;

    public Task<int?> RunAsync(string program, string arguments, bool needsAdministrator, CancellationToken ct) =>
        Task.FromResult<int?>(null);
}
