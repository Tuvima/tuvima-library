using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services.StashBox;

namespace MediaEngine.Providers.Tests.StashBox;

/// <summary>The server list, the keys and the fingerprint permission.</summary>
public sealed class StashBoxSettingsTests : IDisposable
{
    private readonly StashBoxConfigFolder _config = new();

    public void Dispose() => _config.Dispose();

    [Fact]
    public async Task GetAsync_StartsWithStashDbAndTheFingerprintPermissionOff()
    {
        var snapshot = await _config.Settings.GetAsync();

        var server = Assert.Single(snapshot.Servers);
        Assert.Equal("stashdb", server.Id);
        Assert.Equal("https://stashdb.org/graphql", server.Address);
        Assert.False(server.HasKey);
        Assert.Equal(StashBoxConnectionStates.NotConnected, server.Connection);
        Assert.False(snapshot.FingerprintPermission);
    }

    [Fact]
    public async Task SetKeyAsync_KeepsTheKeyOnlyInTheSecretsStore_AndNeverShowsItAgain()
    {
        await _config.Settings.SetKeyAsync("stashdb", "super-secret-key-9999");
        var added = await _config.Settings.AddServerAsync("Mine", "https://mine.example.org/");
        await _config.Settings.SetKeyAsync(added!.Id, "another-secret-key-9999");

        var snapshot = await _config.Settings.GetAsync();
        Assert.All(snapshot.Servers, s => Assert.True(s.HasKey));
        var listText = File.ReadAllText(Path.Combine(_config.Path, StashBoxSettings.FileName));
        Assert.DoesNotContain("super-secret-key-9999", listText, StringComparison.Ordinal);
        Assert.DoesNotContain("another-secret-key-9999", listText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", System.Text.Json.JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
        Assert.Equal("super-secret-key-9999", (await _config.Settings.GetCredentialAsync("stashdb"))!.Value.Key);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has a space in it 123")]
    public async Task SetKeyAsync_RefusesSomethingThatIsNotAKey(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _config.Settings.SetKeyAsync("stashdb", key));
    }

    [Theory]
    [InlineData("http://stash.example.org/graphql")]
    [InlineData("https://user:pass@stash.example.org/graphql")]
    [InlineData("https://stash.example.org/graphql?x=1")]
    [InlineData("ftp://stash.example.org/graphql")]
    [InlineData("not an address")]
    [InlineData("")]
    public async Task AddServerAsync_RefusesAnAddressThatIsNotHttps(string address)
    {
        Assert.Null(await _config.Settings.AddServerAsync("Bad", address));
        Assert.Single((await _config.Settings.GetAsync()).Servers);
    }

    [Theory]
    [InlineData("http://localhost:9999/graphql")]
    [InlineData("http://127.0.0.1:9999")]
    [InlineData("https://stash.example.org")]
    public async Task AddServerAsync_AllowsHttpsAndAServerOnThisMachine(string address)
    {
        var server = await _config.Settings.AddServerAsync("Mine", address);

        Assert.NotNull(server);
        Assert.EndsWith("/graphql", server.Address, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddServerAsync_RefusesTheSameAddressTwice_AndTheDefaultCannotBeRemoved()
    {
        Assert.NotNull(await _config.Settings.AddServerAsync("A", "https://a.example.org"));
        Assert.Null(await _config.Settings.AddServerAsync("A again", "https://a.example.org/graphql"));
        Assert.False(await _config.Settings.RemoveServerAsync("stashdb"));
    }

    [Fact]
    public async Task SetOrderAsync_ChangesTheOrderTheyAreTriedIn()
    {
        var a = await _config.Settings.AddServerAsync("A", "https://a.example.org");
        var b = await _config.Settings.AddServerAsync("B", "https://b.example.org");

        await _config.Settings.SetOrderAsync([b!.Id, "stashdb"]);

        Assert.Equal([b.Id, "stashdb", a!.Id], (await _config.Settings.GetAsync()).Servers.Select(s => s.Id));
    }

    [Fact]
    public async Task RemoveServerAsync_RemovesItsKeyToo()
    {
        var added = await _config.Settings.AddServerAsync("A", "https://a.example.org");
        await _config.Settings.SetKeyAsync(added!.Id, "a-secret-account-key");

        Assert.True(await _config.Settings.RemoveServerAsync(added.Id));

        Assert.Null(await _config.Settings.GetCredentialAsync(added.Id));
        Assert.Empty(await _config.Secrets.ListNamesAsync(SecretOwner.Plugin("stashbox-servers")));
    }

    [Fact]
    public async Task SetFingerprintPermissionAsync_IsOffUntilTurnedOnAndSurvivesAReload()
    {
        await _config.Settings.SetFingerprintPermissionAsync(true);

        var reloaded = new StashBoxSettings(_config.Loader, _config.Secrets, Microsoft.Extensions.Logging.Abstractions.NullLogger<StashBoxSettings>.Instance);
        Assert.True((await reloaded.GetAsync()).FingerprintPermission);
    }

    [Theory]
    [InlineData("https://cdn.stashdb.org/a/b.jpg", true)]
    [InlineData("https://stashdb.org/a/b.jpg", true)]
    [InlineData("http://cdn.stashdb.org/a/b.jpg", false)]
    [InlineData("https://evil.example.net/a/b.jpg", false)]
    [InlineData("https://notstashdb.org/a/b.jpg", false)]
    [InlineData("https://stashdb.org.evil.example.net/a.jpg", false)]
    [InlineData("https://user@cdn.stashdb.org/a.jpg", false)]
    public void IsOwnImageHost_OnlyAcceptsTheServersOwnSite(string image, bool expected)
    {
        Assert.Equal(expected, StashBoxAddress.IsOwnImageHost(new Uri("https://stashdb.org/graphql"), new Uri(image)));
    }
}
