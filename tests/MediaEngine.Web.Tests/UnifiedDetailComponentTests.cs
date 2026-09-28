using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;

namespace MediaEngine.Web.Tests;

public sealed class UnifiedDetailComponentTests
{
    [Fact]
    public void HeroBackdrop_RendersCentralizedHeroArtworkModes()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/Details/HeroBackdrop.razor");
        var styles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");

        Assert.Contains("Artwork.HeroArtwork", source);
        Assert.Contains("HeroArtworkMode.BackdropWithLogo", source);
        Assert.Contains("HeroArtworkMode.BackdropWithRenderedTitle", source);
        Assert.Contains("HeroArtworkMode.ArtworkFallback", source);
        Assert.Contains("tl-detail-media-stage--background", source);
        Assert.DoesNotContain("tl-detail-media-stage__background-fill", source);
        Assert.Contains("tl-detail-media-stage--artwork-fallback", source);
        Assert.Contains("tl-detail-hero__artwork", source);
        Assert.Contains("tl-detail-hero__overlays", source);
        Assert.Contains("tl-detail-hero__foreground-art", source);
        Assert.Contains("tl-detail-media-stage__cover-atmosphere", source);
        Assert.Contains("tl-detail-media-stage__cover-wrap", source);
        Assert.Contains("tl-detail-media-stage__foreground", source);
        Assert.Contains("@onerror=\"HandleImageError\"", source);
        Assert.Contains("DetailEntityType.Book or DetailEntityType.Work => HeroForegroundTreatment.Book", source);
        Assert.Contains("DetailEntityType.Audiobook => HeroForegroundTreatment.Cover", source);
        Assert.Contains("DetailEntityType.Audiobook => \"tl-detail-media-stage--audiobook\"", source);
        Assert.Contains("grid-template-columns: minmax(0, auto) clamp(1rem, 2.2vw, 1.65rem)", styles);
        Assert.Contains("grid-column: 2", styles);
        Assert.Contains("align-self: stretch", styles);
        Assert.Contains("transform: translateX(-0.12rem)", styles);
        Assert.Contains("height: min(47rem, 55svh)", styles);
        Assert.Contains("max-height: min(47rem, 55svh) !important", styles);
        Assert.Contains("width: min(45vw, 42rem)", styles);
        Assert.Contains("justify-content: flex-end", styles);
        Assert.DoesNotContain("right: 0.12rem", styles);

