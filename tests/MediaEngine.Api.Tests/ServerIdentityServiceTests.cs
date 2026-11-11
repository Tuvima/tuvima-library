using MediaEngine.Api.Services.Networking;
using MediaEngine.Contracts.Setup;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Library's friendly name and network name that Settings &gt; General reads and saves (and setup shares): the network
/// name follows the friendly name until someone sets it by hand. Runs against a real temp configuration folder.
/// </summary>
public sealed class ServerIdentityServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-identity-{Guid.NewGuid():N}");
    private readonly ConfigurationDirectoryLoader _config;

    public ServerIdentityServiceTests() => _config = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Save_WhileTheNetworkNameFollowsTheLibraryName_DerivesItFromTheNewName()
    {
        var (saved, error) = ServerIdentityService.Save(_config, new SetupServerIdentityDto("Shaya's Library", "ignored", HostNameDerived: true));

        Assert.Null(error);
        Assert.Equal("Shaya's Library", saved!.ServerName);
        Assert.Equal("shayas-library", saved.HostName);
        Assert.True(saved.HostNameDerived);
        Assert.Equal(saved, ServerIdentityService.Read(_config));
    }

    [Fact]
    public void Rename_AfterAHandSetNetworkName_LeavesTheHandSetNameAlone()
    {
        ServerIdentityService.Save(_config, new SetupServerIdentityDto("Shaya's Library", "Den TV", HostNameDerived: false));

        var (renamed, _) = ServerIdentityService.Save(_config, new SetupServerIdentityDto("Family Server", "den-tv", HostNameDerived: false));

        Assert.Equal("den-tv", renamed!.HostName);
        Assert.False(renamed.HostNameDerived);
        Assert.Equal("den-tv", _config.LoadNetwork().Local.PreferredServerName);
        Assert.Equal("Family Server", _config.LoadCore().ServerName);
    }

    [Fact]
    public void Save_WithTheDerivedFlagBackOn_HandsTheNetworkNameBackToTheLibraryName()
    {
        ServerIdentityService.Save(_config, new SetupServerIdentityDto("Shaya's Library", "den-tv", HostNameDerived: false));

        var (saved, _) = ServerIdentityService.Save(_config, new SetupServerIdentityDto("Family Server", "den-tv", HostNameDerived: true));

        Assert.Equal("family-server", saved!.HostName);
        Assert.True(saved.HostNameDerived);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Save_RefusesABlankName_AndChangesNothing(string name)
    {
        ServerIdentityService.Save(_config, new SetupServerIdentityDto("Kept", "kept", true));

        var (saved, error) = ServerIdentityService.Save(_config, new SetupServerIdentityDto(name, "kept", true));

        Assert.Null(saved);
        Assert.NotNull(error);
        Assert.Equal("Kept", _config.LoadCore().ServerName);
    }

    [Fact]
    public void Save_RefusesANameOverSixtyFourCharacters_AndANameWithNoLettersOrNumbers()
    {
        var tooLong = ServerIdentityService.Save(_config, new SetupServerIdentityDto(new string('a', 65), "x", true));
        var noLetters = ServerIdentityService.Save(_config, new SetupServerIdentityDto("!!!", "x", true));

        Assert.Null(tooLong.Saved);
        Assert.Null(noLetters.Saved);
        Assert.Contains("1 to 64", tooLong.Error, StringComparison.Ordinal);
        Assert.Contains("letter or number", noLetters.Error, StringComparison.Ordinal);
    }
}
