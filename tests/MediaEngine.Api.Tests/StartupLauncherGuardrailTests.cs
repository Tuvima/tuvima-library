namespace MediaEngine.Api.Tests;

public sealed class StartupLauncherGuardrailTests
{
    [Fact]
    public void CombinedLauncher_UsesPublicLivenessWithoutBypassingReadinessSecurity()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "Start-TuvimaApp.ps1"));

        Assert.Contains(
            "Invoke-RestMethod -Uri \"http://localhost:61495/health/live\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains("if ([string]$status -eq \"Healthy\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Invoke-RestMethod -Uri \"http://localhost:61495/health/ready\"",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Invoke-RestMethod -Uri \"http://localhost:61495/system/status\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CombinedLauncher_StartsAndVerifiesDashboardBeforeOpeningIt()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "Start-TuvimaApp.ps1"));

        Assert.Contains("Start-Process -FilePath \"dotnet\"", source, StringComparison.Ordinal);
        Assert.Contains("Wait-ForDashboard -Process $dashboardProcess", source, StringComparison.Ordinal);
        Assert.Contains(
            "Invoke-RestMethod -Uri \"http://localhost:5016/health/ready\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Start-Process \"http://localhost:5016\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CombinedLauncher_LeavesTheAppRunningWhenTheSlowDashboardHasNotAnsweredYet()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "Start-TuvimaApp.ps1"));
        var start = source.IndexOf("function Wait-ForDashboard", StringComparison.Ordinal);
        var end = source.IndexOf("if ($Role -eq \"Both\")", start, StringComparison.Ordinal);
        var dashboardWait = source[start..end];

        // A first Dashboard start on a fresh data store can be slow: wait minutes, and on a timeout say so plainly and keep both programs running.
        Assert.Contains("AddSeconds(300)", dashboardWait, StringComparison.Ordinal);
        Assert.Contains("the app is left up", dashboardWait, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Process", dashboardWait, StringComparison.Ordinal);
    }

    [Fact]
    public void CombinedLauncher_SerializesLaunchesAndWaitsForPriorPortsToRelease()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tools",
            "Start-TuvimaApp.ps1"));

        Assert.Contains("Local\\TuvimaLibrary.DevLauncher", source, StringComparison.Ordinal);
        Assert.Contains("WaitOne(0)", source, StringComparison.Ordinal);
        Assert.Contains("$process.WaitForExit(10000)", source, StringComparison.Ordinal);
        Assert.Contains("Wait-ForTuvimaPortRelease", source, StringComparison.Ordinal);
        Assert.Contains("@(61494, 61495, 5016)", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
