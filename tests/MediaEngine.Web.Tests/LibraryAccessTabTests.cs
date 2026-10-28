using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Vault;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// A library's Access tab (Library pages WP6, D9, D11): Standard and Selected profiles, Vault-protected shown but never switched here, the
// profiles to tick, and the Save changes / Discard bar that keeps edits until they are saved.
public sealed class LibraryAccessTabTests : AsyncBunitContext
{
    private const string LibraryId = "11111111-1111-4111-8111-111111111111";
    private static readonly Guid Sam = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid Kit = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000002");
    private static readonly Guid Admin = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000003");

    private readonly List<LibraryMutationRequest> _mutations = [];
    private readonly List<IReadOnlyList<Guid>> _grantSaves = [];
    private LibraryAccessGrantsDto? _access;
    private bool _mutationSucceeds = true;
    private int _changed;

    public LibraryAccessTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        _access = Access("standard");
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler("get_LastError", _ => null);
            stub.SetHandler(nameof(IEngineApiClient.GetNsfwStatusAsync), _ => Task.FromResult(
                VaultCall<NsfwStatusDto>.Ok(new NsfwStatusDto { Mode = "allowed", Unlocked = true, HasPin = true, IdleLockMinutes = 60 })));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryAccessAsync), _ => Task.FromResult(_access));
            stub.SetHandler(nameof(IEngineApiClient.MutateLibraryAsync), args =>
            {
                _mutations.Add((LibraryMutationRequest)args![0]!);
                return Task.FromResult<LibrariesConfigurationDto?>(_mutationSucceeds ? new LibrariesConfigurationDto() : null);
            });
            stub.SetHandler(nameof(IEngineApiClient.SetLibraryProfilesAsync), args =>
            {
                var ids = (IReadOnlyList<Guid>)args![1]!;
                _grantSaves.Add(ids);
                return Task.FromResult<LibraryAccessGrantsDto?>(Access("selected", [.. ids]));
            });
        }));
    }

    // An administrator signs in with Vault mode on unless told otherwise; the Vault choice is drawn only then.
    private void SignInAsAdministrator(bool vaultMode = true)
    {
        var profile = Guid.NewGuid();
        Services.GetRequiredService<DashboardSessionAccessor>().Set("token", profile, profile, Guid.NewGuid(),
            new DashboardAuthorityResponse(Guid.NewGuid(), profile, true, true, 1, 1, true, true, null, 1, [], ["settings.administration"], ["household.manage"]));
        if (vaultMode)
        {
            Services.GetRequiredService<MediaEngine.Web.Services.Vault.NsfwModeState>().RefreshAsync(profile, isKids: false).GetAwaiter().GetResult();
        }
    }

    private static LibraryAccessGrantsDto Access(string access, params Guid[] chosen) => new(
        Guid.Parse(LibraryId),
        access,
        chosen,
    [
        new(Admin, "Alex", "#3366FF", null, null, false, true),
        new(Sam, "Sam", "#FF6633", null, null, false, false),
        new(Kit, "Kit", "#33CC66", null, null, true, false),
    ]);

    private static LibraryFolderDto Library(string access = "standard") => new()
    {
        Id = LibraryId,
        Name = "Movies",
        KindKey = "movies",
        Area = "watch",
        MediaTypes = ["Movies"],
        Access = access,
        Sources = [new() { Id = "movies-main", Path = "E:\\Movies", ManagementMode = "existing_library", AccessMode = "read_only" }],
    };

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private IRenderedComponent<LibraryAccessTab> Open(string access = "standard", bool readOnly = false)
    {
        var cut = Render<LibraryAccessTab>(parameters => parameters
            .Add(x => x.Library, Library(access))
            .Add(x => x.ReadOnly, readOnly)
            .Add(x => x.Changed, EventCallback.Factory.Create(this, () => _changed++)));
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading access", cut.Markup));
        return cut;
    }

    private static IReadOnlyList<AngleSharp.Dom.IElement> Choices(IRenderedComponent<LibraryAccessTab> cut) =>
        [.. cut.Find("[aria-label='Access'] [role=group], [role=group][aria-label='Access']").QuerySelectorAll("button")];

    private static AngleSharp.Dom.IElement Choice(IRenderedComponent<LibraryAccessTab> cut, string title) =>
        Choices(cut).Single(button => button.QuerySelector("strong")!.TextContent == title);

    private static AngleSharp.Dom.IElement ProfileRow(IRenderedComponent<LibraryAccessTab> cut, Guid id) =>
        cut.Find($"[data-profile='{id}']");

    private static AngleSharp.Dom.IElement Tick(IRenderedComponent<LibraryAccessTab> cut, Guid id) =>
        ProfileRow(cut, id).QuerySelector("input[type=checkbox]")!;

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<LibraryAccessTab> cut, string label) =>
        cut.FindAll("button").Single(button => Text(button) == label);

    [Fact]
    public void AnAdministratorSeesThreeChoices_StandardSelected_SelectedAndVaultChosenOnlyWhenALibraryIsMade()
    {
        SignInAsAdministrator();
        var cut = Open();

        Assert.Equal(["Standard", "Selected profiles", "Vault-protected"], Choices(cut).Select(choice => choice.QuerySelector("strong")!.TextContent).ToArray());
        Assert.True(Choice(cut, "Standard").ClassList.Contains("is-selected"));
        Assert.False(Choice(cut, "Selected profiles").HasAttribute("disabled"));
        Assert.True(Choice(cut, "Vault-protected").HasAttribute("disabled"));
        Assert.Contains("Chosen when a library is made, and can't be changed later.", Text(Choice(cut, "Vault-protected")), StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll(".library-access__heading .app-info-tip"));
        Assert.Empty(cut.FindAll("[data-profile]"));
        Assert.Empty(cut.FindAll(".app-save-bar"));
    }

    [Fact]
    public void AnAdministratorWhoseVaultModeIsOffSeesTwoChoicesAndNoVaultWordAnywhereOnTheTab()
    {
        SignInAsAdministrator(vaultMode: false);
        var cut = Open();

        Assert.Equal(["Standard", "Selected profiles"], Choices(cut).Select(choice => choice.QuerySelector("strong")!.TextContent).ToArray());
        Assert.DoesNotContain("Vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AVaultProtectedLibraryRendersNothingForAnAdministratorWhoseVaultModeIsOff()
    {
        SignInAsAdministrator(vaultMode: false);
        var cut = Render<LibraryAccessTab>(parameters => parameters.Add(x => x.Library, Library("vault")));

        Assert.DoesNotContain("Vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public void ANonAdministratorSeesTwoChoicesAndNoVaultWordAnywhereOnTheTab()
    {
        var cut = Open();

        Assert.Equal(["Standard", "Selected profiles"], Choices(cut).Select(choice => choice.QuerySelector("strong")!.TextContent).ToArray());
        Assert.DoesNotContain("Vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChoosingSelectedProfilesStartsWithNobodyTickedAWarningAndAnUnsavedChangesBar()
    {
        var cut = Open();

        Choice(cut, "Selected profiles").Click();

        Assert.Contains("Nobody but administrators will see this library until you tick at least one profile.", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(["Alex", "Sam", "Kit"], cut.FindAll("[data-profile] .app-checkbox-row__label").Select(Text).ToArray());
        Assert.False(Tick(cut, Sam).HasAttribute("checked"));
        Assert.False(Tick(cut, Kit).HasAttribute("checked"));
        Assert.Contains("You have unsaved changes", Text(cut.Find(".app-save-bar")), StringComparison.Ordinal);
        Assert.Empty(_mutations);
    }

    [Fact]
    public void KidsProfilesAreMarked_AdministratorsAreAlwaysIncludedAndCannotBeUnticked()
    {
        var cut = Open();
        Choice(cut, "Selected profiles").Click();

        Assert.Contains("Kids profile", Text(ProfileRow(cut, Kit)), StringComparison.Ordinal);
        Assert.DoesNotContain("Kids profile", Text(ProfileRow(cut, Sam)), StringComparison.Ordinal);
        Assert.Contains("Administrators always see every library", Text(ProfileRow(cut, Admin)), StringComparison.Ordinal);
        Assert.True(Tick(cut, Admin).HasAttribute("disabled"));
        Assert.True(Tick(cut, Admin).HasAttribute("checked"));
        Assert.NotNull(ProfileRow(cut, Sam).QuerySelector("svg.profile-avatar"));
    }

    [Fact]
    public void TickingAProfileClearsTheWarning_AndSavingSavesTheChoiceAndThenTheProfiles()
    {
        var cut = Open();
        Choice(cut, "Selected profiles").Click();

        Tick(cut, Sam).Change(true);
        Assert.DoesNotContain("Nobody but administrators", cut.Markup, StringComparison.Ordinal);
        Button(cut, "Save changes").Click();

        cut.WaitForAssertion(() => Assert.Single(_grantSaves));
        var request = Assert.Single(_mutations);
        Assert.Equal("selected", request.Library!.Access);
        Assert.Equal("standard", request.ExpectedLibrary!.Access);
        Assert.Equal(LibraryId, request.Library.Id);
        Assert.Equal([Sam], _grantSaves[0]);
        cut.WaitForAssertion(() => Assert.Equal(1, _changed));
    }

    [Fact]
    public void DiscardPutsTheSavedChoiceBackAndTheBarGoesAway()
    {
        var cut = Open();
        Choice(cut, "Selected profiles").Click();
        Tick(cut, Sam).Change(true);

        Button(cut, "Discard").Click();

        Assert.True(Choice(cut, "Standard").ClassList.Contains("is-selected"));
        Assert.Empty(cut.FindAll("[data-profile]"));
        Assert.Empty(cut.FindAll(".app-save-bar"));
        Assert.Empty(_mutations);
        Assert.Empty(_grantSaves);
    }

    [Fact]
    public void ASelectedLibraryShowsWhoIsTicked_AndChangingTheTicksSavesOnlyTheProfiles()
    {
        _access = Access("selected", Kit);
        var cut = Open("selected");

        Assert.True(Choice(cut, "Selected profiles").ClassList.Contains("is-selected"));
        Assert.True(Tick(cut, Kit).HasAttribute("checked"));
        Assert.False(Tick(cut, Sam).HasAttribute("checked"));
        Assert.Empty(cut.FindAll(".app-save-bar"));

        Tick(cut, Sam).Change(true);
        Tick(cut, Kit).Change(false);
        Button(cut, "Save changes").Click();

        cut.WaitForAssertion(() => Assert.Single(_grantSaves));
        Assert.Equal([Sam], _grantSaves[0]);
        Assert.Empty(_mutations);
    }

    [Fact]
    public void SwitchingASelectedLibraryBackToStandardSavesOnlyTheChoice()
    {
        _access = Access("selected", Sam);
        var cut = Open("selected");

        Choice(cut, "Standard").Click();
        Button(cut, "Save changes").Click();

        cut.WaitForAssertion(() => Assert.Single(_mutations));
        Assert.Equal("standard", _mutations[0].Library!.Access);
        Assert.Equal("selected", _mutations[0].ExpectedLibrary!.Access);
        Assert.Empty(_grantSaves);
    }

    [Fact]
    public void WhenTheChoiceCannotBeSavedNoProfilesAreSavedAndTheEditsStay()
    {
        _mutationSucceeds = false;
        var cut = Open();
        Choice(cut, "Selected profiles").Click();
        Tick(cut, Sam).Change(true);

        Button(cut, "Save changes").Click();

        cut.WaitForAssertion(() => Assert.Single(_mutations));
        Assert.Empty(_grantSaves);
        Assert.Equal(0, _changed);
        Assert.NotEmpty(cut.FindAll(".app-save-bar"));
    }

    [Fact]
    public void OnAPhoneTheChoiceIsShownButNothingCanBeChanged()
    {
        _access = Access("selected", Sam);
        var cut = Open("selected", readOnly: true);

        Assert.All(Choices(cut), choice => Assert.True(choice.HasAttribute("disabled")));
        Assert.True(Choice(cut, "Selected profiles").ClassList.Contains("is-selected"));
        Assert.True(Tick(cut, Sam).HasAttribute("disabled"));
        Assert.Empty(cut.FindAll(".app-save-bar"));
    }

    [Fact]
    public void AVaultProtectedLibraryIsShownReadOnlyToAnAdministratorAndAsksTheEngineForNothing()
    {
        SignInAsAdministrator();
        var cut = Render<LibraryAccessTab>(parameters => parameters.Add(x => x.Library, Library("vault")));

        Assert.True(Choice(cut, "Vault-protected").ClassList.Contains("is-selected"));
        Assert.All(Choices(cut), choice => Assert.True(choice.HasAttribute("disabled")));
        Assert.Contains("can't be switched on or off later", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".app-save-bar"));
        Assert.DoesNotContain(nameof(IEngineApiClient.GetLibraryAccessAsync), ((EngineApiClientStub)(object)Services.GetRequiredService<IEngineApiClient>()).Calls);
    }

    [Fact]
    public void AVaultProtectedLibraryRendersNothingForSomeoneWhoIsNotAnAdministrator()
    {
        var cut = Render<LibraryAccessTab>(parameters => parameters.Add(x => x.Library, Library("vault")));

        Assert.DoesNotContain("Vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("button"));
        Assert.DoesNotContain(nameof(IEngineApiClient.GetLibraryAccessAsync), ((EngineApiClientStub)(object)Services.GetRequiredService<IEngineApiClient>()).Calls);
    }

    [Fact]
    public void WhenTheAccessCannotBeLoadedTheTabSaysSoAndOffersTryAgain()
    {
        _access = null;
        var cut = Open();

        Assert.Contains("Access could not be loaded", cut.Markup, StringComparison.Ordinal);
        _access = Access("standard");
        cut.FindAll("button").Single(button => Text(button) == "Try again").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(Choices(cut)));
    }

    [Fact]
    public void TheSaveBarAppearsOnlyWithChanges_AndItsButtonsRaiseTheirCallbacks()
    {
        var saved = 0;
        var discarded = 0;
        var cut = Render<AppSaveBar>(parameters => parameters
            .Add(x => x.OnSave, EventCallback.Factory.Create(this, () => saved++))
            .Add(x => x.OnDiscard, EventCallback.Factory.Create(this, () => discarded++)));
        Assert.Empty(cut.FindAll(".app-save-bar"));

        cut.Render(parameters => parameters.Add(x => x.HasChanges, true)
            .Add(x => x.OnSave, EventCallback.Factory.Create(this, () => saved++))
            .Add(x => x.OnDiscard, EventCallback.Factory.Create(this, () => discarded++)));
        cut.FindAll("button").Single(button => Text(button) == "Save changes").Click();
        cut.FindAll("button").Single(button => Text(button) == "Discard").Click();

        Assert.Equal((1, 1), (saved, discarded));
        Assert.Equal("You have unsaved changes", Text(cut.Find(".app-save-bar__message")));
    }

    [Fact]
    public void WhileSavingBothButtonsWait_AndSaveIsOffWhenTheEditsCannotBeSavedYet()
    {
        var cut = Render<AppSaveBar>(parameters => parameters.Add(x => x.HasChanges, true).Add(x => x.CanSave, false));
        Assert.True(cut.FindAll("button").Single(button => Text(button) == "Save changes").HasAttribute("disabled"));
        Assert.False(cut.FindAll("button").Single(button => Text(button) == "Discard").HasAttribute("disabled"));

        cut.Render(parameters => parameters.Add(x => x.HasChanges, true).Add(x => x.Saving, true));
        Assert.All(cut.FindAll(".app-save-bar button"), button => Assert.True(button.HasAttribute("disabled")));
    }
}
