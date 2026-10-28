using MediaEngine.Api.Services.Libraries;

namespace MediaEngine.Api.Tests;

public sealed class FreeSpaceCheckTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void MarginFor_IsOneGigabyteForSmallTotals()
    {
        Assert.Equal(Gb, FreeSpaceCheck.MarginFor(0));
        Assert.Equal(Gb, FreeSpaceCheck.MarginFor(5 * Gb));
    }

    [Fact]
    public void MarginFor_IsTenPercentForLargeTotals() =>
        Assert.Equal(20 * Gb, FreeSpaceCheck.MarginFor(200 * Gb));

    [Fact]
    public void Check_FitsWhenFreeSpaceCoversTheSourcesAndTheMargin()
    {
        var result = FreeSpaceCheck.Check([3 * Gb, 4 * Gb], freeBytes: 9 * Gb);

        Assert.True(result.Fits);
        Assert.True(result.Verified);
        Assert.Equal(7 * Gb, result.SourceBytes);
        Assert.Equal(8 * Gb, result.NeededBytes);
        Assert.Equal(9 * Gb, result.FreeBytes);
        Assert.Null(result.Message);
    }

    [Fact]
    public void Check_RefusesWhenTheMarginDoesNotFitEvenThoughTheFilesWould()
    {
        var result = FreeSpaceCheck.Check([7 * Gb], freeBytes: 7 * Gb + Gb / 2);

        Assert.False(result.Fits);
        Assert.Equal("Not enough space: 8 GB needed, 7.5 GB free.", result.Message);
    }

    [Fact]
    public void Check_SaysHowMuchIsNeededAndHowMuchIsFree()
    {
        var result = FreeSpaceCheck.Check([(long)(7.8 * Gb)], freeBytes: 2 * Gb);

        Assert.False(result.Fits);
        Assert.Equal("Not enough space: 8.8 GB needed, 2 GB free.", result.Message);
    }

    [Fact]
    public void Check_UsesTenPercentWhenThatIsMoreThanOneGigabyte()
    {
        var result = FreeSpaceCheck.Check([100 * Gb], freeBytes: 105 * Gb);

        Assert.False(result.Fits);
        Assert.Equal(110 * Gb, result.NeededBytes);
    }

    [Fact]
    public void Check_DoesNotRefuseWhenFreeSpaceCouldNotBeRead()
    {
        var result = FreeSpaceCheck.Check([50 * Gb], freeBytes: null);

        Assert.True(result.Fits);
        Assert.False(result.Verified);
        Assert.Null(result.FreeBytes);
        Assert.Null(result.Message);
    }

    [Fact]
    public void Check_AnEmptyListNeedsOnlyTheMargin()
    {
        var result = FreeSpaceCheck.Check([], freeBytes: 2 * Gb);

        Assert.True(result.Fits);
        Assert.Equal(Gb, result.NeededBytes);
    }

    [Fact]
    public void Check_ReadsTheRealDriveForAnExistingFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tuvima-free-space-tests", Guid.NewGuid().ToString("N"), "not-created-yet");

        var result = FreeSpaceCheck.Check([1024], folder);

        Assert.True(result.Fits);
        Assert.NotNull(FreeSpaceCheck.FreeBytesAt(folder));
    }

    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(512L, "512 bytes")]
    [InlineData(12L * 1024, "12 KB")]
    [InlineData(640L * 1024 * 1024, "640 MB")]
    [InlineData((long)(7.8 * 1024 * 1024 * 1024), "7.8 GB")]
    public void FormatBytes_WritesSizesPeopleRead(long bytes, string expected) =>
        Assert.Equal(expected, FreeSpaceCheck.FormatBytes(bytes));
}
