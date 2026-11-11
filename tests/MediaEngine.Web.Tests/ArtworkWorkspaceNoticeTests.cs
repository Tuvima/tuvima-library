using Bunit;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Web.Components.Artwork;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The artwork workspace tells what an action did in one alert above the artwork: a staged change says so, a refusal or a failed
/// upload says why, and it stays until the next action or the close button. There is no pop-up notice.
/// </summary>
public sealed class ArtworkWorkspaceNoticeTests : AsyncBunitContext
{
    private int _uploadCalls;

    public ArtworkWorkspaceNoticeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.UploadCanonicalArtworkAsync), _ =>
            {
                _uploadCalls++;
                return Task.FromResult<ArtworkAssetDto?>(null);
            })));
    }

    [Fact]
    public async Task StagingAnUpload_SaysSoInOneNoticeAboveTheArtwork()
    {
        var cut = RenderWorkspace(deferMutations: true);

        await Upload(cut, "image/jpeg", "cover.jpg");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-artwork-notice]")));
        Assert.Equal(0, _uploadCalls);
    }

    [Fact]
    public async Task ChoosingAFileThatIsNotAPictureOfTheRightKind_ExplainsWhyAndStaysUntilDismissed()
    {
        var cut = RenderWorkspace(deferMutations: true);

        await Upload(cut, "text/plain", "notes.txt");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-artwork-notice]")));
        Assert.Equal(0, _uploadCalls);

        await cut.Find("[data-artwork-notice] .tl-alert-close").ClickAsync();

        Assert.Empty(cut.FindAll("[data-artwork-notice]"));
    }

    [Fact]
    public async Task AnUploadTheEngineRefuses_ShowsAnErrorAlertInPlace()
    {
        var cut = RenderWorkspace(deferMutations: false);

        await Upload(cut, "image/png", "cover.png");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-artwork-notice][role=alert]")));
        Assert.Equal(1, _uploadCalls);
    }

    private IRenderedComponent<ArtworkWorkspace> RenderWorkspace(bool deferMutations)
    {
        var item = new ArtworkLibraryItemDto(
            Guid.Parse("60000000-0000-0000-0000-000000000001"), "Work", "Dune", "Books", "1965", null, null, "CoverArt", ["CoverArt"], 0, 0, false);
        var cut = Render<ArtworkWorkspace>(p => p
            .Add(c => c.Item, item)
            .Add(c => c.DeferMutations, deferMutations)
            .Add(c => c.PersistArtworkChangesImmediately, !deferMutations));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindComponents<InputFile>()));
        return cut;
    }

    private static Task Upload(IRenderedComponent<ArtworkWorkspace> cut, string contentType, string fileName) =>
        cut.InvokeAsync(() => cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], fileName, null, contentType)));
}
