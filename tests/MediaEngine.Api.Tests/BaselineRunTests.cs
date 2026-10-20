using MediaEngine.Api.DevSupport;

namespace MediaEngine.Api.Tests;

public sealed class BaselineRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_baseline_run_{Guid.NewGuid():N}");

    public BaselineRunTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void Inventory_IsStableWhileNothingChangesAndMovesWhenAFileDoes()
    {
        var file = Path.Combine(_root, "books", "Dune.epub");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "original");
        File.SetLastWriteTimeUtc(file, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        // Windows can update a folder's time a moment after a file is written into it; pin it so only the file moves.
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(file)!, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var before = BaselineRun.Inventory(_root);
        var again = BaselineRun.Inventory(_root);
        File.SetLastWriteTimeUtc(file, new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var touched = BaselineRun.Inventory(_root);

        Assert.Equal(before, again);
        Assert.NotEqual(before.Digest, touched.Digest);
        Assert.Equal(before.Count, touched.Count);
    }

    [Fact]
    public void Inventory_NoticesAnAddedFileLikeAProbeLeftInASourceFolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "music"));
        var before = BaselineRun.Inventory(_root);

        File.WriteAllText(Path.Combine(_root, "music", ".tuvima_probe_x"), string.Empty);
        var after = BaselineRun.Inventory(_root);

        Assert.NotEqual(before.Digest, after.Digest);
        Assert.Equal(before.Count + 1, after.Count);
    }

    [Fact]
    public async Task RunAsync_RequiresTheRepoAndExactlyOneSampleSource()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => BaselineRun.RunAsync(["--generate"]));
        await Assert.ThrowsAsync<ArgumentException>(() => BaselineRun.RunAsync(["--repo", _root]));
        await Assert.ThrowsAsync<ArgumentException>(() => BaselineRun.RunAsync(["--repo", _root, "--generate", "--sample-root", _root]));
        await Assert.ThrowsAsync<ArgumentException>(() => BaselineRun.RunAsync(["--repo", _root, "--generate", "--label", "bad label!"]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task RunAsync_WhenTheRunFails_StillRemovesTheCopiedProviderKeys()
    {
        var configSource = Path.Combine(_root, "source-config");
        Directory.CreateDirectory(Path.Combine(configSource, "secrets"));
        File.WriteAllText(Path.Combine(configSource, "core.json"), "{}");
        File.WriteAllText(Path.Combine(configSource, "libraries.json"), "{}");
        File.WriteAllText(Path.Combine(configSource, "secrets", "tmdb.json"), "{\"api_key\":\"not-a-real-key\"}");
        var outRoot = Path.Combine(_root, "out");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => BaselineRun.RunAsync(
            ["--repo", _root, "--sample-root", Path.Combine(_root, "missing-samples"), "--config-source", configSource, "--out-root", outRoot]));

        var run = Assert.Single(Directory.GetDirectories(outRoot));
        Assert.True(File.Exists(Path.Combine(run, "config", "core.json")));
        Assert.False(Directory.Exists(Path.Combine(run, "config", "secrets")));
        Assert.True(File.Exists(Path.Combine(configSource, "secrets", "tmdb.json")));
    }

    [Fact]
    public async Task Prepare_RefusesAConfigFolderThatIsNotABaselineRunCopy()
    {
        var config = Path.Combine(_root, "config");
        var samples = Path.Combine(_root, "samples");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(samples);
        File.WriteAllText(Path.Combine(config, "core.json"), "{}");
        File.WriteAllText(Path.Combine(config, "libraries.json"), "{}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => BaselinePrepare.RunAsync(config, samples));

        Assert.Equal("{}", File.ReadAllText(Path.Combine(config, "core.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "data")));
    }

    [Fact]
    public void Prepare_HoldsEveryClockSchedule_SoARunAcrossTheNightMatchesOneThatIsNot()
    {
        var repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "MediaEngine.slnx")))
        {
            repo = Path.GetDirectoryName(repo)!;
        }

        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        File.Copy(Path.Combine(repo, "config", "maintenance.json"), Path.Combine(config, "maintenance.json"));

        var start = new DateTimeOffset(2026, 10, 20, 9, 15, 0, TimeSpan.Zero);
        BaselinePrepare.HoldClockSchedules(config, start);

        using var held = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(config, "maintenance.json")));
        var schedules = held.RootElement.GetProperty("schedules").EnumerateObject().ToList();
        Assert.NotEmpty(schedules);
        Assert.All(schedules, schedule => Assert.Equal(BaselinePrepare.HeldScheduleFrom(start), schedule.Value.GetString()));
        Assert.False(held.RootElement.GetProperty("retag_sweep").GetProperty("enabled").GetBoolean());
        Assert.False(held.RootElement.GetProperty("storage_maintenance").GetProperty("enabled").GetBoolean());
        // Fires once, 30 days on: never inside a run, and short enough for a scheduled task to wait for (Task.Delay
        // refuses anything past about 49.7 days, which stops the Engine).
        var next = MediaEngine.Api.Services.CronScheduler.NextOccurrence(BaselinePrepare.HeldScheduleFrom(start), start);
        Assert.NotNull(next);
        Assert.InRange(next.Value - start, TimeSpan.FromDays(29), TimeSpan.FromDays(31));
        Assert.True(next.Value - start < TimeSpan.FromMilliseconds(uint.MaxValue - 1));
    }
}
