using System.Reflection;
using Bunit;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Rules;
using MediaEngine.Web.Components.Vault;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Services.Vault;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Vault images in the Dashboard (Videos spec WP16, D44): image cards that follow the cover mode, the timeline layout, the viewer with
/// next and previous, tags and favourites, Blur on every picture, Delete for an administrator only, the Images view's galleries and the
/// Vault mode of the Gallery editor. Hand-written spies for the catalogue and the Engine connection.
/// </summary>
public sealed class VaultImageViewTests : AsyncBunitContext
{
    private static readonly Guid Me = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid Third = Guid.Parse("00000000-0000-0000-0000-0000000000b3");

    private readonly ImageCatalogue _catalogue = new();
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<string> _seen = [];
    private readonly List<(Guid Id, bool Favorite)> _favorites = [];
    private readonly List<(Guid Id, string[] Tags)> _tagged = [];
    private readonly List<Guid> _deleted = [];
    private readonly List<VaultImageGalleryRequest> _created = [];
    private readonly List<string> _viewGalleriesCreated = [];

    public VaultImageViewTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddScoped<IVaultCatalogue>(_ => _catalogue);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped(provider => new GalleryEditorLauncherService(
            provider.GetRequiredService<IAppDialogService>(), provider.GetRequiredService<DeviceContextService>(), provider.GetRequiredService<IAppToastService>()));
    }

    private NsfwModeState State => Services.GetRequiredService<NsfwModeState>();

    private async Task UnlockAsync(bool administrator = false)
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetNsfwStatusAsync), _ =>
                Task.FromResult(VaultCall<NsfwStatusDto>.Ok(new NsfwStatusDto { Mode = "allowed", Unlocked = true, HasPin = false, IdleLockMinutes = 15 })));
            stub.SetHandler(nameof(IEngineApiClient.LockNsfwAsync), _ => Task.FromResult(VaultCall<bool>.Ok(true)));
            stub.SetHandler(nameof(IEngineApiClient.GetVaultImageAsync), args =>
            {
                var id = (Guid)args![0]!;
                _seen.Add($"get:{id:D}");
                return Task.FromResult(VaultCall<VaultImageDetailDto>.Ok(new VaultImageDetailDto
                {
                    Image = new VaultImageDto { Id = id, Title = "Detail", FileName = "detail.jpg", Width = 1600, Height = 900, Favorite = false },
                    Tags = [new VaultImageTagDto { Id = Guid.NewGuid(), Name = "sea", ImageCount = 1 }],
                    Folder = "Trips",
                }));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetVaultImageGalleriesAsync), _ =>
                Task.FromResult(VaultCall<IReadOnlyList<VaultImageGalleryDto>>.Ok(
                [
                    new VaultImageGalleryDto { Id = Guid.NewGuid(), Kind = VaultImageGalleryKinds.Manual, Name = "Picks", Editable = true },
                    new VaultImageGalleryDto { Id = Guid.NewGuid(), Kind = VaultImageGalleryKinds.Folder, Name = "Trips" },
                ])));
            stub.SetHandler(nameof(IEngineApiClient.SetVaultImageFavoriteAsync), args =>
            {
                _favorites.Add(((Guid)args![0]!, (bool)args[1]!));
                return Task.FromResult(VaultCall<bool>.Ok(true));
            });
            stub.SetHandler(nameof(IEngineApiClient.SetVaultImageTagsAsync), args =>
            {
                var id = (Guid)args![0]!;
                var tags = ((IReadOnlyList<string>)args[1]!).ToArray();
                _tagged.Add((id, tags));
                return Task.FromResult(VaultCall<VaultImageDetailDto>.Ok(new VaultImageDetailDto
                {
                    Image = new VaultImageDto { Id = id },
                    Tags = tags.Select(name => new VaultImageTagDto { Id = Guid.NewGuid(), Name = name }).ToList(),
                }));
            });
            stub.SetHandler(nameof(IEngineApiClient.DeleteVaultImageAsync), args =>
            {
                _deleted.Add((Guid)args![0]!);
                return Task.FromResult(VaultCall<bool>.Ok(true));
            });
            stub.SetHandler(nameof(IEngineApiClient.CreateVaultImageGalleryAsync), args =>
            {
                var request = (VaultImageGalleryRequest)args![0]!;
                _created.Add(request);
                return Task.FromResult(VaultCall<VaultImageGalleryDto>.Ok(new VaultImageGalleryDto { Id = Guid.NewGuid(), Name = request.Name, Kind = request.Kind }));
            });
            stub.SetHandler(nameof(IEngineApiClient.CreateViewGalleryAsync), _ =>
            {
                _viewGalleriesCreated.Add("view");
                return Task.FromResult<MediaEngine.Contracts.LocalAssets.ViewGalleryDto?>(null);
            });
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

    private static VaultCatalogueItem ImageItem(Guid id, string title, DateTimeOffset? at = null, bool favorite = false) =>
        new(id.ToString("D"), title, null, $"/engine-image/api/v1/vault/images/{id:D}/thumbnail?w=320", VaultViews.TileRoute("images", "all"))
        {
            Shape = VaultCardShape.Image,
            ImageId = id,
            FileName = title.Replace(' ', '-') + ".jpg",
            AddedAt = at ?? new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
            CoverUrl2x = $"/engine-image/api/v1/vault/images/{id:D}/thumbnail?w=640",
            Favorite = favorite,
        };

    private static VaultCatalogueResult Three() => new(true,
    [
        ImageItem(First, "Harbour", new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero)),
        ImageItem(Second, "Lighthouse", new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero)),
        ImageItem(Third, "Dunes", new DateTimeOffset(2026, 7, 2, 12, 0, 0, TimeSpan.Zero)),
    ]);

    // -- Cards: Blur on every thumbnail -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(VaultCoverMode.Blur, "vault-cover--blur", true)]
    [InlineData(VaultCoverMode.Show, "vault-cover--show", true)]
    [InlineData(VaultCoverMode.Hide, "vault-cover--hide", false)]
    public void AnImageCard_FollowsTheCoverMode_AndHideDrawsNoPictureAtAll(VaultCoverMode mode, string expectedClass, bool picture)
    {
        var cut = Render<VaultCover>(p => p
            .Add(c => c.Title, "Harbour").Add(c => c.Shape, VaultCardShape.Image).Add(c => c.Mode, mode)
            .Add(c => c.CoverUrl, $"/engine-image/api/v1/vault/images/{First:D}/thumbnail?w=320"));

        Assert.Contains("vault-cover--image", cut.Markup);
        Assert.Contains(expectedClass, cut.Markup);
        Assert.Equal(picture, cut.FindAll("img").Count == 1);
        Assert.Equal(picture, cut.Markup.Contains(First.ToString("D"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClickingAnImageCard_OpensTheViewerInsteadOfNavigating()
    {
        await UnlockAsync();
        var items = Three().Items;

        var list = Render<VaultItemList>(p => p.Add(c => c.Items, items).Add(c => c.Mode, VaultCoverMode.Show));
        await list.InvokeAsync(() => list.FindAll("a.vault-cover")[1].Click());

        list.WaitForAssertion(() => Assert.NotEmpty(list.FindAll(".vault-viewer")));
        Assert.Contains($"{Second:D}/preview?w=1280", list.Find(".vault-viewer__stage img").GetAttribute("src"));
        Assert.Contains("2 of 3", list.Find(".vault-viewer__count").TextContent);
    }

    // -- Timeline ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheTimelineLayout_GroupsImagesByMonth_NewestFirst_AsTheEngineSentThem()
    {
        var list = Render<VaultItemList>(p => p
            .Add(c => c.Items, Three().Items).Add(c => c.Layout, MediaEngine.Contracts.Vault.VaultLayout.Timeline).Add(c => c.Mode, VaultCoverMode.Show));

        Assert.Equal(["August 2026", "July 2026"], list.FindAll(".vault-list__heading").Select(h => h.TextContent.Trim()).ToArray());
        Assert.Equal(2, list.FindAll(".vault-list__group")[0].QuerySelectorAll("a.vault-cover").Length);
    }

    // -- The viewer -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheViewer_WalksNextAndPrevious_AndStopsAtTheEnds()
    {
        await UnlockAsync();
        var items = Three().Items;
        var index = 0;
        var cut = Render<VaultImageViewer>(p => p
            .Add(c => c.Images, items).Add(c => c.Index, index).Add(c => c.Mode, VaultCoverMode.Show)
            .Add(c => c.IndexChanged, (int next) => { index = next; }));

        Assert.True(cut.FindAll("button").Single(b => b.TextContent.Trim() == "Previous").HasAttribute("disabled"));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Next").Click());
        Assert.Equal(1, index);
        cut.Render(p => p.Add(c => c.Images, items).Add(c => c.Index, 2).Add(c => c.Mode, VaultCoverMode.Show));
        Assert.True(cut.FindAll("button").Single(b => b.TextContent.Trim() == "Next").HasAttribute("disabled"));
        Assert.Contains("3 of 3", cut.Find(".vault-viewer__count").TextContent);
    }

    [Fact]
    public async Task TheViewer_FollowsTheArrowKeys_AndEscapeClosesIt()
    {
        await UnlockAsync();
        var items = Three().Items;
        var index = 1;
        var closed = false;
        var cut = Render<VaultImageViewer>(p => p
            .Add(c => c.Images, items).Add(c => c.Index, index).Add(c => c.Mode, VaultCoverMode.Show)
            .Add(c => c.IndexChanged, (int next) => { index = next; })
            .Add(c => c.OnClose, () => { closed = true; }));

        await cut.Find(".vault-viewer").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight" });
        Assert.Equal(2, index);
        await cut.Find(".vault-viewer").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowLeft" });
        Assert.Equal(0, index);
        await cut.Find(".vault-viewer").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        Assert.True(closed);
    }

    [Fact]
    public async Task TheViewerPicture_BlursUntilPointedAt_AndHideDrawsNoPictureOrAddress()
    {
        await UnlockAsync();
        var items = Three().Items;

        var blur = Render<VaultImageViewer>(p => p.Add(c => c.Images, items).Add(c => c.Index, 0).Add(c => c.Mode, VaultCoverMode.Blur));
        Assert.Contains("vault-viewer__stage--blur", blur.Markup);
        Assert.Single(blur.FindAll(".vault-viewer__stage img"));

        var hidden = Render<VaultImageViewer>(p => p.Add(c => c.Images, items).Add(c => c.Index, 0).Add(c => c.Mode, VaultCoverMode.Hide));
        Assert.Empty(hidden.FindAll(".vault-viewer__stage img"));
        Assert.DoesNotContain(First.ToString("D") + "/preview", hidden.Markup, StringComparison.Ordinal);
        Assert.Contains("Pictures are hidden", hidden.Markup);
    }

    [Fact]
    public async Task TheViewer_SavesTheFavouriteAndTheTags_ByTheImagesId()
    {
        await UnlockAsync();
        var cut = Render<VaultImageViewer>(p => p.Add(c => c.Images, Three().Items).Add(c => c.Index, 0).Add(c => c.Mode, VaultCoverMode.Show));
        cut.WaitForAssertion(() => Assert.Contains("sea", cut.Markup));

        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Favourite").Click());
        Assert.Equal((First, true), Assert.Single(_favorites));

        SetField(cut.Instance, "_tags", "sea,  dunes , ,pier");
        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save tags").Click());
        var saved = Assert.Single(_tagged);
        Assert.Equal(First, saved.Id);
        Assert.Equal(["sea", "dunes", "pier"], saved.Tags);
    }

    [Fact]
    public async Task Delete_IsOfferedToAnAdministratorOnly_AndClosesTheViewer()
    {
        await UnlockAsync(administrator: false);
        var person = Render<VaultImageViewer>(p => p.Add(c => c.Images, Three().Items).Add(c => c.Index, 0).Add(c => c.IsAdministrator, false).Add(c => c.Mode, VaultCoverMode.Show));
        person.WaitForAssertion(() => Assert.Contains("Save tags", person.Markup));
        Assert.DoesNotContain(person.FindAll("button"), b => b.TextContent.Trim() == "Delete");

        var closed = false;
        var changed = false;
        var admin = Render<VaultImageViewer>(p => p
            .Add(c => c.Images, Three().Items).Add(c => c.Index, 0).Add(c => c.IsAdministrator, true).Add(c => c.Mode, VaultCoverMode.Show)
            .Add(c => c.OnClose, () => { closed = true; }).Add(c => c.OnChanged, () => { changed = true; }));
        admin.WaitForAssertion(() => Assert.Contains("Save tags", admin.Markup));
        await admin.InvokeAsync(() => admin.FindAll("button").Single(b => b.TextContent.Trim() == "Delete").Click());

        Assert.Equal(First, Assert.Single(_deleted));
        Assert.True(closed);
        Assert.True(changed);
    }

    [Fact]
    public async Task TheViewer_ClosesTheMomentNsfwModeLocks()
    {
        await UnlockAsync();
        var closed = false;
        var cut = Render<VaultImageViewer>(p => p
            .Add(c => c.Images, Three().Items).Add(c => c.Index, 0).Add(c => c.Mode, VaultCoverMode.Show).Add(c => c.OnClose, () => { closed = true; }));

        await cut.InvokeAsync(() => State.LockAsync());

        cut.WaitForAssertion(() => Assert.True(closed));
    }

    // -- The Images view --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheImagesView_ListsGalleryTiles_AndAnAdministratorCanLookForNewImages()
    {
        _catalogue.Set("images", new VaultCatalogueResult(true,
        [
            new VaultCatalogueItem("all", "All images", "3 images", null, VaultViews.TileRoute("images", "all")),
            new VaultCatalogueItem(Third.ToString("D"), "Trips", "2 images", null, VaultViews.TileRoute("images", Third.ToString("D"))) { Count = 2 },
        ]));
        await UnlockAsync(administrator: true);

        var cut = Render<VaultArea>(p => p.Add(c => c.View, "images"));

        cut.WaitForAssertion(() => Assert.Contains("Trips", cut.Markup));
        Assert.Contains($"/vault/images/{Third:D}", cut.Markup);
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "New gallery");
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Look for new images");
    }

    [Fact]
    public async Task APerson_WhoIsNotAnAdministrator_DoesNotSeeLookForNewImages()
    {
        _catalogue.Set("images", new VaultCatalogueResult(true, [new VaultCatalogueItem("all", "All images", null, null, VaultViews.TileRoute("images", "all"))]));
        await UnlockAsync(administrator: false);

        var cut = Render<VaultArea>(p => p.Add(c => c.View, "images"));

        cut.WaitForAssertion(() => Assert.Contains("All images", cut.Markup));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Look for new images");
    }

    [Fact]
    public async Task AGalleryPage_AsksForThatGallery_AndShowMoreSendsTheCursorTheEngineGave()
    {
        var gallery = Guid.NewGuid();
        _catalogue.Set("images", new VaultCatalogueResult(true, [ImageItem(First, "Harbour")]) { NextCursor = "page-2" });
        await UnlockAsync();
        var cut = Render<VaultArea>(p => p.Add(c => c.View, "images").Add(c => c.Id, gallery.ToString("D")));
        cut.WaitForAssertion(() => Assert.Contains("Harbour", cut.Markup));
        Assert.Equal(gallery.ToString("D"), _catalogue.Calls[^1].Filter?.ImageGallery);

        _catalogue.Set("images", new VaultCatalogueResult(true, [ImageItem(Second, "Lighthouse")]));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Show more").Click());

        cut.WaitForAssertion(() => Assert.Contains("Lighthouse", cut.Markup));
        Assert.Equal("page-2", _catalogue.Calls[^1].Filter?.Cursor);
        Assert.Contains("Harbour", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Show more");
    }

    [Fact]
    public async Task ALockedSession_DrawsNoImagesAndAsksForNone()
    {
        _catalogue.Set("images", Three());
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetNsfwStatusAsync), _ => Task.FromResult(VaultCall<NsfwStatusDto>.Of(VaultOutcome.NotAvailable))));
        Services.AddSingleton(api);
        Services.AddScoped(_ => new DashboardSessionAccessor());
        await State.RefreshAsync(Me, isKids: false);

        var cut = Render<VaultArea>(p => p.Add(c => c.View, "images"));

        Assert.Empty(cut.FindAll("img"));
        Assert.DoesNotContain("Harbour", cut.Markup);
        Assert.Empty(_catalogue.Calls);
    }

    // -- The Gallery editor in its Vault mode -----------------------------------------------------------------------------

    [Fact]
    public async Task TheGalleryEditor_InVaultMode_OffersOnlyWhatVaultImagesHave_AndSavesVaultGalleries()
    {
        await UnlockAsync(administrator: true);
        var cut = Render<GalleryEditorShell>(p => p.Add(c => c.Request, new GalleryEditorLaunchRequest { Vault = true }));

        var tabs = cut.FindAll(".sme-section-nav button").Select(b => b.TextContent.Trim()).ToArray();
        Assert.Equal(["Details", "Membership"], tabs);
        Assert.DoesNotContain("Soundtrack", cut.Markup);

        await cut.InvokeAsync(() => cut.FindAll(".sme-section-nav button").Single(b => b.TextContent.Trim() == "Membership").Click());
        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Smart").Click());
        Assert.Contains("data-rule-domain=\"vault\"", cut.Markup);

        SetField(cut.Instance, "_name", "Picks");
        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Create Gallery").Click());
        Assert.Empty(_created);

        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Manual").Click());
        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Create Gallery").Click());

        var request = Assert.Single(_created);
        Assert.Equal("Picks", request.Name);
        Assert.Equal(VaultImageGalleryKinds.Manual, request.Kind);
        Assert.Empty(_viewGalleriesCreated);
    }

    [Fact]
    public void TheVaultRuleCatalog_HasNoViewOnlyFields_AndEveryFieldIsOneTheEngineAccepts()
    {
        var fields = VaultRuleCatalog.Instance.Fields.Select(f => f.Key).ToArray();

        Assert.Equal("vault", VaultRuleCatalog.Instance.Domain);
        Assert.All(fields, key => Assert.Contains(key, MediaEngine.Domain.PersonalMedia.ViewSmartGalleryRules.VaultFields));
        Assert.DoesNotContain(fields, key => key is "people" or "place" or "device" or "owner" or "source" or "correspondent" or "document_type" or "text_contains");
        Assert.Contains("favorite", fields);
    }

    private static void SetField(object instance, string name, object? value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    /// <summary>Hand-written spy: answers each view with what the test set and remembers every call.</summary>
    private sealed class ImageCatalogue : IVaultCatalogue
    {
        private readonly Dictionary<string, VaultCatalogueResult> _lists = new(StringComparer.Ordinal);

        public List<(string View, VaultViewPreferences Preferences, VaultListFilter? Filter)> Calls { get; } = [];

        public void Set(string view, VaultCatalogueResult result) => _lists[view] = result;

        public Task<VaultCatalogueResult> ListAsync(string viewKey, VaultViewPreferences preferences, VaultListFilter? filter = null, CancellationToken ct = default)
        {
            Calls.Add((viewKey, preferences, filter));
            return Task.FromResult(_lists.TryGetValue(viewKey, out var result) ? result : new VaultCatalogueResult(true, []));
        }

        public Task<VaultCatalogueResult> SearchAsync(string query, CancellationToken ct = default) => Task.FromResult(VaultCatalogueResult.NotYetAvailable);

        public Task<VaultSceneDetail?> GetSceneAsync(Guid assetId, CancellationToken ct = default) => Task.FromResult<VaultSceneDetail?>(null);
    }
}
