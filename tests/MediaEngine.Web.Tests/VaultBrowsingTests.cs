using System.Net;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Vault;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Vault;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Browsing the Vault in the Dashboard (Videos spec WP14, D39 to D41). The rules under test: the main page lists scenes (matched and
/// unmatched together, with chips), Performers, Studios and Tags are lists of their own, an unmatched video appears only in Recently
/// added, Scenes, Folders and Unmatched, a tile opens to the owned scenes, the Unmatched list is sortable and mounts Identify for an
/// administrator on a computer, card shapes and pictures follow the spec, Blur applies to every card and the optional hover stills
/// cycle only when asked for.
/// </summary>
public sealed class VaultBrowsingTests : AsyncBunitContext
{
    private static readonly Guid Me = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid SceneA = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid SceneB = Guid.Parse("00000000-0000-0000-0000-0000000000d2");
    private static readonly Guid Image = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid Performer = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    private readonly ScriptedCatalogue _catalogue = new();
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public VaultBrowsingTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddScoped<IVaultCatalogue>(_ => _catalogue);
    }

    private NsfwModeState State => Services.GetRequiredService<NsfwModeState>();

    private async Task UnlockAsync(bool administrator = false)
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetNsfwStatusAsync), _ =>
                Task.FromResult(VaultCall<NsfwStatusDto>.Ok(new NsfwStatusDto { Mode = "allowed", Unlocked = true, HasPin = false, IdleLockMinutes = 15 })));
            stub.SetHandler(nameof(IEngineApiClient.SearchVaultIdentifyAsync), _ =>
                Task.FromResult(VaultCall<VaultCandidateListDto>.Ok(new VaultCandidateListDto { Status = "ok" })));
        });
        Services.AddSingleton(api);
        var session = new DashboardSessionAccessor();
        if (administrator)
        {
            session.Set("token", Me, Me, Guid.NewGuid(),
                new MediaEngine.Contracts.Authentication.DashboardAuthorityResponse(Me, Me, true, true, 1, 1, true, true, null, 1, [], ["settings.administration"], ["household.manage"]));
        }

        Services.AddScoped(_ => session);
        await State.RefreshAsync(Me, isKids: false);
        await State.UnlockAsync(null);
    }

    private Task UseDesktopAsync() =>
        _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

    private static VaultCatalogueItem Scene(Guid id, string title, bool matched, string folder = "Series A", DateTimeOffset? added = null, string? cover = "/engine-image/x?w=640") =>
        new(id.ToString("D"), title, matched ? "Studio A · 2024" : null, cover, VaultViews.ScenePath(id))
        {
            Shape = VaultCardShape.Scene,
            Unmatched = !matched,
            AssetId = id,
            FileName = title.Replace(' ', '.') + ".mkv",
            Folder = folder,
            FolderId = folder.ToLowerInvariant().Replace(' ', '-'),
            DurationSeconds = 3725,
            SizeBytes = 1_500_000_000,
            AddedAt = added ?? new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero),
            Tags = ["alpha"],
        };

    // -- The main page ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RecentlyAdded_ListsMatchedAndUnmatchedTogether_WithTheUnmatchedMark_AndNoPerformersOrStudios()
    {
        _catalogue.Set(VaultViews.RecentlyAdded, new VaultCatalogueResult(true, [Scene(SceneA, "Matched Scene", true), Scene(SceneB, "Plain File", false)]));
        await UnlockAsync();

        var cut = Render<VaultArea>();

        cut.WaitForAssertion(() => Assert.Contains("Matched Scene", cut.Markup));
        Assert.Contains("Plain File", cut.Markup);
        Assert.Single(cut.FindAll(".vault-cover__mark"));
        Assert.Equal("Unmatched", cut.Find(".vault-cover__mark").TextContent);
        // The main page asks for scenes only: no performer, studio or tag tile is requested for it.
        Assert.All(_catalogue.Calls, call => Assert.Equal(VaultViews.RecentlyAdded, call.View));
        Assert.DoesNotContain(cut.FindAll(".vault-cover--performer, .vault-cover--studio, .vault-list__chip"), _ => true);
    }

    [Fact]
    public async Task TheMatchedAndUnmatchedChips_NarrowTheRequest()
    {
        _catalogue.Set(VaultViews.RecentlyAdded, new VaultCatalogueResult(true, [Scene(SceneA, "Matched Scene", true)]));
        await UnlockAsync();
        var cut = Render<VaultArea>();
        cut.WaitForAssertion(() => Assert.Contains("Matched Scene", cut.Markup));

        var chips = cut.Find("[aria-label='Matched or unmatched']");
        await cut.InvokeAsync(() => chips.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Unmatched").Click());

        cut.WaitForAssertion(() => Assert.Equal("unmatched", _catalogue.Calls[^1].Filter?.Match));
        await cut.InvokeAsync(() => cut.Find("[aria-label='Matched or unmatched']").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Matched").Click());
        cut.WaitForAssertion(() => Assert.Equal("matched", _catalogue.Calls[^1].Filter?.Match));
    }

    [Fact]
    public async Task AnEmptyVault_SaysSo_AndAnUnreachableOneSaysThatInstead()
    {
        _catalogue.Set(VaultViews.RecentlyAdded, new VaultCatalogueResult(true, []));
        await UnlockAsync();
        var empty = Render<VaultArea>();
        empty.WaitForAssertion(() => Assert.Contains("Nothing in Recently added yet", empty.Markup));

        _catalogue.Set("scenes", VaultCatalogueResult.NotYetAvailable);
        var unreachable = Render<VaultArea>(parameters => parameters.Add(p => p.View, "scenes"));
        unreachable.WaitForAssertion(() => Assert.Contains("The Vault could not be read", unreachable.Markup));
    }

    [Fact]
    public async Task ShowMore_AppendsTheNextPage()
    {
        var first = Enumerable.Range(0, 3).Select(i => Scene(Guid.NewGuid(), $"First {i}", true)).ToList();
        _catalogue.Set(VaultViews.RecentlyAdded, new VaultCatalogueResult(true, first) { Total = 5 });
        await UnlockAsync();
        var cut = Render<VaultArea>();
        cut.WaitForAssertion(() => Assert.Contains("First 2", cut.Markup));

        _catalogue.Set(VaultViews.RecentlyAdded, new VaultCatalogueResult(true, [Scene(Guid.NewGuid(), "Second 0", true), Scene(Guid.NewGuid(), "Second 1", true)]) { Total = 5 });
        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Show more").Click());

        cut.WaitForAssertion(() => Assert.Contains("Second 1", cut.Markup));
        Assert.Contains("First 0", cut.Markup);
        Assert.Equal(3, _catalogue.Calls[^1].Filter?.Skip);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Show more");
    }

    // -- The separate lists and their tiles ---------------------------------------------------------------------------------

    [Fact]
    public async Task Performers_AreTheirOwnList_OfPortraitTiles_ThatOpenToTheOwnedScenes()
    {
        _catalogue.Set("performers", new VaultCatalogueResult(true,
        [
            new VaultCatalogueItem(Performer.ToString("D"), "Ana Example", "2 scenes", "/engine-image/p?w=400", VaultViews.TileRoute("performers", Performer.ToString("D")))
            {
                Shape = VaultCardShape.Performer,
                CoverUrl2x = "/engine-image/p?w=800",
                Count = 2,
            },
        ]));
        await UnlockAsync();

        var list = Render<VaultArea>(parameters => parameters.Add(p => p.View, "performers"));

        list.WaitForAssertion(() => Assert.Contains("Ana Example", list.Markup));
        Assert.Contains("vault-cover--performer", list.Markup);
        Assert.Contains($"/vault/performers/{Performer:D}", list.Markup);
        Assert.Contains("2 scenes", list.Markup);

        _catalogue.Set("performers", new VaultCatalogueResult(true, [Scene(SceneA, "Owned Scene", true)]) { Heading = "Ana Example" });
        var tile = Render<VaultArea>(parameters => parameters.Add(p => p.View, "performers").Add(p => p.Id, Performer.ToString("D")));

        tile.WaitForAssertion(() => Assert.Contains("Owned Scene", tile.Markup));
        Assert.Equal(Performer, _catalogue.Calls[^1].Filter?.PerformerId);
        Assert.Contains("Ana Example", tile.Find(".vault-area__heading").TextContent);
        Assert.Contains("href=\"/vault/performers\"", tile.Markup);
    }

    [Fact]
    public async Task StudioTilesOpenToTheirScenes_AndTagsAreChipsWithNoImage()
    {
        var studio = Guid.NewGuid();
        var tag = Guid.NewGuid();
        _catalogue.Set("studios", new VaultCatalogueResult(true,
            [new VaultCatalogueItem(studio.ToString("D"), "Studio A", "1 scene", null, VaultViews.TileRoute("studios", studio.ToString("D"))) { Shape = VaultCardShape.Studio, Count = 1 }]));
        _catalogue.Set("tags", new VaultCatalogueResult(true,
            [new VaultCatalogueItem(tag.ToString("D"), "alpha", "3 scenes", null, VaultViews.TileRoute("tags", tag.ToString("D"))) { Shape = VaultCardShape.Tag, Count = 3 }]));
        await UnlockAsync();

        var studios = Render<VaultArea>(parameters => parameters.Add(p => p.View, "studios"));
        studios.WaitForAssertion(() => Assert.Contains("vault-cover--studio", studios.Markup));
        // A studio with no logo shows its name on the neutral tile.
        Assert.Contains("vault-cover__initials", studios.Markup);
        Assert.Contains($"/vault/studios/{studio:D}", studios.Markup);

        var tags = Render<VaultArea>(parameters => parameters.Add(p => p.View, "tags"));
        tags.WaitForAssertion(() => Assert.Contains("vault-list__chip", tags.Markup));
        Assert.DoesNotContain("<img", tags.Markup);
        Assert.DoesNotContain("vault-cover--", tags.Markup);
    }

    [Fact]
    public async Task Folders_OpenToTheirScenes_ThroughTheFolderId()
    {
        _catalogue.Set("folders", new VaultCatalogueResult(true,
            [new VaultCatalogueItem("abc123", "Series A", "4 scenes", "/engine-image/f?w=640", VaultViews.TileRoute("folders", "abc123")) { Count = 4 }]));
        await UnlockAsync();
        var folders = Render<VaultArea>(parameters => parameters.Add(p => p.View, "folders"));
        folders.WaitForAssertion(() => Assert.Contains("/vault/folders/abc123", folders.Markup));

        _catalogue.Set("folders", new VaultCatalogueResult(true, [Scene(SceneA, "In The Folder", false)]));
        var scenes = Render<VaultArea>(parameters => parameters.Add(p => p.View, "folders").Add(p => p.Id, "abc123"));

        scenes.WaitForAssertion(() => Assert.Contains("In The Folder", scenes.Markup));
        Assert.Equal("abc123", _catalogue.Calls[^1].Filter?.FolderId);
        Assert.Contains("Series A", scenes.Find(".vault-area__heading").TextContent);
    }

    // -- Locked sessions ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALockedSession_DrawsNothing_AndAsksForNothing()
    {
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetNsfwStatusAsync), _ => Task.FromResult(VaultCall<NsfwStatusDto>.Of(VaultOutcome.NotAvailable))));
        Services.AddSingleton(api);
        Services.AddScoped(_ => new DashboardSessionAccessor());
        await State.RefreshAsync(Me, isKids: false);

        var area = Render<VaultArea>();
        var scene = Render<VaultSceneView>(parameters => parameters.Add(p => p.AssetId, SceneA));

        Assert.Equal(string.Empty, area.Markup.Trim());
        Assert.Equal(string.Empty, scene.Markup.Trim());
        Assert.Empty(_catalogue.Calls);
        Assert.Empty(_catalogue.SceneCalls);
    }

    // -- The Unmatched list and Identify -----------------------------------------------------------------------------------

    [Fact]
    public async Task TheUnmatchedList_IsSortable_ByEachColumn()
    {
        _catalogue.Set("unmatched", new VaultCatalogueResult(true,
        [
            Scene(SceneA, "Bravo", false, added: new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)),
            Scene(SceneB, "Alpha", false, added: new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero)),
        ]));
        await UnlockAsync();
        var cut = Render<VaultArea>(parameters => parameters.Add(p => p.View, "unmatched"));
        cut.WaitForAssertion(() => Assert.Contains("Bravo", cut.Markup));

        // Newest first by default.
        Assert.Equal(["Alpha", "Bravo"], cut.FindAll(".vault-unmatched__title").Select(a => a.TextContent.Trim()).ToArray());
        Assert.Contains("1:02:05", cut.Markup);
        Assert.Contains("1.4 GB", cut.Markup);

        await cut.InvokeAsync(() => cut.FindAll(".vault-unmatched__sort").Single(b => b.TextContent.Contains("Title")).Click());
        Assert.Equal(["Alpha", "Bravo"], cut.FindAll(".vault-unmatched__title").Select(a => a.TextContent.Trim()).ToArray());
        await cut.InvokeAsync(() => cut.FindAll(".vault-unmatched__sort").Single(b => b.TextContent.Contains("Title")).Click());
        Assert.Equal(["Bravo", "Alpha"], cut.FindAll(".vault-unmatched__title").Select(a => a.TextContent.Trim()).ToArray());
        Assert.Contains("descending", cut.Markup);
    }

    [Fact]
    public async Task Identify_IsOfferedFromTheUnmatchedList_OnlyToAnAdministratorOnAComputer_AndMountsThePanel()
    {
        _catalogue.Set("unmatched", new VaultCatalogueResult(true, [Scene(SceneA, "Plain File", false)]));
        await UnlockAsync(administrator: true);

        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "phone", true, FinePointer: false, Hover: false));
        var phone = Render<VaultArea>(parameters => parameters.Add(p => p.View, "unmatched"));
        phone.WaitForAssertion(() => Assert.Contains("Plain File", phone.Markup));
        Assert.DoesNotContain("vault-unmatched__identify", phone.Markup);

        await UseDesktopAsync();
        var desktop = Render<VaultArea>(parameters => parameters.Add(p => p.View, "unmatched"));
        desktop.WaitForAssertion(() => Assert.Contains("vault-unmatched__identify", desktop.Markup));
        Assert.DoesNotContain("data-vault-identify", desktop.Markup);

        await desktop.InvokeAsync(() => desktop.Find(".vault-unmatched__identify").Click());

        Assert.Contains("data-vault-identify", desktop.Markup);
    }

    [Fact]
    public async Task Identify_IsNotOfferedToAPersonWhoIsNotAnAdministrator()
    {
        _catalogue.Set("unmatched", new VaultCatalogueResult(true, [Scene(SceneA, "Plain File", false)]));
        await UnlockAsync(administrator: false);
        await UseDesktopAsync();

        var cut = Render<VaultArea>(parameters => parameters.Add(p => p.View, "unmatched"));

        cut.WaitForAssertion(() => Assert.Contains("Plain File", cut.Markup));
        Assert.DoesNotContain("vault-unmatched__identify", cut.Markup);
    }

    // -- A scene's own page -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheScenePage_ShowsWhatIsStored_LinksNamesToTheirScenes_PlaysThroughTheWork_AndOffersIdentifyWhenUnmatched()
    {
        var work = Guid.NewGuid();
        var performer = Guid.NewGuid();
        _catalogue.SetScene(new VaultSceneDetail(
            Scene(SceneA, "Plain File", false),
            work,
            "A stored description.",
            null,
            [new VaultLinkedName(performer, "Ana Example")],
            [],
            []));
        await UnlockAsync(administrator: true);
        await UseDesktopAsync();

        var cut = Render<VaultSceneView>(parameters => parameters.Add(p => p.AssetId, SceneA));

        cut.WaitForAssertion(() => Assert.Contains("A stored description.", cut.Markup));
        Assert.Contains($"/vault/performers/{performer:D}", cut.Markup);
        Assert.Contains($"/watch/player/{work:D}", cut.Markup);
        Assert.Contains("data-vault-identify", cut.Markup);
        Assert.Contains("1:02:05", cut.Markup);
    }

    [Fact]
    public async Task TheScenePage_OfAMatchedVideo_HasNoIdentifyPanel_AndAMissingOneSaysSo()
    {
        _catalogue.SetScene(new VaultSceneDetail(Scene(SceneA, "Matched Scene", true), Guid.NewGuid(), null, "fingerprint", [], [], []));
        await UnlockAsync(administrator: true);
        await UseDesktopAsync();

        var matched = Render<VaultSceneView>(parameters => parameters.Add(p => p.AssetId, SceneA));
        matched.WaitForAssertion(() => Assert.Contains("Matched Scene", matched.Markup));
        Assert.DoesNotContain("data-vault-identify", matched.Markup);

        var missing = Render<VaultSceneView>(parameters => parameters.Add(p => p.AssetId, SceneB));
        missing.WaitForAssertion(() => Assert.Contains("This video isn't here", missing.Markup));
    }

    // -- Cards: shapes, blur, fallbacks, hover stills ---------------------------------------------------------------------

    [Fact]
    public void Cards_FollowTheirShape_AndBlurAppliesToEveryPicture()
    {
        var scene = Render<VaultCover>(p => p.Add(x => x.Title, "A Scene").Add(x => x.CoverUrl, "/c/640").Add(x => x.CoverUrl2x, "/c/1280").Add(x => x.Mode, VaultCoverMode.Blur));
        Assert.Contains("vault-cover--scene", scene.Markup);
        Assert.Contains("vault-cover--blur", scene.Markup);
        Assert.Contains("srcset=\"/c/640 1x, /c/1280 2x\"", scene.Markup);

        var performer = Render<VaultCover>(p => p.Add(x => x.Title, "Ana Example").Add(x => x.Shape, VaultCardShape.Performer).Add(x => x.Mode, VaultCoverMode.Show));
        Assert.Contains("vault-cover--performer", performer.Markup);
        // No portrait: initials, never a broken image.
        Assert.DoesNotContain("<img", performer.Markup);
        Assert.Contains("AE", performer.Find(".vault-cover__initials").TextContent);

        var studio = Render<VaultCover>(p => p.Add(x => x.Title, "Studio A").Add(x => x.Shape, VaultCardShape.Studio).Add(x => x.CoverUrl, "/c/300").Add(x => x.Mode, VaultCoverMode.Blur));
        Assert.Contains("vault-cover--studio", studio.Markup);
        Assert.Contains("src=\"/c/300\"", studio.Markup);

        var hidden = Render<VaultCover>(p => p.Add(x => x.Title, "Studio A").Add(x => x.Shape, VaultCardShape.Studio).Add(x => x.CoverUrl, "/c/300").Add(x => x.Mode, VaultCoverMode.Hide));
        Assert.DoesNotContain("/c/300", hidden.Markup);
        Assert.Equal("SA", hidden.Find(".vault-cover__initials").TextContent);
    }

    [Fact]
    public void HoverStills_CycleOnlyWhenTheCardHasThem_AndAreBlurredLikeTheRest()
    {
        var plain = Render<VaultCover>(p => p.Add(x => x.Title, "A Scene").Add(x => x.CoverUrl, "/c/0"));
        plain.Find("a").MouseEnter();
        Assert.DoesNotContain("vault-cover__still", plain.Markup);

        var cycling = Render<VaultCover>(p => p
            .Add(x => x.Title, "A Scene").Add(x => x.CoverUrl, "/c/0").Add(x => x.Mode, VaultCoverMode.Blur)
            .Add(x => x.HoverUrls, ["/c/1", "/c/2", "/c/3", "/c/4"]));
        Assert.DoesNotContain("vault-cover__still", cycling.Markup);

        cycling.Find("a").MouseEnter();

        // The card's own picture and up to three more: four in all, each a layer of the cycle.
        Assert.Equal(4, cycling.FindAll("img.vault-cover__still").Count);
        Assert.Contains("vault-cover--blur", cycling.Markup);

        var hidden = Render<VaultCover>(p => p
            .Add(x => x.Title, "A Scene").Add(x => x.CoverUrl, "/c/0").Add(x => x.Mode, VaultCoverMode.Hide).Add(x => x.HoverUrls, ["/c/1", "/c/2"]));
        hidden.Find("a").MouseEnter();
        Assert.DoesNotContain("<img", hidden.Markup);
    }

    // -- Layouts and grouping ----------------------------------------------------------------------------------------------

    private static List<VaultCatalogueItem> TwoFolders() =>
    [
        Scene(Guid.NewGuid(), "One", true, "Series A", new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero)),
        Scene(Guid.NewGuid(), "Two", true, "Series B", new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero)),
        Scene(Guid.NewGuid(), "Three", false, "Series A", new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero)),
    ];

    [Fact]
    public void EveryView_CanUseEveryLayout()
    {
        var items = TwoFolders();

        var grid = Render<VaultItemList>(p => p.Add(x => x.Items, items).Add(x => x.Layout, VaultLayout.Grid));
        Assert.Equal(3, grid.FindAll(".vault-cover").Count);
        Assert.Empty(grid.FindAll(".vault-list__heading"));

        var shelves = Render<VaultItemList>(p => p.Add(x => x.Items, items).Add(x => x.Layout, VaultLayout.ShelfRows).Add(x => x.GroupBy, VaultGroupBy.Folder));
        Assert.Equal(["Series A", "Series B"], shelves.FindAll(".vault-list__heading").Select(h => h.TextContent).ToArray());
        Assert.Equal(2, shelves.FindAll(".vault-list__cards--shelf").Count);

        var timeline = Render<VaultItemList>(p => p.Add(x => x.Items, items).Add(x => x.Layout, VaultLayout.Timeline));
        Assert.Equal(["October 2026", "September 2026"], timeline.FindAll(".vault-list__heading").Select(h => h.TextContent).ToArray());

        var details = Render<VaultItemList>(p => p.Add(x => x.Items, items).Add(x => x.Layout, VaultLayout.DetailsList));
        Assert.Equal(3, details.FindAll(".vault-list__row").Count);
        Assert.Contains("Unmatched", details.Markup);
    }

    [Fact]
    public void ShelfRowsGroupByTagToo_AndASingleShelfSimplyWraps()
    {
        var items = TwoFolders();
        var byTag = Render<VaultItemList>(p => p.Add(x => x.Items, items).Add(x => x.Layout, VaultLayout.ShelfRows).Add(x => x.GroupBy, VaultGroupBy.Tag));
        Assert.Equal(["alpha"], byTag.FindAll(".vault-list__heading").Select(h => h.TextContent).ToArray());
        Assert.Empty(byTag.FindAll(".vault-list__cards--shelf"));
        Assert.Single(byTag.FindAll(".vault-list__cards--grid"));
    }

    [Fact]
    public void TitlesCanStayHiddenUntilPointedAt_InEveryLayout()
    {
        var items = TwoFolders();
        foreach (var layout in Enum.GetValues<VaultLayout>())
        {
            var cut = Render<VaultItemList>(p => p.Add(x => x.Items, items).Add(x => x.Layout, layout).Add(x => x.TitlesHidden, true));
            Assert.True(
                cut.Markup.Contains("vault-cover--title-hidden", StringComparison.Ordinal) || cut.Markup.Contains("vault-list__title--hidden", StringComparison.Ordinal),
                layout.ToString());
        }
    }

    // -- The Vault group in Search ------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSearchGroup_DrawsPerformerAndStudioTilesBesideScenes_ButOnlyWhileUnlocked()
    {
        var performer = new VaultCatalogueItem(Performer.ToString("D"), "Ana Example", "1 scene", null, VaultViews.TileRoute("performers", Performer.ToString("D"))) { Shape = VaultCardShape.Performer };
        _catalogue.SetSearch(new VaultCatalogueResult(true, [Scene(SceneA, "Matched Scene", true), performer]));
        await UnlockAsync();

        var cut = Render<VaultSearchGroup>(p => p.Add(x => x.Query, "scene"));

        cut.WaitForAssertion(() => Assert.Contains("Ana Example", cut.Markup));
        Assert.Contains("Matched Scene", cut.Markup);
        Assert.Contains("vault-cover--performer", cut.Markup);

        await cut.InvokeAsync(() => State.LockAsync());
        cut.WaitForAssertion(() => Assert.Equal(string.Empty, cut.Markup.Trim()));
    }

    // -- Small pieces -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Formatting_ReadsInPlainWords()
    {
        Assert.Equal("1:02:05", VaultFormat.Duration(3725));
        Assert.Equal("42:07", VaultFormat.Duration(2527));
        Assert.Equal(string.Empty, VaultFormat.Duration(null));
        Assert.Equal(string.Empty, VaultFormat.Duration(0));
        Assert.Equal("1.4 GB", VaultFormat.Size(1_500_000_000));
        Assert.Equal("850 MB", VaultFormat.Size(850L * 1024 * 1024));
        Assert.Equal(string.Empty, VaultFormat.Size(null));
    }

    [Fact]
    public void VaultArtwork_CanBeFetchedThroughTheDashboardImageRoute_AndNothingElseUnderVaultCan()
    {
        var id = Guid.NewGuid();
        Assert.True(EngineImageProxyPath.IsAllowedEnginePath($"/api/v1/vault/artwork/{id:D}?w=640"));
        Assert.Equal($"/engine-image/api/v1/vault/artwork/{id:D}?w=640", VaultArtworkUrls.Scene(id));
        Assert.EndsWith("?w=1280", VaultArtworkUrls.SceneLarge(id), StringComparison.Ordinal);
        Assert.EndsWith("?w=400", VaultArtworkUrls.Performer(id), StringComparison.Ordinal);
        Assert.EndsWith("?w=800", VaultArtworkUrls.PerformerLarge(id), StringComparison.Ordinal);
        Assert.EndsWith("?w=300", VaultArtworkUrls.Studio(id), StringComparison.Ordinal);
        Assert.False(EngineImageProxyPath.IsAllowedEnginePath("/api/v1/vault/scenes"));
        Assert.False(EngineImageProxyPath.IsAllowedEnginePath($"/api/v1/vault/items/{id:D}/match"));
        Assert.False(EngineImageProxyPath.IsAllowedEnginePath("/api/v1/vault/artwork/not-a-guid"));
    }

    // -- A scripted catalogue ---------------------------------------------------------------------------------------------

    private sealed class ScriptedCatalogue : IVaultCatalogue
    {
        private readonly Dictionary<string, VaultCatalogueResult> _lists = new(StringComparer.Ordinal);
        private VaultSceneDetail? _scene;
        private VaultCatalogueResult _search = VaultCatalogueResult.NotYetAvailable;

        public List<(string View, VaultViewPreferences Preferences, VaultListFilter? Filter)> Calls { get; } = [];

        public List<Guid> SceneCalls { get; } = [];

        public void Set(string view, VaultCatalogueResult result) => _lists[view] = result;

        public void SetScene(VaultSceneDetail scene) => _scene = scene;

        public void SetSearch(VaultCatalogueResult result) => _search = result;

        public Task<VaultCatalogueResult> ListAsync(string viewKey, VaultViewPreferences preferences, VaultListFilter? filter = null, CancellationToken ct = default)
        {
            Calls.Add((viewKey, preferences, filter));
            return Task.FromResult(_lists.TryGetValue(viewKey, out var result) ? result : new VaultCatalogueResult(true, []));
        }

        public Task<VaultCatalogueResult> SearchAsync(string query, CancellationToken ct = default) => Task.FromResult(_search);

        public Task<VaultSceneDetail?> GetSceneAsync(Guid assetId, CancellationToken ct = default)
        {
            SceneCalls.Add(assetId);
            return Task.FromResult(_scene is not null && _scene.Scene.AssetId == assetId ? _scene : null);
        }
    }
}

