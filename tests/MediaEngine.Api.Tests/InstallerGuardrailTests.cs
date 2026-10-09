namespace MediaEngine.Api.Tests;

public sealed class InstallerGuardrailTests
{
    [Fact]
    public void WindowsInstaller_EngineServiceListensOnlyOnThisComputer()
    {
        var installer = File.ReadAllText(Path.Combine(FindRepoRoot(), "installer.iss"));
        var engineServiceLine = installer.Split('\n')
            .Single(line => line.Contains("{#EngineSvc}", StringComparison.Ordinal)
                && line.Contains("/v Environment", StringComparison.Ordinal));

        var match = System.Text.RegularExpressions.Regex.Match(engineServiceLine, @"ASPNETCORE_URLS=(http://[^\\""]+)");

        Assert.True(match.Success, "The Engine service should set ASPNETCORE_URLS.");
        Assert.Equal("http://127.0.0.1:{#EnginePort}", match.Groups[1].Value);
    }

    [Fact]
    public void Entrypoint_EngineListensOnlyOnLoopback()
    {
        var entrypoint = File.ReadAllText(Path.Combine(FindRepoRoot(), "docker-entrypoint.sh"));

        Assert.Contains("ASPNETCORE_URLS=\"http://127.0.0.1:61495\"", entrypoint);
        Assert.DoesNotContain("http://+:61495", entrypoint, StringComparison.Ordinal);
        Assert.DoesNotContain("http://0.0.0.0:61495", entrypoint, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
