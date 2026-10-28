using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Services.Vault;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// Who may see the Vault in the Libraries screens (Library setup spec D12, D13) and that first-run setup, which draws the same table in
// simple mode, never lists or offers it. A Vault library is created from the Create library dialog (Library pages spec WP9), which
// brings its own tests.
public sealed class LibraryVaultVisibilityTests : AsyncBunitContext
{
    private static readonly Guid Me = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly LibrariesConfigurationDto _configuration = new()
    {
        StorageLocations = [new() { Id = "media", Label = "Media storage", Path = "C:\\media", AllowWrite = true }],
        Libraries = [],
    };
    private readonly IRenderedComponent<AppDialogHost> _dialogs;

    public LibraryVaultVisibilityTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(_configuration));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryOverviewAsync), _ => Task.FromResult<LibraryOverviewDto?>(new()));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryKindsAsync), _ => Task.FromResult<IReadOnlyList<LibraryKindDto>?>(LibraryKindsTestData.BuiltIn()));
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(
                new List<ProfileViewModel> { new(Me, "Test User", "#C9922E", "Administrator", DateTimeOffset.UtcNow) }));
            stub.SetHandler("get_LastError", _ => null);
            stub.SetHandler(nameof(IEngineApiClient.GetServerFolderRootsAsync), _ => Task.FromResult<IReadOnlyList<ServerStorageLocationDto>>(_configuration.StorageLocations));
            stub.SetHandler(nameof(IEngineApiClient.GetNsfwStatusAsync), _ => Task.FromResult(
                VaultCall<NsfwStatusDto>.Ok(new NsfwStatusDto { Mode = "allowed", Unlocked = true, HasPin = true, IdleLockMinutes = 60 })));
        }));
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEngine.Web.Services.Settings.LibraryKindsState>();
        _dialogs = Render<AppDialogHost>();
    }

    private NsfwModeState State => Services.GetRequiredService<NsfwModeState>();

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private async Task SetUpAsync(bool kids = false)
    {
        Services.GetRequiredService<DashboardSessionAccessor>().Set("token", Me, Me, Guid.NewGuid(),
            new DashboardAuthorityResponse(Me, Me, true, true, 1, 1, true, true, null, 1, [], ["settings.administration"], ["household.manage"]));
        await State.RefreshAsync(Me, kids);
    }

    [Fact]
    public async Task AKidsProfileNeverSeesTheVaultEvenIfVaultModeWereOnAndTheyAreAnAdministrator()
    {
        await SetUpAsync();
        var session = Services.GetRequiredService<DashboardSessionAccessor>();

        Assert.True(LibraryVaultAccess.CanShow(State, session, isKids: false));
        Assert.False(LibraryVaultAccess.CanShow(State, session, isKids: true));
    }

    [Fact]
    public async Task TheSharedTableInFirstRunSetupNeverListsOrMentionsTheVaultEvenWithVaultModeOn()
    {
        await SetUpAsync();
        _configuration.Libraries.Add(new() { Id = "11111111-1111-4111-8111-111111111111", Name = "Movies", KindKey = "movies", Area = "watch", Kind = "catalogued", MediaTypes = ["Movies"], Sources = [new() { Id = "m1", Path = "E:\\Movies" }] });
        _configuration.Libraries.Add(new() { Id = "99999999-9999-4999-8999-999999999999", Name = "Private movies", KindKey = "movies", Area = "watch", Kind = "catalogued", MediaTypes = ["Movies"], Access = "vault", Sources = [new() { Id = "v1", Path = "E:\\Vault" }] });

        var settings = Render<LibrariesOverview>(parameters => parameters.Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.Configuration, _configuration).Add(x => x.ShowVault, true));
        var setup = Render<LibrariesOverview>(parameters => parameters.Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.Configuration, _configuration).Add(x => x.ShowVault, true).Add(x => x.Simple, true));

        Assert.Equal(["Movies", "Private movies"], settings.FindAll(".libs__link").Select(Text).ToArray());
        Assert.Equal(["Movies"], setup.FindAll(".libs__link").Select(Text).ToArray());
        Assert.DoesNotContain("vault", setup.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
