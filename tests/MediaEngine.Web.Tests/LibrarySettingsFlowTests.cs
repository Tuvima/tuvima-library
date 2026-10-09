using Bunit;
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

public sealed class LibrarySettingsFlowTests : AsyncBunitContext
{
    private readonly LibrariesConfigurationDto _configuration = new()
    {
        StorageLocations = [new() { Id = "media", Label = "Media storage", Path = "C:\\media", AllowWrite = true }],
        Libraries = [new() { Id = "22222222-2222-4222-8222-222222222222", Name = "Audiobooks", Area = "listen", MediaTypes = ["Audiobooks"], Sources = [new() { Id = "source", Path = "C:\\media\\Audiobooks", ManagementMode = "existing_library" }] }],
    };
    private int _saves, _loads, _pathChecks;
    private bool _failSave;
    private UpdateLibrariesRequest? _lastSave;

    public LibrarySettingsFlowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => { _loads++; return Task.FromResult<LibrariesConfigurationDto?>(_configuration); });
            stub.SetHandler(nameof(IEngineApiClient.GetSetupLibrariesAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(_configuration));
            stub.SetHandler(nameof(IEngineApiClient.TestPathAsync), _ => { _pathChecks++; return Task.FromResult<PathTestResultDto?>(new() { Exists = true, HasRead = true, HasWrite = false }); });
            stub.SetHandler(nameof(IEngineApiClient.UpdateLibrariesAsync), args => { _saves++; _lastSave = (UpdateLibrariesRequest)args![0]!; return Task.FromResult<LibrariesConfigurationDto?>(_failSave ? null : _configuration); });
            stub.SetHandler(nameof(IEngineApiClient.MutateLibraryAsync), args =>
            {
                _saves++;
                var mutation = (LibraryMutationRequest)args![0]!;
                _lastSave = new UpdateLibrariesRequest { Libraries = _configuration.Libraries, ViewStorage = mutation.ViewStorage ?? _configuration.ViewStorage };
                return Task.FromResult<LibrariesConfigurationDto?>(_failSave ? null : _configuration);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetServerFolderRootsAsync), _ => Task.FromResult<IReadOnlyList<ServerStorageLocationDto>>(_configuration.StorageLocations));
            stub.SetHandler(nameof(IEngineApiClient.BrowseServerFoldersAsync), _ => Task.FromResult<BrowseServerFoldersResultDto?>(new() { DisplayPath = "C:\\media" }));
            stub.SetHandler(nameof(IEngineApiClient.ValidateServerFolderAsync), _ => Task.FromResult<ServerFolderValidationResultDto?>(new() { Path = "C:\\media", StorageLocationId = "media", Exists = true, HasRead = true, HasWrite = true, CanSelect = true }));
        }));
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Render<AppPopoverHost>();
    }

    [Fact]
    public void ViewFilterNavigatesToItsOwnScope()
    {
        var cut = Render<LibrariesTab>();
        cut.WaitForElement(".libraries-table__row");
        cut.FindAll(".app-segmented-control__item").Single(x => x.TextContent.Trim() == "View").Click();
        Assert.EndsWith("/settings/libraries?scope=view", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
    }

    [Fact]
    public void ScopeChangesReuseLoadedLibrariesAndDoNotProbeAgain()
    {
        var cut = Render<LibrariesTab>();
        cut.WaitForElement(".libraries-table__row");
        cut.WaitForAssertion(() => Assert.Equal(1, _pathChecks));
        foreach (var scope in new[] { "listen", "read", "watch", "view", "all" })
        {
            cut.Render(parameters => parameters.Add(x => x.Scope, scope));
        }

        Assert.Equal(1, _loads);
        Assert.Equal(1, _pathChecks);
        Assert.Contains("/settings/libraries/view", cut.Markup);
    }

    [Fact]
    public void DetailOwnsAddFolderInsideFoldersAndHasNoGlobalSave()
    {
        var cut = Render<LibrariesTab>(parameters => parameters.Add(x => x.Subsection, _configuration.Libraries[0].Id));
        cut.WaitForElement(".library-detail__header");
        Assert.Empty(cut.FindAll(".library-detail__header button"));
        Assert.Single(cut.FindAll(".libraries-card__heading button"), x => x.TextContent.Contains("Add folder"));
        Assert.DoesNotContain("Save changes", cut.Markup);
    }

    [Fact]
    public void ReadOnlySourceIsHealthyAndDetailPublishesItsName()
    {
        var names = new List<string>();
        var cut = Render<LibrariesTab>(parameters => parameters
            .Add(component => component.Subsection, _configuration.Libraries[0].Id)
            .Add(component => component.LibraryNameChanged, name => names.Add(name)));
        cut.WaitForAssertion(() => Assert.Contains("Audiobooks", names));
        Assert.Equal("Audiobooks", cut.Find("h1").TextContent);
        Assert.Contains("Healthy", cut.Find(".library-detail__folder-editor").TextContent);
        Assert.Contains("Read-only", cut.Find(".library-detail__folder-editor").TextContent);
        Assert.Empty(cut.FindAll(".library-detail__tabs"));
        Assert.Equal(0, _saves);
    }

    [Fact]
    public async Task MobileDetailsExposeTheLibraryNameWithEditingDisabled()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync("mobile");
        var libraryId = _configuration.Libraries[0].Id;
        Assert.True(MediaEngine.Web.Models.ViewDTOs.SettingsNav.IsMobileRouteAvailable(MediaEngine.Web.Models.ViewDTOs.SettingsSection.Libraries, libraryId));
        Assert.False(MediaEngine.Web.Models.ViewDTOs.SettingsNav.IsMobileRouteAvailable(MediaEngine.Web.Models.ViewDTOs.SettingsSection.Libraries, "new"));
        var cut = Render<LibrariesTab>(parameters => parameters.Add(component => component.Subsection, libraryId));
        cut.WaitForElement("h1");
        Assert.Equal("Audiobooks", cut.Find("h1").TextContent);
        Assert.All(cut.FindAll("button").Where(button => button.GetAttribute("aria-label")?.StartsWith("Test folder path", StringComparison.Ordinal) != true),
            button => Assert.True(button.HasAttribute("disabled")));
        Assert.All(cut.FindAll("input"), input => Assert.True(input.HasAttribute("disabled") || input.HasAttribute("readonly")));
    }

    [Fact]
    public void OverviewUsesFullWidthLibraryLinksAndUnknownCountsStayUnknown()
    {
        var cut = Render<LibrariesTab>(parameters => parameters.Add(component => component.Scope, "listen"));
        cut.WaitForElement(".libraries-table__row");
        var row = cut.Find(".libraries-table__row");
        Assert.Equal("a", row.LocalName);
        Assert.EndsWith("?scope=listen", row.GetAttribute("href"));
        Assert.Contains("Unavailable", row.TextContent);
        Assert.Equal(cut.Find(".libraries-table__head").Children.Length, row.Children.Length);
    }

    [Fact]
    public void PersonalWizardKeepsThreeStepsAndDoesNotCreateAStructuredLibrary()
    {
        var cut = Render<AddLibraryWizard>();
        cut.WaitForElement(".add-library-wizard__branches");
        Assert.Equal(3, cut.FindAll(".add-library-wizard__steps li").Count);
        cut.FindAll("button").Single(button => button.TextContent.Contains("Personal Media")).Click();
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Next").Click();
        cut.WaitForAssertion(() => Assert.Contains("Choose Personal Media storage", cut.Markup));
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Next").Click();
        cut.WaitForAssertion(() => Assert.Contains("Review Personal Media storage", cut.Markup));
        Assert.Single(_configuration.Libraries);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void PersonalSaveFailureRetainsReviewAndRetryKeepsStructuredLibrariesUnchanged()
    {
        var cut = Render<AddLibraryWizard>();
        cut.WaitForElement(".add-library-wizard__branches");
        cut.FindAll("button").Single(button => button.TextContent.Contains("Personal Media")).Click();
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Next").Click();
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Next").Click();
        _failSave = true;
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save View storage").Click();
        cut.WaitForAssertion(() => Assert.Equal(1, _saves));
        Assert.Contains("Review Personal Media storage", cut.Markup);
        Assert.Single(_lastSave!.Libraries);
        _failSave = false;
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save View storage").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, _saves));
        Assert.Single(_lastSave.Libraries);
        Assert.Single(_configuration.Libraries);
    }

    [Fact]
    public async Task EditingManualPathImmediatelyInvalidatesAnEarlierSelection()
    {
        var cut = Render<AppDialogHost>();
        await cut.InvokeAsync(() => Services.GetRequiredService<IAppDialogService>().ShowAsync<ServerFolderPicker>(""));
        cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Select this folder").HasAttribute("disabled")));
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Enter path manually").Click();
        cut.WaitForElement(".server-folder-picker__manual");
        Assert.True(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Select this folder").HasAttribute("disabled"));
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Validate path").Click();
        cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Select this folder").HasAttribute("disabled")));
        cut.Find(".server-folder-picker__manual input").Input("C:\\media\\changed");
        Assert.True(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Select this folder").HasAttribute("disabled"));
    }
}
