using System.IO;
using System.Text.RegularExpressions;

namespace MediaEngine.Api.Tests;

public sealed class ItemEndpointRouteTests
{
    [Fact]
    public void LibraryItemEndpoints_UseLibraryItemsRootWithoutDuplicateItemsSegment()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs"));

        Assert.Contains("var group = app.MapGroup(\"/library/items\")", source, StringComparison.Ordinal);
        Assert.Contains("group.MapGet(\"\", async (", source, StringComparison.Ordinal);
        Assert.Contains("group.MapGet(\"/{entityId}/detail\", async (", source, StringComparison.Ordinal);
        Assert.Contains("group.MapGet(\"/{entityId:guid}/history\", async (", source, StringComparison.Ordinal);
        Assert.DoesNotContain("group.MapGet(\"/items\", async (", source, StringComparison.Ordinal);
        Assert.DoesNotContain("group.MapGet(\"/items/{entityId}/detail\", async (", source, StringComparison.Ordinal);
        Assert.DoesNotContain("group.MapGet(\"/items/{entityId:guid}/history\", async (", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_MapsLibraryItemEndpointsOnly()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/DependencyInjection/ApiEndpointRouteBuilderExtensions.cs"));
        var oldSurface = "Reg" + "istry";

        Assert.Contains("MapLibraryItemEndpoints", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Map" + oldSurface + "Endpoints", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackedSource_DoesNotContainRemovedLibraryItemSurfaceNames()
    {
        var root = GetRepoFilePath("");
        var offenders = FindRemovedLibraryItemSurfaceOffenders(root);

        Assert.Empty(offenders);
    }

    [Fact]
    public void TrackedSource_FindsRetiredRootRoutesAndImplementationSymbols()
    {
        Assert.True(ContainsRemovedLibraryItemSurface("[Old route](/" + "registry)"));
        Assert.True(ContainsRemovedLibraryItemSurface("Href=\"/" + "registry\""));
        Assert.True(ContainsRemovedLibraryItemSurface("public static class Reg" + "istryEndpoints"));
        Assert.True(ContainsRemovedLibraryItemSurface("app.MapReg" + "istryEndpoints()"));
    }

    [Fact]
    public void TrackedSource_IgnoresGeneratedSiteFilesAndBenignSlashSeparatedProse()
    {
        Assert.True(IsIgnoredGeneratedSiteFile(Path.Combine("site", "search", "search_index.json")));
        Assert.False(ContainsRemovedLibraryItemSurface("gateway/registry integration"));
    }

    [Fact]
    public void ItemCanonicalEndpoints_AreGenericItemEndpointsUnderLibraryItems()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs"));

        Assert.Contains("public static class ItemCanonicalEndpoints", source, StringComparison.Ordinal);
        Assert.Contains("MapItemCanonicalEndpoints", source, StringComparison.Ordinal);
        Assert.Contains(".WithTags(\"Items\")", source, StringComparison.Ordinal);
        Assert.Contains("group.MapPut(\"/{entityId:guid}/preferences\", async (", source, StringComparison.Ordinal);
        Assert.Contains("group.MapGet(\"/{entityId:guid}/editor-preferences/{profileId:guid}\", async (", source, StringComparison.Ordinal);
        Assert.Contains("group.MapPut(\"/{entityId:guid}/editor-preferences/{profileId:guid}\", async (", source, StringComparison.Ordinal);
        Assert.Contains("group.MapPost(\"/{entityId:guid}/canonical-search\", async (", source, StringComparison.Ordinal);
        Assert.Contains("group.MapPost(\"/{entityId:guid}/canonical-apply\", async (", source, StringComparison.Ordinal);
        Assert.Contains("group.MapPost(\"/{entityId:guid}/retail-match-preview\", async (", source, StringComparison.Ordinal);
        Assert.Contains("group.MapPost(\"/{entityId:guid}/retail-match\", async (", source, StringComparison.Ordinal);
        Assert.Contains("group.MapPost(\"/{entityId:guid}/wikidata-match\", async (", source, StringComparison.Ordinal);
        Assert.Contains("expected_identity_revision", File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Contracts/Matching/MatchingDtos.cs")), StringComparison.Ordinal);
        Assert.Contains("IsGenericTvChildPolicy(policy)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemEditorEndpoints_ResolveCurrentMediaAssetOrWorkTargets()
    {
        var canonical = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs"));
        var canonicalData = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Storage/ItemCanonicalRepository.cs"));
        var libraryItems = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs"));
        var libraryItemData = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Storage/LibraryItemCurationRepository.cs"));

        Assert.Contains("ResolveWorkAssetContextAsync", canonical, StringComparison.Ordinal);
        Assert.Contains("No current media asset or work target found", canonical, StringComparison.Ordinal);
        Assert.Contains("GuidSql.ToBlob(entityId)", canonicalData, StringComparison.Ordinal);
        Assert.Contains("store.ResolveTargetAsync(entityId, ct)", libraryItems, StringComparison.Ordinal);
        Assert.Contains("WHERE ma.id = @entityId", libraryItemData, StringComparison.Ordinal);
        Assert.Contains("OR e.work_id = @entityId", libraryItemData, StringComparison.Ordinal);
        Assert.DoesNotContain("No media asset found for work", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("No media asset found for work", libraryItems, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemCanonicalEndpoints_RouteManualWritesByLineageScope()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs"));
        var canonicalData = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Storage/ItemCanonicalRepository.cs"));

        Assert.Contains("IWorkRepository workRepo", source, StringComparison.Ordinal);
        Assert.Contains("ResolveScopedTarget(context.AssetId, lineage, key)", source, StringComparison.Ordinal);
        Assert.Contains("ResolveScopedTarget(context.AssetId, lineage, kv.Key)", source, StringComparison.Ordinal);
        Assert.Contains("ClaimScopeCatalog.IsParentScoped(key, lineage.MediaType)", source, StringComparison.Ordinal);
        Assert.Contains("COALESCE(ma.id, child_ma.id, grandchild_ma.id)", canonicalData, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemCanonicalEndpoints_UseTypedDataServiceInsteadOfDirectDatabaseAccess()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs"));

        // ClearStaleIdsAsync (the caller of itemCanonicalData.DeleteIdentityArtifactsAsync) was
        // extracted into CanonicalCandidateBuilder as part of Stage 5A wave 2 (packet f2, Job 3
        // helper extraction) alongside the rest of the candidate-scoring / field-bag cluster.
        // The endpoint file still injects and calls the typed data service directly for its own
        // work (ResolveWorkAssetContextAsync); the identity-artifact deletion call now lives in
        // the extracted service, so it is asserted there instead.
        var candidateBuilder = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/Canonical/CanonicalCandidateBuilder.cs"));

        Assert.Contains("IItemCanonicalRepository itemCanonicalData", source, StringComparison.Ordinal);
        Assert.Contains("itemCanonicalData.ResolveWorkAssetContextAsync", source, StringComparison.Ordinal);
        Assert.Contains("itemCanonicalData.DeleteIdentityArtifactsAsync", candidateBuilder, StringComparison.Ordinal);
        Assert.DoesNotContain("IDatabaseConnection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateConnection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GuidSql", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDatabaseConnection", candidateBuilder, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateConnection", candidateBuilder, StringComparison.Ordinal);
        Assert.DoesNotContain("GuidSql", candidateBuilder, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemCanonicalEndpoints_SupportRetailAndWikidataSearchModes()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs"));
        var models = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Contracts/Matching/MatchingDtos.cs"));

        Assert.Contains("SearchMode", models, StringComparison.Ordinal);
        Assert.Contains("\"retail_only\"", source, StringComparison.Ordinal);
        Assert.Contains("\"wikidata_only\"", source, StringComparison.Ordinal);
        Assert.Contains("\"combined\"", source, StringComparison.Ordinal);
        Assert.Contains("shouldSearchRetail", source, StringComparison.Ordinal);
        Assert.Contains("shouldSearchUniverse", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemCanonicalEndpoints_ManualWikidataReplacementQueuesAssetEnrichment()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs"));

        Assert.DoesNotContain("pipeline.RunSynchronousAsync(new HarvestRequest", source, StringComparison.Ordinal);
        Assert.Contains("await pipeline.EnqueueAsync(new HarvestRequest", source, StringComparison.Ordinal);
        Assert.Contains("EntityId = context.AssetId", source, StringComparison.Ordinal);
        Assert.Contains("MediaType = MediaTypeParser.Parse(context.MediaType)", source, StringComparison.Ordinal);
        Assert.Contains("IsUserResolution = true", source, StringComparison.Ordinal);
        Assert.Contains("This file was queued for the full enrichment cycle.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemCanonicalEndpoints_WikidataReplacementAppliesAcceptedCanonicalCandidateFields()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs"));
        var contracts = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Contracts/Matching/MatchingDtos.cs"));
        var route = source[source.IndexOf("/{entityId:guid}/wikidata-match", StringComparison.Ordinal)..];

        Assert.Contains("AcceptedSuggestedKeys", contracts, StringComparison.Ordinal);
        Assert.Contains("SuggestedFields", contracts, StringComparison.Ordinal);
        Assert.Contains("policy.RequiredFieldKeys", route, StringComparison.Ordinal);
        Assert.Contains(".Concat(policy.SuggestedFieldKeys)", route, StringComparison.Ordinal);
        Assert.Contains("request.SuggestedFields.TryGetValue", route, StringComparison.Ordinal);
        Assert.Contains("EntityId = ResolvePolicyWorkTarget(lineage, policy, pair.Key)", route, StringComparison.Ordinal);
        Assert.Contains("IsUserLocked = true", route, StringComparison.Ordinal);
        Assert.Contains("fieldsApplied = 1 + acceptedCandidateFields.Count", route, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemCanonicalEndpoints_RetailReplacementQueuesBeforeOptionalProviderSideEffects()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs"));
        var retailRoute = source[source.IndexOf("/{entityId:guid}/retail-match", StringComparison.Ordinal)..];
        retailRoute = retailRoute[..retailRoute.IndexOf("/{entityId:guid}/wikidata-match", StringComparison.Ordinal)];

        var enqueueIndex = retailRoute.IndexOf("await pipeline.EnqueueAsync", StringComparison.Ordinal);
        var artworkIndex = retailRoute.IndexOf("ReplaceProviderArtworkAsync", StringComparison.Ordinal);
        var manifestIndex = retailRoute.IndexOf("EnsureAlbumTrackManifestAsync", StringComparison.Ordinal);

        Assert.True(enqueueIndex >= 0);
        Assert.True(enqueueIndex < artworkIndex);
        Assert.True(enqueueIndex < manifestIndex);
        Assert.Contains("IdentityRevision", retailRoute, StringComparison.Ordinal);
    }

    private static string GetRepoFilePath(
        string relativePath,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var directory = !string.IsNullOrWhiteSpace(sourceFile)
            ? new DirectoryInfo(Path.GetDirectoryName(sourceFile)!)
            : new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is not null)
        {
            return Path.GetFullPath(Path.Combine(directory.FullName, relativePath));
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
    }

    private static readonly Regex RemovedRootRoute = new(
        "(?<![\\w-])/reg" + "istry(?=[/\\s\\\"'`?#)\\]]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string RemovedImplementationSymbol = "Reg" + "istryEndpoints";

    private static string[] FindRemovedLibraryItemSurfaceOffenders(string root) =>
        EnumerateTrackedFiles(root)
            .Where(path => TextExtensions.Contains(Path.GetExtension(path)))
            .Where(path => !Path.GetFileName(path).Equals("api.log", StringComparison.OrdinalIgnoreCase))
            .Where(path =>
            {
                var relative = Path.GetRelativePath(root, path);
                return !relative.StartsWith(@"src/MediaEngine.Contracts/Details/", StringComparison.OrdinalIgnoreCase)
                    && !relative.StartsWith(@"src/MediaEngine.Api/Services/Details/", StringComparison.OrdinalIgnoreCase)
                    && !relative.StartsWith(@"src/MediaEngine.Web/Components/Details/", StringComparison.OrdinalIgnoreCase)
                    && !relative.StartsWith(@"src/MediaEngine.Domain/Capabilities/", StringComparison.OrdinalIgnoreCase)
                    && !relative.Equals(@"src/MediaEngine.Api/Services/ReviewQueueRouter.cs", StringComparison.OrdinalIgnoreCase)
                    && !relative.Equals(@"src/MediaEngine.Api/Program.cs", StringComparison.OrdinalIgnoreCase)
                    && !relative.Equals(@"src/MediaEngine.Api/DependencyInjection/TuvimaStorageServiceCollectionExtensions.cs", StringComparison.OrdinalIgnoreCase)
                    && !relative.Equals(@"tests\MediaEngine.Web.Tests\UnifiedDetailComponentTests.cs", StringComparison.OrdinalIgnoreCase);
            })
            .Where(path => ContainsRemovedLibraryItemSurface(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path))
            .Take(20)
            .ToArray();

    private static IEnumerable<string> EnumerateTrackedFiles(string root)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);

        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                if (!IsIgnoredDirectory(childDirectory, root))
                {
                    pendingDirectories.Push(childDirectory);
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                yield return file;
            }
        }
    }

    private static bool ContainsRemovedLibraryItemSurface(string text) =>
        RemovedRootRoute.IsMatch(text)
        || text.Contains(RemovedImplementationSymbol, StringComparison.OrdinalIgnoreCase);

    private static bool IsIgnoredGeneratedSiteFile(string relativePath) =>
        relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .StartsWith($"site{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static bool IsIgnoredDirectory(string path, string root) =>
        IsIgnoredPath(Path.Combine(path, "__ignored_directory_probe__"), root);

    private static bool IsIgnoredPath(string path, string root)
    {
        var separator = Path.DirectorySeparatorChar;
        return IsIgnoredGeneratedSiteFile(Path.GetRelativePath(root, path))
            || path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{separator}.data{separator}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{separator}logs{separator}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{separator}artifacts{separator}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{separator}.git{separator}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{separator}.claude{separator}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{separator}.tmp{separator}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{separator}TestResults{separator}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{separator}wwwroot{separator}lib{separator}", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".css", ".js", ".json", ".md", ".xml", ".iss", ".ps1", ".bat", ".sql", ".yml", ".yaml", ".txt"
    };
}
