using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The first check of a newly added folder takes its numbers from <c>config/maintenance.json</c> (Library setup spec D35): how many files it
/// tries and what share of Unmatched files pauses the folder. Out-of-range numbers are refused and missing ones fall back to the defaults.
/// </summary>
public sealed class FirstCheckSettingsValidationTests
{
    private static MaintenanceSettings Shipped()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return JsonSerializer.Deserialize<MaintenanceSettings>(File.ReadAllText(Path.Combine(dir!, "config", "maintenance.json")))!;
    }

    [Fact]
    public void TheDefaults_ApplyWhenTheSettingsAreMissing_AndMatchTheShippedFile()
    {
        var missing = JsonSerializer.Deserialize<MaintenanceSettings>("{}")!;
        var shipped = Shipped();

        Assert.Equal((50, 0.5), (missing.FirstCheckFiles, missing.FirstCheckUnmatchedShare));
        Assert.Equal((50, 0.5), (shipped.FirstCheckFiles, shipped.FirstCheckUnmatchedShare));
        Assert.Empty(JsonConfigValidator.Validate(missing, "maintenance.json"));
        Assert.Empty(JsonConfigValidator.Validate(shipped, "maintenance.json"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void FewerThanOneFile_IsRefused(int files)
    {
        var errors = JsonConfigValidator.Validate(new MaintenanceSettings { FirstCheckFiles = files }, "maintenance.json");

        Assert.Contains(errors, error => error.StartsWith("first_check_files must be at least 1", StringComparison.Ordinal));
    }

    [Fact]
    public void OneFile_IsTheSmallestFirstCheckAllowed()
    {
        Assert.Empty(JsonConfigValidator.Validate(new MaintenanceSettings { FirstCheckFiles = 1 }, "maintenance.json"));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.2)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void AShareThatIsNotAboveZeroAndBelowOne_IsRefused(double share)
    {
        var errors = JsonConfigValidator.Validate(new MaintenanceSettings { FirstCheckUnmatchedShare = share }, "maintenance.json");

        Assert.Contains(errors, error => error.StartsWith("first_check_unmatched_share must be above 0 and below 1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.5)]
    [InlineData(0.99)]
    public void AShareBetweenZeroAndOne_IsAccepted(double share)
    {
        Assert.Empty(JsonConfigValidator.Validate(new MaintenanceSettings { FirstCheckUnmatchedShare = share }, "maintenance.json"));
    }

    [Theory]
    [InlineData(30, 50, 0.5, true)]
    [InlineData(25, 50, 0.5, false)]
    [InlineData(6, 8, 0.5, true)]
    [InlineData(4, 8, 0.5, false)]
    [InlineData(0, 0, 0.5, false)]
    [InlineData(1, 10, 0.05, true)]
    public void MoreThanTheShare_IsWhatPausesAFolder(int unmatched, int tried, double share, bool expected)
    {
        Assert.Equal(expected, FirstCheckLimits.ExceedsShare(unmatched, tried, share));
    }

    [Fact]
    public void ASettingOutsideTheLimits_FallsBackToTheDefaultShare()
    {
        Assert.True(FirstCheckLimits.ExceedsShare(6, 10, 7.0));
        Assert.False(FirstCheckLimits.ExceedsShare(5, 10, 0));
    }

    [Theory]
    [InlineData(@"D:\Media\Movies\", "d:/media/movies")]
    [InlineData("/mnt/Media/Films/", "/mnt/media/films")]
    [InlineData("D:/Media/Ünïcode", "d:/media/Ünïcode")]
    public void AFolderPath_IsComparedWithSeparatorsAndAsciiCaseFolded(string path, string expected)
    {
        Assert.Equal(expected, MediaEngine.Domain.Models.FolderFirstCheckPaths.Key(path));
    }
}
