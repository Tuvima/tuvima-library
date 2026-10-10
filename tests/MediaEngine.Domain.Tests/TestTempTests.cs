using MediaEngine.TestSupport;

namespace MediaEngine.Domain.Tests;

public sealed class TestTempTests
{
    [Fact]
    public void TempPath_IsInsideThePrivateRunFolder()
    {
        Assert.NotEmpty(TestTemp.RunRoot);
        Assert.StartsWith(TestTemp.RunRoot, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeleteDatabase_RemovesDatabaseAndSidecarFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_temp_helper_{Guid.NewGuid():N}.db");
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal", ".legacy-text-guid.1.bak" })
        {
            File.WriteAllText(path + suffix, "x");
        }

        TestTemp.DeleteDatabase(path);

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"));
    }

    [Fact]
    public void DeleteDatabase_MissingFile_DoesNotThrow()
    {
        TestTemp.DeleteDatabase(Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.db"));
    }

    [Fact]
    public void SweepStaleRuns_RemovesDeadAndOldRunsButKeepsLiveOnes()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"sweep_{Guid.NewGuid():N}");
        var dead = Directory.CreateDirectory(Path.Combine(baseDir, "111-20200101000000")).FullName;
        var live = Directory.CreateDirectory(Path.Combine(baseDir, "222-20200101000000")).FullName;
        var junk = Directory.CreateDirectory(Path.Combine(baseDir, "not-a-run")).FullName;
        try
        {
            TestTemp.SweepStaleRuns(baseDir, DateTime.UtcNow, pid => pid == 222);

            Assert.False(Directory.Exists(dead));
            Assert.True(Directory.Exists(live));
            Assert.False(Directory.Exists(junk));

            TestTemp.SweepStaleRuns(baseDir, DateTime.UtcNow.AddDays(2), pid => pid == 222);
            Assert.False(Directory.Exists(live));
        }
        finally
        {
            TestTemp.DeleteDirectory(baseDir);
        }
    }
}
