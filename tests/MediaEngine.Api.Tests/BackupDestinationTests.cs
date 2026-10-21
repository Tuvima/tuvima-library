using System.IO.Compression;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Contracts.Backups;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Backup destinations (Developer tools spec WP25): the passphrase-sealed file format, a copy that leaves the computer being
/// encrypted and read back, "keep the last N", a restore from a destination, and what a backup is allowed to hold.
/// </summary>
public sealed class BackupDestinationTests : IDisposable
{
    private const string Passphrase = "correct horse battery";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-backup-destinations-{Guid.NewGuid():N}");
    private readonly DatabaseConnection _db;
    private readonly DatabaseBackupService _backups;
    private readonly BackupDestinationRepository _repository;
    private readonly BackupSettingsAccessor _settings;
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeRemote _remote = new();
    private readonly SpyPublisher _published = new();
    private readonly BackupOrchestrator _orchestrator;

    public BackupDestinationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", "core.json"), "{\"server_name\":\"Home\"}");
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _backups = new DatabaseBackupService(_db, Path.Combine(_root, "config"), Path.Combine(_root, "backups"));
        _repository = new BackupDestinationRepository(_db);
        _settings = new BackupSettingsAccessor(_repository, _clock);
        _orchestrator = new BackupOrchestrator(
            _backups, [new LocalFolderBackupDestination(_backups), _remote], _settings, _repository, _published, _clock, NullLogger<BackupOrchestrator>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Encrypt_ThenDecryptWithThePassphrase_ReturnsTheOriginalBytes()
    {
        var plain = new byte[300_000];
        Random.Shared.NextBytes(plain);
        var keys = BackupEncryption.CreateKeyMaterial(Passphrase);

        var sealedBytes = await SealAsync(plain, keys);
        var opened = await OpenAsync(sealedBytes, Passphrase);

        Assert.Equal(plain, opened);
    }

    [Fact]
    public async Task Decrypt_WithTheWrongPassphrase_SaysSoInPlainWords()
    {
        var sealedBytes = await SealAsync([1, 2, 3, 4], BackupEncryption.CreateKeyMaterial(Passphrase));

        var ex = await Assert.ThrowsAsync<BackupPassphraseException>(() => OpenAsync(sealedBytes, "not the passphrase"));

        Assert.Contains("passphrase", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Decrypt_AFileThatWasTamperedWithOrCutShort_IsRefused()
    {
        var plain = new byte[200_000];
        Random.Shared.NextBytes(plain);
        var sealedBytes = await SealAsync(plain, BackupEncryption.CreateKeyMaterial(Passphrase));

        var flipped = (byte[])sealedBytes.Clone();
        flipped[^50] ^= 0xFF;
        var truncated = sealedBytes[..(sealedBytes.Length - 100)];

        await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(flipped, Passphrase));
        await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(truncated, Passphrase));
    }

    [Fact]
    public void APassphraseThatIsTooShort_IsRefused() =>
        Assert.Throws<ArgumentException>(() => BackupEncryption.CreateKeyMaterial("short"));

    [Fact]
    public async Task ARemoteDestinationWithNoPassphrase_FailsAndNothingLeavesTheComputer()
    {
        await EnableRemoteAsync(withPassphrase: false);

        var run = await _orchestrator.RunAsync(CancellationToken.None);

        Assert.False(run.AllOk);
        Assert.Empty(_remote.Files);
        Assert.Contains(_published.Requests, request => request.EventId == NotificationEventIds.BackupFailed);
    }

    [Fact]
    public async Task ABackupSentToARemoteDestination_IsEncrypted_AndCheckedAfterwards()
    {
        await EnableRemoteAsync(withPassphrase: true);

        var run = await _orchestrator.RunAsync(CancellationToken.None);

        Assert.True(run.AllOk);
        var file = Assert.Single(_remote.Files);
        Assert.EndsWith(BackupEncryption.FileExtension, file.Key);
        Assert.NotEqual((byte)'P', file.Value[0]); // not a plain zip
        var copy = Assert.Single(await _repository.ListCopiesAsync(_remote.Id, 10, CancellationToken.None));
        Assert.Equal("verified", copy.State);
    }

    [Fact]
    public async Task ACopyThatArrivesDamaged_ShowsAsFailed()
    {
        await EnableRemoteAsync(withPassphrase: true);
        _remote.Corrupt = true;

        var run = await _orchestrator.RunAsync(CancellationToken.None);

        Assert.False(run.AllOk);
        Assert.Contains(run.Destinations, item => item.Id == _remote.Id && item.Outcome == "failed");
    }

    [Fact]
    public async Task OnlyTheNewestBackupsAreKept()
    {
        await EnableRemoteAsync(withPassphrase: true, keep: 2);

        for (var i = 0; i < 4; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(5));
            await _orchestrator.RunAsync(CancellationToken.None);
        }

        Assert.Equal(2, _remote.Files.Count);
    }

    [Fact]
    public async Task RestoringFromADestination_NeedsTheRightPassphrase_ThenStagesTheRestore()
    {
        await EnableRemoteAsync(withPassphrase: true);
        await _orchestrator.RunAsync(CancellationToken.None);
        var name = _remote.Files.Keys.Single();

        await Assert.ThrowsAsync<BackupPassphraseException>(() => _orchestrator.RestoreFromDestinationAsync(_remote, name, null, CancellationToken.None));
        await Assert.ThrowsAsync<BackupPassphraseException>(() => _orchestrator.RestoreFromDestinationAsync(_remote, name, "wrong passphrase", CancellationToken.None));
        var staged = await _orchestrator.RestoreFromDestinationAsync(_remote, name, Passphrase, CancellationToken.None);

        Assert.True(staged.Scheduled);
    }

