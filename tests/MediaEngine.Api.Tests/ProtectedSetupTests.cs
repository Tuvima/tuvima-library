using System.Reflection;
using MediaEngine.Api.DevSupport;
using MediaEngine.Api.Services;
using MediaEngine.Contracts.Setup;

namespace MediaEngine.Api.Tests;

public sealed class ProtectedSetupTests
{
    [Theory]
    [InlineData("POST", "/setup/v1/begin", true)]
    [InlineData("POST", "/setup/v1/preflight", true)]
    [InlineData("POST", "/setup/v1/administrator", true)]
    [InlineData("POST", "/setup/v1/media-locations/validate", true)]
    [InlineData("POST", "/setup/v1/steps/providers", true)]
    [InlineData("POST", "/setup/v1/complete", true)]
    [InlineData("PUT", "/setup/v1/locale", true)]
    [InlineData("PUT", "/setup/v1/providers/comicvine/credentials", true)]
    [InlineData("POST", "/setup/v1/providers/comicvine/credentials/test", true)]
    [InlineData("PUT", "/setup/v1/providers/comicvine/credentials/extra", false)]
    [InlineData("PUT", "/setup/v1/libraries", false)]
    [InlineData("POST", "/setup/v1/restore/upload", false)]
    [InlineData("POST", "/setup/v1/server-folders/validate", false)]
    [InlineData("DELETE", "/setup/v1/preflight", false)]
    [InlineData("POST", "/setup/v1/preflight/other", false)]
    public void ProtectedSetupAllowsOnlyKnownSafeOperations(string method, string path, bool allowed)
        => Assert.Equal(allowed, RealMediaEndpoints.IsSafeSetupRequest(method, path));

    [Fact]
    public void ReadOnlyPreflightDoesNotWriteOrCreateDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "setup-readonly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "original.txt"), "original");
            var timestamp = Directory.GetLastWriteTimeUtc(root);
            var check = Probe(root, false);
            Assert.Equal("passed", check.Status);
            Assert.False(check.Writable);
            Assert.Equal(timestamp, Directory.GetLastWriteTimeUtc(root));
            Assert.Equal(new[] { "original.txt" }, Directory.GetFiles(root).Select(Path.GetFileName));
            Assert.Equal("original", File.ReadAllText(Path.Combine(root, "original.txt")));
            var missing = Path.Combine(root, "missing");
            Assert.Equal("blocked", Probe(missing, false).Status);
            Assert.False(Directory.Exists(missing));
            Assert.Equal("passed", Probe(missing, true).Status);
        }
        finally { Directory.Delete(root, true); }
    }

    private static SetupPathCheckDto Probe(string path, bool write) =>
        (SetupPathCheckDto)typeof(SetupPreflightService)
            .GetMethod("Probe", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { "test", "Test", path, "test", write })!;
}
