using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Storage.Tests;

public sealed class ShippedCoreConfigurationTests
{
    [Theory]
    [InlineData("config/core.json")]
    [InlineData("docker/config/core.json")]
    public void ShippedCoreJson_PassesValidator(string relativePath)
    {
        var core = LoadThroughLoader(relativePath);

        var errors = JsonConfigValidator.Validate(core, "core.json");

        Assert.Empty(errors);
    }

    [Fact]
    public void DockerCoreJson_DefaultsToLocalSignInWithoutLocalhostBypassOrObsoleteOidc()
    {
        var core = LoadThroughLoader("docker/config/core.json");

        Assert.Equal("Local", core.Auth.Mode);
        Assert.False(core.Auth.LocalhostBypass);

        using var document = JsonDocument.Parse(File.ReadAllText(RepoFile("docker/config/core.json")));
        Assert.False(document.RootElement.GetProperty("auth").TryGetProperty("oidc", out _));
    }

    [Theory]
    [InlineData("Local")]
    [InlineData("DisabledLocalOnly")]
    [InlineData("optional")]
    [InlineData("Required")]
    public void CoreAuthentication_AcceptsKnownModes(string mode)
    {
        var core = new CoreConfiguration { Auth = new AuthSettings { Mode = mode } };

        var errors = JsonConfigValidator.Validate(core, "core.json");

        Assert.DoesNotContain(errors, error => error.StartsWith("auth.mode", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Anonymous")]
    [InlineData("")]
    public void CoreAuthentication_RejectsUnknownModes(string mode)
    {
        var core = new CoreConfiguration { Auth = new AuthSettings { Mode = mode } };

        var errors = JsonConfigValidator.Validate(core, "core.json");

        Assert.Contains(errors, error => error == "auth.mode must be one of Local, DisabledLocalOnly, Optional, or Required.");
    }

    private static CoreConfiguration LoadThroughLoader(string relativePath)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tuvima-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(RepoFile(relativePath), Path.Combine(directory, "core.json"), overwrite: true);
            var loader = new ConfigurationDirectoryLoader(directory);
            return loader.LoadCore();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string RepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
        return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
