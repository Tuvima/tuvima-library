namespace MediaEngine.Api.Tests;

public sealed class ProfileEndpointRouteTests
{
    [Fact]
    public void ProfileEndpoints_ExposeUserOverviewWithoutUsingUnfilteredActivity()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs"));

        Assert.Contains("group.MapGet(\"/{id:guid}/overview\"", source, StringComparison.Ordinal);
        Assert.Contains("GetProfileOverview", source, StringComparison.Ordinal);
        Assert.Contains("IProfileOverviewReadService overviewReadService", source, StringComparison.Ordinal);
        Assert.Contains("overviewReadService.GetOverviewAsync(id, ct)", source, StringComparison.Ordinal);
        Assert.Contains("ApiErrors.NotFound($\"Profile '{id}' not found.\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDatabaseConnection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM user_states us", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSystemStatus", source, StringComparison.Ordinal);

        var serviceSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/ReadServices/ProfileOverviewReadService.cs"));
        Assert.Contains("GetRecentByProfileAsync(profileId, 20, ct)", serviceSource, StringComparison.Ordinal);
        Assert.Contains("FROM user_states us", serviceSource, StringComparison.Ordinal);
        Assert.Contains("RecentlyAddedItems = recentlyAdded", serviceSource, StringComparison.Ordinal);
        Assert.Contains("ConsumedSeconds = items.Sum(EstimateConsumedSeconds)", serviceSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemActivityRepository_HasProfileScopedQuery()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Storage/SystemActivityRepository.cs"));

        Assert.Contains("GetRecentByProfileAsync", source, StringComparison.Ordinal);
        Assert.Contains("WHERE  profile_id = @profileId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadersAndPlayers_SaveProgressMetadataForOverviewStats()
    {
        var watchSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Pages/WatchPlayerPage.razor"));
        var videoHostSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor"));
        var playerServiceSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/Playback/PlayerService.cs"));
        var readerSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Pages/EpubReader.razor"));

        Assert.Contains("Playback.PlayVideoAsync", watchSource, StringComparison.Ordinal);
        Assert.Contains("Playback.ReportHeartbeatAsync", videoHostSource, StringComparison.Ordinal);
        Assert.Contains("SaveResumeAsync(", playerServiceSource, StringComparison.Ordinal);
        Assert.Contains("[\"position_seconds\"]", readerSource, StringComparison.Ordinal);
        Assert.Contains("[\"media_kind\"] = \"reading\"", readerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewPolicyEndpoints_AreProfileScopedAndAdministratorOrOwnHouseholdAdministratorOnly()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs"));

        var getStart = source.IndexOf("group.MapGet(\"/{id:guid}/settings/view\"", StringComparison.Ordinal);
        var putStart = source.IndexOf("group.MapPut(\"/{id:guid}/settings/view\"", StringComparison.Ordinal);
        var playbackPut = source.IndexOf("group.MapPut(\"/{id:guid}/settings/playback\"", StringComparison.Ordinal);

        Assert.True(getStart >= 0);
        Assert.True(putStart > getStart);
        Assert.True(playbackPut > putStart);
        Assert.Contains(".RequireEffectiveAdministratorOrHouseholdAdministrator();", source[getStart..putStart], StringComparison.Ordinal);
        Assert.Contains(".RequireEffectiveAdministratorOrHouseholdAdministrator();", source[putStart..playbackPut], StringComparison.Ordinal);
        // A household administrator only reaches profiles in their own household, and never an administrator's own person.
        Assert.Contains("GetGrantAsync", source, StringComparison.Ordinal);
        Assert.Contains("MayManageViewPolicyAsync", source[getStart..putStart], StringComparison.Ordinal);
        Assert.Contains("MayManageViewPolicyAsync", source[putStart..playbackPut], StringComparison.Ordinal);
        Assert.Contains("IViewProfileRepository viewProfiles", source[getStart..playbackPut], StringComparison.Ordinal);
        Assert.DoesNotContain("RequireAnyRole", source[getStart..playbackPut], StringComparison.Ordinal);
    }

    private static string GetRepoFilePath(string relativePath)
    {
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {baseDir}");
    }
}
