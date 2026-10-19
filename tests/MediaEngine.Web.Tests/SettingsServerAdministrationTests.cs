namespace MediaEngine.Web.Tests;

public sealed class SettingsServerAdministrationTests
{
    [Fact]
    public void Delivery_HasAUsefulRootAndUrlBackedAdvancedSections()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/PlaybackDeliverySettingsTab.razor");
        var jobs = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/OfflineDownloadsTab.razor");

        Assert.Contains("data-delivery-section", source, StringComparison.Ordinal);
        Assert.Contains("Streaming limits and quality", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private RenderFragment LandingPage", source, StringComparison.Ordinal);
        // Streaming opens on Limits and Quality; hardware acceleration, optimized copies and diagnostics are its sub-pages.
        Assert.Contains("<StreamingLimitsSettingsTab />", source, StringComparison.Ordinal);
        Assert.Contains("case \"hardware\"", source, StringComparison.Ordinal);
        Assert.Contains("case \"optimized-copies\"", source, StringComparison.Ordinal);
        Assert.Contains("Section=\"diagnostics\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("case \"scheduling\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("case \"active-jobs\"", source, StringComparison.Ordinal);
        // Hardware Acceleration, Optimized Copies and Diagnostics are rows of the Settings menu tree, not link cards.
        Assert.DoesNotContain("/settings/delivery/", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppTabs", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Active streams", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Bandwidth", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Run cleanup", jobs, StringComparison.Ordinal);
        Assert.DoesNotContain("CleanupAsync", jobs, StringComparison.Ordinal);
    }

    [Fact]
    public void Delivery_UsesEngineBackedSettingsJobsAndDiagnostics()
    {
        var settings = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/EncodeSettingsTab.razor");
        var jobs = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/OfflineDownloadsTab.razor");

        Assert.Contains("GetTranscodingSettingsAsync", settings, StringComparison.Ordinal);
        Assert.Contains("SaveTranscodingSettingsAsync", settings, StringComparison.Ordinal);
        Assert.Contains("GetPlaybackDiagnosticsAsync", settings, StringComparison.Ordinal);
        Assert.Contains("GetEncodeJobsAsync", jobs, StringComparison.Ordinal);
        Assert.Contains("CancelEncodeJobAsync", jobs, StringComparison.Ordinal);
        Assert.Contains("No sample status has been substituted", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Access_HasAUsefulRootAndUrlBackedAdvancedSections()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/UsersAccessSettingsTab.razor");

        Assert.Contains("data-access-section", source, StringComparison.Ordinal);
        Assert.Contains("case \"authentication\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("session-policy", source, StringComparison.Ordinal);
        Assert.Contains("case \"applications\"", source, StringComparison.Ordinal);
        Assert.Contains("<ManagedAccessApplications />", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsSectionHeader", source, StringComparison.Ordinal);

        var security = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/SecurityTab.razor");
        Assert.DoesNotContain("Who can connect", security, StringComparison.Ordinal);
        Assert.Contains(">Sessions</h3>", security, StringComparison.Ordinal);
        Assert.Contains("Trusted local networks", security, StringComparison.Ordinal);
        Assert.Contains("UpdateExternalAuthProviderAsync", security, StringComparison.Ordinal);
        Assert.Contains("Disabled=\"@(!ExternalCapableMode)\"", security, StringComparison.Ordinal);
    }

    [Fact]
    public void Access_DoesNotPresentDerivedIdsOrCreationDatesAsRuntimeStatus()
    {
        var users = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/ManagedAccessUsers.razor");
        var applications = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/ManagedAccessApplications.razor");

        Assert.Contains("Last active", users, StringComparison.Ordinal);
        Assert.Contains("Profile access", users, StringComparison.Ordinal);
        Assert.DoesNotContain("Manage profiles", users, StringComparison.Ordinal);
        Assert.DoesNotContain("pending invite", users, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Copy this credential now", applications, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleReveal", applications, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupRestore_ExplainsStagingAndRestartSemantics()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/BackupRecoveryPanel.razor");

        Assert.Contains("validates and stages", source, StringComparison.Ordinal);
        Assert.Contains("Restart the Engine to apply it", source, StringComparison.Ordinal);
        Assert.Contains("recovery copy", source, StringComparison.Ordinal);
        Assert.Contains("result.RestartRequired", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_backups.Take(5)", source, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            relativePath)));
}