/// <summary>
/// The Dashboard side of the Vault routes (Videos spec WP14): the right route and parameters per view, pictures asked for at the
/// sizes of D41, a locked session reading "not available", and typed search text never going into an address or a log.
/// </summary>
public sealed class EngineVaultCatalogueTests
{
    private const string Typed = "secret-title-words";

    private sealed record Seen(HttpMethod Method, string PathAndQuery, string Body);

    private static (EngineVaultCatalogue Catalogue, List<Seen> Requests, CapturingLoggerProvider Logs) Create(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var requests = new List<Seen>();
        var logs = new CapturingLoggerProvider();
        var http = new HttpClient(new FuncHandler(request =>
        {
            requests.Add(new Seen(request.Method, request.RequestUri!.PathAndQuery, request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty));
            return answer(request);
        }))
        { BaseAddress = new Uri("http://engine.test") };
        var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var client = new EngineApiClient(http, factory.CreateLogger<EngineApiClient>(), loggerFactory: factory);
        return (new EngineVaultCatalogue(client), requests, logs);
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { title = "Not found.", detail = "Not found.", status = 404 }) };

    private static VaultSceneDto SceneDto(bool matched, Guid? image = null, params Guid[] stills) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = Guid.NewGuid(),
        Title = "A Scene",
        Matched = matched,
        FileName = "a.scene.mkv",
        Studio = matched ? "Studio A" : null,
        ReleaseDate = matched ? "2024-05-06" : null,
        ImageId = image,
        StillIds = stills,
    };

    [Fact]
    public async Task RecentlyAdded_AsksForEveryScene_NewestFirst_AtTheSceneSizes()
    {
        var image = Guid.NewGuid();
        var (catalogue, requests, _) = Create(request => request.RequestUri!.AbsolutePath.StartsWith("/api/v1/vault/images", StringComparison.Ordinal)
            ? Json(new VaultImagePageDto())
            : Json(new VaultSceneListDto { Items = [SceneDto(true, image), SceneDto(false, image)], Total = 2 }));

        var result = await catalogue.ListAsync(VaultViews.RecentlyAdded, new VaultViewPreferences(Sort: VaultSort.Title), new VaultListFilter { Match = "all" });

        // Videos first; the newest images join the same page (Recently added shows both, with chips to narrow it).
        Assert.Equal(2, requests.Count);
        Assert.StartsWith("/api/v1/vault/images?", requests[1].PathAndQuery, StringComparison.Ordinal);
        var request = requests[0];
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.StartsWith("/api/v1/vault/scenes?", request.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("match=all", request.PathAndQuery, StringComparison.Ordinal);
        // Recently added is always newest first, whatever the sort option says.
        Assert.Contains("sort=date_added", request.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("desc=true", request.PathAndQuery, StringComparison.Ordinal);
        Assert.True(result.Supported);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal($"/engine-image/api/v1/vault/artwork/{image:D}?w=640", result.Items[0].CoverUrl);
        Assert.Equal($"/engine-image/api/v1/vault/artwork/{image:D}?w=1280", result.Items[0].CoverUrl2x);
        Assert.Equal("Studio A · 2024", result.Items[0].Subtitle);
        Assert.False(result.Items[0].Unmatched);
        Assert.True(result.Items[1].Unmatched);
        Assert.Null(result.Items[1].Subtitle);
    }

    [Theory]
    [InlineData("scenes", "match=all")]
    [InlineData("unmatched", "match=unmatched")]
    public async Task ScenesAndUnmatched_UseTheScenesRoute_WithTheirMatchFilter(string view, string expected)
    {
        var (catalogue, requests, _) = Create(_ => Json(new VaultSceneListDto()));

        await catalogue.ListAsync(view, new VaultViewPreferences(Sort: VaultSort.Duration), new VaultListFilter { Match = "matched" });

        var request = Assert.Single(requests);
        Assert.StartsWith("/api/v1/vault/scenes?", request.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains(expected, request.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("sort=duration", request.PathAndQuery, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(VaultSort.Title, "sort=title", "desc=false")]
    [InlineData(VaultSort.Year, "sort=release_date", "desc=true")]
    [InlineData(VaultSort.RecentlyPlayed, "sort=date_added", "desc=true")]
    public async Task TheSortOptions_MapToTheEnginesSorts_AndWatchingLeavesNoTraceToSortBy(VaultSort sort, string expectedSort, string expectedDirection)
    {
        var (catalogue, requests, _) = Create(_ => Json(new VaultSceneListDto()));

        await catalogue.ListAsync("scenes", new VaultViewPreferences(Sort: sort));

        Assert.Contains(expectedSort, Assert.Single(requests).PathAndQuery, StringComparison.Ordinal);
        Assert.Contains(expectedDirection, requests[0].PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APerformerStudioTagOrFolderFilter_ListsTheOwnedScenes_AndCarriesTheHeading()
    {
        var performer = Guid.NewGuid();
        var (catalogue, requests, _) = Create(_ => Json(new VaultSceneListDto { Items = [SceneDto(true)], Total = 1, Heading = "Ana Example" }));

        var result = await catalogue.ListAsync("performers", VaultViewPreferences.Default, new VaultListFilter { PerformerId = performer });

        Assert.Contains($"performer={performer:D}", Assert.Single(requests).PathAndQuery, StringComparison.Ordinal);
        Assert.Equal("Ana Example", result.Heading);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task PerformersStudiosAndTags_UseTheirOwnRoutes_AtTheirOwnSizes()
    {
        var portrait = Guid.NewGuid();
        var logo = Guid.NewGuid();
        var (catalogue, requests, _) = Create(request => request.RequestUri!.AbsolutePath.EndsWith("/performers", StringComparison.Ordinal)
            ? Json(new List<VaultNameTileDto> { new() { Id = Guid.NewGuid(), Name = "Ana", SceneCount = 2, ImageId = portrait } })
            : request.RequestUri.AbsolutePath.EndsWith("/studios", StringComparison.Ordinal)
                ? Json(new List<VaultNameTileDto> { new() { Id = Guid.NewGuid(), Name = "Studio A", SceneCount = 1, ImageId = logo } })
                : Json(new List<VaultNameTileDto> { new() { Id = Guid.NewGuid(), Name = "alpha", SceneCount = 3 } }));

        var performers = await catalogue.ListAsync("performers", VaultViewPreferences.Default);
        var studios = await catalogue.ListAsync("studios", VaultViewPreferences.Default);
        var tags = await catalogue.ListAsync("tags", VaultViewPreferences.Default);

        Assert.Equal(["/api/v1/vault/performers", "/api/v1/vault/studios", "/api/v1/vault/tags"], requests.Select(r => r.PathAndQuery.Split('?')[0]).ToArray());
        Assert.Equal(VaultCardShape.Performer, performers.Items[0].Shape);
        Assert.Equal($"/engine-image/api/v1/vault/artwork/{portrait:D}?w=400", performers.Items[0].CoverUrl);
        Assert.Equal($"/engine-image/api/v1/vault/artwork/{portrait:D}?w=800", performers.Items[0].CoverUrl2x);
        Assert.Equal("2 scenes", performers.Items[0].Subtitle);
        Assert.Equal(VaultCardShape.Studio, studios.Items[0].Shape);
        Assert.Equal($"/engine-image/api/v1/vault/artwork/{logo:D}?w=300", studios.Items[0].CoverUrl);
        Assert.Equal("1 scene", studios.Items[0].Subtitle);
        Assert.Equal(VaultCardShape.Tag, tags.Items[0].Shape);
        Assert.Null(tags.Items[0].CoverUrl);
    }

    [Fact]
    public async Task TheSceneViews_NeverAskForPerformersStudiosOrTags()
    {
        var (catalogue, requests, _) = Create(request => request.RequestUri!.AbsolutePath.StartsWith("/api/v1/vault/images", StringComparison.Ordinal)
            ? Json(new VaultImagePageDto())
            : Json(new VaultSceneListDto()));

        foreach (var view in new[] { VaultViews.RecentlyAdded, "scenes", "unmatched" })
        {
            await catalogue.ListAsync(view, VaultViewPreferences.Default);
        }

        // Only the scenes route, and (for Recently added) the images route: never the name lists.
        Assert.All(requests, request =>
            Assert.True(
                request.PathAndQuery.StartsWith("/api/v1/vault/scenes", StringComparison.Ordinal)
                || request.PathAndQuery.StartsWith("/api/v1/vault/images", StringComparison.Ordinal),
                request.PathAndQuery));
    }

    [Fact]
    public async Task HoverStills_AreOfferedOnlyForMatchedScenesWithMoreThanOne_AndOnlyWhenTheOptionIsOn()
    {
        var stills = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var (catalogue, _, _) = Create(_ => Json(new VaultSceneListDto
        {
            Items = [SceneDto(true, stills[0], stills), SceneDto(false, stills[0], stills), SceneDto(true, stills[0], stills[0])],
        }));

        var off = await catalogue.ListAsync("scenes", VaultViewPreferences.Default);
        Assert.All(off.Items, item => Assert.Empty(item.HoverUrls));

        var on = await catalogue.ListAsync("scenes", new VaultViewPreferences(HoverStills: true));
        Assert.Equal(4, on.Items[0].HoverUrls.Count);
        Assert.Empty(on.Items[1].HoverUrls);
        Assert.Empty(on.Items[2].HoverUrls);
    }

    [Fact]
    public async Task ALockedSession_ReadsNotAvailable_ForEveryCall_AndLogsNoWarning()
    {
        var (catalogue, _, logs) = Create(_ => NotFound());

        foreach (var view in new[] { VaultViews.RecentlyAdded, "scenes", "unmatched", "folders", "performers", "studios", "tags" })
        {
            var result = await catalogue.ListAsync(view, VaultViewPreferences.Default);
            Assert.False(result.Supported, view);
            Assert.Empty(result.Items);
        }

        Assert.False((await catalogue.SearchAsync(Typed)).Supported);
        Assert.Null(await catalogue.GetSceneAsync(Guid.NewGuid()));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Images_ListTheGalleriesAsTiles_WithAllImagesFirst_AndAThumbnailAtTheOfferedSizes()
    {
        var cover = Guid.NewGuid();
        var folder = Guid.NewGuid();
        var (catalogue, requests, _) = Create(_ => Json(new List<VaultImageGalleryDto>
        {
            new() { Id = folder, Kind = VaultImageGalleryKinds.Folder, Name = "Trips", ImageCount = 2, CoverImageId = cover },
            new() { Id = Guid.NewGuid(), Kind = VaultImageGalleryKinds.Smart, Name = "Starred", ImageCount = 1, Editable = true },
        }));

        var result = await catalogue.ListAsync("images", VaultViewPreferences.Default);

        Assert.Equal("/api/v1/vault/images/galleries", Assert.Single(requests).PathAndQuery);
        Assert.True(result.Supported);
        Assert.Equal(["All images", "Trips", "Starred"], result.Items.Select(i => i.Title).ToArray());
        Assert.Equal(VaultViews.TileRoute("images", "all"), result.Items[0].Route);
        Assert.Equal(VaultViews.TileRoute("images", folder.ToString("D")), result.Items[1].Route);
        Assert.Equal($"/engine-image/api/v1/vault/images/{cover:D}/thumbnail?w=320", result.Items[1].CoverUrl);
        Assert.Equal($"/engine-image/api/v1/vault/images/{cover:D}/thumbnail?w=640", result.Items[1].CoverUrl2x);
        Assert.Equal("2 images", result.Items[1].Subtitle);
        Assert.Equal("1 image", result.Items[2].Subtitle);
    }

    [Fact]
    public async Task AGalleryTile_ListsItsImages_ByCursor_AsImageCards_InTheOrderTheEngineSentThem()
    {
        var gallery = Guid.NewGuid();
        var first = new VaultImageDto { Id = Guid.NewGuid(), Title = "One", FileName = "one.jpg", AddedAt = DateTimeOffset.UtcNow, CapturedAt = new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero), Favorite = true, Tags = ["sea"] };
        var (catalogue, requests, _) = Create(_ => Json(new VaultImagePageDto { Items = [first], NextCursor = "next-page", HasMore = true }));

        var result = await catalogue.ListAsync(
            "images", VaultViewPreferences.Default, new VaultListFilter { ImageGallery = gallery.ToString("D"), Cursor = "this-page", Take = 50 });

        var request = Assert.Single(requests);
        Assert.StartsWith("/api/v1/vault/images?", request.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains($"gallery={gallery:D}", request.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("cursor=this-page", request.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("take=50", request.PathAndQuery, StringComparison.Ordinal);
        var card = Assert.Single(result.Items);
        Assert.Equal(VaultCardShape.Image, card.Shape);
        Assert.Equal(first.Id, card.ImageId);
        Assert.True(card.Favorite);
        Assert.Equal(first.CapturedAt, card.AddedAt);
        Assert.Equal($"/engine-image/api/v1/vault/images/{first.Id:D}/thumbnail?w=320", card.CoverUrl);
        Assert.Equal("next-page", result.NextCursor);
    }

    [Fact]
    public async Task AllImages_AsksForNoGallery_AndTheImagesChip_ListsTheNewestImagesNotVideos()
    {
        var (catalogue, requests, _) = Create(_ => Json(new VaultImagePageDto()));

        await catalogue.ListAsync("images", VaultViewPreferences.Default, new VaultListFilter { ImageGallery = VaultViews.AllImages });
        await catalogue.ListAsync(VaultViews.RecentlyAdded, VaultViewPreferences.Default, new VaultListFilter { Kind = "images" });

        Assert.All(requests, request => Assert.DoesNotContain("gallery=", request.PathAndQuery, StringComparison.Ordinal));
        Assert.All(requests, request => Assert.StartsWith("/api/v1/vault/images?", request.PathAndQuery, StringComparison.Ordinal));
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task ALockedSession_ReadsTheImagesAsNotAvailable_AndDrawsNothing()
    {
        var (catalogue, _, _) = Create(_ => NotFound());

        var galleries = await catalogue.ListAsync("images", VaultViewPreferences.Default);
        var images = await catalogue.ListAsync("images", VaultViewPreferences.Default, new VaultListFilter { ImageGallery = VaultViews.AllImages });

        Assert.False(galleries.Supported);
        Assert.False(images.Supported);
        Assert.Empty(galleries.Items);
        Assert.Empty(images.Items);
    }

    [Fact]
    public void TheProxy_AllowsOnlyAVaultImageThumbnailAndPreview_ByIdAndNothingElseUnderImages()
    {
        var id = Guid.NewGuid();

        Assert.True(EngineImageProxyPath.IsAllowedEnginePath($"/api/v1/vault/images/{id:D}/thumbnail?w=320"));
        Assert.True(EngineImageProxyPath.IsAllowedEnginePath($"/api/v1/vault/images/{id:D}/preview?w=1280"));
        Assert.False(EngineImageProxyPath.IsAllowedEnginePath($"/api/v1/vault/images/{id:D}"));
        Assert.False(EngineImageProxyPath.IsAllowedEnginePath($"/api/v1/vault/images/{id:D}/delete"));
        Assert.False(EngineImageProxyPath.IsAllowedEnginePath("/api/v1/vault/images/galleries"));
        Assert.False(EngineImageProxyPath.IsAllowedEnginePath("/api/v1/vault/images/not-a-guid/thumbnail"));
        Assert.False(EngineImageProxyPath.IsAllowedEnginePath("/api/v1/vault/images/trash"));
    }

    [Fact]
    public async Task Search_PostsTheTextInTheBody_NeverInTheAddressOrALog_AndReturnsScenesAndNames()
    {
        var (catalogue, requests, logs) = Create(_ => Json(new VaultSearchResultDto
        {
            Scenes = [SceneDto(true)],
            Performers = [new VaultNameTileDto { Id = Guid.NewGuid(), Name = "Ana", SceneCount = 1 }],
            Studios = [new VaultNameTileDto { Id = Guid.NewGuid(), Name = "Studio A", SceneCount = 1 }],
        }));

        var result = await catalogue.SearchAsync(Typed);

        // One search for the videos and names, one for the Vault's images; both carry the words in a body and neither in an address.
        Assert.Equal(["/api/v1/vault/search", "/api/v1/vault/images/search"], requests.Select(r => r.PathAndQuery).ToArray());
        Assert.All(requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains(Typed, request.Body, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(Typed, StringComparison.Ordinal));
        Assert.Equal([VaultCardShape.Scene, VaultCardShape.Performer, VaultCardShape.Studio], result.Items.Select(i => i.Shape).ToArray());
    }

    [Fact]
    public async Task AScenePage_ReadsTheDetail_AndItsNames()
    {
        var performer = Guid.NewGuid();
        var scene = SceneDto(true);
        var (catalogue, requests, _) = Create(_ => Json(new VaultSceneDetailDto
        {
            Scene = scene,
            Description = "A description.",
            MatchedBy = "fingerprint",
            Performers = [new VaultNameLinkDto { Id = performer, Name = "Ana" }],
        }));

        var detail = await catalogue.GetSceneAsync(scene.Id);

        Assert.Equal($"/api/v1/vault/scenes/{scene.Id:D}", Assert.Single(requests).PathAndQuery);
        Assert.NotNull(detail);
        Assert.Equal(scene.WorkId, detail.WorkId);
        Assert.Equal("A description.", detail.Description);
        Assert.Equal(performer, Assert.Single(detail.Performers).Id);
        Assert.Equal(VaultViews.ScenePath(scene.Id), detail.Scene.Route);
    }

    private sealed class FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
