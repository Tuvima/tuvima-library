using System.Text.Json.Nodes;
using Dapper;
using MediaEngine.Api.DevSupport;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Secrets;

namespace MediaEngine.Api.Tests;

public sealed class BaselineKeysTests : IDisposable
{
    private const string KeyValue = "tmdb-secret-key-value-123";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_baseline_keys_{Guid.NewGuid():N}");
    private readonly ISecretsRootKey _installKey = new FixedSecretsRootKey(Enumerable.Repeat((byte)7, 32).ToArray());
    private readonly ISecretsRootKey _otherKey = new FixedSecretsRootKey(Enumerable.Repeat((byte)9, 32).ToArray());

    public BaselineKeysTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Left behind only if a scanner holds a temp file; the unique folder name keeps runs apart.
        }
    }

    /// <summary>A copied run config with a sealed TMDB key and switched-on key-needing TMDB, TheTVDB and Comic Vine files.</summary>
    private async Task<string> ConfigWithSealedTmdbKeyAsync()
    {
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(config, "providers"));
        foreach (var name in new[] { "tmdb", "tvdb", "comicvine" })
        {
            File.WriteAllText(Path.Combine(config, "providers", name + ".json"),
                new JsonObject { ["name"] = name, ["enabled"] = true, ["requires_api_key"] = true, ["http_client"] = new JsonObject { ["api_key"] = "" } }.ToJsonString());
        }

        await new ConfigFolderSecretsStore(config, _installKey)
            .SetAsync(SecretOwner.Provider("tmdb"), new Dictionary<string, string> { ["api_key"] = KeyValue });
        return config;
    }

    [Fact]
    public async Task KeysThatCannotBeOpened_HoldFilmsAndSayWhyInPlainWords()
    {
        var config = await ConfigWithSealedTmdbKeyAsync();
        var samples = Path.Combine(_root, "samples");
        Directory.CreateDirectory(Path.Combine(samples, "movies", "Dune (2021)"));
        File.WriteAllText(Path.Combine(samples, "movies", "Dune (2021)", "Dune (2021).mkv"), string.Empty);
        Directory.CreateDirectory(Path.Combine(samples, "books"));

        var states = BaselineKeys.Inspect(config, _otherKey);
        var held = BaselineKeys.FindHeldKinds(config, samples, ["books", "movies"], states);

        Assert.Equal(BaselineKeys.CannotBeRead, states["tmdb"]);
        var movies = Assert.Single(held);
        Assert.Equal("movies", movies.Kind);
        Assert.Equal(["movies/Dune (2021)/Dune (2021).mkv"], movies.Files);
        Assert.Contains("tmdb key", movies.Reason);
        Assert.Contains("tvdb has no key saved", movies.Reason);
        Assert.Single(BaselineKeys.Explain(held));
    }

    [Fact]
    public async Task KeysThatOpen_HoldNothingForFilmsAndStillHoldComicsWithoutAKey()
    {
        var config = await ConfigWithSealedTmdbKeyAsync();
        var samples = Path.Combine(_root, "samples");
        Directory.CreateDirectory(Path.Combine(samples, "movies"));
        Directory.CreateDirectory(Path.Combine(samples, "comics"));
        File.WriteAllText(Path.Combine(samples, "comics", "Saga 01.cbz"), string.Empty);

        var states = BaselineKeys.Inspect(config, _installKey);
        var held = BaselineKeys.FindHeldKinds(config, samples, ["movies", "comics"], states);

        Assert.Equal(BaselineKeys.Readable, states["tmdb"]);
        var comics = Assert.Single(held);
        Assert.Equal("comics", comics.Kind);
        Assert.Equal(["comics/Saga 01.cbz"], comics.Files);
    }

    [Fact]
    public async Task LoadSecrets_IncludesTheRealKeyValueOfASealedFileSoARecordingCannotCarryIt()
    {
        var config = await ConfigWithSealedTmdbKeyAsync();

        Assert.Contains(KeyValue, ProviderAnswerStore.LoadSecrets(config, _installKey));
        Assert.DoesNotContain(KeyValue, ProviderAnswerStore.LoadSecrets(config, _otherKey));
    }

    [Fact]
    public void PointAtInstallKey_UsesTheInstallKeyOnlyWhenNoneIsNamedAndItExists()
    {
        var installKey = Path.Combine(_root, "root.key");
        File.WriteAllText(installKey, "x");
        string? set = null;

        Assert.Contains("own install key", BaselineKeys.PointAtInstallKey(null, null, installKey, value => set = value));
        Assert.Equal(installKey, set);

        set = null;
        Assert.Contains("TUVIMA_SECRETS_KEY.", BaselineKeys.PointAtInstallKey("abc", null, installKey, value => set = value));
        Assert.Contains("named by", BaselineKeys.PointAtInstallKey(null, installKey, installKey, value => set = value));
        Assert.Null(set);

        Assert.Contains("none found", BaselineKeys.PointAtInstallKey(null, null, Path.Combine(_root, "missing.key"), value => set = value));
        Assert.Null(set);
    }

    [Fact]
    public void FindOrCreateLibrary_MakesAnEmptyLibraryOnAFreshCheckoutAndKeepsTheSameIdEveryRun()
    {
        var config = Path.Combine(_root, "fresh-config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "library_kinds.json"),
            """{"kinds":[{"key":"books","area":"read","format":"Books"},{"key":"movies","area":"watch","format":"Movies"}]}""");
        var libraries = new MediaEngine.Domain.Configuration.LibrariesConfiguration();

        var books = BaselinePrepare.FindOrCreateLibrary(libraries, config, "Books");
        var again = BaselinePrepare.FindOrCreateLibrary(libraries, config, "Books");
        var other = new MediaEngine.Domain.Configuration.LibrariesConfiguration();

        Assert.Same(books, again);
        Assert.Single(libraries.Libraries);
        Assert.Equal("books", books.KindKey);
        Assert.Equal(["Books"], books.MediaTypes);
        Assert.Equal(books.Id, BaselinePrepare.FindOrCreateLibrary(other, config, "Books").Id);
        Assert.Throws<InvalidOperationException>(() => BaselinePrepare.FindOrCreateLibrary(libraries, config, "Videos"));
    }

    [Fact]
    public void ResetSetup_PutsFirstTimeSetupBackToItsFirstStepAndKeepsTheOwnerProfile()
    {
        DapperConfiguration.Configure();
        var path = Path.Combine(_root, "library.db");
        using (var database = new DatabaseConnection(path))
        {
            database.InitializeSchema();
            database.RunStartupChecks();
            using (var connection = database.CreateConnection())
            {
                connection.Execute("UPDATE onboarding_workflows SET state='complete', current_step='done', completed_at='2026-01-01' WHERE workflow_version=1; UPDATE onboarding_steps SET status='passed' WHERE workflow_version=1;");
            }

            BaselineOpenStore.ResetSetup(database);

            using var check = database.CreateConnection();
            Assert.Equal(("in_progress", "welcome"), check.QuerySingle<(string, string)>("SELECT state, current_step FROM onboarding_workflows WHERE workflow_version=1"));
            Assert.Equal(0, check.ExecuteScalar<int>("SELECT COUNT(*) FROM onboarding_steps WHERE status <> 'not_started'"));
            Assert.Equal(1, check.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles"));
            Assert.Equal(0, check.ExecuteScalar<int>("SELECT COUNT(*) FROM accounts"));
        }

        DatabaseConnection.ReleasePooledConnections(path);
    }
}
