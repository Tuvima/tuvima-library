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
    public void Reader_OffersPersistedAppearanceChoicesAndKeepsSessionControlsInTheReader()
    {
        var source = ReadRepoFile("src", "MediaEngine.Web", "Components", "Pages", "EpubReader.razor");
        var settings = ReadRepoFile("src", "MediaEngine.Web", "Models", "ViewDTOs", "EpubReaderDtos.cs");

        Assert.Contains("reader-theme-@ReaderTheme", source, StringComparison.Ordinal);
        Assert.Contains("reader-width-@ReaderWidth", source, StringComparison.Ordinal);
        Assert.Contains("Reading Settings", source, StringComparison.Ordinal);
        Assert.Contains("SetTheme", source, StringComparison.Ordinal);
        Assert.Contains("SetContentWidth", source, StringComparison.Ordinal);
        Assert.Contains("Theme {", settings, StringComparison.Ordinal);
        Assert.Contains("ContentWidth {", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void ReaderSettings_MigrateOlderSavedPreferencesWithoutChangingTextControls()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<MediaEngine.Web.Models.ViewDTOs.ReaderSettingsDto>(
            """{"FontFamily":"Georgia","FontSize":22,"LineHeight":2.0,"Margins":64}""");

        Assert.NotNull(settings);
        Assert.Equal("light", settings.Theme);
        Assert.Equal("standard", settings.ContentWidth);
        Assert.Equal("Georgia", settings.FontFamily);
        Assert.Equal(22, settings.FontSize);
        Assert.Equal(2.0, settings.LineHeight);
        Assert.Equal(64, settings.Margins);
    }

    private static string ReadRepoFile(params string[] segments)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine([root, .. segments]));
    }
}
