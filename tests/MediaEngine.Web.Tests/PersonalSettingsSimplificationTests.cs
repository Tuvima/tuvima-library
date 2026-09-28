namespace MediaEngine.Web.Tests;

public sealed class PersonalSettingsSimplificationTests
{
    [Fact]
    public void ProfilePage_CombinesUsefulPersonalInformationWithoutAppearanceControls()
    {
        var source = ReadRepoFile("src", "MediaEngine.Web", "Components", "Settings", "UserOverviewTab.razor");

        Assert.Contains("Activity summary", source, StringComparison.Ordinal);
        Assert.Contains("Continue where you left off", source, StringComparison.Ordinal);
        Assert.Contains(">Taste<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveSubsection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Appearance", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AccentColor", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackPage_OnlySurfacesRuntimeBackedGlobalDefaults()
    {
        var source = ReadRepoFile("src", "MediaEngine.Web", "Components", "Settings", "PlaybackTab.razor");

        Assert.Contains("Resume & Progress", source, StringComparison.Ordinal);
        Assert.Contains("Default playback speed", source, StringComparison.Ordinal);
        Assert.Contains("Audiobook default speed", source, StringComparison.Ordinal);
        Assert.Contains("Resume rewind", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Theme", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultSleepTimer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PreferredVideoQuality", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_UsesOneSupportedPaletteAndKeepsSessionControlsInTheReader()
    {
        var source = ReadRepoFile("src", "MediaEngine.Web", "Components", "Pages", "EpubReader.razor");
        var settings = ReadRepoFile("src", "MediaEngine.Web", "Models", "ViewDTOs", "EpubReaderDtos.cs");

        Assert.Contains("reader-theme-dark", source, StringComparison.Ordinal);
        Assert.Contains("Reading Settings", source, StringComparison.Ordinal);
        Assert.DoesNotContain("reader-theme-picker", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetTheme", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Theme {", settings, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] segments)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine([root, .. segments]));
    }
}
