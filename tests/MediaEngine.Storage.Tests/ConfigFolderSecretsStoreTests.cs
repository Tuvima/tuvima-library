using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Secrets;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class ConfigFolderSecretsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-secrets-{Guid.NewGuid():N}");

    private string ConfigDirectory => Path.Combine(_root, "config");

    [Fact]
    public async Task Secrets_RoundTripPerOwnerKind()
    {
        var store = new ConfigFolderSecretsStore(ConfigDirectory);
        var provider = SecretOwner.Provider("tmdb");
        var plugin = SecretOwner.Plugin("tuvima.example");

        await store.SetAsync(provider, new Dictionary<string, string> { ["api_key"] = "provider-value" });
        await store.SetAsync(plugin, new Dictionary<string, string> { ["token"] = "plugin-value" });

        Assert.Equal("provider-value", await store.GetAsync(provider, "api_key"));
        Assert.Equal("plugin-value", await store.GetAsync(plugin, "token"));
        Assert.Null(await store.GetAsync(provider, "token"));
        Assert.Null(await store.GetAsync(plugin, "api_key"));
        Assert.Equal(["token"], await store.ListNamesAsync(plugin));
        Assert.Equal("plugin-value", (await store.GetAllAsync(plugin))["token"]);
    }

    [Fact]
    public async Task Plugin_CannotReadAnotherPluginsSecrets()
    {
        var store = new ConfigFolderSecretsStore(ConfigDirectory);
        await store.SetAsync(SecretOwner.Plugin("plugin-a"), new Dictionary<string, string> { ["token"] = "a-secret" });
        await store.SetAsync(SecretOwner.Plugin("plugin-b"), new Dictionary<string, string> { ["token"] = "b-secret" });

        Assert.Equal("b-secret", await store.GetAsync(SecretOwner.Plugin("plugin-b"), "token"));
        Assert.Equal("a-secret", await store.GetAsync(SecretOwner.Plugin("plugin-a"), "token"));
        Assert.Empty(await store.GetAllAsync(SecretOwner.Plugin("plugin-c")));
        // A plugin and a provider with the same name are different owners.
        Assert.Empty(await store.GetAllAsync(SecretOwner.Provider("plugin-a")));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData(".hidden")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("white space")]
    public void OwnerNames_ThatCouldLeaveTheirOwnFile_AreRefused(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => SecretOwner.Provider(name));
        Assert.ThrowsAny<ArgumentException>(() => SecretOwner.Plugin(name));
    }

    [Fact]
    public async Task DefaultOwner_IsRefusedByTheStore()
    {
        var store = new ConfigFolderSecretsStore(ConfigDirectory);

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAllAsync(default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(default, new Dictionary<string, string> { ["k"] = "v" }));
        Assert.False(Directory.Exists(ConfigDirectory));
    }

    [Fact]
    public async Task ProviderFile_KeepsTodaysFlatShapeAndLocation()
    {
        var store = new ConfigFolderSecretsStore(ConfigDirectory);

        await store.SetAsync(SecretOwner.Provider("tmdb"), new Dictionary<string, string>
        {
            ["api_key"] = "abc",
            ["pin"] = "1234",
        });

        var path = Path.Combine(ConfigDirectory, "secrets", "tmdb.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var properties = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Equal(new Dictionary<string, string?> { ["api_key"] = "abc", ["pin"] = "1234" }, properties);
        Assert.Empty(Directory.GetFiles(Path.Combine(ConfigDirectory, "secrets"), "*.tmp"));
        Assert.True(File.Exists(Path.Combine(ConfigDirectory, "secrets", "tmdb.json")));
    }

    [Fact]
    public async Task PluginFile_LivesUnderSecretsPlugins()
    {
        var store = new ConfigFolderSecretsStore(ConfigDirectory);

        await store.SetAsync(SecretOwner.Plugin("tuvima.example"), new Dictionary<string, string> { ["token"] = "t" });

        Assert.True(File.Exists(Path.Combine(ConfigDirectory, "secrets", "plugins", "tuvima.example.json")));
    }

    [Fact]
    public async Task Set_KeepsTheOwnersOtherSecrets_AndReplaceDropsThem()
    {
        var store = new ConfigFolderSecretsStore(ConfigDirectory);
        var owner = SecretOwner.Plugin("tuvima.example");
        await store.SetAsync(owner, new Dictionary<string, string> { ["one"] = "1", ["two"] = "2" });

        await store.SetAsync(owner, new Dictionary<string, string> { ["two"] = "22", ["three"] = "3" });
        Assert.Equal(["one", "three", "two"], await store.ListNamesAsync(owner));
        Assert.Equal("22", await store.GetAsync(owner, "two"));

        await store.ReplaceAsync(owner, new Dictionary<string, string> { ["only"] = "x" });
        Assert.Equal(["only"], await store.ListNamesAsync(owner));
    }

    [Fact]
    public async Task Remove_DeletesTheFileWhenTheLastSecretGoes()
    {
        var store = new ConfigFolderSecretsStore(ConfigDirectory);
        var owner = SecretOwner.Plugin("tuvima.example");
        await store.SetAsync(owner, new Dictionary<string, string> { ["one"] = "1", ["two"] = "2" });
        var path = Path.Combine(ConfigDirectory, "secrets", "plugins", "tuvima.example.json");

        await store.RemoveAsync(owner, "one");
        Assert.True(File.Exists(path));
        await store.RemoveAsync(owner, "two");
        Assert.False(File.Exists(path));

        await store.SetAsync(owner, new Dictionary<string, string> { ["one"] = "1" });
        await store.RemoveAllAsync(owner);
        Assert.False(File.Exists(path));
        await store.RemoveAllAsync(owner);
        await store.RemoveAsync(owner, "never-set");
    }

    [Fact]
    public async Task Keys_SurviveDeletingTheDataStoreInTheSameDataFolder()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "library.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE probe (id INTEGER); INSERT INTO probe VALUES (1);";
            command.ExecuteNonQuery();
        }

        await new ConfigFolderSecretsStore(ConfigDirectory)
            .SetAsync(SecretOwner.Provider("tmdb"), new Dictionary<string, string> { ["api_key"] = "kept" });
        await new ConfigFolderSecretsStore(ConfigDirectory)
            .SetAsync(SecretOwner.Plugin("tuvima.example"), new Dictionary<string, string> { ["token"] = "kept-too" });

        File.Delete(databasePath);

        var afterWipe = new ConfigFolderSecretsStore(ConfigDirectory);
        Assert.Equal("kept", await afterWipe.GetAsync(SecretOwner.Provider("tmdb"), "api_key"));
        Assert.Equal("kept-too", await afterWipe.GetAsync(SecretOwner.Plugin("tuvima.example"), "token"));
    }

    [Fact]
    public async Task UnreadableFile_FailsWithoutRevealingItsContent()
    {
        var secretsDirectory = Path.Combine(ConfigDirectory, "secrets");
        Directory.CreateDirectory(secretsDirectory);
        File.WriteAllText(Path.Combine(secretsDirectory, "tmdb.json"), "{\"api_key\": \"super-secret-value\" oops");
        var store = new ConfigFolderSecretsStore(ConfigDirectory);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync(SecretOwner.Provider("tmdb")));

        Assert.DoesNotContain("super-secret-value", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("tmdb", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loader_OverlaysProviderKeysFromTheStore()
    {
        using var loader = new ConfigurationDirectoryLoader(ConfigDirectory);
        loader.SaveProvider(new ProviderConfiguration { Name = "contract_provider", DisplayName = "Contract Provider" });
        await new ConfigFolderSecretsStore(ConfigDirectory).SetAsync(
            SecretOwner.Provider("contract_provider"),
            new Dictionary<string, string> { ["api_key"] = "loaded-key", ["username"] = "someone" });

        var provider = loader.LoadProvider("contract_provider");

        Assert.Equal("loaded-key", provider!.HttpClient!.ApiKey);
        Assert.Equal("someone", provider.HttpClient.Username);
    }

    [Fact]
    public void Loader_ReportsAnUnreadableSecretsFileOnceInsteadOfSkippingItSilently()
    {
        using var loader = new ConfigurationDirectoryLoader(ConfigDirectory);
        loader.SaveProvider(new ProviderConfiguration { Name = "contract_provider", DisplayName = "Contract Provider" });
        File.WriteAllText(Path.Combine(ConfigDirectory, "secrets", "contract_provider.json"), "{\"api_key\": \"super-secret-value\" oops");
        var reports = new List<ConfigurationFileChangedEventArgs>();
        loader.ConfigurationChanged += (_, args) => reports.Add(args);

        var first = loader.LoadProvider("contract_provider");
        var second = loader.LoadProvider("contract_provider");

        Assert.NotNull(first);
        Assert.Null(first.HttpClient?.ApiKey);
        Assert.NotNull(second);
        var report = Assert.Single(reports);
        Assert.False(report.Applied);
        Assert.Equal("secrets/contract_provider.json", report.RelativePath);
        Assert.DoesNotContain("super-secret-value", report.Error!.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
