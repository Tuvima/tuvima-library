using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Components.Vault;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Services.Vault;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

// What the Vault's image viewer and gallery tools say after an action: a quiet line or an alert in place, never a pop-up.
public sealed class VaultFeedbackTests : AsyncBunitContext
{
    private static readonly Guid ImageId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

    private VaultCall<bool> _favorite = VaultCall<bool>.Ok(true);
    private VaultCall<VaultImageDetailDto> _tags = VaultCall<VaultImageDetailDto>.Ok(Detail("night"));
    private VaultCall<bool> _delete = VaultCall<bool>.Ok(true);
    private VaultCall<VaultImageScanDto> _scan = VaultCall<VaultImageScanDto>.Ok(new VaultImageScanDto { ImagesAdded = 0 });
    private int _closed;
    private int _changed;

    public VaultFeedbackTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddScoped(_ => new DashboardSessionAccessor());
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped(provider =>
        {
            var device = provider.GetRequiredService<DeviceContextService>();
            return new GalleryEditorLauncherService(new ViewEditorHost(null!, device, new ViewWorkspaceService(null!), NullLogger<ViewEditorHost>.Instance), device, new AppToastService());
        });
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetVaultImageAsync), _ => Task.FromResult(VaultCall<VaultImageDetailDto>.Ok(Detail("night"))));
            stub.SetHandler(nameof(IEngineApiClient.GetVaultImageGalleriesAsync), _ => Task.FromResult(VaultCall<IReadOnlyList<VaultImageGalleryDto>>.Ok([])));
            stub.SetHandler(nameof(IEngineApiClient.SetVaultImageFavoriteAsync), _ => Task.FromResult(_favorite));
            stub.SetHandler(nameof(IEngineApiClient.SetVaultImageTagsAsync), _ => Task.FromResult(_tags));
            stub.SetHandler(nameof(IEngineApiClient.DeleteVaultImageAsync), _ => Task.FromResult(_delete));
            stub.SetHandler(nameof(IEngineApiClient.ScanVaultImagesAsync), _ => Task.FromResult(_scan));
        }));
    }

    private static VaultImageDetailDto Detail(string tag) => new()
    {
        Image = new VaultImageDto { Id = ImageId, Title = "Harbor", FileName = "harbor.jpg" },
        Tags = [new VaultImageTagDto { Id = Guid.NewGuid(), Name = tag }],
    };

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private IRenderedComponent<VaultImageViewer> RenderViewer()
    {
        var cut = Render<VaultImageViewer>(parameters => parameters
            .Add(x => x.Images, [new VaultCatalogueItem("1", "Harbor", null, null, "/vault/images/1") { ImageId = ImageId }])
            .Add(x => x.Index, 0)
            .Add(x => x.IsAdministrator, true)
            .Add(x => x.OnClose, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => _closed++))
            .Add(x => x.OnChanged, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => _changed++)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".vault-viewer__actions")));
        return cut;
    }

    private static Task PressAsync(IRenderedComponent<VaultImageViewer> cut, string label) =>
        cut.InvokeAsync(() => cut.FindAll("button").First(button => Text(button) == label).Click());

    [Fact]
    public async Task WhenTheFavoriteMarkCannotBeSaved_AnAlertSaysSoInPlace_AndTheButtonKeepsItsLabel()
    {
        _favorite = VaultCall<bool>.Of(VaultOutcome.Rejected);
        var cut = RenderViewer();

        await PressAsync(cut, "Favourite");

        Assert.Equal("The favorite mark could not be saved.", Text(cut.Find("[data-vault-viewer-failure]")));
        Assert.Contains(cut.FindAll("button"), button => Text(button) == "Favourite");
        Assert.Empty(Services.GetRequiredService<AppToastService>().Toasts);
    }

    [Fact]
    public async Task SavingTags_ShowsTheQuietSavedLineAndNoAlertOrToast()
    {
        var cut = RenderViewer();

        await PressAsync(cut, "Save tags");

        cut.WaitForAssertion(() => Assert.Contains("Saved", Text(cut.Find("[data-vault-tags-saved]"))));
        Assert.Empty(cut.FindAll("[data-vault-viewer-failure]"));
        Assert.Empty(Services.GetRequiredService<AppToastService>().Toasts);
    }

    [Fact]
    public async Task WhenTheTagsCannotBeSaved_TheEnginesReasonStaysAsAnAlert_AndNothingSaysSaved()
    {
        _tags = VaultCall<VaultImageDetailDto>.Of(VaultOutcome.Rejected, "Tags can't be longer than 40 letters.");
        var cut = RenderViewer();

        await PressAsync(cut, "Save tags");

        Assert.Equal("Tags can't be longer than 40 letters.", Text(cut.Find("[data-vault-viewer-failure]")));
        Assert.DoesNotContain("Saved", Text(cut.Find("[data-vault-tags-saved]")));
        Assert.Empty(Services.GetRequiredService<AppToastService>().Toasts);
    }

    [Fact]
    public async Task DeletingAnImage_ClosesTheViewerAndListsItAsChanged_WithNoToast()
    {
        var cut = RenderViewer();

        await PressAsync(cut, "Delete");

        Assert.Equal(1, _closed);
        Assert.Equal(1, _changed);
        Assert.Empty(Services.GetRequiredService<AppToastService>().Toasts);
    }

    [Fact]
    public async Task WhenTheImageCannotBeDeleted_TheViewerStaysOpenWithAnAlert()
    {
        _delete = VaultCall<bool>.Of(VaultOutcome.Rejected);
        var cut = RenderViewer();

        await PressAsync(cut, "Delete");

        Assert.Equal("The image could not be deleted.", Text(cut.Find("[data-vault-viewer-failure]")));
        Assert.Equal(0, _closed);
        Assert.Equal(0, _changed);
    }

    private IRenderedComponent<VaultGalleryTools> RenderTools() =>
        Render<VaultGalleryTools>(parameters => parameters
            .Add(x => x.IsAdministrator, true)
            .Add(x => x.OnChanged, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => _changed++)));

    [Fact]
    public async Task LookingForNewImages_SaysWhatWasFoundInAQuietLine_AndReadsTheListAgain()
    {
        _scan = VaultCall<VaultImageScanDto>.Ok(new VaultImageScanDto { ImagesAdded = 3 });
        var cut = RenderTools();

        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => Text(button) == "Look for new images").Click());

        cut.WaitForAssertion(() => Assert.Equal("3 new images added.", Text(cut.Find("[data-vault-scan-result]"))));
        Assert.Equal(1, _changed);
        Assert.Empty(cut.FindAll("[data-vault-scan-failure]"));
        Assert.Empty(Services.GetRequiredService<AppToastService>().Toasts);
    }

    [Fact]
    public async Task WhenTheVaultCannotBeScanned_AnAlertSaysSo_AndNothingIsReadAgain()
    {
        _scan = VaultCall<VaultImageScanDto>.Of(VaultOutcome.Rejected);
        var cut = RenderTools();

        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => Text(button) == "Look for new images").Click());

        cut.WaitForAssertion(() => Assert.Equal("The Vault could not be scanned.", Text(cut.Find("[data-vault-scan-failure]"))));
        Assert.Equal(0, _changed);
    }
}
