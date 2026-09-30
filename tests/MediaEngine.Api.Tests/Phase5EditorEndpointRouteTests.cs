namespace MediaEngine.Api.Tests;

public sealed class Phase5EditorEndpointRouteTests
{
    [Fact]
    public void EditorAndReviewEndpointsExposeRealPhase5Operations()
    {
        var metadata = ReadSource("src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs");
        var navigator = ReadSource("src/MediaEngine.Api/Endpoints/MetadataEndpoints.MediaEditorNavigator.cs");
        var canonical = ReadSource("src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs");
        var review = ReadSource("src/MediaEngine.Api/Endpoints/ReviewEndpoints.cs");

        Assert.Contains("group.MapGet(\"/{entityId:guid}/editor-context\"", metadata, StringComparison.Ordinal);
        Assert.Contains("group.MapGet(\"/{entityId:guid}/navigator\"", navigator, StringComparison.Ordinal);
        Assert.Contains("/membership-preview", navigator, StringComparison.Ordinal);
        Assert.Contains("/membership-apply", navigator, StringComparison.Ordinal);
        Assert.Contains("/{entityId:guid}/preferences", canonical, StringComparison.Ordinal);
        Assert.Contains("/{entityId:guid}/display-overrides", canonical, StringComparison.Ordinal);
        Assert.Contains("/{entityId:guid}/editor-preferences/{profileId:guid}", canonical, StringComparison.Ordinal);
        Assert.Contains("ExpectedRevision", canonical, StringComparison.Ordinal);
        Assert.Contains("/{entityId:guid}/canonical-search", canonical, StringComparison.Ordinal);
        Assert.Contains("retailMatchPreview.SearchAsync", canonical, StringComparison.Ordinal);
        Assert.Contains("wikidataMatchPreview.PreviewCandidatesAsync", canonical, StringComparison.Ordinal);
        Assert.Contains("/{entityId:guid}/canonical-apply", canonical, StringComparison.Ordinal);
        Assert.Contains("/{entityId:guid}/retail-match", canonical, StringComparison.Ordinal);
        Assert.Contains("/{entityId:guid}/wikidata-match", canonical, StringComparison.Ordinal);
        Assert.Contains("/{id:guid}/resolve", review, StringComparison.Ordinal);
        Assert.Contains("/{id:guid}/dismiss", review, StringComparison.Ordinal);
        Assert.Contains("/{id:guid}/skip-universe", review, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaEditorNavigator_ReturnsCompactOrdinalsTechnicalBadgesAndClickableState()
    {
        var navigator = ReadSource("src/MediaEngine.Api/Endpoints/MetadataEndpoints.MediaEditorNavigator.cs");
        var metadata = ReadSource("src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs");

        var navigatorService =
            ReadSource("src/MediaEngine.Api/Services/ReadServices/MediaEditorNavigationReadService.cs")
            + ReadSource("src/MediaEngine.Application/ReadModels/MediaEditorReadModels.cs");
        Assert.Contains("IMediaEditorNavigationReadService navigationReadService", navigator, StringComparison.Ordinal);
        Assert.Contains("IMediaEditorMembershipReadService membershipReadService", navigator, StringComparison.Ordinal);
        Assert.Contains("IHierarchyAlignmentService hierarchyAlignment", navigator, StringComparison.Ordinal);
        Assert.Contains("compact_ordinal_label", navigatorService, StringComparison.Ordinal);
        Assert.Contains("technical_badges", navigatorService, StringComparison.Ordinal);
        Assert.Contains("primary_asset_id", navigatorService, StringComparison.Ordinal);
        Assert.Contains("is_clickable", navigatorService, StringComparison.Ordinal);
        Assert.Contains("can_select_as_editor_target", navigatorService, StringComparison.Ordinal);
        Assert.Contains("BuildNavigatorTechnicalBadges", navigatorService, StringComparison.Ordinal);
        Assert.Contains("playback_inspection_cache", navigatorService, StringComparison.Ordinal);
        Assert.Contains("file_size_bytes", navigatorService, StringComparison.Ordinal);
        Assert.Contains("video_width", navigatorService, StringComparison.Ordinal);
        Assert.Contains("disc_number", navigatorService, StringComparison.Ordinal);
        Assert.Contains("display_overrides_json", navigatorService, StringComparison.Ordinal);
        Assert.Contains("GetDisplayOverrideValue(value, \"title\")", navigatorService, StringComparison.Ordinal);
        Assert.DoesNotContain("\"display_title\"", navigatorService, StringComparison.Ordinal);
        Assert.DoesNotContain("\"display_subtitle\"", navigatorService, StringComparison.Ordinal);
        Assert.Contains("IsContainerEditorLaunch(launch)", metadata, StringComparison.Ordinal);
        Assert.Contains("!string.Equals(launch.WorkKind, \"child\"", metadata, StringComparison.Ordinal);
        Assert.Contains("BuildEditorScopeCapabilities", metadata, StringComparison.Ordinal);
        Assert.Contains("CanonicalIdentityOwnerScopeId", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaEditorCapabilities_KeepChaptersOnlyForAudiobooks()
    {
        var metadata = ReadSource("src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs");

        Assert.DoesNotContain("tabs.Add(\"episodes\")", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("tabs.Add(\"tracks\")", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("contentLabel = \"Contents\"", metadata, StringComparison.Ordinal);
        Assert.Contains("tabs.Add(\"chapters\")", metadata, StringComparison.Ordinal);
        Assert.Contains("contentLabel = \"Chapters\"", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorLaunchContext_ResolvesCollectionShelvesToCanonicalContainerWork()
    {
        var metadataData = ReadSource("src/MediaEngine.Storage/MetadataEditorRepository.cs");
        var navigatorService = ReadSource("src/MediaEngine.Api/Services/ReadServices/MediaEditorNavigationReadService.cs");

        Assert.Contains("private sealed record EditorLaunchCollectionRow", metadataData, StringComparison.Ordinal);
        Assert.Contains("private sealed record EditorLaunchCollectionRow", navigatorService, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN works target ON target.id = COALESCE(gp.id, p.id, w.id)", metadataData, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN works target ON target.id = COALESCE(gp.id, p.id, w.id)", navigatorService, StringComparison.Ordinal);
        Assert.Contains("\"Collection\",", metadataData, StringComparison.Ordinal);
        Assert.Contains("\"Collection\",", navigatorService, StringComparison.Ordinal);
        Assert.Contains("new { entityId }", metadataData, StringComparison.Ordinal);
        Assert.Contains("new { entityId }", navigatorService, StringComparison.Ordinal);
        Assert.Contains("GetRepresentativeAssetForWorkTree(connection, collectionRow.WorkId, ct)", metadataData, StringComparison.Ordinal);
        Assert.Contains("GetRepresentativeAssetForWorkTree(conn, collectionWorkId)", navigatorService, StringComparison.Ordinal);
    }

    [Fact]
    public void RetailReplacement_UsesExplicitScopeProviderProvenanceAndPipelineJob()
    {
        var canonical = ReadSource("src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs");
        var models = ReadSource("src/MediaEngine.Contracts/Matching/MatchingDtos.cs");

        Assert.Contains("ResolveTargetPolicy(context.MediaType, request.TargetKind, request.TargetFieldGroup)", canonical, StringComparison.Ordinal);
        Assert.Contains("DecisionSourceProviderId = WellKnownProviders.UserManual", canonical, StringComparison.Ordinal);
        Assert.Contains("MetadataFieldConstants.IdentityProviderItemId", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("FindChildParentIdentityConflictAsync(", canonical, StringComparison.Ordinal);
        Assert.Contains("IHierarchyAlignmentService hierarchyAlignment", canonical, StringComparison.Ordinal);
        Assert.Contains("BuildHierarchyAlignmentRequest", canonical, StringComparison.Ordinal);
        Assert.Contains("ResolveWorkIdForAssetAsync(context.AssetId, ct)", canonical, StringComparison.Ordinal);
        Assert.Contains("HierarchyExternalIdentifierMutation", canonical + ReadSource("src/MediaEngine.Application/ReadModels/MediaEditorReadModels.cs"), StringComparison.Ordinal);
        Assert.Contains("var postAlignmentWorkId = ClaimScopeCatalog.IsParentScoped(BridgeIdKeys.WikidataQid, lineage.MediaType)", canonical, StringComparison.Ordinal);
        Assert.Contains("LoadWorkWikidataStateAsync(postAlignmentWorkId, ct)", canonical, StringComparison.Ordinal);
        Assert.Contains("UpdateWorkWikidataMatchStateAsync(postAlignmentWorkId", canonical, StringComparison.Ordinal);
        Assert.Contains("JsonPropertyName(\"hierarchy_changed\")", models, StringComparison.Ordinal);
        Assert.Contains("JsonPropertyName(\"previous_path\")", models, StringComparison.Ordinal);
        Assert.Contains("BridgeIdKeys.TvdbEpisodeId", ReadSource("src/MediaEngine.Api/Endpoints/MetadataEndpoints.TvdbMatching.cs"), StringComparison.Ordinal);
        Assert.Contains("(\"Music\", \"track\")", canonical, StringComparison.Ordinal);
        Assert.Contains("identityJobId = await pipeline.EnqueueAsync", canonical, StringComparison.Ordinal);
        Assert.Contains("ReplaceProviderArtworkAsync", canonical, StringComparison.Ordinal);
        Assert.Contains("SystemActionType.MediaUpdated", canonical, StringComparison.Ordinal);
        Assert.Contains("SystemActionType.HydrationEnqueued", canonical, StringComparison.Ordinal);
        Assert.Contains("JsonPropertyName(\"identity_job_id\")", models, StringComparison.Ordinal);
        Assert.Contains("JsonPropertyName(\"cover_url\")", models, StringComparison.Ordinal);
        Assert.Contains("JsonPropertyName(\"artwork_changed\")", models, StringComparison.Ordinal);
        Assert.Contains("JsonPropertyName(\"target_scope_id\")", models, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitCanonicalSearch_DoesNotReuseCurrentParentAsARequiredSearchField()
    {
        var canonical = ReadSource("src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs");
        var builder = ReadSource("src/MediaEngine.Api/Services/Canonical/CanonicalCandidateBuilder.cs");

        Assert.Contains("BuildRetailSearchContext(", canonical, StringComparison.Ordinal);
        Assert.Contains("SearchFields: retailSearch.SearchFields", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("Episode matching must stay inside the selected series/season context.", builder, StringComparison.Ordinal);
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
