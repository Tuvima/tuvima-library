using System.Reflection;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Tests;

public sealed class FileWatcherTests
{
    [Theory]
    [InlineData(@"C:\Library\Books\Dune\Dune.epub.tuvima.bak", true)]
    [InlineData(@"C:\Library\Books\Dune\Dune.EPUB.TUVIMA.BAK", true)]
    [InlineData(@"C:\Library\Books\Dune\Dune.epub", false)]
    public void TheLibrarysOwnTagBackups_AreNeverPickedUpAsNewFiles(string path, bool ignored)
    {
        var method = typeof(IngestionEngine).GetMethod("IsIgnoredScanFile", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Equal(ignored, (bool)method.Invoke(null, [path])!);
    }

    [Fact]
    public void OnError_RecordsStatusAndRaisesWatcherError()
    {
        using var watcher = new FileWatcher();
        FileWatcherErrorEvent? observed = null;
        watcher.WatcherError += (_, evt) => observed = evt;

        var onError = typeof(FileWatcher).GetMethod(
            "OnError",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onError);

        onError.Invoke(
            watcher,
            [watcher, new ErrorEventArgs(new InternalBufferOverflowException("buffer overflow"))]);

        Assert.Equal(1, watcher.ErrorCount);
        Assert.NotNull(watcher.LastErrorAt);
        Assert.Equal("buffer_overflow", watcher.LastErrorKind);
        Assert.Equal("buffer overflow", watcher.LastErrorMessage);
        Assert.NotNull(observed);
        Assert.True(observed.IsBufferOverflow);
        Assert.Equal("buffer_overflow", observed.Kind);
    }

    [Fact]
    public void RemoveDirectory_stops_only_that_folder_and_leaves_the_others_watched()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tuvima_fw_remove_{Guid.NewGuid():N}");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            using var watcher = new FileWatcher();
            watcher.AddDirectory(first);
            watcher.AddDirectory(second);
            watcher.Start();

            Assert.True(watcher.RemoveDirectory(first + Path.DirectorySeparatorChar));
            Assert.False(watcher.RemoveDirectory(first));

            Assert.Equal([second], watcher.WatchedPaths);
            Assert.True(watcher.IsRunning);
            Assert.True(Directory.Exists(first), "removing a watch never touches the folder");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_folder_added_after_Start_raises_events_straight_away_and_a_removed_one_stops()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tuvima_fw_live_{Guid.NewGuid():N}");
        var folder = Path.Combine(root, "live");
        Directory.CreateDirectory(folder);
        try
        {
            using var watcher = new FileWatcher();
            var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
            watcher.FileDetected += (_, evt) => seen.Enqueue(evt.Path);
            watcher.Start();

            watcher.AddDirectory(folder);
            var first = Path.Combine(folder, "first.epub");
            File.WriteAllText(first, "x");
            await WaitForAsync(() => seen.Any(path => path == first));

            Assert.True(watcher.RemoveDirectory(folder));
            var second = Path.Combine(folder, "second.epub");
            File.WriteAllText(second, "x");
            await Task.Delay(500);

            Assert.DoesNotContain(second, seen);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_event_already_in_flight_for_a_removed_folder_is_dropped()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tuvima_fw_inflight_{Guid.NewGuid():N}");
        var removed = Path.Combine(root, "removed");
        var kept = Path.Combine(root, "kept");
        Directory.CreateDirectory(removed);
        Directory.CreateDirectory(kept);
        try
        {
            using var watcher = new FileWatcher();
            var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
            watcher.FileDetected += (_, evt) => seen.Enqueue(evt.Path);
            watcher.AddDirectory(removed);
            watcher.AddDirectory(kept);
            watcher.Start();

            var field = typeof(FileWatcher).GetField("_watchers", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var all = (List<FileSystemWatcher>)field.GetValue(watcher)!;
            var removedWatcher = all.Single(w => w.Path == removed);
            var keptWatcher = all.Single(w => w.Path == kept);
            Assert.True(watcher.RemoveDirectory(removed));

            var onCreated = typeof(FileWatcher).GetMethod("OnCreated", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(onCreated);
            var removedFile = Path.Combine(removed, "late.epub");
            var keptFile = Path.Combine(kept, "ok.epub");
            File.WriteAllText(removedFile, "x");
            File.WriteAllText(keptFile, "x");
            onCreated.Invoke(watcher, [removedWatcher, new FileSystemEventArgs(WatcherChangeTypes.Created, removed, "late.epub")]);
            onCreated.Invoke(watcher, [keptWatcher, new FileSystemEventArgs(WatcherChangeTypes.Created, kept, "ok.epub")]);

            // The real kept watcher may report its own file too; what matters is that nothing arrives for the removed folder.
            Assert.Contains(keptFile, seen);
            Assert.DoesNotContain(seen, path => path.StartsWith(removed, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetIncludeSubdirectories_reports_each_watched_folders_setting()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tuvima_fw_subs_{Guid.NewGuid():N}");
        var flat = Path.Combine(root, "flat");
        var deep = Path.Combine(root, "deep");
        Directory.CreateDirectory(flat);
        Directory.CreateDirectory(deep);
        try
        {
            using var watcher = new FileWatcher();
            watcher.AddDirectory(flat, includeSubdirectories: false);
            watcher.AddDirectory(deep, includeSubdirectories: true);

            Assert.False(watcher.GetIncludeSubdirectories(flat));
            Assert.True(watcher.GetIncludeSubdirectories(deep + Path.DirectorySeparatorChar));
            Assert.Null(watcher.GetIncludeSubdirectories(Path.Combine(root, "other")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for a file event.");
            await Task.Delay(25);
        }
    }
}
