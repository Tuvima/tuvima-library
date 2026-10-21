namespace MediaEngine.Api.Tests;

/// <summary>
/// The Videos Editing Mode actions on the Engine: every one needs the same permission as the video order, nothing here moves a
/// file except the explicit rename, and the YouTube lookup only runs where the video's library allows it.
/// </summary>
public sealed class VideoEditingEndpointRouteTests
{
    private static readonly string[] Routes =
    [
        "\"/{entityId:guid}/video-order\"",
        "\"/{entityId:guid}/video-rename\"",
        "\"/{entityId:guid}/video-series\"",
        "\"/{entityId:guid}/video-series/remove\"",
        "\"/{entityId:guid}/video-identify\"",
    ];

    [Fact]
    public void EveryVideoEditingRoute_RequiresTheMetadataWritePermission_LikeTheVideoOrder()
    {
        var source = ReadCanonicalEndpoints();

        foreach (var route in Routes)
        {
            var from = source.IndexOf(route, StringComparison.Ordinal);
            Assert.True(from >= 0, $"{route} is not mapped");
            // Each GET/PUT/POST registration ends with its auth lines before the next one starts.
            var next = source.IndexOf("group.Map", from + route.Length, StringComparison.Ordinal);
            var block = source[from..(next < 0 ? source.Length : next)];
            Assert.Contains("RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)", block, StringComparison.Ordinal);
            Assert.Contains("RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite)", block, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RenamingFiles_GoesThroughTheSharedRenamer_AndRefusesAGroupThatIsNotWhollyManaged()
    {
        var source = ReadCanonicalEndpoints();
        var rename = Between(source, "\"/{entityId:guid}/video-rename\"", "group.MapGet(\"/{entityId:guid}/video-series\"");

        Assert.Contains("renamer.CanRenameAllAsync(", rename, StringComparison.Ordinal);
        Assert.Contains("ApiErrors.Conflict(", rename, StringComparison.Ordinal);
        Assert.Contains("renamer.RenameAsync(", rename, StringComparison.Ordinal);
        Assert.True(
            rename.IndexOf("CanRenameAllAsync", StringComparison.Ordinal) < rename.IndexOf("renamer.RenameAsync", StringComparison.Ordinal),
            "the folder check must come before any rename");

        var renamer = ReadSource("src/MediaEngine.Ingestion/Organise/VideoOrderRenamer.cs");
        Assert.Contains("AllowsFileMutation", renamer, StringComparison.Ordinal);
        Assert.Contains("SourceMutationKind.Rename", renamer, StringComparison.Ordinal);
        Assert.Contains("_organizer.ExecuteMoveAsync", renamer, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Move", renamer, StringComparison.Ordinal);
    }

    [Fact]
    public void JoiningOrLeavingASeries_NeverTouchesAFile()
    {
        var source = ReadCanonicalEndpoints();
        var series = Between(source, "group.MapGet(\"/{entityId:guid}/video-series\"", "group.MapGet(\"/{entityId:guid}/video-identify\"");

        Assert.DoesNotContain("File.", series, StringComparison.Ordinal);
        Assert.DoesNotContain("renamer", series, StringComparison.Ordinal);

        // The locked series fields are written by the repository, in the same transaction as the move.
        var repository = ReadSource("src/MediaEngine.Storage/VideoSeriesRepository.cs");
        Assert.Contains("IsUserLocked = true", repository, StringComparison.Ordinal);
        Assert.Contains("MetadataFieldConstants.VideoEpisodeIndex", repository, StringComparison.Ordinal);
        Assert.Contains("_db.ExecuteWriteAsync", repository, StringComparison.Ordinal);
        Assert.DoesNotContain("claimRepo", series, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSeriesVideoCanJoin_AreCheckedAgainstTheCallersAccess()
    {
        var source = ReadCanonicalEndpoints();
        var list = Between(source, "group.MapGet(\"/{entityId:guid}/video-series\"", "group.MapPut(\"/{entityId:guid}/video-series\"");
        var put = Between(source, "group.MapPut(\"/{entityId:guid}/video-series\"", ".WithName(\"PromoteVideoToSeries\")");

        Assert.Contains("ListTargetsAsync(workId", list, StringComparison.Ordinal);
        Assert.Contains("EvaluateAnyEntityAsync(", list, StringComparison.Ordinal);
        Assert.Contains("EvaluateAnyEntityAsync(", put, StringComparison.Ordinal);
        Assert.True(
            put.IndexOf("EvaluateAnyEntityAsync", StringComparison.Ordinal) < put.IndexOf("series.PromoteAsync(", StringComparison.Ordinal),
            "the chosen series must be checked before the video moves");
    }

    [Fact]
    public void IdentifyingFromALink_OnlyRunsWhereTheLibraryAllowsTheYoutubeLookup()
    {
        var source = ReadCanonicalEndpoints();
        var identify = Between(source, "group.MapPost(\"/{entityId:guid}/video-identify\"", "group.MapPut(\"/{entityId:guid}/display-overrides\"");

        var gate = identify.IndexOf("YoutubeLookup != true", StringComparison.Ordinal);
        var lookup = identify.IndexOf("lookup.LookupAsync(", StringComparison.Ordinal);
        Assert.True(gate >= 0 && lookup > gate, "the library switch must be checked before anything is sent");
        Assert.Contains("YoutubeLink.TryGetVideoId(", identify, StringComparison.Ordinal);
        Assert.Contains("lookup.LookupAsync(videoId", identify, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentifyingFromALink_NeverAsksYoutubeAboutAVaultVideo()
    {
        var identify = Between(ReadCanonicalEndpoints(), "group.MapPost(\"/{entityId:guid}/video-identify\"", "group.MapPut(\"/{entityId:guid}/display-overrides\"");

        var gate = identify.IndexOf("audienceGate.MayAskAboutItemAsync(\"youtube\"", StringComparison.Ordinal);
        var lookup = identify.IndexOf("lookup.LookupAsync(videoId", StringComparison.Ordinal);
        Assert.True(gate >= 0 && lookup > gate, "the audience gate must be asked before anything is sent to YouTube");
    }

    private static string ReadCanonicalEndpoints() => ReadSource("src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs");

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"{start} not found");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"{end} not found after {start}");
        return source[from..to];
    }

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
