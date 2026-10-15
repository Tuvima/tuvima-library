using System.Reflection;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Tests;

public sealed class FileWatcherTests
{
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