    [Fact]
    public async Task ARestoreNameThatIsAPath_IsRefused() =>
        await Assert.ThrowsAsync<BackupDestinationException>(() => _orchestrator.RestoreFromDestinationAsync(_remote, "../outside.zip", null, CancellationToken.None));

    [Fact]
    public async Task ABackupHoldsOnlyTheDataStoreAndConfiguration_NeverMediaFiles()
    {
        // Media files and folders are the operating system's job to back up (Shy, 2026-10-21): the archive carries the
        // Library's own data (reading positions, bookmarks, preferences live in the data store) and safe configuration only.
        var path = await _backups.CreateAsync(CancellationToken.None);

        using var archive = ZipFile.OpenRead(path);
        Assert.All(archive.Entries, entry => Assert.True(
            entry.FullName == "manifest.json"
            || entry.FullName == "database/library.db"
            || entry.FullName.StartsWith("config/", StringComparison.Ordinal),
            entry.FullName));
    }

    [Fact]
    public async Task APlainZipAtARemoteDestination_IsNeverRestored()
    {
        _remote.Plant("tuvima-backup-planted.zip", [1, 2, 3]);

        await Assert.ThrowsAsync<BackupDestinationException>(() => _orchestrator.RestoreFromDestinationAsync(_remote, "tuvima-backup-planted.zip", null, CancellationToken.None));
    }

    [Fact]
    public async Task Decrypt_BytesAfterTheEnd_AreRefused()
    {
        var sealedBytes = await SealAsync([1, 2, 3], BackupEncryption.CreateKeyMaterial(Passphrase));

        await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync([.. sealedBytes, 0], Passphrase));
    }

    [Fact]
    public async Task ARestoreArchiveHoldingSecrets_IsRefusedWhenStaged()
    {
        var path = Path.Combine(_root, "backups", "tuvima-backup-hostile.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var good = await _backups.CreateAsync(CancellationToken.None);
        File.Copy(good, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(zip.CreateEntry("config/secrets/key.json").Open());
            writer.Write("{}");
        }

        Assert.Throws<InvalidDataException>(() => _backups.ScheduleRestore("tuvima-backup-hostile.zip"));
    }

    private async Task EnableRemoteAsync(bool withPassphrase, int keep = 10)
    {
        var settings = await _settings.LoadAsync(CancellationToken.None);
        settings.EnabledDestinations = [_remote.Id];
        settings.KeepLast = keep;
        if (withPassphrase)
        {
            var keys = BackupEncryption.CreateKeyMaterial(Passphrase);
            settings.PublicKey = Convert.ToBase64String(keys.PublicKey);
            settings.EncryptedPrivateKey = Convert.ToBase64String(keys.EncryptedPrivateKey);
        }

        await _settings.SaveAsync(settings, CancellationToken.None);
    }

    private static async Task<byte[]> SealAsync(byte[] plain, BackupKeyMaterial keys)
    {
        using var output = new MemoryStream();
        await BackupEncryption.EncryptAsync(new MemoryStream(plain), output, keys, CancellationToken.None);
        return output.ToArray();
    }

    private static async Task<byte[]> OpenAsync(byte[] sealedBytes, string passphrase)
    {
        using var output = new MemoryStream();
        await BackupEncryption.DecryptAsync(new MemoryStream(sealedBytes), output, passphrase, CancellationToken.None);
        return output.ToArray();
    }

    private sealed class SpyPublisher : INotificationPublisher
    {
        public List<NotificationRequest> Requests { get; } = [];

        public void Publish(NotificationRequest request) => Requests.Add(request);
    }

    /// <summary>A destination that keeps files in memory and can be told to damage what it stores.</summary>
    private sealed class FakeRemote : IBackupDestination
    {
        private readonly Dictionary<string, (byte[] Bytes, DateTimeOffset At)> _files = [];
        private int _counter;

        public Dictionary<string, byte[]> Files => _files.ToDictionary(item => item.Key, item => item.Value.Bytes);

        public bool Corrupt { get; set; }

        public void Plant(string name, byte[] bytes) => _files[name] = (bytes, DateTimeOffset.UnixEpoch);

        public string Id => "fake-remote";

        public string Name => "Fake remote";

        public bool LeavesMachine => true;

        public bool IsConfigured => true;

        public Task<DestinationTestResult> TestAsync(CancellationToken ct) => Task.FromResult(new DestinationTestResult(true, "ok"));

        public async Task SendAsync(string name, Stream content, long length, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();
            if (Corrupt)
            {
                bytes[^1] ^= 0xFF;
            }

            _files[name] = (bytes, new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero).AddMinutes(++_counter));
        }

        public Task<IReadOnlyList<DestinationBackup>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DestinationBackup>>([.. _files.Select(item => new DestinationBackup(item.Key, item.Value.Bytes.Length, item.Value.At))]);

        public Task<Stream> FetchAsync(string name, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream(_files[name].Bytes));

        public Task DeleteAsync(string name, CancellationToken ct)
        {
            _files.Remove(name);
            return Task.CompletedTask;
        }

        public Task<long?> FreeSpaceAsync(CancellationToken ct) => Task.FromResult<long?>(null);
    }
}
