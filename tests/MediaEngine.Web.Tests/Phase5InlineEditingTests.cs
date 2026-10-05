namespace MediaEngine.Web.Tests;

public sealed class Phase5InlineEditingTests
{
    [Fact]
    public void DetailPage_EditActionLaunchesSharedEditor()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor");

        Assert.Contains("MediaEditorLauncherService MediaEditorLauncher", source, StringComparison.Ordinal);
        Assert.Contains("action.Key is \"edit-media\" or \"edit\"", source, StringComparison.Ordinal);
        Assert.Contains("MediaEditorLauncher.OpenAsync(request)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaEditorLauncher.BeginInline", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<SharedMediaEditorShell", source, StringComparison.Ordinal);
        Assert.Contains("<DetailPrimaryModule Model=\"Model\"", source, StringComparison.Ordinal);
        Assert.Contains("ActiveProfileId = activeProfile?.Id", source, StringComparison.Ordinal);
        Assert.Contains("Mode = SharedMediaEditorMode.Normal", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailPage_KeepsDetailContentMountedWhileModalEditorIsOpen()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor");
        var heroIndex = source.IndexOf("<DetailHero Model=\"Model\"", StringComparison.Ordinal);
        var primaryIndex = source.IndexOf("<DetailPrimaryModule Model=\"Model\"", heroIndex, StringComparison.Ordinal);
        var tabsIndex = source.IndexOf("<DetailTabs Tabs=\"VisibleTabs\"", primaryIndex, StringComparison.Ordinal);
        var bodyIndex = source.IndexOf("<section id=\"@CurrentActiveTab\"", tabsIndex, StringComparison.Ordinal);

        Assert.True(heroIndex >= 0);
        Assert.True(primaryIndex > heroIndex);
        Assert.True(tabsIndex > primaryIndex);
        Assert.True(bodyIndex > tabsIndex);
        Assert.DoesNotContain("tl-detail-edit-stage", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowseRowEditActionUsesSharedEditorWhileCardsOpenDetails()
    {
        var browse = ReadSource("src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor");
        var table = ReadSource("src/MediaEngine.Web/Components/Library/LibraryConfigurableTable.razor");
        var card = ReadSource("src/MediaEngine.Web/Components/MediaTiles/MediaTile.razor");

        Assert.Contains("MediaEditorLauncherService MediaEditorLauncher", browse, StringComparison.Ordinal);
        Assert.Contains("OnEditClicked=\"OpenItemEditorAsync\"", browse, StringComparison.Ordinal);
        Assert.DoesNotContain("OnEditClicked=\"OpenCardEditorAsync\"", browse, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaGroupPage", browse, StringComparison.Ordinal);
        Assert.Contains("MediaEditorLauncher.OpenAsync(new MediaEditorLaunchRequest", browse, StringComparison.Ordinal);
        Assert.Contains("OnEditClicked.InvokeAsync(item.EntityId)", table, StringComparison.Ordinal);
        Assert.Contains("href=\"@DetailsNavigationUrl\"", card, StringComparison.Ordinal);
        Assert.DoesNotContain("OnEditClicked", card, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchResults_StayNavigationFirstAndLeaveEditingToDetailPages()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/Pages/SearchPage.razor");

        Assert.Contains("<UniversalSearchResults", source, StringComparison.Ordinal);
        Assert.Contains("GetUniversalSearchAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaEditorLauncherService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenSearchResultEditorAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewQueue_ReviewActionLaunchesEditorInReviewModeWithReviewId()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/Settings/SettingsReviewQueueTab.razor");

        Assert.Contains("Mode = SharedMediaEditorMode.Review", source, StringComparison.Ordinal);
        Assert.Contains("ReviewItemId = item.Id", source, StringComparison.Ordinal);
        Assert.Contains("Label=\"Review\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Desktop required", source, StringComparison.Ordinal);
        Assert.Contains("InitialTab = \"links\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Open complete review", source, StringComparison.Ordinal);
        Assert.Contains("await LoadAsync();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_ReviewResolutionIsExplicitAndUsesEngineApi()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");

        Assert.Contains("GetDirtySaveLabel", shell, StringComparison.Ordinal);
        Assert.Contains("GetResolveReviewLabel", shell, StringComparison.Ordinal);
        Assert.Contains("ResolveReviewWithoutChangesAsync", code, StringComparison.Ordinal);
        Assert.Contains("Orchestrator.ResolveReviewAsync", code, StringComparison.Ordinal);
        Assert.Contains("Review was not resolved because changes could not be saved.", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_DetailsTabUsesInlineMetadataOverrideLayout()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var start = source.IndexOf("else if (_activeTab == \"details\")", StringComparison.Ordinal);
        var end = source.IndexOf("else if (_activeTab == \"artwork\")", start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Could not isolate the Details branch.");
        var shell = source[start..end];

        Assert.Contains("sme-details-inspector__grid", shell, StringComparison.Ordinal);
        Assert.Contains("DetailsHeading", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("DetailsPrimaryFacts", shell, StringComparison.Ordinal);
        Assert.Contains("DetailsSecondaryFields", shell, StringComparison.Ordinal);
        Assert.Contains("DetailsImageSrcSet", shell, StringComparison.Ordinal);
        Assert.Contains("EditorDetailsInlineField", shell, StringComparison.Ordinal);
        Assert.Contains("<h3>Files</h3>", shell, StringComparison.Ordinal);
        Assert.Contains("DetailsRecentActivity", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("File & processing", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginDetailsEdit", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveDetailsAsync", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Refresh enrichment", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Edit file tools", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("_editorContext?.FileMetadataSyncStatus", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Match Information", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Title=\"Local Library Details\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Title=\"Field Rules\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Title=\"Review Focus\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Personal notes", shell, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharedEditor_UsesOneNaturalAspectPosterCoverSlotAndOmitsUnsupportedArtworkTypes()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");

        Assert.Contains("Poster / Cover", code, StringComparison.Ordinal);
        Assert.Contains("GetArtworkPreviewClass", shell, StringComparison.Ordinal);
        Assert.Contains("GetArtworkPreviewClass", code, StringComparison.Ordinal);
        Assert.Contains("sme-cover-cluster--single {StaticHeaderArtworkShapeClass}", shell, StringComparison.Ordinal);
        Assert.Contains("GetSectionNavClass(tab.Id)", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("BannerArtworkSlot", code, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Banner\" =>", code, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SquareArt\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("\"DiscArt\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ClearArt\"", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_SupportsSeasonPosterAndThumbInTheSeasonScope()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");
        var scopeService = ReadSource("src/MediaEngine.Api/Services/Metadata/ArtworkScopeService.cs");

        Assert.Contains("SeasonPosterArtworkSlot", code, StringComparison.Ordinal);
        Assert.Contains("SeasonThumbArtworkSlot", code, StringComparison.Ordinal);
        Assert.Contains("(\"TV\", \"season\")", code, StringComparison.Ordinal);
        Assert.Contains("\"SeasonPoster\"", scopeService, StringComparison.Ordinal);
        Assert.Contains("\"SeasonThumb\"", scopeService, StringComparison.Ordinal);
        Assert.Contains("GetArtworkThumbnailUrl", shell, StringComparison.Ordinal);
        Assert.Contains("MediaTileArtworkUrl.Sized", code, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorNavigation_HighlightsEverySelectedMenuItemWithTheSharedState()
    {
        var sharedShell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var sharedCode = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");
        var personShell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/PersonEditorDialog.razor");
        var collectionShell = ReadSource("src/MediaEngine.Web/Components/Collections/CollectionEditorShell.razor");
        var globalStyles = ReadSource("src/MediaEngine.Web/wwwroot/app.css");

        Assert.Contains("app-selection-nav__item sme-section-nav__item is-active", sharedCode, StringComparison.Ordinal);
        Assert.Contains("app-selection-nav__item person-editor__nav-item is-active", personShell, StringComparison.Ordinal);
        Assert.Contains("app-selection-nav__item sme-section-nav__item is-active", collectionShell, StringComparison.Ordinal);
        Assert.Contains("is-active is-group-open", sharedCode, StringComparison.Ordinal);
        Assert.Contains("is-active is-group-open", personShell, StringComparison.Ordinal);
        Assert.Contains("is-active is-group-open", collectionShell, StringComparison.Ordinal);
        Assert.Contains("aria-current", sharedShell, StringComparison.Ordinal);
        Assert.Contains("aria-current", personShell, StringComparison.Ordinal);
        Assert.Contains("aria-current", collectionShell, StringComparison.Ordinal);
        Assert.Contains(".app-selection-nav__item.is-active", globalStyles, StringComparison.Ordinal);
        Assert.Contains("background: var(--tl-menu-item-selected) !important", globalStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void PersonEditor_UsesMediaArtworkWorkspaceAndOmitsMatchAndPersonalNotes()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/MediaEditor/PersonEditorDialog.razor");
        var styles = ReadSource("src/MediaEngine.Web/Components/MediaEditor/PersonEditorDialog.razor.css");
        var contracts = ReadSource("src/MediaEngine.Contracts/Persons/PersonEditorContracts.cs");

        Assert.Contains("(\"details\", \"Details\"", source, StringComparison.Ordinal);
        Assert.Contains("(\"artwork\", \"Artwork\"", source, StringComparison.Ordinal);
        Assert.Contains("(\"history\", \"History\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("(\"match\", \"Match\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_activeTab == \"match\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Personal notes", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PersonalNotes", contracts, StringComparison.Ordinal);

        Assert.Contains("<ArtworkWorkspace", source, StringComparison.Ordinal);
        Assert.Contains("ShowClose=\"false\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StartInEditMode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("person-editor__tab-heading", source, StringComparison.Ordinal);
        Assert.Contains("GetPersonTabClass(captured.Id)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Banner\" =>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("person-editor__artwork-type-rail", source, StringComparison.Ordinal);
        Assert.DoesNotContain("person-editor__artwork-sidebar", source, StringComparison.Ordinal);
        Assert.Contains("PersonArtworkItem", source, StringComparison.Ordinal);
        Assert.Contains("PersonArtworkChangedAsync", source, StringComparison.Ordinal);
        Assert.Contains("background: var(--tl-bg-surface-raised)", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("#", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_OverlaysArtworkActionsOnTheSelectedImage()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");

        Assert.Contains("sme-artwork-image-actions", source, StringComparison.Ordinal);
        Assert.Contains("AriaLabel=\"Open full size in new tab\"", source, StringComparison.Ordinal);
        Assert.Contains("AriaLabel=\"@GetArtworkRemovalLabel(focusedItem)\"", source, StringComparison.Ordinal);
        Assert.Contains("Delete uploaded image", code, StringComparison.Ordinal);
        Assert.Contains("Remove from item", code, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.Check", source, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.Close", source, StringComparison.Ordinal);
        Assert.Contains("GetArtworkRemovalConfirmLabel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<span>@GetArtworkRemovalQuestion(focusedItem)</span>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"sme-artwork-primary-actions\">", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedFieldRow_ProvidesSideActionForLockedCanonicalOverrides()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/Shared/AppFormFieldRow.razor");

        Assert.Contains("ActionIcon", source, StringComparison.Ordinal);
        Assert.Contains("AppIconButton", source, StringComparison.Ordinal);
        Assert.Contains("OnAction.InvokeAsync()", source, StringComparison.Ordinal);
        Assert.Contains("ConfirmingAction", source, StringComparison.Ordinal);
        Assert.Contains("OnConfirmAction.InvokeAsync()", source, StringComparison.Ordinal);
        Assert.Contains("OnCancelAction.InvokeAsync()", source, StringComparison.Ordinal);
        Assert.Contains("Tone=\"AppUiTone.Success\"", source, StringComparison.Ordinal);
        Assert.Contains("Tone=\"AppUiTone.Error\"", source, StringComparison.Ordinal);
        Assert.Contains("Unlocked", source, StringComparison.Ordinal);
        var styles = ReadSource("src/MediaEngine.Web/Components/Shared/AppFormFieldRow.razor.css");
        Assert.Contains("flex-direction: row", styles, StringComparison.Ordinal);
        Assert.Contains("tl-field-grid--confirming", styles, StringComparison.Ordinal);
        Assert.Contains("tl-field-confirm-button--accept", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_InlineCanonicalOverridesSaveThroughDisplayOverrides()
    {
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");

        Assert.Contains("ShouldSaveAsDisplayOverride(scopedKey, key)", code, StringComparison.Ordinal);
        Assert.Contains("ShouldSaveAsDisplayOverride(entry.RawKey, entry.Key.Key)", code, StringComparison.Ordinal);
        Assert.Contains("SaveItemEditorPreferencesAsync", code, StringComparison.Ordinal);
        Assert.Contains("SaveItemDisplayOverridesAsync(scopeGroup.Key.EntityId, overrideFields)", code, StringComparison.Ordinal);
        Assert.Contains("SaveItemPreferencesAsync(scopeGroup.Key.EntityId, preferenceFields)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_MatchInformationUsesProviderNameAndOmitsTechnicalRows()
    {
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");

        Assert.Contains("GetRetailMatchDisplayName(summary)", code, StringComparison.Ordinal);
        Assert.Contains("(\"Retail Match\", !string.IsNullOrWhiteSpace(provider) ? provider : hasProviderEvidence ? \"Retail matched\" : \"Not linked\")", code, StringComparison.Ordinal);
        Assert.Contains("(\"Provider ID\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("(\"Match Source\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("NormalizeRetailProviderLabel(summary?.MatchSource)", code, StringComparison.Ordinal);
        Assert.Contains("Guid.TryParse(trimmed, out _)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ProviderNameFromBridgeIdentifier", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_InlineModeKeepsDiscardAndStructuralConfirmationsVisible()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");

        Assert.Contains("Inline && _confirmDiscard", shell, StringComparison.Ordinal);
        Assert.Contains("Discard unsaved changes?", shell, StringComparison.Ordinal);
        Assert.Contains("Inline && _pendingMembershipPreview is not null", shell, StringComparison.Ordinal);
        Assert.Contains("Save and Move", shell, StringComparison.Ordinal);
        Assert.Contains("HasPendingDetailsInlineEdit", ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs"), StringComparison.Ordinal);
        Assert.Contains("BeginTvdbCrossShowSearch", shell, StringComparison.Ordinal);
        Assert.Contains("Search another show", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_AudiobooksUseFocusedChapterContentsAndFileDerivedBoundaries()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");
        var metadata = ReadSource("src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs");

        Assert.Contains("else if (_activeTab == \"chapters\"", shell, StringComparison.Ordinal);
        Assert.Contains("This track boundary is embedded in the audiobook file. Timing remains read-only.", shell, StringComparison.Ordinal);
        Assert.Contains("QueueAudiobookChapterReset", shell, StringComparison.Ordinal);
        Assert.Contains("UpsertAudiobookChapterTitleOverrideAsync", code, StringComparison.Ordinal);
        Assert.Contains("DeleteAudiobookChapterTitleOverrideAsync", code, StringComparison.Ordinal);
        Assert.Contains("case (\"Audiobooks\", \"audiobook\")", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_ContainerFilesAreAnAggregateReconciliationSummary()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");

        Assert.Contains("Title=\"Attached files\"", shell, StringComparison.Ordinal);
        Assert.Contains("A reconciliation summary across the owned children", shell, StringComparison.Ordinal);
        Assert.Contains("ContainerAttachedFileCount", code, StringComparison.Ordinal);
        Assert.Contains("ContainerMissingFileCount", code, StringComparison.Ordinal);
        Assert.Contains("Open a file entry", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_MatchIdentityTabUsesStructuredRetailAndWikidataWorkflow()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");
        var styles = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.css");

        Assert.Contains("sme-match-current-column", shell, StringComparison.Ordinal);
        Assert.Contains("sme-match-search-panel", shell, StringComparison.Ordinal);
        Assert.Contains("Retail Provider", shell, StringComparison.Ordinal);
        Assert.Contains("Wikidata", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Change match", shell, StringComparison.Ordinal);
        Assert.Contains("sme-match-columns", shell, StringComparison.Ordinal);
        Assert.Contains("sme-match-selected-column", shell, StringComparison.Ordinal);
        Assert.Contains("sme-match-result-row", shell, StringComparison.Ordinal);
        Assert.Contains("GetRetailCandidateScore(candidate)", shell, StringComparison.Ordinal);
        Assert.Contains("CanonicalConfidencePercentage(candidate)", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("sme-match-search-panel--collapsed", shell, StringComparison.Ordinal);
        Assert.Contains("<section class=\"sme-match-search-panel\" aria-label=\"Match search results\">", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Rematch", shell, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Advanced canonical identity", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Choose what to update", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Identity target", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("sme-header-actions", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("sme-header-match-state", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("EditorTargetSummary", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Review matches", shell, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.Close", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"sme-breadcrumb\">@BreadcrumbText</div>", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"sme-qid-link\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppMediaTypeSelect", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Change Type", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("CanReclassifyMediaType", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ReclassifyMediaTypeAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("sme-match-type-select", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("sme-search-targets", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Keep Match", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Keep QID", shell, StringComparison.Ordinal);
        Assert.Contains("Clear canonical match", shell, StringComparison.Ordinal);
        Assert.Contains("Saving…", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Use retail match", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Use canonical identity", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("sme-match-quality--@GetMatchQualityClass", shell, StringComparison.Ordinal);
        Assert.Contains("BuildCanonicalComparisonRows", shell, StringComparison.Ordinal);
        Assert.Contains("BuildRetailComparisonRows", shell, StringComparison.Ordinal);
        Assert.Contains("Selected match", shell, StringComparison.Ordinal);
        Assert.Contains("MatchComparisonLocalColumn", shell, StringComparison.Ordinal);
        Assert.Contains("Your album", code, StringComparison.Ordinal);
        Assert.Contains("BuildCurrentRetailMatchCard", code, StringComparison.Ordinal);
        Assert.Contains("BuildCurrentWikidataMatchCard", code, StringComparison.Ordinal);
        Assert.Contains("UsesParentRetailIdentityOnly", code, StringComparison.Ordinal);
        Assert.Contains("Optional · Not matched", shell, StringComparison.Ordinal);
        Assert.Contains("The Series match supplies search context", code, StringComparison.Ordinal);
        Assert.Contains("CanonicalIdentityTargetSummary", code, StringComparison.Ordinal);
        Assert.Contains("<EditorContextNavigator", shell, StringComparison.Ordinal);
        Assert.Contains("Select {label.ToLowerInvariant()}", code, StringComparison.Ordinal);
        Assert.Contains("IdentityLinkDisplay", code, StringComparison.Ordinal);
        Assert.Contains(".GetExternalUrls(identifiers, EditorMediaType", code, StringComparison.Ordinal);
        Assert.Contains("BuildCurrentIdentityIdentifiers", code, StringComparison.Ordinal);
        Assert.Contains("identifiers.Remove(\"wikidata_qid\")", code, StringComparison.Ordinal);
        Assert.Contains("identifiers.Remove(\"wikipedia_url\")", code, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildProviderItemUrl", code, StringComparison.Ordinal);
        Assert.DoesNotContain("https://www.themoviedb.org/", code, StringComparison.Ordinal);
        Assert.Contains("left: calc(8.75rem + 0.85rem + 1.625rem);", styles, StringComparison.Ordinal);
        Assert.Contains("background: transparent !important;", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("https://www.wikidata.org/wiki/", code, StringComparison.Ordinal);
        Assert.Contains("BuildCandidateChips", code, StringComparison.Ordinal);
        Assert.Contains("FormatCandidateScore", code, StringComparison.Ordinal);
        Assert.Contains("CanonicalEndpointEntityId => CurrentEntityId", code, StringComparison.Ordinal);
        Assert.Contains("CanonicalEndpointEntityId,", code, StringComparison.Ordinal);
        Assert.Contains("\"Unknown\" => \"Books\"", code, StringComparison.Ordinal);
        Assert.Contains("MediaType = EditorMediaType", code, StringComparison.Ordinal);
        Assert.Contains("Retail Provider", shell, StringComparison.Ordinal);
        Assert.Contains("Wikidata", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("RetailMatchChangeDescription", shell, StringComparison.Ordinal);
        Assert.Contains("The Series canonical identity remains unchanged.", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Cancel change", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Provider match pending", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentMatchStatusLabel", code, StringComparison.Ordinal);
        Assert.Contains(".sme-match-result-copy strong", styles, StringComparison.Ordinal);
        Assert.Contains("color:var(--tl-text-primary)", styles, StringComparison.Ordinal);
        Assert.Contains("font-weight:600", styles, StringComparison.Ordinal);
        Assert.Contains("Searching…", shell, StringComparison.Ordinal);
        Assert.Contains("sme-match-searching", shell, StringComparison.Ordinal);
        Assert.Contains("IsActiveMatchSearchPending", shell, StringComparison.Ordinal);
        Assert.Contains("sme-tvdb-search-state", shell, StringComparison.Ordinal);
        Assert.Contains("sme-tvdb-search-error", shell, StringComparison.Ordinal);
        Assert.Contains("Retry", shell, StringComparison.Ordinal);
        Assert.Contains("SelectMatchSearchMode", code, StringComparison.Ordinal);
        Assert.Contains("CancelActiveMatchSearch", code, StringComparison.Ordinal);
        Assert.Contains("_retailSearchResponse", code, StringComparison.Ordinal);
        Assert.Contains("_wikidataSearchResponse", code, StringComparison.Ordinal);
        Assert.DoesNotContain("_canonicalSearchResponse", code, StringComparison.Ordinal);
        Assert.Contains("The user must explicitly select a candidate", code, StringComparison.Ordinal);
        Assert.Contains("CanApplyRetailCandidate", code, StringComparison.Ordinal);
        Assert.Contains("CanApplyLinkedCandidate", code, StringComparison.Ordinal);
        Assert.Contains("candidate.RequiredFields", code, StringComparison.Ordinal);
        Assert.Contains(".Concat(candidate.SuggestedFields)", code, StringComparison.Ordinal);
        Assert.Contains("SuggestedFields = candidateFields", code, StringComparison.Ordinal);
        Assert.Contains("AcceptedSuggestedKeys = candidateFields.Keys.ToList()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("_selectedSuggestedFieldKeys", code, StringComparison.Ordinal);
        Assert.Contains("sme-match-result-row", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Canonical ID", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("sme-match-result-art--qid", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("sme-match-consequence-note", shell, StringComparison.Ordinal);
        Assert.Contains(".sme-match-comparison-table", styles, StringComparison.Ordinal);
        Assert.Contains(".sme-match-workflow", styles, StringComparison.Ordinal);
        Assert.Contains(".sme-match-result-row.is-selected", styles, StringComparison.Ordinal);
        Assert.Contains(".sme-match-result-row:focus-visible", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("_selectedCandidateId = GetCandidateId(response.LinkedCandidates[0])", code, StringComparison.Ordinal);
        Assert.DoesNotContain("await SelectCandidateAsync(response.RetailCandidates[0])", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_MatchIdentityRefreshStaysInsideSharedEditor()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");

        Assert.DoesNotContain("workbench", shell, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workbench", code, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SharedMediaEditorMode.Review", code, StringComparison.Ordinal);
        Assert.Contains("SharedMediaEditorMode.Review", code, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorIdentitySummary_UsesRetailProviderNameFromContext()
    {
        var endpoint = ReadSource("src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs");
        var model = ReadSource("src/MediaEngine.Domain/Models/LibraryItemModels.cs");

        Assert.Contains("detail?.RetailProviderName", endpoint, StringComparison.Ordinal);
        Assert.Contains("detail?.RetailProviderItemId", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProviderBridgeId(detail)", endpoint, StringComparison.Ordinal);
        Assert.Contains("retail_provider_name", model, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedFieldRow_UsesCompactDetailGridAndLargerReadableFields()
    {
        var styles = ReadSource("src/MediaEngine.Web/Components/Shared/AppFormFieldRow.razor.css");

        Assert.Contains("grid-template-columns: minmax(calc(var(--tl-space-10) * 2)", styles, StringComparison.Ordinal);
        Assert.Contains("font-size: var(--tl-font-size-md) !important", styles, StringComparison.Ordinal);
        Assert.Contains("font-weight: var(--tl-font-weight-regular) !important", styles, StringComparison.Ordinal);
        Assert.Contains("tl-field-grid--locked", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_DescriptionFieldGetsMoreRoomAndPreservesParagraphSpacing()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");
        var styles = ReadSource("src/MediaEngine.Web/Components/Shared/AppFormFieldRow.razor.css");

        Assert.Contains("Lines=\"@GetFieldLineCount(field)\"", shell, StringComparison.Ordinal);
        Assert.Contains("field.Key, \"description\"", code, StringComparison.Ordinal);
        Assert.Contains("? 7 : 4", code, StringComparison.Ordinal);
        Assert.Contains("TrimEnd()", code, StringComparison.Ordinal);
        Assert.Contains("min-height: calc(var(--tl-space-10) * 4) !important", styles, StringComparison.Ordinal);
        Assert.Contains("white-space: pre-wrap !important", styles, StringComparison.Ordinal);
        Assert.Contains("resize: vertical !important", styles, StringComparison.Ordinal);
        Assert.Contains("NormalizeDescriptionParagraphs", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_DetailsKeepsFactsInOneInlineMetadataList()
    {
        var rowStyles = ReadSource("src/MediaEngine.Web/Components/MediaEditor/EditorDetailsInlineField.razor.css");
        var shellStyles = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.css");
        var details = ReadDetailsBranch();
        var presenter = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.DetailsPresentation.cs");
        var inlineRow = ReadSource("src/MediaEngine.Web/Components/MediaEditor/EditorDetailsInlineField.razor");

        Assert.DoesNotContain(".sme-details-fact-strip", shellStyles, StringComparison.Ordinal);
        Assert.Contains(".sme-details-row__text", rowStyles, StringComparison.Ordinal);
        Assert.Contains("font-size: 14px", rowStyles, StringComparison.Ordinal);
        Assert.Contains("font-size: 13px", rowStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("Primary facts", details, StringComparison.Ordinal);
        Assert.DoesNotContain("DetailsPrimaryFacts", details, StringComparison.Ordinal);
        Assert.Contains("DetailsSecondaryFields", details, StringComparison.Ordinal);
        Assert.Contains("DetailsLibrarySummary", details, StringComparison.Ordinal);
        Assert.Contains("ActiveScope.FieldEntityId", details, StringComparison.Ordinal);
        Assert.Contains("MediaEditorDetailsPresenter.Build", presenter, StringComparison.Ordinal);
        Assert.Contains("Field.ProviderName", inlineRow, StringComparison.Ordinal);
        Assert.DoesNotContain("Title=\"Source facts\"", details, StringComparison.Ordinal);
        Assert.DoesNotContain("sme-match-override-summary", details, StringComparison.Ordinal);
        Assert.DoesNotContain("fields overridden", details, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtworkLightbox_SupportsFitActualZoomPanAndKeyboardNavigation()
    {
        var adapter = ReadSource("src/MediaEngine.Web/Components/MediaEditor/MediaEditorArtworkLightbox.razor");
        var viewer = ReadSource("src/MediaEngine.Web/Components/Shared/MediaViewerShell.razor");
        var styles = ReadSource("src/MediaEngine.Web/Components/Shared/MediaViewerShell.razor.css");
        var script = ReadSource("src/MediaEngine.Web/wwwroot/js/media-viewer.js");

        Assert.Contains("<MediaViewerShell", adapter, StringComparison.Ordinal);
        Assert.Contains("AriaLabel=\"Zoom out\"", viewer, StringComparison.Ordinal);
        Assert.Contains("AriaLabel=\"Zoom in\"", viewer, StringComparison.Ordinal);
        Assert.Contains("AriaLabel=\"Fit image to window\"", viewer, StringComparison.Ordinal);
        Assert.Contains("ArrowLeft", viewer, StringComparison.Ordinal);
        Assert.Contains("ArrowRight", viewer, StringComparison.Ordinal);
        Assert.Contains("args.Key is \"+\" or \"=\"", viewer, StringComparison.Ordinal);
        Assert.Contains("args.Key == \"0\"", viewer, StringComparison.Ordinal);
        Assert.Contains("touch-action:none", styles, StringComparison.Ordinal);
        Assert.Contains(".media-viewer__stage img,.media-viewer__stage video{position:absolute;inset:0;display:block;width:100%;height:100%", styles, StringComparison.Ordinal);
        Assert.Contains("object-fit:contain", styles, StringComparison.Ordinal);
        Assert.Contains("pointermove", script, StringComparison.Ordinal);
        Assert.Contains("pinchDistance", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_UsesInlineOverrideIndicatorsAndBlankUniverseDash()
    {
        var details = ReadDetailsBranch();
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");
        var inlineRow = ReadSource("src/MediaEngine.Web/Components/MediaEditor/EditorDetailsInlineField.razor");
        var inlineStyles = ReadSource("src/MediaEngine.Web/Components/MediaEditor/EditorDetailsInlineField.razor.css");
        var editing = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.DetailsEditing.cs");

        Assert.Contains("EditorDetailsInlineField", details, StringComparison.Ordinal);
        Assert.Contains("Field.CanOverride", inlineRow, StringComparison.Ordinal);
        Assert.Contains("Field.CanRevert", inlineRow, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.Lock", inlineRow, StringComparison.Ordinal);
        Assert.Contains("Local override", inlineRow, StringComparison.Ordinal);
        Assert.Contains("SaveItemDisplayOverridesAsync", editing, StringComparison.Ordinal);
        Assert.DoesNotContain("Yellow underline means local override", details, StringComparison.Ordinal);
        Assert.DoesNotContain("EditorTargetSummary", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Override in use", details, StringComparison.Ordinal);
        Assert.Contains("(\"Universe\", string.IsNullOrWhiteSpace(summary?.UniverseName) ? \"-\"", code, StringComparison.Ordinal);
        Assert.Contains(".sme-details-row.is-overridden .sme-details-row__value", inlineStyles, StringComparison.Ordinal);
        Assert.Contains("OverrideKey", editing, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_RevertQueuesEmptyDisplayOverrideValue()
    {
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");

        Assert.Contains("_editedValues[scopedKey] = string.Empty;", code, StringComparison.Ordinal);
        Assert.Contains("_clearedInlineOverrideKeys.Add(scopedKey);", code, StringComparison.Ordinal);
        Assert.Contains("_pendingInlineRevertKeys.Add(scopedKey);", code, StringComparison.Ordinal);
        Assert.Contains("ConfirmInlineFieldRevertAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmRemoveOverrideAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DialogService.ShowAsync<AppConfirmDialog>", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_DoesNotRenderGenericContainerContents()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");
        var dto = ReadSource("src/MediaEngine.Contracts/Metadata/MediaEditorContracts.cs");
        var libraryDto = ReadSource("src/MediaEngine.Web/Models/ViewDTOs/LibraryCatalogDtos.cs");
        var schema = ReadSource("src/MediaEngine.Web/Services/Editing/MediaEditorModels.cs");

        Assert.DoesNotContain("item.TechnicalBadges", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectContentItemAsync(group, item)", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("sme-content-inspector", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveFocusedContentItemAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildTrackContentGroups", code, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildEpisodeContentGroups", code, StringComparison.Ordinal);
        Assert.DoesNotContain("GetContentOrdinalLabel", code, StringComparison.Ordinal);
        Assert.Contains("CanSelectAsEditorTarget", dto, StringComparison.Ordinal);
        Assert.Contains("CompactOrdinalLabel", dto, StringComparison.Ordinal);
        Assert.Contains("PrimaryAssetId", dto, StringComparison.Ordinal);
        Assert.Contains("IsClickable", dto, StringComparison.Ordinal);
        Assert.Contains("JsonPropertyName(\"episode_title\")", libraryDto, StringComparison.Ordinal);
        Assert.Contains("detail.SeasonNumber ?? FindCanonicalValue(canonicals, \"season_number\")", schema, StringComparison.Ordinal);
        Assert.Contains("detail.EpisodeNumber ?? FindCanonicalValue(canonicals, \"episode_number\")", schema, StringComparison.Ordinal);
        Assert.Contains("detail.EpisodeTitle ?? FindCanonicalValue(canonicals, \"episode_title\")", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("Field(\"episode_title\", \"Title\", identity: true)", schema, StringComparison.Ordinal);
        Assert.Contains("(\"TV\", \"episode\") => [\"episode_title\", \"description\", \"custom_tags\", \"sort_title\"]", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_ConsolidatesSingleItemOptionsIntoDetailsAndKeepsHistoryOutOfFile()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var details = ReadDetailsBranch();
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");
        var metadata = ReadSource("src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs");
        var schema = ReadSource("src/MediaEngine.Web/Services/Editing/MediaEditorModels.cs");

        Assert.Contains("\"details\" => new[] { \"details\", \"options\", \"sorting\" }", code, StringComparison.Ordinal);
        Assert.DoesNotContain("tabs.Add(\"options\");", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("EditorTargetSummary", shell, StringComparison.Ordinal);
        Assert.Contains("DetailsSecondaryFields", details, StringComparison.Ordinal);
        Assert.Contains("DetailsAdditionalFields", details, StringComparison.Ordinal);
        Assert.Contains("return TabDisplayOrder", code, StringComparison.Ordinal);
        Assert.DoesNotContain("(\"options\", \"Options\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("(\"file\", \"Files\"", code, StringComparison.Ordinal);
        Assert.Contains("<h3>Files</h3>", details, StringComparison.Ordinal);
        Assert.Contains("DetailsFiles", details, StringComparison.Ordinal);
        Assert.DoesNotContain("File & processing", details, StringComparison.Ordinal);
        Assert.DoesNotContain("RereadFileMetadataAsync", details, StringComparison.Ordinal);
        Assert.DoesNotContain("RetryWritebackAsync", details, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshTextTracksAsync", details, StringComparison.Ordinal);
        Assert.Contains("Change history", shell, StringComparison.Ordinal);

        Assert.DoesNotContain("Field(\"edition\", \"Edition\")", schema, StringComparison.Ordinal);
        Assert.Contains("Field(\"custom_tags\", \"Library tags\")", schema, StringComparison.Ordinal);
        Assert.Contains("Add(values, \"custom_tags\"", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_DetailsShowsAttachedFilesAsReadOnlyRows()
    {
        var details = ReadDetailsBranch();
        var tabState = ReadSource("src/MediaEngine.Web/Components/MediaEditor/MediaEditorTabState.cs");

        Assert.Contains("<h3>Files</h3>", details, StringComparison.Ordinal);
        Assert.Contains("DetailsFiles", details, StringComparison.Ordinal);
        Assert.Contains("file.FileName", details, StringComparison.Ordinal);
        Assert.Contains("DetailsTechnicalFacts", details, StringComparison.Ordinal);
        Assert.DoesNotContain("sme-details-file-count", details, StringComparison.Ordinal);
        Assert.DoesNotContain("RereadFileMetadataAsync", details, StringComparison.Ordinal);
        Assert.DoesNotContain("RetryWritebackAsync", details, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshTextTracksAsync", details, StringComparison.Ordinal);
        Assert.DoesNotContain("ImportTextTrackAsync", details, StringComparison.Ordinal);
        Assert.DoesNotContain("SetPreferredTextTrackAsync", details, StringComparison.Ordinal);
        Assert.Contains("\"file\" => \"details\"", tabState, StringComparison.Ordinal);
        Assert.Contains("\"inspector\" => \"details\"", tabState, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_UsesPagedOwnedChildInspectionBeforeProviderMatching()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");
        var client = ReadSource("src/MediaEngine.Web/Services/Integration/EngineApiClient.Details.cs");

        Assert.Contains("<EditorContextNavigator", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("<MediaEditorOwnedChildBrowser", shell, StringComparison.Ordinal);
        Assert.Contains("SearchEditorContextOptionsAsync", code, StringComparison.Ordinal);
        Assert.Contains("GetMediaEditorOwnedChildrenAsync", code, StringComparison.Ordinal);
        Assert.Contains("GetMediaEditorWorkVersionsAsync", code, StringComparison.Ordinal);
        Assert.Contains("/metadata/{entityId}/owned-children", client, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_SingleMovieOffersProviderSearchImmediately()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");
        var inspector = ReadSource("src/MediaEngine.Web/Components/MediaEditor/MediaEditorOwnedFileInspector.razor");

        Assert.DoesNotContain("ShowSingleMovieOwnedFileInspector", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("ChangeMatch=\"ShowSingleMovieMatchSearch\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("_showSingleMovieMatchSearch", code, StringComparison.Ordinal);
        Assert.Contains("Search retail providers", shell, StringComparison.Ordinal);
        Assert.Contains("GetMediaEditorOwnedChildrenAsync", inspector, StringComparison.Ordinal);
        Assert.Contains("Review the local file before looking for a different provider match.", inspector, StringComparison.Ordinal);
        Assert.Contains("Change match", inspector, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_RejectsBatchLaunches()
    {
        var launcher = ReadSource("src/MediaEngine.Web/Services/Editing/MediaEditorLauncherService.cs");
        var browse = ReadSource("src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor");

        Assert.Contains("request.Mode == SharedMediaEditorMode.Batch || request.EntityIds.Count > 1", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenBatchEditorAsync", browse, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_DoesNotLabelBareIsbnAsOpenLibraryMatch()
    {
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");
        var start = code.IndexOf("private string? InferProviderNameFromIdentifierFields()", StringComparison.Ordinal);
        Assert.True(start >= 0);

        var end = code.IndexOf("private string FormatRetailIdentifierChip", start, StringComparison.Ordinal);
        Assert.True(end > start);

        var method = code[start..end];
        Assert.DoesNotContain("GetBaselineValue(\"isbn\")", method, StringComparison.Ordinal);
        Assert.DoesNotContain("return \"open_library\"", method, StringComparison.Ordinal);
        Assert.Contains("ISBN: {providerItemId}", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_ExposesHierarchyIdentityTargetsAndParentArtworkNavigation()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");
        var context = ReadSource("src/MediaEngine.Contracts/Metadata/MediaEditorContracts.cs");

        Assert.Contains("<EditorContextNavigator", shell, StringComparison.Ordinal);
        Assert.Contains("OnTargetSelected=\"SelectEditorContextTargetAsync\"", shell, StringComparison.Ordinal);
        Assert.Contains("Save and switch", shell, StringComparison.Ordinal);
        Assert.Contains("Discard and switch", shell, StringComparison.Ordinal);
        Assert.Contains("Stay here", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Open in Library", shell, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Identity target", shell, StringComparison.Ordinal);
        Assert.Contains("Selected match", shell, StringComparison.Ordinal);
        Assert.Contains("Confirm parent move", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("sme-inherited-capability", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("EditInheritedArtworkOwnerAsync", shell, StringComparison.Ordinal);
        Assert.Contains("private Guid CanonicalEndpointEntityId => CurrentEntityId", code, StringComparison.Ordinal);
        Assert.Contains("CoverUrl = candidate.CoverUrl", code, StringComparison.Ordinal);
        Assert.Contains("await Request.OnArtworkChanged.Invoke()", code, StringComparison.Ordinal);
        Assert.Contains("ActiveScope?.IdentitySummary", code, StringComparison.Ordinal);
        Assert.Contains("IdentityProviderItemId", ReadSource("src/MediaEngine.Domain/MetadataFieldConstants.cs"), StringComparison.Ordinal);
        Assert.Contains("JsonPropertyName(\"identity_summary\")", context, StringComparison.Ordinal);
        Assert.Contains("JsonPropertyName(\"available_tabs\")", context, StringComparison.Ordinal);
        Assert.Contains("JsonPropertyName(\"canonical_identity_mode\")", context, StringComparison.Ordinal);
        Assert.Contains("JsonPropertyName(\"can_select_as_editor_target\")", context, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_PreviewsRetailHierarchyBeforeApplyAndReloadsAuthoritativeSelection()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");

        Assert.Contains("<RetailHierarchyImpactNotice Preview=\"@GetRetailHierarchyImpactPreview(retailSelected)\" />", shell, StringComparison.Ordinal);
        Assert.Contains("ApiClient.PreviewMediaEditorMembershipAsync(entityId, request, cancellation.Token)", code, StringComparison.Ordinal);
        Assert.Contains("SelectedSuggestions = new Dictionary<string, MediaEditorMembershipSuggestionDto>", code, StringComparison.Ordinal);
        Assert.Contains("suggestionKey = \"show\"", code, StringComparison.Ordinal);
        Assert.Contains("suggestionKey = \"album\"", code, StringComparison.Ordinal);
        Assert.Contains("RetailHierarchyPreviewPolicy.CanApply", code, StringComparison.Ordinal);
        Assert.Contains("IsRetailSeriesLeafScope()", code, StringComparison.Ordinal);
        Assert.Contains("(\"Movies\", \"movie\") => true", code, StringComparison.Ordinal);
        Assert.Contains("FieldValues = fields.ToDictionary", code, StringComparison.Ordinal);
        Assert.Contains("BuildRetailCandidateFieldValues(candidate)", code, StringComparison.Ordinal);
        Assert.Contains("reloadFromSelectedEntity: true", code, StringComparison.Ordinal);

        var applyStart = code.IndexOf("protected async Task ApplyRetailCandidateAsync(", StringComparison.Ordinal);
        var applyEnd = code.IndexOf("protected async Task ApplyLinkedCandidateAsync(", applyStart, StringComparison.Ordinal);
        Assert.True(applyStart >= 0 && applyEnd > applyStart);
        var applyMethod = code[applyStart..applyEnd];
        Assert.Contains("RequiredFields = new Dictionary<string, string>(candidate.RequiredFields", applyMethod, StringComparison.Ordinal);
        Assert.Contains("SuggestedFields = new Dictionary<string, string>(candidate.SuggestedFields", applyMethod, StringComparison.Ordinal);
        Assert.Contains("&& !IsDirty", code.Substring(code.IndexOf("protected bool CanApplyRetailCandidate(", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.DoesNotContain("DialogService.ShowAsync", applyMethod, StringComparison.Ordinal);

        var reloadStart = code.IndexOf("private async Task ReloadAfterRetailMatchAsync(", StringComparison.Ordinal);
        var reloadEnd = code.IndexOf("private async Task NotifyParentArtworkChangedAsync(", reloadStart, StringComparison.Ordinal);
        Assert.True(reloadStart >= 0 && reloadEnd > reloadStart);
        var reloadMethod = code[reloadStart..reloadEnd];
        Assert.Contains("response.SelectedEntityId", reloadMethod, StringComparison.Ordinal);
        Assert.Contains("if (IsDirty)", reloadMethod, StringComparison.Ordinal);
        Assert.Contains("_navigator = null", reloadMethod, StringComparison.Ordinal);
        Assert.Contains("resetEditorState: true", reloadMethod, StringComparison.Ordinal);
        Assert.Contains("ResetMatchSearchState()", reloadMethod, StringComparison.Ordinal);
        Assert.Contains("_pendingMembershipPreview = null", reloadMethod, StringComparison.Ordinal);

        var loadStart = code.IndexOf("private async Task LoadSingleItemAsync(", StringComparison.Ordinal);
        var loadEnd = code.IndexOf("private async Task LoadProfilePreferencesAsync(", loadStart, StringComparison.Ordinal);
        Assert.True(loadStart >= 0 && loadEnd > loadStart);
        var loadMethod = code[loadStart..loadEnd];
        Assert.Contains("GetMediaEditorContextAsync(entityId)", loadMethod, StringComparison.Ordinal);
        Assert.Contains("GetMediaEditorNavigatorAsync(entityId)", loadMethod, StringComparison.Ordinal);
        Assert.Contains("LoadScopeStateAsync(forceReload: true)", loadMethod, StringComparison.Ordinal);
        Assert.Contains("LoadProfilePreferencesAsync(CurrentEntityId)", loadMethod, StringComparison.Ordinal);
        Assert.Contains("LoadEditorSuggestionsAsync()", loadMethod, StringComparison.Ordinal);
        Assert.Contains("LoadTextTracksAsync()", loadMethod, StringComparison.Ordinal);

        var noticeStyles = ReadSource("src/MediaEngine.Web/Components/MediaEditor/RetailHierarchyImpactNotice.razor.css");
        Assert.Contains("--tl-status-danger-border", noticeStyles, StringComparison.Ordinal);
        Assert.Contains("--tl-status-danger-soft", noticeStyles, StringComparison.Ordinal);
        Assert.Contains("--tl-status-danger", noticeStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditor_UsesPersistentGenericHierarchicalContextNavigation()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");
        var navigator = ReadSource("src/MediaEngine.Web/Components/MediaEditor/EditorContextNavigator.razor");
        var styles = ReadSource("src/MediaEngine.Web/Components/MediaEditor/EditorContextNavigator.razor.css");
        var models = ReadSource("src/MediaEngine.Web/Components/MediaEditor/EditorContextModels.cs");

        Assert.Contains("Levels=\"@EditorContextLevels\"", shell, StringComparison.Ordinal);
        Assert.Contains("sme-header-identity", shell, StringComparison.Ordinal);
        Assert.True(shell.IndexOf("<EditorContextNavigator", StringComparison.Ordinal) < shell.IndexOf("sme-workspace", StringComparison.Ordinal));
        Assert.DoesNotContain("Edit season", shell, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sme-context-level-action", shell, StringComparison.Ordinal);
        Assert.Contains("BuildEditorContextLevels", code, StringComparison.Ordinal);
        Assert.Contains("GetContextArtworkUrl", code, StringComparison.Ordinal);
        Assert.Contains("ApiClient.ToAbsoluteEngineUrl", code, StringComparison.Ordinal);
        Assert.Contains("GetContextRetailStatus", code, StringComparison.Ordinal);
        Assert.Contains("GetContextCanonicalStatus", code, StringComparison.Ordinal);
        Assert.Contains("node.EntityId == selectedNode?.EntityId ?", code, StringComparison.Ordinal);
        Assert.Contains("IsTextOnlyContextNode", code, StringComparison.Ordinal);
        Assert.Contains("Math.Max(2, discoveredMaxDepth)", code, StringComparison.Ordinal);
        Assert.Contains("(\"TV\", 0) => \"series\"", code, StringComparison.Ordinal);
        Assert.Contains("(\"TV\", 1) => \"season\"", code, StringComparison.Ordinal);
        Assert.Contains("(\"TV\", 2) => \"episode\"", code, StringComparison.Ordinal);
        Assert.Contains("\"film_series\" => \"Film Series\"", code, StringComparison.Ordinal);
        Assert.Contains("\"movie\" => \"Movie\"", code, StringComparison.Ordinal);
        Assert.Contains("public sealed record EditorContextLevel", models, StringComparison.Ordinal);
        Assert.Contains("AppOverflowMenu", navigator, StringComparison.Ordinal);
        Assert.Contains("\"film_series\" => Icons.Material.Outlined.VideoLibrary", navigator, StringComparison.Ordinal);
        Assert.Contains("\"movie\" => Icons.Material.Outlined.Movie", navigator, StringComparison.Ordinal);
        Assert.Contains("SearchThreshold", navigator, StringComparison.Ordinal);
        Assert.Contains("SearchOptionsAsync", navigator, StringComparison.Ordinal);
        Assert.Contains("MaximumSearchResults = 100", navigator, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource", navigator, StringComparison.Ordinal);
        Assert.DoesNotContain("Take(MaxVisibleOptions)", navigator, StringComparison.Ordinal);
        Assert.Contains("editor-context-option-search", navigator, StringComparison.Ordinal);
        Assert.Contains("editor-context-level__artwork", navigator, StringComparison.Ordinal);
        Assert.DoesNotContain("editor-context-level__statuses", navigator, StringComparison.Ordinal);
        Assert.Contains("editor-context-option is-active", navigator, StringComparison.Ordinal);
        Assert.Contains("editor-context-level__body", navigator, StringComparison.Ordinal);
        Assert.DoesNotContain("editor-context__separator", navigator, StringComparison.Ordinal);
        Assert.Contains("level.Label", navigator, StringComparison.Ordinal);
        Assert.Contains("level.Title", navigator, StringComparison.Ordinal);
        Assert.Contains("MatchAnchorWidth", navigator, StringComparison.Ordinal);
        Assert.Contains("border-left:", styles, StringComparison.Ordinal);
        Assert.Contains("flex-wrap:wrap", styles, StringComparison.Ordinal);
        Assert.Contains("@media(max-width:700px)", styles, StringComparison.Ordinal);
        Assert.Contains("editor-context-option-search__input", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedHeader_ReservesCloseSpaceAndKeepsControlsAlignedAcrossBreakpoints()
    {
        var shellStyles = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.css");
        var selectorStyles = ReadSource("src/MediaEngine.Web/Components/MediaEditor/EditorContextNavigator.razor.css");
        Assert.Contains("padding-right:3.5rem", shellStyles);
        Assert.Contains("@media(max-width:1100px)", shellStyles);
        Assert.Contains("@media(max-width:700px)", selectorStyles);
        Assert.Contains("flex-basis:100%", selectorStyles);
        Assert.Contains("position:absolute", shellStyles);
        Assert.Contains("min-height:5.6rem", selectorStyles);
    }

    [Fact]
    public void ViewVideoControls_CenterTransportOverStageAndKeepToolsAtBottom()
    {
        var viewer = ReadSource("src/MediaEngine.Web/Components/Shared/MediaViewerShell.razor");
        var styles = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackVideoChrome.razor.css");
        Assert.Contains("<PlaybackVideoChrome", viewer);
        Assert.Contains("<BottomContent>", viewer);
        Assert.Contains("<PlaybackSeekRail", viewer);
        Assert.Contains("playback-video-controls__left", viewer);
        Assert.Contains("playback-video-controls__right", viewer);
        Assert.DoesNotContain("<TransportContent>", viewer);
        Assert.Contains("--playback-primary-size:44px", styles);
        Assert.Contains("align-items:center", styles);
        Assert.Contains("@media(max-width:720px)", styles);
    }

    private static string ReadDetailsBranch()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var start = source.IndexOf("else if (_activeTab == \"details\")", StringComparison.Ordinal);
        var end = source.IndexOf("else if (_activeTab == \"chapters\"", start + 1, StringComparison.Ordinal);
        if (end < 0)
        {
            end = source.IndexOf("else if (_activeTab == \"artwork\")", start + 1, StringComparison.Ordinal);
        }
        Assert.True(start >= 0 && end > start, "Could not isolate the Details branch.");
        return source[start..end];
    }

    private static string ReadSource(
        string relativePath,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "") =>
        File.ReadAllText(Path.Combine(FindRepoRoot(sourceFile), relativePath));

    private static string FindRepoRoot(string sourceFile)
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
            return directory.FullName;
        }

        directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
