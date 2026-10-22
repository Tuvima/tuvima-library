using System.IO.Compression;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Contracts.Backups;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Backup destinations (Developer tools spec WP25, Backup and restore spec D15): the same plain backup sent to every destination
/// and read back, "keep the last N", a restore from a destination with the usual safety checks, and what a backup is allowed to hold.
/// </summary>
public sealed class BackupDestinationTests : IDisposable
{
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
        _backups = BackupTestKit.Service(_db, Path.Combine(_root, "config"), Path.Combine(_root, "backups"));
        _repository = new BackupDestinationRepository(_db);
        _settings = new BackupSettingsAccessor(_repository, _clock);
        _orchestrator = new BackupOrchestrator(
            _backups, [new LocalFolderBackupDestination(_backups), _remote], new ConfiguredBackupDestinations(_settings, new ReversingSecretStore(), new BackupHttp()), _settings, _repository, _published, _clock, NullLogger<BackupOrchestrator>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ABackupSentToARemoteDestination_IsTheSamePlainBackup_AndCheckedAfterwards()
    {
        await EnableRemoteAsync();

        var run = await _orchestrator.RunAsync(CancellationToken.None);

        Assert.True(run.AllOk);
        var file = Assert.Single(_remote.Files);
        Assert.Equal(run.ArchiveName, file.Key);
        Assert.EndsWith(".zip", file.Key);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(_backups.BackupDirectory, run.ArchiveName)), file.Value);
        using (var archive = new ZipArchive(new MemoryStream(file.Value)))
        {
            Assert.Contains(archive.Entries, entry => entry.FullName == "manifest.json");
        }

        var copy = Assert.Single(await _repository.ListCopiesAsync(_remote.Id, 10, CancellationToken.None));
        Assert.Equal("verified", copy.State);
    }

    [Fact]
    public async Task ACopyThatArrivesDamaged_ShowsAsFailed()
    {
        await EnableRemoteAsync();
        _remote.Corrupt = true;

        var run = await _orchestrator.RunAsync(CancellationToken.None);

        Assert.False(run.AllOk);
        Assert.Contains(run.Destinations, item => item.Id == _remote.Id && item.Outcome == "failed");
    }

    [Fact]
    public async Task OnlyTheNewestBackupsAreKept()
    {
        await EnableRemoteAsync(keep: 2);

        for (var i = 0; i < 4; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(5));
            await _orchestrator.RunAsync(CancellationToken.None);
        }

        Assert.Equal(2, _remote.Files.Count);
    }

    [Fact]
    public async Task RestoringFromADestination_StagesTheRestoreWithoutAnyPassphrase()
    {
        await EnableRemoteAsync();
        await _orchestrator.RunAsync(CancellationToken.None);
        var name = _remote.Files.Keys.Single();

        var staged = await _orchestrator.RestoreFromDestinationAsync(_remote, name, CancellationToken.None);

        Assert.True(staged.Scheduled);
    }

    [Fact]
    public async Task AnOlderSealedCopy_IsRefusedInPlainWords()
    {
        _remote.Plant("tuvima-backup-20260101-000000.tvbk", [1, 2, 3]);

        var ex = await Assert.ThrowsAsync<BackupDestinationException>(() => _orchestrator.RestoreFromDestinationAsync(_remote, "tuvima-backup-20260101-000000.tvbk", CancellationToken.None));

        Assert.Equal("This copy was made by an older version and can't be restored.", ex.Message);
    }

    [Fact]
    public async Task AnOlderSealedCopy_IsNeverTrimmedOrListedAsABackup()
    {
        await EnableRemoteAsync(keep: 1);
        _remote.Plant("tuvima-backup-20260101-000000.tvbk", [1, 2, 3]);

        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(5));
            await _orchestrator.RunAsync(CancellationToken.None);
        }

        Assert.Contains("tuvima-backup-20260101-000000.tvbk", _remote.Files.Keys);
        Assert.Equal(2, _remote.Files.Count);
    }

    [Fact]
    public async Task ARestoreNameThatIsAPath_IsRefused() =>
        await Assert.ThrowsAsync<BackupDestinationException>(() => _orchestrator.RestoreFromDestinationAsync(_remote, "../outside.zip", CancellationToken.None));

    [Fact]
    public async Task ABackupHoldsOnlySettingsPersonalStateAndConfiguration_NeverMediaFilesOrAStoreCopy()
    {
        // Media files and folders are the operating system's job to back up (Shy, 2026-10-21): the archive carries the
        // Library's settings and each person's own state (as data entries) and safe configuration only.
        var path = await _backups.CreateAsync(CancellationToken.None);

        using var archive = ZipFile.OpenRead(path);
        Assert.All(archive.Entries, entry => Assert.True(
            entry.FullName == "manifest.json"
            || entry.FullName.StartsWith("data/", StringComparison.Ordinal)
            || entry.FullName.StartsWith("config/", StringComparison.Ordinal),
            entry.FullName));
        Assert.Null(archive.GetEntry("database/library.db"));
    }

    [Fact]
    public async Task AFileThatIsNotABackup_FetchedFromADestination_IsRefusedAndLeavesNothingBehind()
    {
        _remote.Plant("tuvima-backup-planted.zip", [1, 2, 3]);

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => _orchestrator.RestoreFromDestinationAsync(_remote, "tuvima-backup-planted.zip", CancellationToken.None));

        Assert.DoesNotContain(Directory.GetFiles(_backups.BackupDirectory), path => Path.GetFileName(path).Contains("restored-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABackupFromADestinationHoldingSecrets_IsRefusedWhenRestored()
    {
        var good = await _backups.CreateAsync(CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(good);
        using (var memory = new MemoryStream())
        {
            memory.Write(bytes);
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("config/secrets/key.json").Open());
                writer.Write("{}");
            }

            _remote.Plant("tuvima-backup-hostile.zip", memory.ToArray());
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => _orchestrator.RestoreFromDestinationAsync(_remote, "tuvima-backup-hostile.zip", CancellationToken.None));

        Assert.DoesNotContain(Directory.GetFiles(_backups.BackupDirectory), path => Path.GetFileName(path).Contains("restored-", StringComparison.Ordinal));
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

    private async Task EnableRemoteAsync(int keep = 10)
    {
        var settings = await _settings.LoadAsync(CancellationToken.None);
        settings.EnabledDestinations = [_remote.Id];
        settings.KeepLast = keep;
        await _settings.SaveAsync(settings, CancellationToken.None);
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
