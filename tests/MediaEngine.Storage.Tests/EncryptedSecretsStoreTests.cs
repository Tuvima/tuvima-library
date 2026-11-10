using System.Text.Json;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Secrets;

namespace MediaEngine.Storage.Tests;

public sealed class EncryptedSecretsStoreTests : IDisposable
{
    private const string Secret = "super-secret-api-value-12345";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-enc-{Guid.NewGuid():N}");

    private string Config => Path.Combine(_root, "config");

    private ConfigFolderSecretsStore NewStore(string keyName = "root.key") =>
        new(Config, new FileSecretsRootKey(Path.Combine(_root, "data", keyName)));

    [Fact]
    public async Task SavedKey_IsNotReadableInTheFile_ButStillWorks()
    {
        var store = NewStore();
        var owner = SecretOwner.Provider("tmdb");
        await store.SetAsync(owner, new Dictionary<string, string> { ["api_key"] = Secret });

        var text = File.ReadAllText(Path.Combine(Config, "secrets", "tmdb.json"));
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain("api_key", text);
        Assert.Equal(Secret, await NewStore().GetAsync(owner, "api_key"));
    }

    [Fact]
    public async Task RootKey_LivesOutsideTheConfigFolder()
    {
        await NewStore().SetAsync(SecretOwner.Plugin("p"), new Dictionary<string, string> { ["t"] = Secret });

        Assert.True(File.Exists(Path.Combine(_root, "data", "root.key")));
        Assert.Empty(Directory.EnumerateFiles(Config, "*.key", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ConfigFolderCopiedWithoutTheRootKey_ShowsCannotBeRead_AndDoesNotCrash()
    {
        var owner = SecretOwner.Plugin("tuvima.example");
        await NewStore().SetAsync(owner, new Dictionary<string, string> { ["token"] = Secret });

        var elsewhere = new ConfigFolderSecretsStore(Config, new FileSecretsRootKey(Path.Combine(_root, "other", "root.key")));
        Assert.Equal(SecretFileState.CannotBeRead, elsewhere.GetState(owner));
        var ex = await Assert.ThrowsAsync<SecretsUnreadableException>(() => elsewhere.ListNamesAsync(owner));
        Assert.DoesNotContain(Secret, ex.Message);
        Assert.False(File.Exists(Path.Combine(_root, "other", "root.key")), "reading must not create a root key");

        // Entering the key again works and replaces the unreadable file.
        await elsewhere.SetAsync(owner, new Dictionary<string, string> { ["token"] = "new-value" });
        Assert.Equal(SecretFileState.Readable, elsewhere.GetState(owner));
        Assert.Equal("new-value", await elsewhere.GetAsync(owner, "token"));
    }

    [Fact]
    public async Task FileCopiedToAnotherOwnersName_DoesNotOpen()
    {
        await NewStore().SetAsync(SecretOwner.Provider("a"), new Dictionary<string, string> { ["k"] = Secret });
        File.Copy(Path.Combine(Config, "secrets", "a.json"), Path.Combine(Config, "secrets", "b.json"));

        Assert.Equal(SecretFileState.CannotBeRead, NewStore().GetState(SecretOwner.Provider("b")));
    }

    [Fact]
    public async Task AlteredFile_DoesNotOpen()
    {
        var owner = SecretOwner.Provider("tmdb");
        await NewStore().SetAsync(owner, new Dictionary<string, string> { ["k"] = Secret });
        var path = Path.Combine(Config, "secrets", "tmdb.json");
        var altered = File.ReadAllText(path);
        var start = altered.IndexOf("\"ct\":\"", StringComparison.Ordinal) + 6;
        var flipped = altered[start] == 'A' ? 'B' : 'A';
        File.WriteAllText(path, altered[..start] + flipped + altered[(start + 1)..]);

        Assert.Equal(SecretFileState.CannotBeRead, NewStore().GetState(owner));
    }

    [Fact]
    public async Task PlainFilesFromAnEarlierVersion_AreLockedOnce_WithNoReentry()
    {
        Directory.CreateDirectory(Path.Combine(Config, "secrets", "plugins"));
        File.WriteAllText(Path.Combine(Config, "secrets", "tmdb.json"), $"{{\"api_key\":\"{Secret}\"}}");
        File.WriteAllText(Path.Combine(Config, "secrets", "plugins", "tuvima.example.json"), $"{{\"token\":\"{Secret}\"}}");

        var store = NewStore();
        var result = store.ConvertPlainFiles();

        Assert.Equal(2, result.Converted.Count);
        Assert.Empty(result.Failed);
        foreach (var file in Directory.EnumerateFiles(Config, "*.json", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(Secret, File.ReadAllText(file));
        }

        Assert.Equal(Secret, await store.GetAsync(SecretOwner.Provider("tmdb"), "api_key"));
        Assert.Equal(Secret, await store.GetAsync(SecretOwner.Plugin("tuvima.example"), "token"));
        Assert.Empty(store.ConvertPlainFiles().Converted);
    }

    [Fact]
    public void PlainFileThatCannotBeConverted_IsLeftUntouched()
    {
        Directory.CreateDirectory(Path.Combine(Config, "secrets"));
        var blocker = Path.Combine(_root, "blocked");
        File.WriteAllText(blocker, "a file, not a folder");
        var path = Path.Combine(Config, "secrets", "tmdb.json");
        File.WriteAllText(path, $"{{\"api_key\":\"{Secret}\"}}");

        var store = new ConfigFolderSecretsStore(Config, new FileSecretsRootKey(Path.Combine(blocker, "root.key")));
        var result = store.ConvertPlainFiles();

        Assert.Single(result.Failed);
        Assert.Contains(Secret, File.ReadAllText(path));
    }

    [Fact]
    public async Task EnvelopeText_NeverHoldsAValue_AndErrorsNameOnlyTheOwner()
    {
        var owner = SecretOwner.Provider("tmdb");
        await NewStore().SetAsync(owner, new Dictionary<string, string> { ["k"] = Secret });
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Config, "secrets", "tmdb.json")));
        Assert.Equal("tuvima.secrets", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