        Assert.Contains("tl-detail-media-stage--background .tl-detail-media-stage__overlay", styles);
        Assert.Contains("tl-detail-media-stage--artwork-fallback .tl-detail-media-stage__overlay", styles);
        Assert.Contains("to right", styles);
        Assert.Contains("rgba(var(--hero-shadow-rgb), 0.86) 0%", styles);
        Assert.Contains("ellipse at 72% 42%", styles);
        Assert.Contains("to bottom", styles);
        Assert.Contains("transparent 45%", styles);
        Assert.Contains("rgba(var(--hero-shadow-rgb), 0.95) 100%", styles);
        Assert.Contains("filter: blur(42px) saturate(1.18) brightness(0.72)", styles);
        Assert.Contains("opacity: 0.78", styles);
        Assert.Contains("transform: scale(1.08)", styles);
        Assert.Contains("object-fit: contain", styles);
        Assert.Contains("tl-detail-media-stage__foreground--cover .tl-detail-media-stage__cover", styles);
        Assert.Contains("opacity: 1", styles);
        Assert.DoesNotContain("opacity: 0.58", styles);
        Assert.Contains("width: fit-content", styles);
        Assert.Contains("height: 100%", styles);
        Assert.DoesNotContain("0 0 0 1px rgba(255, 255, 255, 0.10)", styles);
        Assert.Contains("content: none", styles);
        Assert.Contains("filter: none", styles);
        Assert.Contains("object-fit: contain", styles);
        Assert.Contains("tl-detail-hero--fallback-generated:not(.tl-detail-hero--watch)", styles);
        Assert.Contains("background-size: cover", styles);
        Assert.Contains("object-position: var(--hero-image-position, center right)", styles);
        Assert.Contains("object-position: var(--mobile-image-position, center top)", styles);
        Assert.DoesNotContain("tl-detail-media-stage__background-fill", styles);
        Assert.Contains("tl-detail-media-stage--background::before", styles);
        Assert.Contains("background-image: var(--hero-image)", styles);
        Assert.Contains("--hero-wash-accent", styles);
        Assert.Contains("mask-image: linear-gradient(to right", styles);
        Assert.Contains("mask-image: linear-gradient(to bottom", styles);
        Assert.Contains("left: auto", styles);
        Assert.Contains("width: auto", styles);
        Assert.Contains("max-width: 100%", styles);
        Assert.Contains("transform: translateY(-50%)", styles);
        Assert.Contains("rgba(0, 0, 0, 0) 0%", styles);
        Assert.Contains("background-size: cover", styles);
        Assert.DoesNotContain("tl-detail-hero--watch .tl-detail-hero__artwork,", styles);
        Assert.DoesNotContain("tl-detail-hero--watch .tl-detail-hero__artwork::after", styles);
        Assert.Contains("height: 95svh", styles);
        Assert.Contains("min-height: 95svh", styles);
        Assert.Contains("CINEMATIC_CONTRACT_EOF", styles);
        Assert.DoesNotContain("min-height: 1000px", styles);
        Assert.Contains("rgba(0, 0, 0, 0.60) 14%", styles);
        Assert.Contains("rgba(0, 0, 0, 0.86) 23%", styles);
        Assert.Contains("#000 32%", styles);
        Assert.Contains("tl-detail-tabs::before", styles);
        Assert.Contains("background: #090c12", styles);
        Assert.Contains("font-family: Georgia, \"Times New Roman\", serif", styles);
        Assert.Contains("font-weight: 500", styles);
        Assert.Contains("max-width: 22ch", styles);
        Assert.DoesNotContain("min-height: calc(100dvh - 5.75rem)", styles);
        Assert.DoesNotContain("tl-detail-backdrop", styles);
        Assert.DoesNotContain("tl-hero-art", styles);
        Assert.DoesNotContain("var(--tl-detail-accent) 30%", styles);
        Assert.DoesNotContain("linear-gradient(135deg, color-mix(in srgb, var(--tl-detail-primary) 36%, #101115)", styles);
    }

    [Fact]
    public void DetailHero_UsesCentralizedGradientVariablesAndProgress()
    {
        var presentation = ReadSource("src/MediaEngine.Web/Components/Details/DetailHeroPresentation.cs");
        var hero = ReadSource("src/MediaEngine.Web/Components/Details/DetailHero.razor");
        var progress = ReadSource("src/MediaEngine.Web/Components/Details/HeroProgressBlock.razor");
        var styles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");
        var client = ReadEngineApiClientSources();
        var appStyles = ReadSource("src/MediaEngine.Web/wwwroot/app.css");
        var layout = ReadSource("src/MediaEngine.Web/Shared/MainLayout.razor");
        var layoutStyles = ReadSource("src/MediaEngine.Web/Shared/MainLayout.razor.css");

        Assert.Contains("tl-detail-hero--backdrop tl-detail-hero--backdrop-logo", presentation);
        Assert.Contains("tl-detail-hero--backdrop tl-detail-hero--backdrop-title", presentation);
        Assert.Contains("tl-detail-hero--watch", presentation);
        Assert.Contains("tl-detail-hero--read", presentation);
        Assert.Contains("tl-detail-hero--listen", presentation);
        Assert.Contains("tl-detail-hero--music", presentation);
        Assert.Contains("tl-detail-hero--fallback-generated", presentation);
        Assert.Contains("(R: 8, G: 12, B: 18)", presentation);
        Assert.Contains("(R: 4, G: 7, B: 12)", presentation);
        Assert.Contains("(R: 220, G: 165, B: 62)", presentation);
        Assert.Contains("BuildArtworkBackground(parsedColors)", presentation);
        Assert.Contains("ResolveSubtitle(model, isWatchHero)", presentation);
        Assert.Contains("isWatchHero || UsesPrimaryHeroChrome(model.EntityType)", presentation);
        Assert.Contains("Title=\"@Model.Title\"", hero);
        Assert.Contains("Progress=\"Presentation.Progress\"", hero);
        Assert.DoesNotContain("<HeroProgressBlock", hero, StringComparison.Ordinal);
        Assert.Contains("HeroCreditLines", hero);
        Assert.Contains("tl-detail-hero-credit-stack--audiobook", hero);
        Assert.Contains("CreditGroupType.Narrators", hero);
        Assert.Contains("CreditGroupType.Illustrators", hero);
        Assert.Contains("case DetailEntityType.MusicAlbum", hero);
        Assert.Contains("IsWatchHero=\"Presentation.IsWatchHero\"", hero);
        Assert.Contains("UsePrimaryHeroChrome=\"Presentation.UsePrimaryHeroChrome\"", hero);
        Assert.Contains("<MediaArtworkGroupPreview", hero);
        Assert.Contains("CollectionHeroArtwork", hero);
        Assert.Contains(".Take(4)", hero);
        Assert.Contains("tl-detail-collection-artwork", hero);
        Assert.Contains("usePrimaryHeroChrome ? string.Empty : FormatEntityType", presentation);
        Assert.Contains("UsesPrimaryHeroChrome", presentation);
        Assert.Contains("tl-detail-hero-progress", progress);
        Assert.Contains("--hero-progress", progress);
        Assert.Contains("Progress = detail.Progress", client);
        Assert.Contains(".tl-detail-hero-progress__fill", styles);
        Assert.Contains("rgba(var(--hero-shadow-rgb), 0.24) 0%", styles);
        Assert.Contains("rgba(var(--hero-shadow-rgb), 0.06) 42%", styles);
        Assert.Contains("rgba(var(--hero-shadow-rgb), 0.96) 100%", styles);
        Assert.Contains("rgba(var(--hero-bg-rgb), 0.38) 38%", styles);
        Assert.Contains("rgba(var(--hero-bg-rgb), 0.09) 58%", styles);
        Assert.Contains("rgba(var(--hero-bg-rgb), 0) 78%", styles);
        Assert.DoesNotContain("rgba(0, 0, 0, 0.10) 36%", styles);
        Assert.Contains("tl-detail-hero--watch .tl-detail-genre-chip", styles);
        Assert.Contains("background: rgba(20, 23, 28, 0.78)", styles);
        Assert.Contains("border: 1px solid rgba(216, 180, 254, 0.88)", styles);
        Assert.Contains("linear-gradient(180deg, var(--tl-accent-primary, #8b5cf6) 0%, var(--tl-accent-primary-active, #7652d6) 100%)", styles);
        Assert.Contains("0 0 28px rgba(139, 92, 246, 0.48)", styles);
        Assert.Contains("IsDetailShell", layout);
        Assert.Contains("MainContainerClass", layout);
        Assert.Contains("py-0", layout);
        Assert.Contains("private string AppBarClass => IsCinematicShell", layout);
        Assert.Contains("private string MainContentClass => IsCinematicShell", layout);
        Assert.Contains("main-content-with-topbar--cinematic", layoutStyles);
        Assert.Contains("layout-shell__appbar--cinematic", layoutStyles);
        Assert.DoesNotContain("body:has(.tl-detail-page) .main-content-with-topbar", appStyles);
        Assert.DoesNotContain("body:has(.tl-detail-page) .layout-shell__appbar,", appStyles);
        Assert.Contains("backdrop-filter: blur(0.7rem)", layoutStyles);
        Assert.DoesNotContain("object-position: right top", styles);
        Assert.Contains("tl-detail-hero:not(.tl-detail-hero--person) .tl-detail-hero__inner", styles);
        Assert.Contains("align-items: center", styles);
        Assert.Contains("width: min(34rem, 100%)", styles);
        Assert.Contains("object-position: left center", styles);
        Assert.Contains(".tl-detail-hero:not(.tl-detail-hero--person) .tl-detail-actions", styles);
        Assert.Contains("tl-detail-secondary-action--watch + .tl-detail-secondary-action--watch::before", styles);
        Assert.Contains("transform: translateY(-1rem)", styles);
    }

    [Fact]
    public void DetailHero_RendersTvHeroBrandBadge()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/Details/DetailHero.razor");
        var metadata = ReadSource("src/MediaEngine.Web/Components/Details/HeroMetadataPills.razor");
        var styles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");
        var client = ReadEngineApiClientSources();

        Assert.Contains("Model.HeroBrand is not null", source);
        Assert.Contains("HeroBrand=\"Model.HeroBrand\"", source);
        Assert.Contains("RenderHeroBrandInMetadata=\"false\"", source);
        Assert.Contains("tl-detail-hero-brand", styles);
        Assert.Contains("ShouldSuppressTypeStat", metadata);
        Assert.Contains("Presentation.IsWatchHero", source);
        Assert.Contains("tl-detail-hero-brand img", styles);
        Assert.Contains("NormalizeHeroBrand", client);
        Assert.Contains("StreamingServiceLogoResolver", client);
        Assert.Contains("ResolveLogoPath(heroBrand.Label)", client);
    }

    [Fact]
    public void HeroMetadata_UsesInlineRowInsteadOfPills()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/Details/HeroMetadataPills.razor");
        var styles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");

        Assert.Contains("IsWatchHero", source);
        Assert.Contains("UsePrimaryHeroChrome", source);
        Assert.Contains("tl-detail-watch-metadata-row", source);
        Assert.Contains("IsWatchHeroStat", source);
        Assert.Contains("IsPrimaryChromeHeroStat", source);
        Assert.DoesNotContain("or \"type\" or \"genre\"", source);
        Assert.Contains("Icons.Material.Filled.Star", source);
        Assert.Contains("tl-detail-watch-metadata-item--rating", source);
        Assert.Contains("tl-detail-watch-metadata-item--classification", source);
        Assert.Contains("WatchIconFor", source);
        Assert.Contains("GenreItems", source);
        Assert.Contains("genre:{item.Label}", source);
        Assert.Contains("<HeroGenreList Genres=\"inlineGenreItems\"", source);
        Assert.Contains("tl-detail-hero-genres", ReadSource("src/MediaEngine.Web/Components/Details/HeroGenreList.razor"));
        Assert.Contains("tl-detail-watch-metadata-row--facts", source);
        Assert.Contains("inlinePrimaryItems", source);
        Assert.Contains("inlineGenreItems", source);
        Assert.DoesNotContain(">, </span>", source);
        Assert.DoesNotContain("WatchSeparator", source);
        Assert.Contains("href=\"@entry.item.Route\"", source);
        Assert.Contains("tl-detail-metadata-row", source);
        Assert.Contains("tl-detail-metadata-item", source);
        Assert.Contains("tl-detail-metadata-item--rating", source);
        Assert.DoesNotContain("tl-detail-pill", source);
        Assert.Contains(".Take(6)", source);
        Assert.Contains(".Where(item => !KindEquals(item, \"genre\"))", source);
        Assert.Contains("or \"episode_count\" or \"duration\" or \"genre\" or \"quality\" or \"subtitles\" or \"rating\"", source);
        Assert.Contains("\"episode_count\" => 2", source);
        Assert.Contains("\"rating\" => 4", source);
    }

    [Fact]
    public void DetailHero_UsesOneLeftAlignedScaleAcrossMediaLanes()
    {
        var styles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");

        Assert.Contains("justify-self: start", styles);
        Assert.Contains("max-height: none", styles);
        Assert.Contains(".tl-detail-watch-metadata-row--genres", styles);
        Assert.Contains(".tl-detail-watch-metadata-row--facts", styles);
        Assert.Contains("flex-wrap: nowrap", styles);
        Assert.Contains("font-size: clamp(1rem, 1.3vw, 1.18rem)", styles);
        Assert.Contains("--tl-detail-hero-action-height: 3.5rem", styles);
        Assert.Contains("height: var(--tl-detail-hero-action-height) !important", styles);
        Assert.Contains("min-height: 4.8rem", styles);
        Assert.Contains(".tl-detail-primary-actions:has(> :only-child)", styles);
        Assert.Contains("grid-template-columns: 1fr", styles);
        Assert.Contains("width: min(34rem, 100%)", styles);
    }

    [Fact]
    public void HeroActions_ExposeHoverMenusAndCapabilityAwareActions()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/Details/HeroActionRow.razor");
        var composer = ReadDetailComposerSource();
        var styles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");

        Assert.Contains("tl-detail-premium-action", source);
        Assert.Contains("tl-detail-action--secondary-button", source);
        Assert.Contains("tl-detail-actions--watch", source);
        Assert.Contains("tl-detail-secondary-action--watch", source);
        Assert.Contains("VisibleSecondaryActions", source);
        Assert.Contains("favorite_filled", source);
        Assert.Contains("Icons.Material.Filled.Favorite", source);
        Assert.Contains("UsePrimaryHeroChrome && action.Key == \"read-listen\"", source);
        Assert.DoesNotContain("add-to-collection", source);
        Assert.Contains("read-listen", composer);
        Assert.DoesNotContain("Key = \"preview\",", composer);
        Assert.Contains("IsReadableEntity", composer);
        Assert.DoesNotContain("watch-party", composer);
        Assert.Contains("Label = isSelected ? \"In My List\" : \"Add to My List\"", composer);
        Assert.Contains("Key = \"my-list\"", composer);
        Assert.DoesNotContain("Label = \"Add to Collection\"", composer);
        Assert.Contains("HasPrimaryOverflowCluster", source);
        Assert.Contains("tl-detail-primary-actions--with-overflow", source);
        Assert.Contains("HasCollectionPrimaryOverflowCluster", source);
        Assert.Contains("tl-detail-primary-actions--collection-overflow", source);
        Assert.Contains("action.Key == \"shuffle-collection\"", source);
        Assert.Contains(".tl-detail-primary-actions--collection-overflow .app-overflow-menu", styles);
        Assert.Contains("flex-wrap: nowrap", styles);
        Assert.Contains("BuildMyListAction", composer);
        Assert.Contains("BuildReactionAction", composer);
        Assert.Contains("reaction-menu", composer);
        Assert.Contains("reaction-dislike", composer);
        Assert.Contains("reaction-like", composer);
        Assert.Contains("reaction-love", composer);
        Assert.Contains("tl-detail-flat-action", source);
        Assert.Contains("ReactionStateClass", source);
        Assert.Contains("border-radius: 0.5rem", styles);
        Assert.Contains("border-radius: 0.75rem", styles);
        Assert.Contains("tl-detail-hero--read:not(.tl-detail-hero--watch) .tl-detail-genre-chip", styles);
        Assert.Contains("tl-detail-hero--fallback-surface:not(.tl-detail-hero--watch) .tl-detail-genre-chip", styles);
        Assert.Contains(".tl-detail-actions--watch .tl-detail-action--primary", styles);
        Assert.Contains(".tl-detail-watch-secondary.is-selected", styles);
        Assert.Contains("tl-detail-hero--read:not(.tl-detail-hero--watch) .tl-detail-actions--watch .tl-detail-action--primary", styles);
        Assert.Contains("tl-detail-hero--read:not(.tl-detail-hero--watch)", styles);
        Assert.Contains("tl-detail-hero-credit-stack--audiobook", styles);
        Assert.Contains("::deep .tl-detail-hero-credit-stack__line", styles);
        Assert.Contains("letter-spacing: 0.03em", styles);
        Assert.Contains("tl-detail-media-stage--book.tl-detail-media-stage--cover-fallback", styles);
        Assert.Contains("overflow: visible", styles);
        Assert.Contains("height: min(50rem, 82vh)", styles);
        Assert.Contains("height: auto", styles);
        Assert.Contains("max-height: min(47rem, 55svh) !important", styles);
        Assert.DoesNotContain("SupportsWatchParty", composer);
    }

    [Fact]
    public void DetailPage_WiresReadAndFavoriteHeroActions()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor");

        Assert.Contains("ResolveWorkToAssetAsync", source);
        Assert.Contains("Nav.NavigateTo($\"/read/{assetId.Value:D}\")", source);
        Assert.Contains("SavedItemService SavedItems", source);
        Assert.Contains("ToggleFavoriteAsync(action)", source);
        Assert.Contains("MediaReactionService Reactions", source);
        Assert.Contains("SetReactionAsync(action)", source);
        Assert.Contains("action.Key == \"my-list\"", source);
    }

    [Fact]
    public void DetailPage_RendersExpectedSpecializedTabs()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor");

        Assert.DoesNotContain("EpisodesTab", source);
        Assert.Contains("CurrentActiveTab is \"episodes\"", source);
        Assert.Contains("<SequencePlacementPanel Placement=\"Model.SequencePlacement\"", source);
        Assert.DoesNotContain("FormatsTab", source);
        Assert.Contains("SyncTab", source);
        Assert.Contains("RegistryTab", source);
    }

    [Fact]
    public void SequencePlacement_UsesArtworkSeasonBrowserForTvEpisodes()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/Details/SequencePlacementPanel.razor");
        var detailPage = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor");
        var editor = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");

        var styles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");

        Assert.Contains("IsSeasonContainer", source);
        Assert.Contains("ShowGroupSelector => IsSeasonContainer", source);
        Assert.Contains("HeadingLabel => IsSeasonContainer ? \"Season\" : \"Series\"", source);
        Assert.Contains("ActiveGroup?.Title", source);
        Assert.Contains("Choose season", source);
        Assert.Contains("tl-season-toggle", source);
        Assert.Contains("tl-season-browser__strip", source);
        Assert.Contains("ArtworkSmallUrl", source);
        Assert.Contains("BackgroundArtworkUrl", source);
        Assert.DoesNotContain("SeasonOwnedSummary", source);
        Assert.DoesNotContain("Edit season artwork", source);
        Assert.DoesNotContain("Edit artwork for {season.Title}", source);
        Assert.DoesNotContain("tl-season-card__edit", source);
        Assert.DoesNotContain("OnEditGroup", source);
        Assert.DoesNotContain("OpenSeasonEditorAsync", detailPage);
        Assert.Contains("SeasonPosterArtworkSlot", editor);
        Assert.Contains("scope.ArtworkSlots", editor);
        Assert.Contains("tl-season-toggle__chevron", source);
        Assert.Contains("Icons.Material.Outlined.ExpandMore", source);
        Assert.DoesNotContain("SeasonCountSummary", source);
        Assert.Contains("aria-label=\"@OwnershipSummaryLabel\"", source);
        Assert.Contains("item.Route", source);
        Assert.Contains("CurrentActiveTab is \"episodes\"", detailPage);
        Assert.Contains("SequencePlacementPanel", detailPage);
        Assert.False(File.Exists(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Details/EpisodesTab.razor")));
        Assert.Contains("var(--tl-accent-primary, #8b5cf6)", styles);
    }

    [Fact]
    public void SequencePlacement_EnlargesSeasonArtworkWithoutGrowingTheBrowserRow()
    {
        var styles = ReadSource("src/MediaEngine.Web/Components/Details/SequencePlacementPanel.razor.css");

        Assert.Contains("grid-auto-columns: clamp(11.5rem, 15vw, 14rem)", styles);
        Assert.Contains("justify-content: start", styles);
        Assert.Contains("text-align: center", styles);
        Assert.Contains(".tl-series-detail.is-season-container .tl-series-detail__carousel", styles);
        Assert.Contains("min-height: clamp(24rem, 58svh, 30rem)", styles);
        Assert.Contains("padding: 0.25rem 0.45rem 0.15rem", styles);
        Assert.Contains(".tl-series-detail.is-season-container .tl-series-detail__ownership", styles);
        Assert.Contains("min-height: 1.65rem", styles);
        Assert.Contains("padding-top: 0.15rem", styles);
        Assert.DoesNotContain(".tl-season-card__edit", styles);
        Assert.DoesNotContain(".tl-series-detail__edit-season", styles);
    }

    [Fact]
    public void DetailPage_CastTabDoesNotInlineCharacters()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor");
        var peopleTab = ReadSource("src/MediaEngine.Web/Components/Details/PeopleAndCharactersTab.razor");

        Assert.Contains("IncludeCharacters=\"IncludeCharactersInPeopleTab\"", source);
        Assert.Contains("CurrentActiveTab is not \"cast\"", source);
        Assert.Contains("IncludeCharacters && CharacterGroups.Count > 0", peopleTab);
    }

    [Fact]
    public void CreditsTab_UsesCastLikeFluidCreditCards()
    {
        var styles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");

        Assert.Contains(".tl-credits-detail-card .tl-credit-grid", styles);
        Assert.Contains("grid-template-columns: repeat(auto-fill, minmax(8.5rem, 1fr))", styles);
        Assert.Contains("align-content: start", styles);
        Assert.Contains("align-items: start", styles);
        Assert.Contains("align-self: start", styles);
        Assert.Contains("max-width: 12rem", styles);
        Assert.DoesNotContain("grid-template-columns: repeat(auto-fill, minmax(8.75rem, 9.75rem))", styles);
    }

    [Fact]
    public void OverviewTab_CombinesOverviewWithCredits()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/Details/OverviewTab.razor");
        var styles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");

        Assert.Contains("tl-overview-surface", source);
        Assert.Contains("tl-overview-section--copy", source);
        Assert.Contains("tl-overview-section--credits", source);
        Assert.Contains("tl-overview-credit-rows", source);
        Assert.Contains("OverviewDescriptionParagraphs", source);
        Assert.Contains("ShowSeriesDescription", source);
        Assert.DoesNotContain("SequencePlacementPanel Placement=\"Model.SequencePlacement\"", source);
        Assert.DoesNotContain("MoreLikeThisItems", source);
        Assert.DoesNotContain("IsRecommendationGroup", source);
        Assert.DoesNotContain("tl-overview-card--related", source);
        Assert.Contains("BuildVideoCreditGroups", source);
        Assert.Contains("CreditsFor(CreditGroupType.Directors).Take(2)", source);
        Assert.Contains("new OverviewCreditGroup(\"Cast\"", source);
        Assert.DoesNotContain("Top billed cast", source);
        Assert.Contains("private static string OverviewCreditGroupTitle", source);
        Assert.Contains("OverviewGroupPriority", source);
        Assert.Contains("OverviewCreditIdentity", source);
        Assert.Contains("tl-overview-credit-groups", source);
        Assert.Contains("View full @CreditPanelTitle.ToLowerInvariant()", source);
        Assert.Contains("OverviewCreditRows", source);
        Assert.Contains("ShowOverviewCredits => Model.EntityType is not (", source);
        Assert.Contains("or DetailEntityType.ComicSeries", source);
        Assert.Contains("tl-overview-surface--collection", source);
        Assert.Contains(".tl-overview-surface--collection", styles);
        Assert.Contains("CreditGroupType.Authors", source);
        Assert.Contains("CreditGroupType.Narrators", source);
        Assert.Contains("CreditGroupType.Writers", source);
        Assert.Contains("CreditGroupType.Illustrators", source);
        Assert.DoesNotContain("RelatedEntityChip", source);
        Assert.Contains("OverviewParagraphs(Model.Description)", source);
        Assert.Contains("Split('\\n'", source);
        Assert.Contains("Replace(\"\\\\n\", \"\\n\", StringComparison.Ordinal)", source);
        Assert.Contains("DescriptionAttribution Attribution=\"Model.DescriptionAttribution\" Compact=\"true\"", source);
        Assert.Contains("grid-template-columns: minmax(0, 1.15fr) minmax(28rem, 1fr)", styles);
        Assert.Contains(".tl-overview-section--secondary", styles);
        Assert.Contains(".tl-overview-credit-rows .tl-credit-card--compact", styles);
        Assert.Contains("width: calc(100% - clamp(6.5rem, 7vw, 9rem))", styles);
        Assert.Contains("white-space: pre-line", styles);
        Assert.Contains(".tl-overview-section__prose p", styles);
    }

    [Fact]
    public void RelatedContent_UsesItsOwnRenderableTab()
    {
        var detailPage = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor");
        var overview = ReadSource("src/MediaEngine.Web/Components/Details/OverviewTab.razor");
        var related = ReadSource("src/MediaEngine.Web/Components/Details/RelatedTab.razor");
        var navigation = ReadSource("src/MediaEngine.Web/Services/Navigation/DetailTabNavigation.cs");

        Assert.Contains("CurrentActiveTab is \"related\"", detailPage);
        Assert.Contains("<RelatedTab Model=\"Model\"", detailPage);
        Assert.DoesNotContain("MoreLikeThisItems", overview);
        Assert.Contains("More from your library", related);
        Assert.Contains("item.EntityType is not DetailEntityType.TvEpisode", related);
        Assert.Contains("\"related\" => model.MediaGroups.Any(IsRecommendationGroup)", navigation);
    }

    [Fact]
    public void DetailHero_PutsOverflowActionsInHeroActionRow()
    {
        var hero = ReadSource("src/MediaEngine.Web/Components/Details/DetailHero.razor");
        var actions = ReadSource("src/MediaEngine.Web/Components/Details/HeroActionRow.razor");
        var menu = ReadSource("src/MediaEngine.Web/Components/Details/OverflowActionMenu.razor");
        var menuItems = ReadSource("src/MediaEngine.Web/Components/Details/ManageActionsMenu.razor");
        var detailPage = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor");
        var appStyles = ReadSource("src/MediaEngine.Web/wwwroot/app.css");

        Assert.Contains("OverflowActions=\"Model.OverflowActions\"", hero);
        Assert.Contains("OverflowActionMenu", actions);
        Assert.Contains("VisibleSecondaryActions", actions);
        Assert.Contains("Actions.Count > 0", menu);
        Assert.Contains("<AppOverflowMenu", menu);
        Assert.Contains("PopoverClass=\"@PopoverRootClass\"", menu);
        Assert.Contains("Class=\"tl-detail-overflow tl-detail-more-action\"", menu);
        Assert.Contains("TriggerClass=\"@ButtonClass\"", menu);
        Assert.DoesNotContain("EffectiveButtonClass", menu);
        Assert.Contains("Actions.Count == 1", menu);
        Assert.Contains("Label=\"More\"", actions);
        Assert.Contains("tl-detail-primary-actions", actions);
        Assert.Contains("<AppMenuItem", menuItems);
        Assert.Contains("Disabled=\"@action.IsDisabled\"", menuItems);
        Assert.Contains("AppUiTone.Error", menuItems);
        Assert.Contains("action.Key is \"edit-media\" or \"edit\"", detailPage);
        Assert.DoesNotContain("OverflowActions.Concat([InlineEditAction])", actions);
        Assert.Contains(".app-menu-popover.mud-popover", appStyles);
        Assert.Contains(".app-menu-item", appStyles);
        Assert.Contains("--tl-secondary-action-border", appStyles);
        var detailStyles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");
        Assert.Contains("--tl-primary-action-height", detailStyles);
        Assert.Contains("max-height: var(--tl-primary-action-height) !important", detailStyles);
    }

    [Fact]
    public void PersonHeroExternalLinksAndMoreTriggerShareOneHeight()
    {
        var detailStyles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");

        Assert.Contains("--tl-detail-person-action-height: 2.7rem", detailStyles);
        Assert.Contains(
            ".tl-detail-person-links .app-overflow-menu__trigger--labeled.tl-detail-person-link",
            detailStyles);
        Assert.Contains("height: var(--tl-detail-person-action-height) !important", detailStyles);
        Assert.Contains("min-height: var(--tl-detail-person-action-height) !important", detailStyles);
    }

    [Fact]
    public void PeopleStrip_RemainsOutOfHeroAndSharedAvatarFallbackStaysAvailable()
    {
        var detailPage = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor");
        var hero = ReadSource("src/MediaEngine.Web/Components/Details/DetailHero.razor");
        var card = ReadSource("src/MediaEngine.Web/Components/Details/PersonCreditCard.razor");
        var avatar = ReadSource("src/MediaEngine.Web/Components/Details/PersonAvatar.razor");
        var group = ReadSource("src/MediaEngine.Web/Components/Details/CreditGroupSection.razor");
        var appStyles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");

        Assert.Contains("<DetailHero Model=\"Model\"", detailPage);
        Assert.DoesNotContain("<PeoplePreviewStrip", detailPage);
        Assert.DoesNotContain("PreviewContributors", hero);
        Assert.Contains("PersonAvatar", card);
        Assert.Contains("@onerror=\"HandleImageError\"", avatar);
        Assert.Contains("tl-credit-group__toggle", group);
        Assert.Contains(".tl-credit-card__image .tl-person-avatar", appStyles);
        Assert.Contains("aspect-ratio: 1 / 1", appStyles);
        Assert.Contains("linear-gradient(to top", appStyles);
    }

    [Fact]
    public void EngineClient_NormalizesSeriesAndCreditImageUrls()
    {
        var source = ReadEngineApiClientSources();

        Assert.Contains("NormalizeSequencePlacement", source);
        Assert.Contains("NormalizeSequenceContainerOption", source);
        Assert.Contains("SourceContainerId = placement.SourceContainerId", source);
        Assert.Contains("EquivalentContainerIds = option.EquivalentContainerIds", source);
        Assert.Contains("NormalizeSequenceItem", source);
        Assert.Contains("NormalizeHeroArtwork", source);
        Assert.Contains("ImageUrl = NormalizeOptionalUrl(credit.ImageUrl)", source);
    }

    [Fact]
    public void SequencePlacementPanel_DoesNotRenderViewAllLink()
    {
        var source = ReadSource("src/MediaEngine.Web/Components/Details/SequencePlacementPanel.razor");
        var styles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");
        var panelStyles = ReadSource("src/MediaEngine.Web/Components/Details/SequencePlacementPanel.razor.css");

        Assert.Contains("tl-series-heading", source);
        Assert.Contains("<span>@HeadingLabel:</span>", source);
        Assert.Contains("<strong>@HeadingValue</strong>", source);
        Assert.Contains("aria-label=\"Choose series\"", source);
        Assert.Contains("aria-label=\"@GroupSelectorAriaLabel\"", source);
        Assert.DoesNotContain("Label=\"Series Set\"", source);
        Assert.Contains("[Parameter] public bool Compact { get; set; }", source);
        Assert.Contains("Placement.ContainerDescription", source);
        Assert.Contains("Placement.Groups.Count}:{Placement.ContainerDescription}", source);
        Assert.Contains("isElementContentOverflowing", source);
        Assert.Contains("_seriesDescriptionExpanded ? \"Less\" : \"More\"", source);
        Assert.Contains("display: flex", styles);
        Assert.Contains("flex: 0 0 2rem", styles);
        Assert.Contains("tl-series-placement--compact", source);
        Assert.DoesNotContain("IsNextSequenceItem", source);
        Assert.DoesNotContain("Part of", source);
        Assert.Contains("StringHelpers.FirstNonBlank(Placement.ContainerTitle, Placement.ContainerLabel, \"Series\")!", source);
        Assert.Contains("tl-series-detail__identity", source);
        Assert.Contains("@if (HasTopControls)", source);
        Assert.Contains("SelectWidthStyle(ContainerSelectOptions)", source);
        Assert.Contains("VisibleItems", source);
        Assert.Contains("tl-series-carousel__arrow", source);
        Assert.Contains("MudChart T=\"double\"", source);
        Assert.Contains("ChartType=\"ChartType.Donut\"", source);
        Assert.Contains("ChartSeries=\"@SequenceDonutSeries\"", source);
        Assert.Contains("ChartLabels=\"@SequenceDonutLabels\"", source);
        Assert.DoesNotContain("View all", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("View Full Series", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tl-series-view-all", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tl-series-position-summary", source);
        Assert.Contains("tl-series-position-summary__donut", source);
        Assert.Contains("tl-series-position-summary__center", source);
        Assert.Contains("ValueChanged=\"SelectContainerValueAsync\"", source);
        Assert.Contains("ShowJumpSelector => ActiveItems.Count > 10", source);
        Assert.Contains("ValueChanged=\"SelectJump\"", source);
        Assert.Contains("tl-series-content", source);
        Assert.Contains("ResetWindowToCurrentItem()", source);
        Assert.Contains("ShowJumpSelector && currentIndex >= 0", source);
        Assert.Contains("new AppSelectOption(option.ContainerId, option.ContainerTitle)", source);
        Assert.DoesNotContain("new AppSelectOption(option.ContainerId, option.ContainerTitle, Icons.Material.Outlined.Layers)", source);
        Assert.Contains("is-stacked", source);
        Assert.Contains("DistinctAvailableContainers", source);
        Assert.Contains("options.Any(IsTrustedContainerOption)", source);
        Assert.Contains("!IsTitleOnlyContainerOption(option)", source);
        Assert.Contains("CanChooseDistinctContainer", source);
        Assert.Contains("grid-template-columns: minmax(0, 1fr) max-content;", styles);
        Assert.Contains("text-overflow: ellipsis !important;", styles);
        Assert.Contains("padding-right: 2.15rem !important;", styles);
        Assert.Contains("tl-series-default-control", source);
        Assert.Contains("Icons.Material.Outlined.StarOutline", source);
        Assert.Contains("Use as your default series", source);
        Assert.Contains("!string.Equals(group.Key, \"all\"", source);
        Assert.DoesNotContain("ContainerOptionLabel", source);
        Assert.DoesNotContain("ContainerSourceLabel", source);
        Assert.DoesNotContain("else if (ActiveGroup is not null)", source);
        Assert.DoesNotContain("tl-series-static-control--group", source);
        Assert.DoesNotContain("NormalizeContainerOptionTitle", source);
        Assert.DoesNotContain("IsGenericContainerWord", source);
        Assert.Contains("item.IsOwned", source);
        Assert.Contains("Artwork unavailable", source);
        Assert.Contains("is-owned-placeholder", styles);
        Assert.Contains("=> OnContainerSelected.InvokeAsync(option)", source);
        Assert.DoesNotContain("Disabled=\"@option.IsSelected\"", source);
        Assert.DoesNotContain("Current position", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tl-series-item__current-badge", source);
        Assert.Contains("tl-series-item__node", source);
        Assert.Contains("SequenceItemRoute(item)", source);
        Assert.Contains("href=\"@route\"", source);
        Assert.Contains("Placement.TotalKnownItems", source);
        Assert.Contains("Placement.HasAuthoritativeTotal", source);
        Assert.Contains("IsComicSequence", source);
        Assert.Contains("CoveragePrimaryText", source);
        Assert.Contains("IsComicSequence && HasCurrentPosition", source);
        Assert.Contains("CoverageCenterClass", source);
        Assert.Contains("is-comic-position", styles);
        Assert.Contains("SequenceNumberOrNull", source);
        Assert.DoesNotContain("owned of {TotalItems}", source);
        Assert.Contains("item.EntityType == DetailEntityType.TvEpisode", source);
        Assert.Contains("tl-series-placement--long", source);
        Assert.Contains("LongSequenceThreshold = 6", source);
        Assert.Contains("PortraitWindowSize = 6", source);
        Assert.DoesNotContain("LandscapeWindowSize", source);
        Assert.Contains("IsSeasonContainer ? ActiveItems", source);
        Assert.Contains("nextStart + (VisibleCount / 2)", source);
        Assert.Contains("SequenceItemTitleClass", source);
        Assert.Contains("is-very-long", source);
        Assert.Contains("SeriesDateRange", source);
        Assert.Contains("SequenceItemDate", source);
        Assert.Contains("item.PublicationDate", source);
        Assert.DoesNotContain("tl-series-item__owned-badge", source);
        Assert.Contains("tl-series-item__completed-badge", source);
        Assert.Contains("Icons.Material.Filled.Check\" Size=\"Size.Small\"", source);
        Assert.Contains("ItemNoun", source);
        Assert.Contains("grid-template-columns: repeat(var(--series-count, 6), minmax(5.8rem, 7.3rem))", styles);
        Assert.Contains("grid-template-columns: repeat(var(--series-count, 7), minmax(7.2rem, 9.2rem))", styles);
        Assert.Contains("scrollbar-width: none", styles);
        Assert.Contains("AreCanonicallyAdjacent(previousItem, item)", source);
        Assert.Contains("CanonicalSequencePosition", source);
        Assert.DoesNotContain("HasExpandableEpisodeDescription", source);
        Assert.DoesNotContain("ToggleEpisodeDescription", source);
        Assert.DoesNotContain("_expandedEpisodeDescriptions", source);
        Assert.Contains("tl-series-detail__ownership", source);
        Assert.Contains("ShowCoverageBar", source);
        Assert.Contains("role=\"progressbar\"", source);
        Assert.Contains("Class=\"tl-series-detail__ownership-progress-fill\"", source);
        Assert.Contains(".tl-series-detail__ownership-progress ::deep .tl-series-detail__ownership-progress-fill", panelStyles);
        Assert.Contains("tl-series-item__connector", source);
        Assert.DoesNotContain(".tl-series-strip::before", styles);
        Assert.Contains("min-width: clamp(5.8rem, 7.2vw, 7.3rem)", styles);
        Assert.Contains("tl-series-layout", styles);
        Assert.Contains("tl-series-content", styles);
        Assert.Contains("width: min(36rem, 100%)", styles);
        Assert.Contains("grid-template-columns: minmax(15rem, 0.24fr) minmax(0, 0.76fr)", styles);
        Assert.Contains("tl-series-description", styles);
        Assert.Contains("isElementContentOverflowing", ReadSource("src/MediaEngine.Web/wwwroot/app.js"));
        Assert.Contains("@ref=\"_seriesDescriptionElement\"", source);
        Assert.Contains("OnAfterRenderAsync", source);
        Assert.DoesNotContain("ContainerDescription?.Length", source);
        Assert.Contains("tl-series-date-range", styles);
        Assert.Contains("tl-series-item__date", styles);
        Assert.Contains("repeat(var(--series-count, 6), clamp(10.25rem, 11vw, 13rem))", styles);
        Assert.Contains("tl-series-item__connector", styles);
        Assert.Contains("justify-content: start", styles);
        Assert.Contains("tl-series-source-link", source);
        Assert.Contains("https://www.wikidata.org/wiki/{qid}", source);
        Assert.Contains("@SourceKindLabel: Wikidata (@SeriesWikidataQid)", source);
        Assert.Contains("@SourceKindLabel: Comic Vine", source);
        Assert.Contains("https://comicvine.gamespot.com/volume/4050-{parsedVolumeId}/", source);
        Assert.Contains("@SourceKindLabel: Wikipedia", source);
        Assert.Contains("Placement.ContainerWikipediaUrl", source);
        Assert.DoesNotContain("tl-series-owned-chip\"", source);
        Assert.Contains("tl-series-item.is-current .tl-series-item__art", styles);
        Assert.Contains("tl-series-item.is-current .tl-series-item__frame", styles);
        Assert.Contains("tl-series-item__frame", source);
        Assert.DoesNotContain("tl-series-item__state-row", source);
        Assert.DoesNotContain("tl-series-item__current-badge", source);
        Assert.DoesNotContain("tl-series-item__next-badge", source);
        Assert.DoesNotContain("Up next", source);
        Assert.Contains("aria-current", source);
        Assert.Contains("SequenceItemAccessibilityLabel", source);
        Assert.Contains("isolation: isolate", styles);
        Assert.Contains("z-index: auto", styles);
        Assert.Contains("0 0 0 3px", styles);
        Assert.DoesNotContain("tl-series-item__owned-badge", styles);
        Assert.Contains("tl-series-owned-summary", styles);
        Assert.Contains("tl-source-links", styles);
        Assert.Contains("minmax(3rem, auto)", styles);
        Assert.Contains("-webkit-line-clamp: 4", styles);
        Assert.Contains("a.tl-series-item", styles);
        Assert.Contains("padding-bottom: 1.4rem", styles);
        Assert.DoesNotContain("tl-series-episode-play", source);
        Assert.DoesNotContain("tl-series-episode-edit", source);
        Assert.Contains("@SequenceItemContent(item, previousItem)", source);
        Assert.DoesNotContain("Icons.Material.Outlined.MoreVert", source);
        Assert.Contains("EpisodeDetailRoute", source);
        Assert.Contains("tl-series-item__description", source);
        Assert.Contains("EpisodeWatchLabel", source);
        Assert.Contains("is-season-episode", source);
        Assert.Contains("-webkit-line-clamp: 4", styles);
        Assert.DoesNotContain("tl-series-position-summary__label", source);
        Assert.Contains("tl-series-show-link", source);
        Assert.DoesNotContain("ShortContainerDescription", source);
        Assert.Contains("!IsSeasonContainer && SequenceItemDate(item)", source);
        Assert.Contains("grid-template-columns: repeat(var(--series-count, 12), clamp(24rem, 28vw, 32rem))", styles);
        Assert.Contains("grid-template-columns: minmax(31rem, 34rem) minmax(0, 1fr)", styles);
        Assert.Contains("tl-series-item__art:hover .tl-series-episode-play", styles);
        Assert.DoesNotContain("tl-series-item:hover .tl-series-episode-play", styles);
        Assert.Contains(".tl-detail-tab-panel,", styles);
        Assert.Contains("background: transparent", styles);
    }

    [Fact]
    public void TvShowEpisodes_UseSeasonCoveragePlaybackAndShowScopedDetailNavigation()
    {
        var sequence = ReadSource("src/MediaEngine.Web/Components/Details/SequencePlacementPanel.razor");
        var composer = ReadDetailComposerSource();
        var navigation = ReadSource("src/MediaEngine.Web/Services/Navigation/MediaNavigation.cs");
        var showPage = ReadSource("src/MediaEngine.Web/Components/Pages/UnifiedDetailPage.razor");
        var routeRequest = ReadSource("src/MediaEngine.Web/Components/Details/DetailRouteRequest.cs");

        Assert.Contains("ShowCoverageDonut", sequence);
        Assert.Contains("OwnedSummary", sequence);
        Assert.Contains("IsSeasonContainer", sequence);
        Assert.Contains("BuildCollectionSequencePlacement", composer);
        Assert.Contains("GroupLabel = labels.GroupLabel", composer);
        Assert.Contains("/details/work/{work.Id}?context=watch", composer);
        Assert.DoesNotContain("/details/tvepisode/", composer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/episode/{workId}", navigation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/details/TvEpisode/", navigation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ShowDetailRoute", sequence);
        Assert.Contains("EpisodeDetailRoute", sequence);
        Assert.Contains("EpisodeDetailRoute(item) is { } episodeRoute", sequence);
        Assert.Contains("return episodeRoute;", sequence);
        Assert.Contains("&episode={episodeId:D}", sequence);
        Assert.Contains("SupplyParameterFromQuery(Name = \"episode\")", showPage);
        Assert.Contains("DetailRouteRequest.ForUnified(EntityType, Id, Context, EpisodeId)", showPage);
        Assert.Contains("parsedEntityType == DetailEntityType.TvShow", routeRequest);
        Assert.Contains("DetailEntityType.TvEpisode", routeRequest);
        Assert.Contains("containerId: id.ToString(\"D\")", routeRequest);
        Assert.DoesNotContain("HeroSecondaryActions", showPage);
        Assert.DoesNotContain("show-details", showPage);
        Assert.DoesNotContain("tl-tv-episode-detail-back", showPage);
        Assert.Contains("@ContainerTitleDisplay", sequence);
        Assert.DoesNotContain("tl-series-episode-details-overlay", sequence);
        Assert.Contains("SequenceItemDisplayTitle", sequence);
        Assert.Contains("EpisodeNumberLabel", sequence);
        Assert.Contains("SequenceItemAirDate", sequence);
        Assert.Contains("Icons.Material.Outlined.CalendarMonth", sequence);
        Assert.Contains("Icons.Material.Outlined.Schedule", sequence);
        Assert.Contains("Label=\"Show missing\"", sequence);
        Assert.Contains("CanShowMissingToggle", sequence);
        Assert.DoesNotContain("ActiveGroup?.HasAuthoritativeTotal == true", sequence);
        Assert.Contains("MissingItemDisplay", sequence);
        Assert.Contains("DefaultVisibility", sequence);
        Assert.Contains("GetSeriesMissingItemPreferenceAsync", sequence);
        Assert.Contains("SaveSeriesMissingItemPreferenceAsync", sequence);
        Assert.Contains("ResetSeriesMissingItemPreferenceAsync", sequence);
        Assert.Contains("tl-series-missing-preference", sequence);
        Assert.Contains("Use media default", sequence);
        Assert.Contains("HasMoreConfiguredMissingItems", sequence);
        Assert.Contains("Load more missing", sequence);
        Assert.Contains("if (IsSeasonContainer)", sequence);
        Assert.Contains("grid-template-rows: auto", ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css"));
        Assert.Contains("Watch S{season} E{episode}", sequence);
    }

    [Fact]
    public void RoutePages_DoNotChooseHeroArtwork()
    {
        var unified = ReadSource("src/MediaEngine.Web/Components/Pages/UnifiedDetailPage.razor");

        Assert.DoesNotContain("HeroArtwork", unified);
        Assert.DoesNotContain("BackdropUrl", unified);
        Assert.DoesNotContain("CoverUrl", unified);
    }

    [Fact]
    public void UnifiedRoutePage_KeepsDetailTabsInPageStateWithoutLegacyAliases()
    {
        var unified = ReadSource("src/MediaEngine.Web/Components/Pages/UnifiedDetailPage.razor");
        var routeHost = ReadSource("src/MediaEngine.Web/Components/Details/DetailRouteHost.razor");
        var routeRequest = ReadSource("src/MediaEngine.Web/Components/Details/DetailRouteRequest.cs");

        Assert.Contains("@page \"/details/{EntityType}/{Id:guid}\"", unified);
        Assert.Contains("entityType != DetailEntityType.TvEpisode", routeRequest);
        Assert.False(File.Exists(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Pages/WatchTvEpisodePage.razor")));
        Assert.False(File.Exists(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Pages/WatchMoviePage.razor")));
        Assert.False(File.Exists(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Pages/WatchTvShowPage.razor")));
        Assert.False(File.Exists(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Pages/BookDetail.razor")));
        Assert.Contains("<DetailRouteHost Request=", unified);
        Assert.DoesNotContain("{Tab}", unified);
        Assert.DoesNotContain("BuildTabUrl", unified);
        Assert.DoesNotContain("DetailTabNavigation.BuildUrl", unified);
        Assert.DoesNotContain("Nav.NavigateTo(BuildTabUrl", unified);
        Assert.DoesNotContain("_activeTab", unified);

        Assert.Contains("_activeTab = tab;", routeHost);
        Assert.Contains("<AppPageState", routeHost);
        Assert.Contains("DetailTabNavigation.Resolve(_detail, _activeTab)", routeHost);
        Assert.Contains("Nav.NavigateTo(action.Route)", routeHost);
        Assert.DoesNotContain("tl-detail-origin-back", routeHost);
        Assert.DoesNotContain("detailOrigin.initialize", routeHost);
        Assert.DoesNotContain("detailOrigin.back", routeHost);
        Assert.DoesNotContain("{Tab}", routeHost);
        Assert.DoesNotContain("BuildTabUrl", routeHost);
        Assert.DoesNotContain("DetailTabNavigation.BuildUrl", routeHost);
        Assert.DoesNotContain("Nav.NavigateTo(BuildTabUrl", routeHost);
    }

    [Fact]
    public void OutwardDetailLinks_UseTheUnifiedRouteAcrossMediaTypes()
    {
        var outwardNavigationSources = string.Join(
            Environment.NewLine,
            ReadSource("src/MediaEngine.Web/Services/Navigation/MediaNavigation.cs"),
            ReadSource("src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor"),
            ReadSource("src/MediaEngine.Web/Components/Pages/ListenPage.razor.cs"),
            ReadSource("src/MediaEngine.Web/Components/Details/SequencePlacementPanel.razor"),
            ReadSource("src/MediaEngine.Api/Services/Display/DisplayCardBuilder.cs"),
            ReadSource("src/MediaEngine.Api/Services/ReadServices/UniversalSearchReadService.cs"),
            ReadSource("src/MediaEngine.Api/Services/Details/DetailRecommendationService.cs"),
            ReadSource("src/MediaEngine.Api/Services/Details/Internals/DetailCompositionOrchestrator.CollectionBuilder.cs"));

        foreach (var legacyRoute in new[]
                 {
                     "/book/",
                     "/watch/movie/",
                     "/watch/tv/show/",
                     "/details/book/",
                     "/details/movie/",
                     "/details/audiobook/",
                     "/details/comicissue/",
                 })
        {
            Assert.DoesNotContain(legacyRoute, outwardNavigationSources, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("/details/work/", outwardNavigationSources, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/details/collection/", outwardNavigationSources, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/details/tvshow/", outwardNavigationSources, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/details/musicalbum/", outwardNavigationSources, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Audiobook\" => $\"/details/collection/", outwardNavigationSources, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailHero_UsesShortProviderCopyAndEpisodeDescriptions()
    {
        var source = ReadDetailComposerSource();
        var hero = ReadSource("src/MediaEngine.Web/Components/Details/DetailHero.razor");
        var heroContent = ReadSource("src/MediaEngine.Web/Components/Details/DetailHeroContent.razor");
        var presentation = ReadSource("src/MediaEngine.Web/Components/Details/DetailHeroPresentation.cs");

        Assert.Contains("BuildHeroSummary(values)", source);
        Assert.Contains("MetadataFieldConstants.ShortDescription", source);
        Assert.Contains("GetValue(canonicalValues, \"tldr\")", source);
        Assert.DoesNotContain("BuildFallbackHeroSummary", source);
        Assert.Contains("data-ai-summary-slot=\"@(AllowAiSummary ? \"tldr\" : null)\"", heroContent);
        Assert.Contains("tl-detail-hero__tagline--ai", heroContent);
        Assert.Contains("model.SecondaryTitleTextKind", presentation);
        Assert.Contains("model.SecondaryTitleText", presentation);
        Assert.Contains("model.Description", presentation);
        Assert.Contains("SecondaryTitleText=\"@Presentation.SecondaryTitleText\"", hero);
        Assert.Contains("SecondaryTitleTrailingContent", heroContent);
        Assert.Contains("ResolveSecondaryTitleText", source);
        Assert.Contains("MetadataFieldConstants.Subtitle", source);
        Assert.Contains("Tagline = semanticTagline", source);
        Assert.DoesNotContain("Tagline = displayTagline ?? heroSummary", source);
        Assert.Contains("tl-detail-hero__synopsis", heroContent);
        Assert.Contains("WatchEpisodeHeading", hero);
        Assert.Contains("EpisodePositionFromWatchAction", hero);
        Assert.Contains("$\"{position}: {episodeTitle.Trim()}\"", hero);
        Assert.Contains("FirstParagraph(copySource)", presentation);
        Assert.Contains("usesFullParagraphCopy = usesReadOverviewCopy || isWatchHero", presentation);
        Assert.Contains("? firstParagraph", presentation);
        Assert.DoesNotContain("(firstParagraph?.Length ?? 0) > copyLimit", presentation);
        Assert.Contains("HeroCopyHasMore", presentation);
        Assert.Contains("(more in overview)", hero);
        Assert.DoesNotContain("HeroGenreChips", hero);
        Assert.Contains("BuildSeriesContextLabel", hero);
        Assert.Contains("{containerTitle} · {positionedItem}", hero);
        Assert.Contains("if (entityType is DetailEntityType.Collection", hero);
        Assert.Contains("or DetailEntityType.BookSeries", hero);
        Assert.Contains("or DetailEntityType.ComicSeries", hero);
        Assert.Contains("or DetailEntityType.MovieSeries", hero);
        Assert.Contains("or DetailEntityType.TvShow", hero);
        Assert.DoesNotContain("{positionedItem} of {total}", hero);
        Assert.DoesNotContain("MembershipScope", hero);
        Assert.DoesNotContain("placement.TotalKnownItems", hero);
        Assert.Contains("SeriesContextClass", hero);
        Assert.Contains("tl-detail-hero__series-context.is-empty", ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css"));
        Assert.Contains("tl-series-information", ReadSource("src/MediaEngine.Web/Components/Details/SequencePlacementPanel.razor"));
        Assert.Contains("Series Description", ReadSource("src/MediaEngine.Web/Components/Details/SequencePlacementPanel.razor"));
        Assert.Contains("tl-series-information__toggle", ReadSource("src/MediaEngine.Web/Components/Details/SequencePlacementPanel.razor"));
        Assert.Contains("ToggleSeriesDescription", ReadSource("src/MediaEngine.Web/Components/Details/SequencePlacementPanel.razor"));
        Assert.Contains("_seriesDescriptionExpanded", ReadSource("src/MediaEngine.Web/Components/Details/SequencePlacementPanel.razor"));
    }

    [Fact]
    public void MusicAlbumDetailsUseTheSharedHeroAndBorderlessOverviewTracks()
    {
        var detailPage = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor");
        var detailPageStyles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");
        var primaryModule = ReadSource("src/MediaEngine.Web/Components/Details/DetailPrimaryModule.razor");
        var albumOverview = ReadSource("src/MediaEngine.Web/Components/Details/MusicAlbumOverviewContent.razor");
        var albumOverviewStyles = ReadSource("src/MediaEngine.Web/Components/Details/MusicAlbumOverviewContent.razor.css");
        var seriesRail = ReadSource("src/MediaEngine.Web/Components/Details/MusicAlbumSeriesRail.razor");
        var backdrop = ReadSource("src/MediaEngine.Web/Components/Details/HeroBackdrop.razor");
        var trackList = ReadSource("src/MediaEngine.Web/Components/Details/MusicTrackList.razor");
        var audioTable = ReadSource("src/MediaEngine.Web/Components/Details/AudioItemTable.razor");
        var audioTableStyles = ReadSource("src/MediaEngine.Web/Components/Details/AudioItemTable.razor.css");
        var apiClient = ReadEngineApiClientSources();
        var albumRoute = ReadSource("src/MediaEngine.Web/Components/Pages/UnifiedDetailPage.razor");
        var capabilities = ReadSource("src/MediaEngine.Web/Components/Details/DetailPresentationCapabilities.cs");

        Assert.Contains("<MusicAlbumOverviewContent Model=\"Model\"", detailPage);
        Assert.Contains("CurrentActiveTab is \"tracks\" && IsAudiobook", detailPage);
        Assert.Contains("tl-detail-stage--music-album", capabilities);
        Assert.Contains("ShowDetailNavigation => Model?.EntityType != DetailEntityType.Person", detailPage);
        Assert.DoesNotContain("<MusicTrackList", primaryModule);
        Assert.Contains("<MusicTrackList Groups=\"TrackGroups\"", albumOverview);
        Assert.Contains("Compact=\"true\"", albumOverview);
        Assert.Contains("<ContributorsSection Groups=\"Model.ContributorGroups\"", albumOverview);
        Assert.Contains("<MusicAlbumSeriesRail Model=\"Model\"", albumOverview);
        Assert.Contains("grid-template-columns: minmax(0, 1fr) minmax(17rem, 27%)", albumOverviewStyles);
        Assert.Contains("background: transparent", albumOverviewStyles);
        Assert.DoesNotContain("grid-template-columns: repeat(2, minmax(0, 1fr))", albumOverviewStyles);
        Assert.Contains("MoreByAlbums.Count: > 0", seriesRail);
        Assert.Contains("loading=\"lazy\"", seriesRail);
        Assert.Contains("width: min(34vw, 58svh) !important", detailPageStyles);
        Assert.Contains("top: 50% !important", detailPageStyles);
        Assert.Contains("tl-detail-media-stage__foreground--album", detailPageStyles);
        Assert.Contains("width: 100% !important", detailPageStyles);
        Assert.Contains("tl-detail-stage--audiobook", detailPageStyles);
        Assert.Contains("aspect-ratio: 2 / 3", detailPageStyles);
        Assert.Contains("tl-detail-media-stage--book.tl-detail-media-stage--cover-fallback", detailPageStyles);
        Assert.Contains("tl-detail-media-stage--landscape.tl-detail-media-stage--cover-fallback", detailPageStyles);
        Assert.Contains("height: min(69svh", detailPageStyles);
        Assert.Contains("max-height: 100% !important", detailPageStyles);
        Assert.Contains("IsMusicAlbumBackdrop", backdrop);
        Assert.Contains("Artwork.CoverUrl ?? Artwork.PosterUrl", backdrop);
        Assert.Contains("tl-detail-media-stage--music-cover-background", backdrop);
        Assert.Contains("MusicAlbumCompanion = NormalizeMusicAlbumCompanion(detail.MusicAlbumCompanion)", apiClient);
        Assert.Contains("ArtworkUrl = NormalizeOptionalUrl(album.ArtworkUrl)", apiClient);
        Assert.Contains("<AudioItemTable", trackList);
        Assert.Contains("ReplaceQueueItemsAsync", audioTable);
        Assert.Contains("tl-embedded-audio__table", audioTable);
        Assert.Contains("grid-template-columns: 4.2rem minmax(16rem, 1fr) 5rem 7.5rem 2.5rem", audioTableStyles);
        Assert.Contains("overflow: auto", audioTableStyles);
        Assert.Contains("Show missing tracks", audioTable);
        Assert.DoesNotContain("_showMissingItems = true", audioTable, StringComparison.Ordinal);
        Assert.Contains("item.IsOwned", audioTable);
        Assert.Contains("DurationSeconds", audioTable);
        Assert.Contains("seconds = minutes * 60d + remainingSeconds", audioTable);
        Assert.Contains("ToggleSort", audioTable);
        Assert.Contains("BeginColumnResize", audioTable);
        Assert.Contains("draggable=\"@CanQueue(item)\"", audioTable);
        Assert.Contains("Icons.Material.Filled.Favorite", audioTable);
        Assert.DoesNotContain("Search tracks", audioTable);
        Assert.DoesNotContain("Placeholder=\"@($\"Search {ItemNounPlural}\")\"", audioTable);
        Assert.DoesNotContain("@page \"/listen/album", albumRoute, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MusicAlbumWorkspace", detailPage + detailPageStyles + primaryModule + albumOverview + seriesRail + apiClient);
        Assert.False(File.Exists(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Details/MusicAlbumTrackSurface.razor")));
        Assert.False(File.Exists(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Details/MusicAlbumTrackSurface.razor.css")));
        Assert.False(File.Exists(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Details/MusicAlbumHeroWorkspace.razor")));
        Assert.False(File.Exists(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Details/MusicAlbumWorkspaceNavigation.razor")));
        Assert.False(File.Exists(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Details/MusicAlbumOverview.razor")));
    }

    [Fact]
    public void PersonDetailsUsePortraitCardsForLinkedIdentitiesAndReadableWorksSpacing()
    {
        var overview = ReadSource("src/MediaEngine.Web/Components/Details/OverviewTab.razor");
        var primaryModuleStyles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPrimaryModule.razor.css");
        var apiClient = ReadEngineApiClientSources();

        Assert.Contains("<h3>Linked identities</h3>", overview);
        Assert.Contains("<PersonCreditCard Credit=\"@ToRelatedIdentityCredit(person)\"", overview);
        Assert.Contains("ImageUrl = person.ImageUrl", overview);
        Assert.Contains("PrimaryRole = string.Empty", overview);
        Assert.DoesNotContain("PrimaryRole = person.Subtitle", overview);
        Assert.Contains("NormalizePersonDetails(detail.PersonDetails)", apiClient);
        Assert.Contains("ImageUrl = NormalizeOptionalUrl(link.ImageUrl)", apiClient);
        Assert.Contains(".tl-detail-primary-module--works .tl-detail-primary-module__header > div.tl-detail-primary-module__title", primaryModuleStyles);
        Assert.Contains("gap: 1.05rem", primaryModuleStyles);
    }

    [Fact]
    public void DetailPrimaryModulesKeepLaneFiltersOnMixedMediaContainersOnly()
    {
        var sequence = ReadSource("src/MediaEngine.Web/Components/Details/SequencePlacementPanel.razor");
        var primaryModule = ReadSource("src/MediaEngine.Web/Components/Details/DetailPrimaryModule.razor");
        var primaryModuleStyles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPrimaryModule.razor.css");

        Assert.DoesNotContain("Available media lanes", sequence);
        Assert.DoesNotContain("DetailLaneOptions", sequence);
        Assert.Contains("Model.PrimaryModule.SupportsLaneFilter", primaryModule);
        Assert.Contains("&& HasMultipleOwnedLanes", primaryModule);
        Assert.Contains("LaneOptions.Count(option => option.Key != \"all\") > 1", primaryModule);
        Assert.Contains("RoleOptions.Count(option => option.Key != \"all\") > 1", primaryModule);
        Assert.Contains("<AppSegmentedControl", primaryModule);
        Assert.DoesNotContain("<AppSelect Value=\"@_selectedRole\"", primaryModule);
        Assert.Contains("ownedLanes.Count == 1 ? ownedLanes[0].Key : \"all\"", primaryModule);
        Assert.Contains("DetailEntityType.Audiobook => \"square\"", primaryModule);
        Assert.Contains("tl-detail-primary-module__filter-separator", primaryModule);
        Assert.Contains("\"read\" => Icons.Material.Outlined.MenuBook", primaryModule);
        Assert.Contains("\"watch\" => Icons.Material.Outlined.Tv", primaryModule);
        Assert.Contains("\"listen\" => Icons.Material.Outlined.Headphones", primaryModule);
        Assert.Contains(".tl-detail-primary-module--works .tl-detail-primary-module__header > div.tl-detail-primary-module__title", primaryModuleStyles);
        Assert.Contains("min-width: max-content;", primaryModuleStyles);
        Assert.Contains(".tl-detail-primary-module--works .tl-detail-primary-module__role-filters,", primaryModuleStyles);
        Assert.Contains(".tl-detail-primary-module--works .tl-detail-primary-module__filters", primaryModuleStyles);
    }

    [Fact]
    public void AudioDetailPagesUseTheBoundedSharedStageWithoutChangingHomeHeroDefault()
    {
        var detailPage = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor");
        var primaryModule = ReadSource("src/MediaEngine.Web/Components/Details/DetailPrimaryModule.razor");
        var primaryModuleStyles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPrimaryModule.razor.css");
        var popupPlayer = ReadSource("src/MediaEngine.Web/Components/Pages/ListenPlayerPopupPage.razor");
        var audioTable = ReadSource("src/MediaEngine.Web/Components/Details/AudioItemTable.razor");
        var listenPage = ReadSource("src/MediaEngine.Web/Components/Pages/ListenPage.razor.cs");
        var playbackService = ReadSource("src/MediaEngine.Web/Services/Playback/PlaybackSessionController.cs");
        var playbackModels = ReadSource("src/MediaEngine.Web/Services/Playback/PlaybackModels.cs");
        var detailStyles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");
        var detailHeroStyles = ReadSource("src/MediaEngine.Web/Components/Details/DetailHero.razor.css");
        var heroMetadata = ReadSource("src/MediaEngine.Web/Components/Details/HeroMetadataPills.razor");
        var playerBar = ReadSource("src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor");
        var playerStyles = ReadSource("src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor.css");
        var transportControls = ReadSource("src/MediaEngine.Web/Components/Listen/ListenTransportControls.razor");
        var transportControlStyles = ReadSource("src/MediaEngine.Web/Components/Listen/ListenTransportControls.razor.css");
        var popupPlayerStyles = ReadSource("src/MediaEngine.Web/Components/Pages/ListenPlayerPopupPage.razor.css");
        var playbackSkipButton = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackRelativeSkipButton.razor");
        var playbackSkipButtonStyles = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackRelativeSkipButton.razor.css");
        var playbackPrimaryButton = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackPrimaryButton.razor");
        var playbackPrimaryButtonStyles = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackPrimaryButton.razor.css");
        var playbackTimelineMetaRow = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackTimelineMetaRow.razor");
        var playbackTimelineMetaRowStyles = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackTimelineMetaRow.razor.css");
        var playbackSpeedControl = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackSpeedControl.razor");
        var playbackSpeedControlStyles = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackSpeedControl.razor.css");
        var playbackSleepTimerControl = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackSleepTimerControl.razor");
        var playbackSleepTimerControlStyles = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackSleepTimerControl.razor.css");
        var playbackRangeSlider = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackRangeSlider.razor");
        var playbackRangeSliderStyles = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackRangeSlider.razor.css");
        var playerScript = ReadSource("src/MediaEngine.Web/wwwroot/app.js");
        Assert.Contains("DetailPresentationCapabilities.For", detailPage);
        Assert.Contains("private bool IsMusicAlbum", detailPage);
        Assert.Contains("private bool IsAudiobook", detailPage);
        Assert.Contains("<DetailPrimaryModule Model=\"Model\"", detailPage);
        Assert.Contains("HasPrimaryModuleBeforeNavigation", detailPage);
        Assert.Contains("HasPrimaryModuleBeforeNavigation => false", detailPage);
        Assert.Contains("HasPrimaryModuleAfterNavigation", detailPage);
        Assert.Contains("ShowPrimaryModuleAfterNavigation", detailPage);
        Assert.Contains("CurrentActiveTab == \"overview\"", detailPage);
        Assert.DoesNotContain("IsEmbeddedPrimaryModuleTab", detailPage);
        Assert.Contains("CurrentActiveTab is \"tracks\" && IsAudiobook", detailPage);
        Assert.Contains("tl-detail-page__primary-content", detailPage);
        Assert.Contains("Embedded=\"true\"", detailPage);
        Assert.Contains("Embedded=\"false\"", detailPage);
        Assert.DoesNotContain("RequestedLane=", detailPage);
        Assert.DoesNotContain("action.Key is \"collection-read\" or \"collection-watch\" or \"collection-listen\"", detailPage);
        Assert.Contains("\"item_count\" or \"read_count\" or \"watch_count\" or \"listen_count\"", heroMetadata);
        Assert.Contains("tl-detail-watch-metadata-item--lane-count", heroMetadata);
        Assert.Contains(".tl-detail-watch-metadata-item--lane-count", detailStyles);
        Assert.Contains("width: clamp(44rem, 56vw, 64rem)", detailHeroStyles);
        Assert.Contains("height: clamp(30rem, 60svh, 42rem)", detailHeroStyles);
        Assert.Contains("Model?.EntityType != DetailEntityType.Person", detailPage);
        Assert.DoesNotContain("<EditionsTab", detailPage);
        Assert.Contains("<DetailHero Model=\"Model\"", detailPage);
        Assert.DoesNotContain("<MusicTrackList", primaryModule);
        Assert.Contains("<MusicAlbumOverviewContent Model=\"Model\"", detailPage);
        Assert.DoesNotContain("AudiobookOverviewContent", detailPage);
        Assert.Contains("<MediaEngine.Web.Components.Details.OverviewTab Model=\"Model\"", detailPage);
        Assert.DoesNotContain("FormatsTab", detailPage);
        Assert.DoesNotContain("CurrentActiveTab is \"formats\"", detailPage);
        Assert.DoesNotContain("<AudiobookChapterList", primaryModule);
        Assert.DoesNotContain("IsAudiobook", ReadSource("src/MediaEngine.Web/Components/Details/MusicAlbumOverviewContent.razor"));
        Assert.Contains("IsAudiobook=\"true\"", ReadSource("src/MediaEngine.Web/Components/Details/AudiobookTracksContent.razor"));
        Assert.Contains("tl-embedded-audio__toolbar", audioTable);
        Assert.Contains("tl-embedded-audio__scroller", audioTable);
        Assert.DoesNotContain("Placeholder=\"@($\"Search {ItemNounPlural}\")\"", audioTable);
        Assert.DoesNotContain("Save to My List", audioTable);
        Assert.DoesNotContain("Play All", audioTable);
        Assert.Contains("height: 95svh", detailStyles);
        Assert.Contains("overflow-y: auto", ReadSource("src/MediaEngine.Web/Components/Details/DetailRouteHost.razor.css"));
        Assert.Contains("touch-action: pan-y", ReadSource("src/MediaEngine.Web/Components/Details/DetailRouteHost.razor.css"));
        Assert.Contains("object-position: center top !important", detailStyles);
        Assert.DoesNotContain("height: clamp(34rem, 70svh, 47.5rem)", detailStyles);
        Assert.Contains("height: clamp(14rem, 28svh, 21rem)", detailStyles);
        Assert.Contains("tl-detail-media-stage__cover-wrap--over-backdrop", ReadSource("src/MediaEngine.Web/Components/Details/HeroBackdrop.razor"));
        Assert.Contains("private bool IsStructuralContainer", ReadSource("src/MediaEngine.Web/Components/Details/HeroBackdrop.razor"));
        Assert.Contains("Structural rails and navigation are", detailStyles);
        Assert.Contains(".tl-detail-stage__navigation", detailStyles);
        Assert.Contains("padding-inline: clamp(4rem, 4.25vw, 5.5rem)", detailStyles);
        Assert.Contains(".tl-detail-primary-module--tracks .tl-detail-primary-module__viewport", detailStyles);
        Assert.Contains(".tl-detail-primary-module--tracks .tl-detail-track-table-shell", detailStyles);
        Assert.Contains("height: 100%", detailStyles);
        Assert.Contains("overflow: auto", detailStyles);
        Assert.Contains(".tl-detail-primary-module--tracks .tl-detail-track-table thead", detailStyles);
        Assert.Contains("position: sticky", detailStyles);
        Assert.Contains("inset: 0 -2rem", primaryModuleStyles);
        Assert.Contains(".tl-detail-actions--watch .tl-detail-secondary-actions", detailStyles);
        Assert.Contains("flex-wrap: nowrap", detailStyles);
        Assert.Contains(".tl-detail-hero--person .tl-detail-person-hero__inner", detailStyles);
        Assert.Contains("height: clamp(26rem, 58svh, 40rem)", detailStyles);
        Assert.Contains("DetailEntityType.Book or DetailEntityType.Work or DetailEntityType.ComicIssue or DetailEntityType.ComicSeries => \"tl-detail-media-stage--book\"", ReadSource("src/MediaEngine.Web/Components/Details/HeroBackdrop.razor"));
        Assert.Contains(".tl-detail-media-stage--book .tl-detail-media-stage__foreground--poster", detailStyles);
        Assert.Contains(".tl-detail-hero--person .tl-detail-person-summary", detailStyles);
        Assert.Contains("<AudioItemTable", ReadSource("src/MediaEngine.Web/Components/Details/MusicTrackList.razor"));
        Assert.Contains("_columns.AddRange(IsAudiobook ? AudiobookColumns() : MusicColumns())", audioTable);
        Assert.Contains("private static IReadOnlyList<AudioTableColumn> AudiobookColumns()", audioTable);
        Assert.Contains("<PlaybackPrimaryButton", transportControls);
        Assert.DoesNotContain("PlayButtonStyle", transportControls);
        Assert.DoesNotContain("PlayIconClass", transportControls);
        Assert.DoesNotContain("Icons.Material.Filled.PlayArrow", transportControls);
        Assert.Contains("[Parameter(CaptureUnmatchedValues = true)]", playbackPrimaryButton);
        Assert.Contains("@attributes=\"AdditionalAttributes\"", playbackPrimaryButton);
        Assert.Contains("playback-primary-button-shell--compact", playbackPrimaryButtonStyles);
        Assert.Contains("playback-primary-button-shell--large", playbackPrimaryButtonStyles);
        Assert.Contains("--playback-primary-icon-size: 34px;", playbackPrimaryButtonStyles);
        Assert.Contains("--playback-primary-size: 58px;", playbackPrimaryButtonStyles);
        Assert.Contains("--playback-primary-size: 64px;", playbackPrimaryButtonStyles);
        Assert.DoesNotContain("--playback-primary-size: 74px;", playbackPrimaryButtonStyles);
        Assert.DoesNotContain("--playback-primary-size: 96px;", playbackPrimaryButtonStyles);
        Assert.Contains("\"popup\" => \"large\"", transportControls);
        Assert.Contains("\"panel\" => \"standard\"", transportControls);
        Assert.Contains("_ => \"compact\"", transportControls);
        Assert.Contains("listen-transport__secondary-icon", transportControls);
        Assert.Contains("--listen-transport-secondary-size: 52px;", transportControlStyles);
        Assert.Contains("--listen-transport-secondary-icon-size: 30px;", transportControlStyles);
        Assert.Contains("--listen-transport-secondary-size: 50px;", transportControlStyles);
        Assert.Contains("--listen-transport-primary-size: 58px;", transportControlStyles);
        Assert.Contains("--listen-transport-secondary-size: 54px;", transportControlStyles);
        Assert.Contains("--listen-transport-primary-size: 64px;", transportControlStyles);
        Assert.Contains("grid-template-columns: var(--listen-transport-secondary-size) var(--listen-transport-secondary-size) var(--listen-transport-primary-size) var(--listen-transport-secondary-size) var(--listen-transport-secondary-size) !important;", transportControlStyles);
        Assert.Contains("width: var(--listen-transport-secondary-size) !important;", transportControlStyles);
        Assert.Contains("--playback-relative-skip-size: var(--listen-transport-secondary-size);", transportControlStyles);
        Assert.Contains("grid-template-columns: 52px 52px 64px 52px 52px;", playerStyles);
        Assert.Contains("grid-template-columns: 54px 54px 64px 54px 54px;", popupPlayerStyles);
        Assert.DoesNotContain("grid-template-columns: 38px 54px 52px 54px 38px", playerStyles);
        Assert.DoesNotContain("grid-template-columns: 72px 64px 104px 64px 72px", popupPlayerStyles);
        Assert.Contains("<PlaybackTimelineMetaRow", popupPlayer);
        Assert.Contains("playback-timeline-meta-row", playbackTimelineMetaRow);
        Assert.Contains("color: var(--playback-text);", playbackTimelineMetaRowStyles);
        Assert.DoesNotContain(".listen-popup__chapter-row", popupPlayerStyles);
        Assert.DoesNotContain("background: var(--listen-accent", transportControlStyles);
        Assert.DoesNotContain("background: var(--listen-audio-accent", transportControlStyles);
        Assert.DoesNotContain(".listen-player__play {", playerStyles);
        Assert.DoesNotContain(".listen-popup__play {", popupPlayerStyles);
        Assert.Contains("<PlaybackRelativeSkipButton", transportControls);
        Assert.Contains("playback-relative-skip", playbackSkipButton);
        Assert.Contains("viewBox=\"0 0 56 56\"", playbackSkipButton);
        Assert.Contains("--playback-relative-skip-size: 46px;", playbackSkipButtonStyles);
        Assert.Contains("--playback-relative-skip-size: 50px;", playbackSkipButtonStyles);
        Assert.Contains("--playback-relative-skip-size: 54px;", playbackSkipButtonStyles);
        Assert.Contains("playback-relative-skip__glyph", playbackSkipButton);
        Assert.Contains("playback-relative-skip__arc", playbackSkipButton);
        Assert.Contains("playback-relative-skip__arrow", playbackSkipButton);
        Assert.Contains("<text class=\"@NumberClass\"", playbackSkipButton);
        Assert.Contains("data-playback-seek-delta", playbackSkipButton);
        Assert.Contains("M47 29A19 19 0 1 1 28 9", playbackSkipButton);
        Assert.Contains("M9 29A19 19 0 1 0 28 9", playbackSkipButton);
        Assert.DoesNotContain("translate(56 0) scale(-1 1)", playbackSkipButton);
        Assert.DoesNotContain("AudiobookSkipButton", transportControls + playbackSkipButton + playbackSkipButtonStyles);
        Assert.DoesNotContain("audiobook-skip-button", transportControls + playbackSkipButton + playbackSkipButtonStyles);
        Assert.DoesNotContain("audiobook-skip-button__line", transportControls + playbackSkipButton + playbackSkipButtonStyles);
        Assert.DoesNotContain("audiobook-skip-button__unit", transportControls + playbackSkipButton + playbackSkipButtonStyles);
        Assert.DoesNotContain("data-listen-seek-delta", transportControls + playbackSkipButton + playbackSkipButtonStyles);
        Assert.DoesNotContain("Icons.Material.Filled.Replay30", transportControls);
        Assert.DoesNotContain("Icons.Material.Filled.Forward30", transportControls);
        Assert.DoesNotContain("audiobook-skip-button__icon", transportControls);
        Assert.DoesNotContain("title=\"@label\"", transportControls + playbackSkipButton);
        Assert.Contains("ChapterDuration", popupPlayer);
        Assert.Contains("chapter.EndSeconds.Value - chapter.StartSeconds", popupPlayer);
        Assert.Contains("playback-relative-skip", playbackSkipButton);
        Assert.Contains("playback-relative-skip__arc", playbackSkipButton);
        Assert.Contains("playback-relative-skip__arrow", playbackSkipButton);
        Assert.Contains("playback-relative-skip__number", playbackSkipButton);
        Assert.Contains("data-playback-seek-delta", playbackSkipButton);
        Assert.Contains("consumeImmediateToggleHandled", popupPlayer);
        Assert.Contains("consumeImmediateSeekHandled", popupPlayer);
        Assert.DoesNotContain("Icons.Material.Filled.Replay30", popupPlayer);
        Assert.DoesNotContain("Icons.Material.Filled.Forward30", popupPlayer);
        Assert.DoesNotContain("audiobook-skip-button__icon", popupPlayer);
        Assert.DoesNotContain("listen-skip-glyph", popupPlayer);
        Assert.DoesNotContain("<strong>@value</strong>", popupPlayer);
        Assert.DoesNotContain("<span>@label</span>", popupPlayer);
        Assert.DoesNotContain("title=\"@label\"", transportControls + playbackSkipButton);
        Assert.Contains("@if (!IsAudiobookMode)", popupPlayer);
        Assert.DoesNotContain("<h1>@current.Title</h1>\n                <p>@PlayerSubtitle(current)</p>", popupPlayer.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("HistorySubtitle(item)", popupPlayer);
        Assert.Contains("HistoryPositionText(item)", popupPlayer);
        Assert.Contains("<PlaybackPositionRow", popupPlayer);
        Assert.Contains("<PlaybackPositionList", popupPlayer);
        Assert.Contains("<PlaybackSpeedControl", popupPlayer);
        Assert.Contains("<PlaybackSleepTimerControl", popupPlayer);
        Assert.Contains("Adjust playback speed", popupPlayer);
        Assert.DoesNotContain("SpeedRates", popupPlayer);
        Assert.DoesNotContain("Choose playback speed", popupPlayer);
        Assert.DoesNotContain("listen-popup-sheet__speed-row", popupPlayer + popupPlayerStyles);
        Assert.DoesNotContain("listen-popup-sheet__radio", popupPlayer + popupPlayerStyles);
        Assert.DoesNotContain("TimerRow(", popupPlayer);
        Assert.DoesNotContain("SleepTimerDisplay", popupPlayer);
        Assert.DoesNotContain("listen-popup-sheet__row--timer", popupPlayer + popupPlayerStyles);
        Assert.Contains("<PlaybackRangeSlider", playbackSpeedControl);
        Assert.Contains("<PlaybackRangeSlider", playbackSleepTimerControl);
        Assert.Contains("[Parameter(CaptureUnmatchedValues = true)]", playbackSpeedControl);
        Assert.Contains("@attributes=\"AdditionalAttributes\"", playbackSpeedControl);
        Assert.Contains("type=\"range\"", playbackRangeSlider);
        Assert.Contains("@oninput=\"HandleInputAsync\"", playbackRangeSlider);
        Assert.Contains("_interactiveValue = snapped", playbackRangeSlider);
        Assert.Contains("ResolvedInputStep", playbackRangeSlider);
        Assert.Contains("ResolvedInputStepText", playbackRangeSlider);
        Assert.Contains("SnapValueToStep", playbackRangeSlider);
        Assert.Contains("SelectTickAsync", playbackRangeSlider);
        Assert.Contains("TickLabelClass", playbackRangeSlider);
        Assert.Contains("TickButtonAriaLabel", playbackRangeSlider);
        Assert.Contains("<AppNativeButton Type=\"button\"", playbackRangeSlider);
        Assert.Contains("playback-range-slider__visual", playbackRangeSlider);
        Assert.Contains("playback-range-slider__track", playbackRangeSlider);
        Assert.Contains("playback-range-slider__fill", playbackRangeSlider);
        Assert.Contains("playback-range-slider__thumb", playbackRangeSlider);
        Assert.Contains("<div class=\"@RootClass\" style=\"@TrackStyle\">", playbackRangeSlider);
        Assert.Contains("Quick presets", playbackSpeedControl);
        Assert.Contains("Fine adjustment", playbackSpeedControl);
        Assert.Contains("Reset to @DisplayFormat.FormatSpeedSlider(ResetValue)", playbackSpeedControl);
        Assert.Contains("Current timer", playbackSleepTimerControl);
        Assert.Contains("Step=\"@SliderStepMinutes\"", playbackSleepTimerControl);
        Assert.Contains("InputStep=\"@SliderStepMinutes\"", playbackSleepTimerControl);
        Assert.Contains("SnapValueToStep=\"@(!IsTimerActive)\"", playbackSleepTimerControl);
        Assert.Contains("MajorPresetMinutes", playbackSleepTimerControl);
        Assert.Contains("SliderMaxMinutes", playbackSleepTimerControl);
        Assert.Contains("ClampToTimerStep", playbackSleepTimerControl);
        Assert.Contains("SleepTimerStop", playbackSleepTimerControl);
        Assert.DoesNotContain("Off\\n0 min", playbackSleepTimerControl);
        Assert.DoesNotContain("playback-sleep-timer__step-label", playbackSleepTimerControl + playbackSleepTimerControlStyles);
        Assert.DoesNotContain("Adjust in @FineStepMinutes-minute steps", playbackSleepTimerControl);
        Assert.Contains("System.Threading.Timer? _refreshTimer", playbackSleepTimerControl);
        Assert.Contains("TimeSpan.FromSeconds(15)", playbackSleepTimerControl);
        Assert.Contains("playback-speed-control__presets", playbackSpeedControlStyles);
        Assert.Contains("playback-speed-control__stepper", playbackSpeedControlStyles);
        Assert.Contains("_pendingValue", playbackSpeedControl);
        Assert.Contains("playback-sleep-timer__presets", playbackSleepTimerControlStyles);
        Assert.Contains("playback-sleep-timer__stepper", playbackSleepTimerControlStyles);
        Assert.Contains("playback-sleep-timer__chapter", playbackSleepTimerControlStyles);
        Assert.Contains("playback-sleep-timer__step--decrease", playbackSleepTimerControl);
        Assert.Contains("playback-sleep-timer__step--increase", playbackSleepTimerControl);
        Assert.Contains("grid-template-columns: repeat(5, minmax(0, 1fr));", playbackSleepTimerControlStyles);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr));", playbackSleepTimerControlStyles);
        Assert.Contains("white-space: nowrap;", playbackSleepTimerControlStyles);
        Assert.DoesNotContain("grid-template-columns: repeat(auto-fit, minmax(82px, 1fr));", playbackSleepTimerControlStyles);
        Assert.Contains("playback-range-slider__input", playbackRangeSliderStyles);
        Assert.Contains("--playback-range-percent", playbackRangeSliderStyles);
        Assert.Contains("--playback-range-track-inset", playbackRangeSliderStyles);
        Assert.Contains("playback-range-slider__visual", playbackRangeSliderStyles);
        Assert.Contains("playback-range-slider__track", playbackRangeSliderStyles);
        Assert.Contains("playback-range-slider__fill", playbackRangeSliderStyles);
        Assert.Contains("playback-range-slider__thumb", playbackRangeSliderStyles);
        Assert.Contains("opacity: 0;", playbackRangeSliderStyles);
        Assert.Contains("inset: 0 var(--playback-range-track-inset);", playbackRangeSliderStyles);
        Assert.Contains("margin: 0 var(--playback-range-track-inset);", playbackRangeSliderStyles);
        Assert.Contains(".playback-range-slider ::deep .playback-range-slider__label", playbackRangeSliderStyles);
        Assert.Contains("position: absolute;", playbackRangeSliderStyles);
        Assert.Contains("top: 0;", playbackRangeSliderStyles);
        Assert.Contains("BookmarkPositionText(bookmark)", popupPlayer);
        Assert.Contains("BookmarkSubtitle(bookmark)", popupPlayer);
        Assert.Contains("Presentation=\"list\"", popupPlayer);
        Assert.Contains("Kind=\"add\"", popupPlayer);
        Assert.DoesNotContain("playback-sheet-row__index", popupPlayer);
        Assert.DoesNotContain("listen-popup-sheet__index", popupPlayer);
        Assert.DoesNotContain("Variant=\"history\"", popupPlayer);
        Assert.DoesNotContain("listen-popup-sheet__primary", popupPlayer);
        Assert.DoesNotContain("SecondaryActionLabel=\"Delete bookmark\"", popupPlayer);
        Assert.Contains("CurrentChapterProgressLabel", popupPlayer);
        Assert.DoesNotContain("FormatChapterTimeRange", audioTable);
        Assert.DoesNotContain("AudioDetailLayout", detailPage);
        Assert.DoesNotContain("Model.PrimaryModule.Kind == DetailPrimaryModuleKind.Tracks", primaryModule);
        Assert.Contains("<MusicAlbumOverviewContent Model=\"Model\"", detailPage);
        Assert.DoesNotContain("AudiobookOverviewContent", detailPage);
        Assert.Contains("listen-page listen-page--detail", listenPage);
        Assert.Contains(".listen-page--detail", ReadSource("src/MediaEngine.Web/Components/Pages/ListenPage.razor.css"));
        Assert.DoesNotContain("HeroMetadataPills", primaryModule);
        Assert.Contains("InitialPositionSeconds = ResumePositionFor(item)", audioTable);
        Assert.Contains("ResolveAudiobookTotalSeconds(chapters)", detailPage);
        Assert.Contains("ResolveAudiobookResumePosition(chapters, Model.Progress?.Percent, Playback.AudiobookNearStartGuardSeconds, totalDurationSeconds)", detailPage);
        Assert.Contains("\"data-listen-start-progress\"", detailPage);
        Assert.Contains("\"data-listen-start-duration\"", detailPage);
        Assert.Contains("\"data-listen-start-rewind\"", detailPage);
        Assert.Contains("Playback.ResumeRewindSeconds", detailPage);
        Assert.Contains("item.ResumePositionSeconds is > 0", detailPage);
        Assert.Contains("StartAudiobookAsync", detailPage);
        Assert.Contains("AudiobookStartKinds.Resume", detailPage);
        Assert.Contains("ResolveAudiobookResumeItem(chapters, resumePositionSeconds)", detailPage);
        Assert.Contains("InitialPositionSeconds = initialPositionSeconds ?? ResumePositionFor(item)", detailPage);
        Assert.DoesNotContain("IsIntroChapter", detailPage);
        Assert.Contains("item.ResumePositionSeconds is > 0", audioTable);
        Assert.Contains("Chapters = chapters.Select(ToPlaybackChapter).ToList()", detailPage);
        Assert.Contains("Chapters = IsAudiobook ? VisibleItems.Select(ToPlaybackChapter).ToList()", audioTable);
        Assert.Contains("BootstrapDirectStream", playbackService);
        Assert.Contains("public int ResumeRewindSeconds", playbackService);
        Assert.Contains("RequestTransportCommandAsync(CreateStartCommand())", playbackService);
        Assert.Contains("public sealed record PlaybackTransportCommand(", playbackModels);
        Assert.Contains("string? StreamUrl = null", playbackModels);
        Assert.Contains("<audio @ref=\"_audioRef\"", playerBar);
        Assert.DoesNotContain("@key=\"Playback.CurrentBrowserStreamUrl\"", playerBar);
        Assert.DoesNotContain("autoplay", playerBar);
        Assert.Contains("StartAudioAsync", playerBar);
        Assert.Contains("listenPlayback.startAudio", playerBar);
        Assert.Contains("id=\"listen-audio-engine\"", playerBar);
        Assert.Contains("listenPlayback.ensureAudioSource", playerBar);
        Assert.DoesNotContain("listenPlayback.loadAudio", playerBar);
        Assert.DoesNotContain("Math.Abs(_lastSeekedPositionSeconds.Value - targetPositionSeconds)", playerBar);
        Assert.DoesNotContain("@ontimeupdate=\"HandleAudioMetricsChangedAsync\"", playerBar);
        Assert.Contains("startAudio: startAudioElement", playerScript);
        Assert.Contains("ensureAudioSource: ensureAudioSource", playerScript);
        Assert.Contains("consumeImmediateToggleHandled", playerScript);
        Assert.Contains("consumeImmediateSeekHandled", playerScript);
        Assert.Contains("data-playback-seek-delta", playerScript);
        Assert.DoesNotContain("data-listen-seek-delta", playerScript);
        Assert.Contains("data-listen-immediate-start", playerScript);
        Assert.Contains("startPositionFromDataset", playerScript);
        Assert.Contains("listenPendingStartPosition", playerScript);
        Assert.Contains("document.addEventListener('click'", playerScript);
        Assert.Contains("openPopupFromImmediateAction", playerScript);
        Assert.Contains("data-listen-popup-route", playerBar);
        Assert.Contains("AudiobookActionAttributes", detailPage);
        Assert.Contains("ActionAttributes=\"@(IsAudiobook ? AudiobookActionAttributes : null)\"", detailPage);
        Assert.Contains("window.detailOrigin.initialize();", playerScript);
        Assert.Contains("[data-detail-origin-scroll]", playerScript);
        Assert.Contains("registerAudioStateObserver: function", playerScript);
        Assert.Contains("grid-template-columns: 3.6rem minmax(0, 1fr) 3.6rem", playerStyles);
        Assert.Contains("grid-column: 1 / -1;", playerStyles);
        Assert.Contains("width: 100%;", playerStyles);
        Assert.Contains("FormatSeconds(item.DurationSeconds.Value, forceHours: false)", audioTable);
        Assert.DoesNotContain("FormatChapterTimeRange", audioTable);
        Assert.Contains("Playback.CurrentTimeSeconds", audioTable);
        Assert.Contains("currentItem.AssetId.Value != assetId", audioTable);
        Assert.DoesNotContain("GetDetailPageAsync(", listenPage);
        Assert.DoesNotContain("<DetailPage", ReadSource("src/MediaEngine.Web/Components/Pages/ListenPage.razor"));
        Assert.DoesNotContain("NavigateTo($\"/book/{WorkId.Value}?mode=listen\"", listenPage, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioDetailActionsAndTablesUseHeartFavoritesAndAlignedHeroIcons()
    {
        var actions = ReadSource("src/MediaEngine.Web/Components/Details/HeroActionRow.razor");
        var styles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");
        var songTable = ReadSource("src/MediaEngine.Web/Components/Listen/ListenSongTable.razor");
        var libraryTable = ReadSource("src/MediaEngine.Web/Components/Library/LibraryConfigurableTable.razor");

        Assert.Contains("else if (IsPrimaryHeroActionRow)", actions);
        Assert.Contains("VisibleSecondaryActions => SecondaryActions", actions);
        Assert.DoesNotContain("watch-party", actions, StringComparison.Ordinal);
        Assert.Contains("tl-detail-flat-action", actions);
        Assert.Contains("border-radius: 999px", styles);
        Assert.Contains("Icons.Material.Filled.Favorite", songTable);
        Assert.Contains("Icons.Material.Outlined.FavoriteBorder", songTable);
        Assert.Contains("Icons.Material.Filled.Favorite", libraryTable);
        Assert.Contains("Icons.Material.Outlined.FavoriteBorder", libraryTable);
        Assert.DoesNotContain("Icons.Material.Filled.Star", songTable);
    }

    [Fact]
    public void PlaybackPopoutUsesGenericShellAndAccessibleActions()
    {
        var popup = ReadSource("src/MediaEngine.Web/Components/Pages/ListenPlayerPopupPage.razor");
        var popupCss = ReadSource("src/MediaEngine.Web/Components/Pages/ListenPlayerPopupPage.razor.css");
        var host = ReadSource("src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor");
        var hostCss = ReadSource("src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor.css");
        var transportControls = ReadSource("src/MediaEngine.Web/Components/Listen/ListenTransportControls.razor");
        var controlStrip = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackControlStrip.razor");
        var controlStripCss = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackControlStrip.razor.css");
        var iconButtonCss = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackIconButton.razor.css");
        var toolSheet = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackToolSheet.razor");
        var toolSheetCss = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackToolSheet.razor.css");
        var sheetHandle = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackSheetHandleButton.razor");
        var sheetHandleCss = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackSheetHandleButton.razor.css");
        var popoutShell = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackPopoutShell.razor");
        var miniPlayer = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackMiniPlayer.razor");
        var sheetList = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackSheetList.razor");
        var sheetListCss = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackSheetList.razor.css");
        var sheetRow = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackSheetRow.razor");
        var sheetRowCss = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackSheetRow.razor.css");
        var positionRow = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackPositionRow.razor");
        var positionRowCss = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackPositionRow.razor.css");
        var positionList = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackPositionList.razor");
        var positionListCss = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackPositionList.razor.css");
        var speedControl = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackSpeedControl.razor");
        var speedControlCss = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackSpeedControl.razor.css");
        var sleepTimerControl = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackSleepTimerControl.razor");
        var sleepTimerControlCss = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackSleepTimerControl.razor.css");
        var rangeSlider = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackRangeSlider.razor");
        var rangeSliderCss = ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackRangeSlider.razor.css");

        Assert.Contains("listen-popup__stage", popup);
        Assert.Contains("<PlaybackPopoutShell", popup);
        Assert.Contains("<PlaybackControlStrip", popup);
        Assert.Contains("<PlaybackToolSheet", popup);
        Assert.Contains("Presentation=\"full-overlay\"", popup);
        Assert.Contains("<PlaybackSheetList", popup);
        Assert.Contains("<PlaybackSheetRow", popup);
        Assert.Contains("<PlaybackPositionRow", popup);
        Assert.Contains("<PlaybackPositionList", popup);
        Assert.Contains("<PlaybackSpeedControl", popup);
        Assert.Contains("<PlaybackSleepTimerControl", popup);
        Assert.Contains("<PlaybackRangeSlider", speedControl);
        Assert.Contains("<PlaybackRangeSlider", sleepTimerControl);
        Assert.Contains("InputStep=\"@Step\"", speedControl);
        Assert.Contains("TickStep=\"0.5\"", speedControl);
        Assert.DoesNotContain("TickStep=\"0.1\"", speedControl);
        Assert.Contains("TickFormatter=\"FormatSpeedTick\"", speedControl);
        Assert.Contains("FormatSpeedTick", speedControl);
        Assert.Contains("[Parameter(CaptureUnmatchedValues = true)]", speedControl);
        Assert.Contains("@attributes=\"AdditionalAttributes\"", speedControl);
        Assert.Contains("playback-popout-shell", popoutShell);
        Assert.Contains("playback-mini-player", miniPlayer);
        Assert.Contains("playback-position-row", positionRow);
        Assert.Contains("[Parameter(CaptureUnmatchedValues = true)]", positionRow);
        Assert.Contains("@attributes=\"AdditionalAttributes\"", positionRow);
        Assert.Contains("Icons.Material.Outlined.CalendarMonth", positionRow);
        Assert.Contains("playback-position-row__action-ring", positionRowCss);
        Assert.Contains("playback-position-row--action-plain", positionRowCss);
        Assert.Contains("playback-position-row-shell--list", positionRowCss);
        Assert.Contains("playback-position-row--list", positionRowCss);
        Assert.Contains("playback-position-row--add", positionRowCss);
        Assert.Contains("border: 1px solid var(--playback-accent-border);", positionRowCss);
        Assert.Contains("border-bottom: 1px solid var(--playback-divider);", positionRowCss);
        Assert.Contains("margin-top: 0;", positionRowCss);
        Assert.Contains("var(--playback-accent)", positionRowCss);
        Assert.Contains("playback-position-list", positionList);
        Assert.Contains("overflow: hidden;", positionListCss);
        Assert.Contains("Quick presets", speedControl);
        Assert.Contains("Fine adjustment", speedControl);
        Assert.Contains("Reset to @DisplayFormat.FormatSpeedSlider(ResetValue)", speedControl);
        Assert.Contains("Current timer", sleepTimerControl);
        Assert.Contains("Step=\"@SliderStepMinutes\"", sleepTimerControl);
        Assert.Contains("InputStep=\"@SliderStepMinutes\"", sleepTimerControl);
        Assert.Contains("SnapValueToStep=\"@(!IsTimerActive)\"", sleepTimerControl);
        Assert.Contains("MajorPresetMinutes", sleepTimerControl);
        Assert.Contains("SliderMaxMinutes", sleepTimerControl);
        Assert.Contains("ClampToTimerStep", sleepTimerControl);
        Assert.Contains("SleepTimerStop", sleepTimerControl);
        Assert.DoesNotContain("Off\\n0 min", sleepTimerControl);
        Assert.DoesNotContain("playback-sleep-timer__step-label", sleepTimerControl + sleepTimerControlCss);
        Assert.DoesNotContain("Adjust in @FineStepMinutes-minute steps", sleepTimerControl);
        Assert.Contains("System.Threading.Timer? _refreshTimer", sleepTimerControl);
        Assert.Contains("TimeSpan.FromSeconds(15)", sleepTimerControl);
        Assert.Contains("EndOfSectionChanged", sleepTimerControl);
        Assert.Contains("type=\"range\"", rangeSlider);
        Assert.Contains("@oninput=\"HandleInputAsync\"", rangeSlider);
        Assert.Contains("@onchange=\"HandleInputAsync\"", rangeSlider);
        Assert.Contains("_interactiveValue = snapped", rangeSlider);
        Assert.Contains("ResolvedInputStep", rangeSlider);
        Assert.Contains("ResolvedInputStepText", rangeSlider);
        Assert.Contains("SnapValueToStep", rangeSlider);
        Assert.Contains("SelectTickAsync", rangeSlider);
        Assert.Contains("TickLabelClass", rangeSlider);
        Assert.Contains("TickButtonAriaLabel", rangeSlider);
        Assert.Contains("<AppNativeButton Type=\"button\"", rangeSlider);
        Assert.Contains("playback-range-slider__visual", rangeSlider);
        Assert.Contains("playback-range-slider__track", rangeSlider);
        Assert.Contains("playback-range-slider__fill", rangeSlider);
        Assert.Contains("playback-range-slider__thumb", rangeSlider);
        Assert.Contains("<div class=\"@RootClass\" style=\"@TrackStyle\">", rangeSlider);
        Assert.Contains("playback-speed-control__presets", speedControlCss);
        Assert.Contains("playback-speed-control__stepper", speedControlCss);
        Assert.Contains(".playback-speed-control ::deep .playback-speed-control__preset", speedControlCss);
        Assert.Contains(".playback-speed-control ::deep .playback-speed-control__step", speedControlCss);
        Assert.Contains("playback-speed-control__fine", speedControlCss);
        Assert.Contains("_pendingValue", speedControl);
        Assert.Contains("playback-sleep-timer__presets", sleepTimerControlCss);
        Assert.Contains("playback-sleep-timer__stepper", sleepTimerControlCss);
        Assert.Contains("playback-sleep-timer__chapter", sleepTimerControlCss);
        Assert.Contains(".playback-sleep-timer ::deep .playback-sleep-timer__preset", sleepTimerControlCss);
        Assert.Contains(".playback-sleep-timer ::deep .playback-sleep-timer__chapter", sleepTimerControlCss);
        Assert.Contains(".playback-sleep-timer ::deep .playback-sleep-timer__step", sleepTimerControlCss);
        Assert.Contains("playback-sleep-timer__step--decrease", sleepTimerControl);
        Assert.Contains("playback-sleep-timer__step--increase", sleepTimerControl);
        Assert.Contains("grid-template-columns: repeat(5, minmax(0, 1fr));", sleepTimerControlCss);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr));", sleepTimerControlCss);
        Assert.Contains("white-space: nowrap;", sleepTimerControlCss);
        Assert.DoesNotContain("grid-template-columns: repeat(auto-fit, minmax(82px, 1fr));", sleepTimerControlCss);
        Assert.Contains("playback-range-slider__input", rangeSliderCss);
        Assert.Contains("--playback-range-percent", rangeSliderCss);
        Assert.Contains("--playback-range-track-inset", rangeSliderCss);
        Assert.Contains("playback-range-slider__visual", rangeSliderCss);
        Assert.Contains("playback-range-slider__track", rangeSliderCss);
        Assert.Contains("playback-range-slider__fill", rangeSliderCss);
        Assert.Contains("playback-range-slider__thumb", rangeSliderCss);
        Assert.Contains("opacity: 0;", rangeSliderCss);
        Assert.Contains("inset: 0 var(--playback-range-track-inset);", rangeSliderCss);
        Assert.Contains("margin: 0 var(--playback-range-track-inset);", rangeSliderCss);
        Assert.Contains(".playback-range-slider ::deep .playback-range-slider__label", rangeSliderCss);
        Assert.Contains("position: absolute;", rangeSliderCss);
        Assert.Contains("top: 0;", rangeSliderCss);
        Assert.Contains("playback-sheet-list", sheetList);
        Assert.Contains("padding: 4px 12px 18px;", sheetListCss);
        Assert.Contains("playback-sheet-row", sheetRow);
        Assert.Contains("playback-sheet-row-shell", sheetRow);
        Assert.Contains(".playback-sheet-row-shell ::deep .playback-sheet-row", sheetRowCss);
        Assert.Contains("justify-content: space-between;", sheetRowCss);
        Assert.Contains("listen-popup__actions", popup);
        Assert.Contains("Size=\"standard\"", popup);
        Assert.Contains("grid-template-columns: repeat(5, minmax(44px, 1fr));", popupCss);
        Assert.Contains(".playback-control-strip.listen-popup__actions", controlStripCss);
        Assert.Contains("grid-template-columns: repeat(5, minmax(44px, 1fr));", controlStripCss);
        Assert.Contains("Speed", popup);
        Assert.Contains("Chapters", popup);
        Assert.Contains("History", popup);
        Assert.Contains("Bookmark", popup);
        Assert.Contains("Sleep Timer", popup);
        Assert.Contains("<PlaybackIconButton", controlStrip);
        Assert.Contains("::deep .playback-icon-button__label", iconButtonCss);
        Assert.Contains("::deep .playback-icon-button__value", iconButtonCss);
        Assert.Contains("Control.BadgeText", ReadSource("src/MediaEngine.Web/Components/Shared/PlaybackIconButton.razor"));
        Assert.Contains("playback-icon-button__badge", iconButtonCss);
        Assert.Contains("playback-icon-button--sleep-timer.is-active", iconButtonCss);
        Assert.Contains("--playback-icon-button-badge-color: var(--playback-accent);", iconButtonCss, StringComparison.Ordinal);
        Assert.DoesNotContain("#c084fc", iconButtonCss, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<PlaybackSheetHandleButton", toolSheet);
        Assert.Contains("NormalizedPresentation", toolSheet);
        Assert.Contains("full-overlay", toolSheet);
        Assert.Contains("playback-tool-sheet--full-overlay", toolSheetCss);
        Assert.Contains("playback-tool-sheet-roll-up", toolSheetCss);
        Assert.Contains("prefers-reduced-motion", toolSheetCss);
        Assert.DoesNotContain("border-bottom: 1px solid rgba(148, 163, 184, 0.16);", toolSheetCss);
        Assert.Contains("[Parameter(CaptureUnmatchedValues = true)]", sheetHandle);
        Assert.Contains("@attributes=\"AdditionalAttributes\"", sheetHandle);
        Assert.Contains("playback-sheet-handle-button-shell", sheetHandle);
        Assert.Contains(".playback-sheet-handle-button-shell ::deep .playback-sheet-handle-button", sheetHandleCss);
        Assert.Contains("align-items: flex-start;", sheetHandleCss);
        Assert.Contains("box-sizing: border-box;", sheetHandleCss);
        Assert.Contains("height: 44px;", sheetHandleCss);
        Assert.Contains("margin: 8px auto 0;", sheetHandleCss);
        Assert.Contains("padding: 4px 36px 0;", sheetHandleCss);
        Assert.Contains("listen-popup-sheet__backdrop", popup);
        Assert.Contains("OnKeyDown=\"HandlePopupKeyDown\"", popup);
        Assert.DoesNotContain("listen-popup-sheet__grabber", popup + popupCss);
        Assert.DoesNotContain("listen-popup-sheet__close", popup + popupCss);
        Assert.DoesNotContain("SpeedActionButton", popup);
        Assert.DoesNotContain("SpeedRates", popup);
        Assert.DoesNotContain("Choose playback speed", popup);
        Assert.DoesNotContain("listen-popup-sheet__speed-row", popup + popupCss);
        Assert.DoesNotContain("listen-popup-sheet__radio", popup + popupCss);
        Assert.DoesNotContain("TimerRow(", popup);
        Assert.DoesNotContain("SleepTimerDisplay", popup);
        Assert.DoesNotContain("listen-popup-sheet__row--timer", popup + popupCss);
        Assert.DoesNotContain("private RenderFragment ActionButton", popup);
        Assert.DoesNotContain("playback-sheet-row__index", popup + sheetRowCss);
        Assert.DoesNotContain("listen-popup-sheet__index", popup + popupCss);
        Assert.DoesNotContain("Variant=\"history\"", popup);
        Assert.DoesNotContain("PlaybackHistoryRow", popup + positionRow + positionRowCss);
        Assert.DoesNotContain("listen-popup-sheet__primary", popup + popupCss);
        Assert.DoesNotContain(".listen-popup__action strong", popupCss);
        Assert.DoesNotContain("display: none", popupCss);
        Assert.Contains("play-chapter", popup);
        Assert.Contains("add-audiobook-bookmark", popup);
        Assert.Contains("set-sleep-timer", popup);
        Assert.Contains("<ListenTransportControls", popup);
        Assert.Contains("<PlaybackRelativeSkipButton Seconds=\"@SkipBackSeconds\" Direction=\"back\"", transportControls);
        Assert.Contains("<PlaybackRelativeSkipButton Seconds=\"@SkipForwardSeconds\" Direction=\"forward\"", transportControls);
        Assert.Contains("listen-popup-menu", popup);
        Assert.Contains("<PlaybackControlStrip", host);
        Assert.Contains("<PlaybackMiniPlayer", host);
        Assert.Contains("Class=\"listen-player__audiobook-actions\"", host);
        Assert.Contains("AriaLabel=\"Audiobook tools\"", host);
        Assert.Contains("OnControl=\"HandleAudiobookPanelControl\"", host);
        Assert.DoesNotContain("<PlaybackValueToolButton", host);
        Assert.Contains("ActiveSheet: Playback.IsPanelOpen ? _activeAudiobookPanelTool : null", host);
        Assert.Contains("SleepTimerValueText: BottomSleepTimerValueText", host);
        Assert.Contains("Playback.TogglePanel();", host);
        Assert.Contains("ShortSleepTimerLabel", host);
        Assert.Contains("--listen-dock-height: 88px", hostCss);
        Assert.Contains(".listen-now-playing__body", hostCss);
        Assert.Contains(".listen-player__actions ::deep .playback-control-strip.listen-player__audiobook-actions", hostCss);
        Assert.Contains("grid-template-columns: repeat(5, minmax(52px, 1fr)) !important;", hostCss);
        Assert.Contains("width: clamp(300px, 24vw, 350px) !important;", hostCss);
        Assert.Contains("@media (max-width: 1240px)", hostCss);
        Assert.Contains("grid-column: 1 / -1;", hostCss);
        Assert.Contains("width: 100%;", hostCss);
        Assert.Contains("grid-template-rows: 24px 10px 12px;", hostCss);
        Assert.Contains("min-height: 52px;", hostCss);
        Assert.Contains("<PlaybackSleepTimerControl", host);
        Assert.Contains("OnClose=\"CloseBottomPanelAsync\"", host);
        Assert.Contains("BottomPanelTitle", host);
        Assert.DoesNotContain("Class=\"listen-player-panel__audiobook-actions\"", host);
        Assert.DoesNotContain("listen-player-panel__audiobook-actions", hostCss);
        Assert.Contains("::deep .listen-player-panel__tool-row", hostCss);
        Assert.DoesNotContain("Playback.CyclePlaybackRateAsync()", host);
        Assert.DoesNotContain("Icons.Material.Outlined.MoreHoriz", host);
        Assert.DoesNotContain("BottomPanelAriaLabel", host);
        Assert.DoesNotContain("SleepTimerRow", host);
        Assert.DoesNotContain("tool-row--timer", host + hostCss);
        Assert.Contains("play-next-chapter", host);
        Assert.Contains("play-previous-chapter", host);
        Assert.Contains("ApplyPlaybackRateAsync", host);
        Assert.Contains("--listen-accent", popupCss);
        Assert.Contains("listen-popup-sheet", popupCss);
        Assert.DoesNotContain("#ff416c", popupCss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#ff2746", popupCss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#8b5cf6", popupCss, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WatchHero_PreservesTheCompleteFirstParagraph()
    {
        var firstParagraph = string.Join(' ', Enumerable.Repeat("A complete movie synopsis should remain readable.", 15));
        var model = new DetailPageViewModel
        {
            EntityType = DetailEntityType.Movie,
            Title = "Example Movie",
            Description = $"{firstParagraph}\n\nA second paragraph belongs in the overview.",
        };

        var presentation = DetailHeroPresentation.From(model);

        Assert.Equal(firstParagraph, presentation.HeroCopy);
        Assert.True(presentation.HeroCopyHasMore);
        Assert.False(presentation.ShowTitleBar);
    }

    [Fact]
    public void MediaHero_UsesTaglineInsteadOfDescriptionWhenAvailable()
    {
        var model = new DetailPageViewModel
        {
            EntityType = DetailEntityType.Movie,
            Title = "Example Movie",
            Tagline = "The truth is closer than it appears.",
            SecondaryTitleText = "The truth is closer than it appears.",
            SecondaryTitleTextKind = "tagline",
            Description = "This longer provider description should remain available in the overview.",
        };

        var presentation = DetailHeroPresentation.From(model);

        Assert.Equal(model.Tagline, presentation.HeroCopy);
        Assert.Null(presentation.SecondaryTitleText);
        Assert.False(presentation.HeroCopyHasMore);
    }

    public static IEnumerable<object[]> DescriptionLikeSecondaryTitleCases()
    {
        foreach (var entityType in Enum.GetValues<DetailEntityType>())
        {
            yield return new object[] { entityType, "description" };
            yield return new object[] { entityType, "short_description" };
        }
    }

    [Theory]
    [MemberData(nameof(DescriptionLikeSecondaryTitleCases))]
    public void DetailHero_MovesDescriptionLikeSecondaryTextBelowActions(
        DetailEntityType entityType,
        string secondaryKind)
    {
        var model = new DetailPageViewModel
        {
            EntityType = entityType,
            Title = "Example Detail",
            SecondaryTitleText = "A short description that was previously shown under the title.",
            SecondaryTitleTextKind = secondaryKind,
            Description = "The description belongs below the action rows.",
        };

        var presentation = DetailHeroPresentation.From(model);

        Assert.Null(presentation.SecondaryTitleText);
        Assert.Equal(model.Description, presentation.HeroCopy);
    }

    [Theory]
    [InlineData(DetailEntityType.Book)]
    [InlineData(DetailEntityType.ComicIssue)]
    [InlineData(DetailEntityType.Audiobook)]
    [InlineData(DetailEntityType.Movie)]
    [InlineData(DetailEntityType.MusicAlbum)]
    public void DetailHero_KeepsSemanticSubtitlesUnderTheTitle(DetailEntityType entityType)
    {
        var model = new DetailPageViewModel
        {
            EntityType = entityType,
            Title = "Example Detail",
            SecondaryTitleText = "A real subtitle",
            SecondaryTitleTextKind = "subtitle",
            Description = "The description still belongs below the action rows.",
        };

        var presentation = DetailHeroPresentation.From(model);

        Assert.Equal(model.SecondaryTitleText, presentation.SecondaryTitleText);
        Assert.Equal(model.Description, presentation.HeroCopy);
    }

    [Fact]
    public void DetailHero_PlacesEverySynopsisAfterActionsAndProgress()
    {
        var content = ReadSource("src/MediaEngine.Web/Components/Details/DetailHeroContent.razor");
        var hero = ReadSource("src/MediaEngine.Web/Components/Details/DetailHero.razor");
        var styles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");
        var actionIndex = content.IndexOf("@ActionContent", StringComparison.Ordinal);
        var progressIndex = content.IndexOf("@ProgressContent", StringComparison.Ordinal);
        var synopsisIndex = content.IndexOf("@if (!string.IsNullOrWhiteSpace(Synopsis))", StringComparison.Ordinal);
        var personLinksIndex = hero.IndexOf("class=\"tl-detail-person-links\"", StringComparison.Ordinal);
        var personActionIndex = hero.LastIndexOf("<ActionContent>", personLinksIndex, StringComparison.Ordinal);
        var personLinksRuleIndex = styles.LastIndexOf(
            ".tl-detail-stage ::deep .tl-detail-person-links {",
            StringComparison.Ordinal);
        var personLinksRuleEnd = styles.IndexOf('}', personLinksRuleIndex);
        var synopsisRuleIndex = styles.LastIndexOf(
            ".tl-detail-stage ::deep .tl-detail-hero__synopsis {",
            StringComparison.Ordinal);
        var synopsisRuleEnd = styles.IndexOf('}', synopsisRuleIndex);

        Assert.True(actionIndex >= 0 && actionIndex < progressIndex);
        Assert.True(progressIndex < synopsisIndex);
        Assert.True(personActionIndex >= 0 && personActionIndex < personLinksIndex);
        Assert.DoesNotContain("<SupplementalContent>", hero, StringComparison.Ordinal);
        Assert.True(personLinksRuleIndex >= 0 && personLinksRuleEnd > personLinksRuleIndex);
        Assert.Contains(
            "order: 3",
            styles[personLinksRuleIndex..personLinksRuleEnd],
            StringComparison.Ordinal);
        Assert.True(synopsisRuleIndex >= 0 && synopsisRuleEnd > synopsisRuleIndex);
        Assert.Contains(
            "order: 6",
            styles[synopsisRuleIndex..synopsisRuleEnd],
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("First paragraph.\\n\\nSecond paragraph.")]
    [InlineData("First paragraph.\r\n \r\nSecond paragraph.")]
    [InlineData("<p>First paragraph.</p><p>Second paragraph.</p>")]
    public void ReadHero_ShowsOnlyTheFirstNormalizedDescriptionParagraph(string description)
    {
        var model = new DetailPageViewModel
        {
            EntityType = DetailEntityType.Book,
            Title = "Example Book",
            Description = description,
        };

        var presentation = DetailHeroPresentation.From(model);

        Assert.Equal("First paragraph.", presentation.HeroCopy);
        Assert.True(presentation.HeroCopyHasMore);
    }

    [Fact]
    public void ReadHero_ShortensAnUnbrokenProviderDescriptionToOneHeroParagraph()
    {
        var description = string.Join(' ', Enumerable.Repeat(
            "A complete sentence keeps the provider description readable on the hero.",
            20));
        var model = new DetailPageViewModel
        {
            EntityType = DetailEntityType.Book,
            Title = "Example Book",
            Description = description,
        };

        var presentation = DetailHeroPresentation.From(model);

        Assert.NotNull(presentation.HeroCopy);
        Assert.True(presentation.HeroCopy.Length <= 520);
        Assert.EndsWith(".", presentation.HeroCopy, StringComparison.Ordinal);
        Assert.True(presentation.HeroCopyHasMore);
    }

    [Fact]
    public void AppHeadOutlet_IsInteractiveSoPageTitlesFollowClientNavigation()
    {
        var app = ReadSource("src/MediaEngine.Web/Components/App.razor");
        var reader = ReadSource("src/MediaEngine.Web/Components/Pages/EpubReader.razor");

        Assert.Contains("<HeadOutlet @rendermode=\"new InteractiveServerRenderMode(prerender: false)\" />", app);
        Assert.Contains("<Routes @rendermode=\"new InteractiveServerRenderMode(prerender: false)\" />", app);
        Assert.Contains("<PageTitle>", reader);
    }

    [Fact]
    public void LandingAndDetailSurfaces_ShareDetailHeroContentWhileHomeOmitsLaneNavigation()
    {
        var detailPage = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor");
        var detailHero = ReadSource("src/MediaEngine.Web/Components/Details/DetailHero.razor");
        var detailHeroContent = ReadSource("src/MediaEngine.Web/Components/Details/DetailHeroContent.razor");
        var detailTabs = ReadSource("src/MediaEngine.Web/Components/Details/DetailTabs.razor");
        var landingHero = ReadSource("src/MediaEngine.Web/Components/Cinematic/CinematicHeroCarousel.razor");
        var mediaHub = ReadSource("src/MediaEngine.Web/Components/MediaHub/MediaHubPage.razor");
        var surfaceNavigation = ReadSource("src/MediaEngine.Web/Components/Cinematic/SurfaceNavigationBar.razor");
        var home = ReadSource("src/MediaEngine.Web/Components/Pages/LibraryBrowsePage.razor");

        Assert.Contains("<DetailHero Model=\"Model\"", detailPage);
        Assert.Contains("<DetailTabs Tabs=\"VisibleTabs\"", detailPage);
        Assert.Contains("<CinematicHeroSurface", detailHero);
        Assert.Contains("<DetailHeroContent", detailHero);
        Assert.Contains("tl-detail-hero__identity", detailHeroContent);
        Assert.Contains("<CinematicHeroSurface", landingHero);
        Assert.Contains("<DetailHeroContent", landingHero);
        Assert.Contains("<SurfaceNavigationBar", detailTabs);
        Assert.Contains("<SurfaceNavigationBar", mediaHub);
        Assert.Contains("<SurfaceTabBar", surfaceNavigation);
        Assert.Contains("<CinematicHeroCarousel", home);
        Assert.DoesNotContain("<SurfaceNavigationBar", home);
        Assert.DoesNotContain("<SurfaceTabBar", home);
    }

    [Fact]
    public void PersonDetailIdentity_UsesCanonicalTitleAndMetadataTypography()
    {
        var hero = ReadSource("src/MediaEngine.Web/Components/Details/DetailHero.razor");
        var heroContent = ReadSource("src/MediaEngine.Web/Components/Details/DetailHeroContent.razor");
        var styles = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");
        var marker = "/* People opt into the same copy hook, identity hierarchy, and metadata row";
        var markerIndex = styles.LastIndexOf(marker, StringComparison.Ordinal);

        Assert.Contains("CopyClass=\"tl-detail-person-hero__copy\"", hero, StringComparison.Ordinal);
        Assert.Contains("tl-detail-watch-metadata-row tl-detail-watch-metadata-row--facts tl-detail-person-fact-row", hero, StringComparison.Ordinal);
        Assert.Contains("tl-detail-watch-metadata-item tl-detail-person-fact", hero, StringComparison.Ordinal);
        Assert.Contains("tl-detail-hero__copy @CopyClass", heroContent, StringComparison.Ordinal);
        Assert.Contains("[Parameter] public string CopyClass", heroContent, StringComparison.Ordinal);
        Assert.Contains(".tl-detail-person-hero__content .tl-detail-hero__copy", styles, StringComparison.Ordinal);
        Assert.Contains("align-self: flex-end", styles, StringComparison.Ordinal);
        Assert.Contains("justify-items: start", styles, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0);
        var finalRules = styles[markerIndex..];
        Assert.Contains("font-size: clamp(3.6rem, 4vw, 4.75rem)", finalRules, StringComparison.Ordinal);
        Assert.Contains("font-weight: 500", finalRules, StringComparison.Ordinal);
        Assert.Contains("font-size: clamp(3rem, 3.5vw, 4.2rem)", finalRules, StringComparison.Ordinal);
        Assert.Contains("font-size: clamp(2.4rem, 3vw, 3.6rem)", finalRules, StringComparison.Ordinal);
        Assert.Contains("font-family: var(--media-identity-font-family, var(--font-ui))", finalRules, StringComparison.Ordinal);
        Assert.Contains("font-size: clamp(1rem, 1.3vw, 1.18rem)", finalRules, StringComparison.Ordinal);
        Assert.Contains("line-height: 1.5", finalRules, StringComparison.Ordinal);
        Assert.Contains("font: inherit", finalRules, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
    {
        var root = FindRepoRoot();
        return File.ReadAllText(Path.Combine(root, relativePath));
    }

    private static string ReadDetailComposerSource()
    {
        var detailsRoot = Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Services",
            "Details");
        var paths = new[]
            {
                Path.Combine(detailsRoot, "DetailComposerService.cs"),
            }
            .Concat(Directory.EnumerateFiles(
                Path.Combine(detailsRoot, "Internals"),
                "*.cs",
                SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        return string.Join("\n", paths.Select(File.ReadAllText));
    }

    private static string ReadEngineApiClientSources()
    {
        var directory = Path.Combine(
            FindRepoRoot(),
            "src", "MediaEngine.Web", "Services", "Integration");

        return string.Join(
            "\n",
            Directory.EnumerateFiles(directory, "EngineApiClient*.cs")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllText));
    }

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
