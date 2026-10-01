using MediaEngine.Ingestion;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// Tests for <see cref="BackedUpMetadataTagger"/> — the shared backup/mutate/restore
/// template used by every <see cref="MediaEngine.Ingestion.Contracts.IMetadataTagger"/>
/// implementation.
///
/// Before this base class existed, each of the four taggers hand-rolled its own copy
/// of this flow, and three of the four used <c>RestoreBackup(string filePath, string backupPath)</c>
/// while one (<see cref="EpubMetadataTagger"/>) used the inverted
/// <c>RestoreBackup(string backup, string original)</c>. A copy-paste between them
/// without noticing the swapped argument order would silently restore a file
/// backwards. <see cref="WithBackup_RestoreDirection_NeverInvertedRegressionTest"/>
/// is the direct regression test for that footgun.
/// </summary>
public sealed class BackedUpMetadataTaggerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"tuvima-backup-tagger-{Guid.NewGuid():N}");

    public BackedUpMetadataTaggerTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort test cleanup */ }
    }

    private string CreateFile(string content)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        return path;
    }

    // ── 1. Success path ─────────────────────────────────────────────────────

    [Fact]
    public void WithBackup_SuccessPath_MutatesFileAndDeletesBackup()
    {
        var path = CreateFile("ORIGINAL");
        var backupPath = path + BackedUpMetadataTagger.BackupSuffix;
        var tagger = new TestBackedUpTagger(new SpyLogger());

        tagger.RunSync(
            path,
            mutate: () =>
            {
                File.WriteAllText(path, "MUTATED");
                // Backup deletion on success is caller-owned (mirrors every real
                // tagger, which deletes the backup itself right after saving).
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            },
            onFailure: _ => { });

        Assert.Equal("MUTATED", File.ReadAllText(path));
        Assert.False(File.Exists(backupPath));
    }

    [Fact]
    public async Task WithBackupAsync_SuccessPath_MutatesFileAndDeletesBackup()
    {
        var path = CreateFile("ORIGINAL");
        var backupPath = path + BackedUpMetadataTagger.BackupSuffix;
        var tagger = new TestBackedUpTagger(new SpyLogger());

        await tagger.RunAsync(
            path,
            mutate: async () =>
            {
                await File.WriteAllTextAsync(path, "MUTATED");
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            },
            onFailure: _ => { });

        Assert.Equal("MUTATED", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(backupPath));
    }

    // ── 2. Failure path: original content restored, original exception propagates ──

    [Fact]
    public void WithBackup_FailurePath_RestoresOriginalContentByteForByte_AndRethrows()
    {
        var path = CreateFile("ORIGINAL CONTENT");
        var tagger = new TestBackedUpTagger(new SpyLogger());
        var thrown = new InvalidOperationException("mutate failed");
        Exception? observedByOnFailure = null;

        var caught = Assert.Throws<InvalidOperationException>(() =>
            tagger.RunSync(
                path,
                mutate: () =>
                {
                    File.WriteAllText(path, "PARTIALLY WRITTEN GARBAGE");
                    throw thrown;
                },
                onFailure: ex => observedByOnFailure = ex));

        Assert.Same(thrown, caught);
        Assert.Same(thrown, observedByOnFailure);
        Assert.Equal("ORIGINAL CONTENT", File.ReadAllText(path));
    }

    // ── 3. Restore direction regression test (the inversion bug) ───────────

    [Fact]
    public void WithBackup_RestoreDirection_NeverInvertedRegressionTest()
    {
        var path = CreateFile("ORIGINAL");
        var backupPath = path + BackedUpMetadataTagger.BackupSuffix;
        var tagger = new TestBackedUpTagger(new SpyLogger());

        Assert.Throws<InvalidOperationException>(() =>
            tagger.RunSync(
                path,
                mutate: () =>
                {
                    // Simulate a tagger corrupting the file before failing.
                    File.WriteAllText(path, "CORRUPTED-BY-MUTATE");
                    throw new InvalidOperationException("boom");
                },
                onFailure: _ => { }));

        // The ORIGINAL must be restored from the BACKUP — never the reverse.
        // (The inverted bug would have left "CORRUPTED-BY-MUTATE" in place, or
        // worse, overwritten the backup with the corrupted content.)
        Assert.Equal("ORIGINAL", File.ReadAllText(path));

        // The backup itself is left in place (not deleted) on a failed write —
        // this matches three of the four original taggers, whose failure path
        // never deleted the backup, leaving it for AutoOrganizeService's sweep.
        Assert.True(File.Exists(backupPath));
        Assert.Equal("ORIGINAL", File.ReadAllText(backupPath));
    }

    // ── 4. Restore failure: original exception still propagates ────────────

    [Fact]
    public void WithBackup_RestoreFailure_OriginalExceptionStillPropagates_AndCriticalLogged()
    {
        var path = CreateFile("ORIGINAL");
        var spy = new SpyLogger();
        var tagger = new TestBackedUpTagger(spy);
        var thrown = new InvalidOperationException("mutate failed");

        // Hold the original file open with a share mode that permits other
        // readers (so the initial backup copy succeeds) but denies writers —
        // this makes the template's restore copy-back
        // (File.Copy(backupPath, filePath, overwrite: true)) throw, which is
        // exactly the "restore also failed" scenario the CRITICAL log guards.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var caught = Assert.Throws<InvalidOperationException>(() =>
                tagger.RunSync(
                    path,
                    mutate: () => throw thrown,
                    onFailure: _ => { }));

            Assert.Same(thrown, caught);
        }

        Assert.Contains(spy.Entries, e => e.Level == LogLevel.Critical);
    }

    [Fact]
    public void WithBackup_WhenBackupCannotBeCreated_DoesNotMutateSource()
    {
        var path = CreateFile("ORIGINAL");
        var backupPath = path + BackedUpMetadataTagger.BackupSuffix;
        var tagger = new TestBackedUpTagger(new SpyLogger());
        Directory.CreateDirectory(backupPath);
        var mutateCalled = false;

        Assert.ThrowsAny<IOException>(() =>
            tagger.RunSync(
                path,
                mutate: () => mutateCalled = true,
                onFailure: _ => { }));

        Assert.False(mutateCalled);
        Assert.Equal("ORIGINAL", File.ReadAllText(path));
        Directory.Delete(backupPath);
    }

    [Fact]
    public void WithBackup_ExistingRecoveryCopyIsNeverOverwritten()
    {
        var path = CreateFile("CURRENT FILE");
        var backupPath = path + BackedUpMetadataTagger.BackupSuffix;
        File.WriteAllText(backupPath, "OLDER RECOVERABLE FILE");
        var tagger = new TestBackedUpTagger(new SpyLogger());
        var mutateCalled = false;

        Assert.Throws<IOException>(() => tagger.RunSync(path,
            mutate: () => mutateCalled = true,
            onFailure: _ => { }));

        Assert.False(mutateCalled);
        Assert.Equal("CURRENT FILE", File.ReadAllText(path));
        Assert.Equal("OLDER RECOVERABLE FILE", File.ReadAllText(backupPath));
    }
}

/// <summary>
/// Minimal concrete subclass exposing <see cref="BackedUpMetadataTagger"/>'s
/// protected template methods so tests can drive them directly.
/// </summary>
file sealed class TestBackedUpTagger : BackedUpMetadataTagger
{
    public TestBackedUpTagger(ILogger logger) : base(logger, "TestTagger")
    {
    }

    public void RunSync(
        string filePath,
        Action mutate,
        Action<Exception> onFailure)
        => WithBackup(filePath, mutate, onFailure);

    public Task RunAsync(
        string filePath,
        Func<Task> mutate,
        Action<Exception> onFailure)
        => WithBackupAsync(filePath, mutate, onFailure);
}

/// <summary>
/// Hand-written <see cref="ILogger"/> spy (no Moq, matching this repo's test
/// conventions) that records every log entry so tests can assert the CRITICAL
/// restore-failure path actually ran.
/// </summary>
file sealed class SpyLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }
}
