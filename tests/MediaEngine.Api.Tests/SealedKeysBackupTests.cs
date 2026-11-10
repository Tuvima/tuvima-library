using System.IO.Compression;
using System.Text.Json;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Secrets;

namespace MediaEngine.Api.Tests;

public sealed class SealedKeysBackupTests : IDisposable
{
    private const string KeyValue = "tmdb-secret-value-777";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-sealed-{Guid.NewGuid():N}");

    private ConfigFolderSecretsStore Store(string machine) =>
        new(Path.Combine(_root, machine, "config"), new FileSecretsRootKey(Path.Combine(_root, machine, "data", "root.key")));

    private async Task<(string Archive, Api.Services.DatabaseBackupService Service)> BackupWithKeysAsync(string pin)
    {
        var secrets = Store("a");
        await secrets.SetAsync(SecretOwner.Provider("tmdb"), new Dictionary<string, string> { ["api_key"] = KeyValue });
        await secrets.SetAsync(SecretOwner.Plugin("tuvima.example"), new Dictionary<string, string> { ["token"] = "plugin-token-1" });
        Directory.CreateDirectory(Path.Combine(_root, "a", "config"));
        File.WriteAllText(Path.Combine(_root, "a", "config", "core.json"), "{}");
        var database = BackupTestKit.OpenStore(Path.Combine(_root, "a.db"));
        var service = BackupTestKit.Service(database, Path.Combine(_root, "a", "config"), secrets: secrets);
        return (await service.CreateAsync(CancellationToken.None, pin), service);
    }

    [Fact]
    public async Task BackupWithKeys_HoldsNoReadableValue_AndTheManifestSaysKeysAreIncluded()
    {
        var (archivePath, _) = await BackupWithKeysAsync("4821");

        using var archive = ZipFile.OpenRead(archivePath);
        Assert.NotNull(archive.GetEntry(SealedKeysBundle.EntryName));
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            var text = reader.ReadToEnd();
            Assert.DoesNotContain(KeyValue, text);
            Assert.DoesNotContain("plugin-token-1", text);
        }

        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        Assert.True(manifest.RootElement.GetProperty("includes_keys").GetBoolean());
        Assert.False(manifest.RootElement.GetProperty("includes_secrets").GetBoolean());
        Assert.DoesNotContain(archive.Entries, e => e.FullName.StartsWith("config/secrets/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RightPin_PutsEveryKeyBack_OnAnotherComputer_LockedUnderItsOwnRootKey()
    {
        var (archivePath, _) = await BackupWithKeysAsync("4821");
        var other = Store("b");

        using var archive = ZipFile.OpenRead(archivePath);
        var outcome = SealedKeysBundle.Restore(archive, "4821", other);

        Assert.Equal(KeysRestoreOutcome.Restored, outcome.Status);
        Assert.Equal(2, outcome.RestoredKeys.Count);
        Assert.Equal(KeyValue, await other.GetAsync(SecretOwner.Provider("tmdb"), "api_key"));
        Assert.Equal("plugin-token-1", await other.GetAsync(SecretOwner.Plugin("tuvima.example"), "token"));
        Assert.DoesNotContain(KeyValue, File.ReadAllText(Path.Combine(_root, "b", "config", "secrets", "tmdb.json")));
    }

    [Theory]
    [InlineData("0000", KeysRestoreOutcome.PinWrong)]
    [InlineData(null, KeysRestoreOutcome.PinMissing)]
    [InlineData("12", KeysRestoreOutcome.PinMissing)]
    public async Task WrongOrMissingPin_RestoresNoKeys_ButIsNotAnError(string? pin, string expected)
    {
        var (archivePath, _) = await BackupWithKeysAsync("4821");
        var other = Store("b");

        using var archive = ZipFile.OpenRead(archivePath);
        var outcome = SealedKeysBundle.Restore(archive, pin, other);

        Assert.Equal(expected, outcome.Status);
        Assert.Empty(outcome.RestoredKeys);
        Assert.Empty(other.ListOwners());
    }

    [Fact]
    public async Task ScheduleRestore_WithWrongPin_StillSchedulesTheRestore_AndSaysKeysWereSkipped()
    {
        var (archivePath, service) = await BackupWithKeysAsync("4821");

        var result = service.ScheduleRestore(Path.GetFileName(archivePath), "9999");

        Assert.True(result.Scheduled);
        Assert.Equal(KeysRestoreOutcome.PinWrong, result.KeysStatus);
        Assert.Contains("PIN didn't open", result.Message);
    }

    [Fact]
    public async Task CreateAsync_RefusesAPinThatIsNotFourDigits_AndWritesNothing()
    {
        var secrets = Store("a");
        Directory.CreateDirectory(Path.Combine(_root, "a", "config"));
        var database = BackupTestKit.OpenStore(Path.Combine(_root, "a.db"));
        var service = BackupTestKit.Service(database, Path.Combine(_root, "a", "config"), secrets: secrets);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(CancellationToken.None, "12a4"));
        Assert.Empty(service.List());
    }

    [Fact]
    public async Task BackupWithoutAPin_NeverIncludesKeys()
    {
        var secrets = Store("a");
        await secrets.SetAsync(SecretOwner.Provider("tmdb"), new Dictionary<string, string> { ["api_key"] = KeyValue });
        Directory.CreateDirectory(Path.Combine(_root, "a", "config"));
        var database = BackupTestKit.OpenStore(Path.Combine(_root, "a.db"));
        var service = BackupTestKit.Service(database, Path.Combine(_root, "a", "config"), secrets: secrets);

        var path = await service.CreateAsync(CancellationToken.None);

        using var archive = ZipFile.OpenRead(path);
        Assert.Null(archive.GetEntry(SealedKeysBundle.EntryName));
        Assert.False(service.List().Single().IncludesKeys);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* temp folder cleanup is best effort */ }
        }
    }
}
