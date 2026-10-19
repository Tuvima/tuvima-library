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
}
