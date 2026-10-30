using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// A library's Unidentified files tab (Libraries redesign D6): each file with its folder and reason, and "Change what this folder is" on a file whose
// folder is one of the library's folders.
public sealed class LibraryUnidentifiedTabTests : AsyncBunitContext
{
    private const string TvId = "33333333-3333-4333-8333-333333333333";

    private static readonly LibraryFolderDto Tv = new()
    {
        Id = TvId,
        Name = "TV Shows",
        KindKey = "tv",
        Area = "watch",
        MediaTypes = ["TV"],
        Sources = [new() { Id = "tv-main", Path = "Z:\\TV Shows", ManagementMode = "existing_library", AccessMode = "read_only" }],
    };

    private readonly LibrariesConfigurationDto _configuration = new()
    {
        StorageLocations = [new() { Id = "media", Label = "Media storage", Path = "Z:\\", AllowWrite = false }],
        Libraries = [Tv],
    };

    public LibraryUnidentifiedTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetUnmatchedItemsAsync), _ => Task.FromResult<UnmatchedPageDto?>(new()
            {
                Total = 2,
                Items =
                [
                    new() { AssetId = Guid.NewGuid(), WorkId = Guid.NewGuid(), LibraryId = TvId, FileName = "Episode 12.mkv", Folder = "Z:\\TV Shows\\Drama\\Season 1", Reason = "no_match", LastTriedAt = DateTimeOffset.UtcNow },
                    new() { AssetId = Guid.NewGuid(), LibraryId = TvId, FileName = "Loose.mkv", Folder = "Y:\\Elsewhere", Reason = "no_match", LastTriedAt = DateTimeOffset.UtcNow },
                ],
            }));
        }));
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEngine.Web.Services.Settings.LibraryKindsState>();
        Render<AppDialogHost>();
    }

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    [Fact]
    public void AFileInsideALibraryFolderCanHaveItsFolderChangedAndOneOutsideCannot()
    {
        var cut = Render<LibraryUnidentifiedTab>(parameters => parameters
            .Add(x => x.Library, Tv)
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog)
            .Add(x => x.Configuration, _configuration));
        cut.WaitForElement("[data-asset]");

        var rows = cut.FindAll("[data-asset]");
        Assert.Contains(rows[0].QuerySelectorAll("button"), button => Text(button) == "Change what this folder is");
        Assert.DoesNotContain(rows[1].QuerySelectorAll("button"), button => Text(button) == "Change what this folder is");
    }

    [Fact]
    public async Task ARowWithAnItemOffersRemoveFromLibrary_OnlyToAnAdministratorOnADesktop()
    {
        var cut = Render<LibraryUnidentifiedTab>(parameters => parameters.Add(x => x.Library, Tv));
        cut.WaitForElement("[data-asset]");
        Assert.DoesNotContain(cut.FindAll("button"), button => Text(button) == "Remove from library");

        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var id = Guid.NewGuid();
        Services.GetRequiredService<DashboardSessionAccessor>().Set("token", id, id, Guid.NewGuid(),
            new DashboardAuthorityResponse(id, id, true, true, 1, 1, true, true, null, 1, [], [], []));
        var admin = Render<LibraryUnidentifiedTab>(parameters => parameters.Add(x => x.Library, Tv));
        admin.WaitForElement("[data-asset]");

        var rows = admin.FindAll("[data-asset]");
        Assert.Contains(rows[0].QuerySelectorAll("button"), button => Text(button) == "Remove from library");
        Assert.DoesNotContain(rows[1].QuerySelectorAll("button"), button => Text(button) == "Remove from library");
    }

    [Fact]
    public void WithoutTheLibraryKindsNoRowOffersChangeWhatThisFolderIs()
    {
        var cut = Render<LibraryUnidentifiedTab>(parameters => parameters.Add(x => x.Library, Tv));
        cut.WaitForElement("[data-asset]");

        Assert.DoesNotContain(cut.FindAll("button"), button => Text(button) == "Change what this folder is");
    }
}
