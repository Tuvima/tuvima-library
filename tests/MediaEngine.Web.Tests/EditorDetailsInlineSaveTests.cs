using System.Reflection;
using Bunit;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.MediaEditor;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Services;

namespace MediaEngine.Web.Tests;

public sealed class EditorDetailsInlineSaveTests : AsyncBunitContext
{
    private static readonly Guid ShowId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EpisodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public EditorDetailsInlineSaveTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task EpisodeTitleOverrideUsesEpisodeScopeAndRevertRestoresCanonicalValue()
    {
        var calls = new List<(Guid EntityId, Dictionary<string, string> Fields)>();
        var saveResults = new Queue<bool>([true, true]);
        var api = CreateApi((entityId, fields) =>
        {
            calls.Add((entityId, fields));
            return saveResults.Dequeue();
        });
        RegisterShellServices(api);
        Render<MudBlazor.MudPopoverProvider>();
        var cut = Render<DetailsSaveShell>(parameters => parameters.Add(shell => shell.Request, Request()));
        var shell = cut.Instance;
        shell.SetPendingMatchDraft();

        await shell.BeginTitleEditAsync();
        await shell.UpdateInlineValueAsync("Revised episode title");
        await shell.SaveTitleAsync();

        var save = Assert.Single(calls);
        Assert.Equal(EpisodeId, save.EntityId);
        Assert.Equal(new Dictionary<string, string> { ["title"] = "Revised episode title" }, save.Fields);
        Assert.Equal("The original episode", shell.CanonicalEpisodeTitle);
        Assert.True(shell.HasMatchDraftForTest);

        var overrideField = shell.TitleField;
        Assert.True(overrideField.CanRevert);
        await shell.RevertTitleAsync(overrideField);

        Assert.Equal(2, calls.Count);
        Assert.Equal(EpisodeId, calls[1].EntityId);
        Assert.Equal(new Dictionary<string, string> { ["title"] = string.Empty }, calls[1].Fields);
        Assert.DoesNotContain("title", shell.ActiveFieldOverrides.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("The original episode", shell.CanonicalEpisodeTitle);
        Assert.True(shell.HasMatchDraftForTest);
    }

    [Fact]
    public async Task FailedOverrideSaveKeepsTheInlineDraftAndError()
    {
        var api = CreateApi((_, _) => false);
        RegisterShellServices(api);
        Render<MudBlazor.MudPopoverProvider>();
        var cut = Render<DetailsSaveShell>(parameters => parameters.Add(shell => shell.Request, Request()));
        var shell = cut.Instance;

        await shell.BeginTitleEditAsync();
        await shell.UpdateInlineValueAsync("Keep this draft");
        await shell.SaveTitleAsync();

        Assert.True(shell.HasInlineDraftForTest);
        Assert.Equal("Keep this draft", shell.InlineDraftForTest);
        Assert.Equal("This field could not be saved.", shell.InlineErrorForTest);
    }

    [Fact]
    public async Task TagsSaveToSharedLibraryOverridesEvenWhenAProfileIsActive()
    {
        var calls = new List<(Guid EntityId, Dictionary<string, string> Fields)>();
        var profileReads = 0;
        var profileWrites = 0;
        var api = CreateApi((entityId, fields) =>
        {
            calls.Add((entityId, fields));
            return true;
        },
        onProfilePreferenceRead: () => profileReads++,
        onProfilePreferenceWrite: () => profileWrites++);
        RegisterShellServices(api);
        Render<MudBlazor.MudPopoverProvider>();
        var cut = Render<DetailsSaveShell>(parameters => parameters
            .Add(shell => shell.Request, Request(Guid.Parse("33333333-3333-3333-3333-333333333333"))));
        var shell = cut.Instance;

        await shell.BeginTagsEditAsync();
        await shell.UpdateInlineValueAsync(" quiet, thoughtful ; quiet ");
        await shell.SaveTagsAsync();

        var save = Assert.Single(calls);
        Assert.Equal(EpisodeId, save.EntityId);
        Assert.Equal(new Dictionary<string, string> { ["custom_tags"] = "quiet; thoughtful" }, save.Fields);
        Assert.Equal(0, profileReads);
        Assert.Equal(0, profileWrites);
        Assert.Equal("quiet; thoughtful", shell.ActiveFieldOverrides["custom_tags"]);
    }

    [Fact]
    public async Task DirtyInlineDraftKeepsTheCurrentScopeUntilUserChoosesHowToSwitch()
    {
        var api = CreateApi((_, _) => true);
        RegisterShellServices(api);
        Render<MudBlazor.MudPopoverProvider>();
        var cut = Render<DetailsSaveShell>(parameters => parameters.Add(shell => shell.Request, Request()));
        var shell = cut.Instance;

        await shell.BeginTitleEditAsync();
        await shell.UpdateInlineValueAsync("Unsaved episode title");
        Assert.False(shell.IsTabDisabledForTest("details"));
        Assert.True(shell.IsTabDisabledForTest("artwork"));
        await shell.SelectArtworkTabAsync();
        Assert.Equal(0, shell.ActiveTabIndexForTest);
        await shell.SelectScopeForTest("series");

        Assert.Equal("episode", shell.ActiveScopeIdForTest);
        Assert.True(shell.HasPendingTargetSwitchForTest);
        Assert.True(shell.HasInlineDraftForTest);
    }

    [Fact]
    public async Task DialogEscapeIsDisabledForAnActiveInlineDraftAndRestoredAfterItEnds()
    {
        var api = CreateApi((_, _) => true);
        RegisterShellServices(api);
        Render<MudBlazor.MudPopoverProvider>();
        var cut = Render<DetailsSaveShell>(parameters => parameters.Add(shell => shell.Request, Request()));
        var shell = cut.Instance;
        var dialog = DispatchProxy.Create<IMudDialogInstance, MudDialogOptionsProxy>();
        var proxy = (MudDialogOptionsProxy)(object)dialog;
        proxy.CurrentOptions = new DialogOptions { CloseOnEscapeKey = true };
        shell.SetDialogForTest(dialog);

        await shell.BeginTitleEditAsync();
        await shell.SynchronizeDialogEscapeForTestAsync();
        Assert.False(proxy.CurrentOptions.CloseOnEscapeKey);

        await shell.CancelDetailsInlineEditForTestAsync();
        await shell.SynchronizeDialogEscapeForTestAsync();
        Assert.True(proxy.CurrentOptions.CloseOnEscapeKey);
    }

    [Fact]
    public async Task DetailsLinkClosesTheEditorBeforeOpeningTheDestination()
    {
        RegisterShellServices(CreateApi((_, _) => true));
        Render<MudPopoverProvider>();
        var closed = false;
        var cut = Render<DetailsSaveShell>(parameters => parameters
            .Add(shell => shell.Request, Request())
            .Add(shell => shell.Inline, true)
            .Add(shell => shell.Closed, _ => { closed = true; }));
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        await cut.Instance.OpenDetailsLinkForTestAsync("/details/person/33333333-3333-3333-3333-333333333333");

        Assert.True(closed);
        Assert.EndsWith("/details/person/33333333-3333-3333-3333-333333333333", navigation.Uri);
    }

    [Fact]
    public async Task DetailsLinkPreservesTheDraftUntilNavigationIsConfirmed()
    {
        RegisterShellServices(CreateApi((_, _) => true));
        Render<MudPopoverProvider>();
        var closed = false;
        var cut = Render<DetailsSaveShell>(parameters => parameters
            .Add(shell => shell.Request, Request())
            .Add(shell => shell.Inline, true)
            .Add(shell => shell.Closed, _ => { closed = true; }));
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var originalLocation = navigation.Uri;
        await cut.Instance.BeginTitleEditAsync();
        await cut.Instance.UpdateInlineValueAsync("Keep the draft");

        await cut.Instance.OpenDetailsLinkForTestAsync("/search?q=Tokyo%20MX");

        Assert.False(closed);
        Assert.Equal(originalLocation, navigation.Uri);
        Assert.Equal("Keep the draft", cut.Instance.InlineDraftForTest);
        await cut.Instance.DiscardNavigationForTestAsync();
        Assert.True(closed);
        Assert.EndsWith("/search?q=Tokyo%20MX", navigation.Uri);
        Assert.False(cut.Instance.HasInlineDraftForTest);
    }

    private IEngineApiClient CreateApi(
        Func<Guid, Dictionary<string, string>, bool> save,
        Action? onProfilePreferenceRead = null,
        Action? onProfilePreferenceWrite = null)
    {
        return EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.SaveItemDisplayOverridesAsync), args =>
            {
                var entityId = (Guid)args![0]!;
                var fields = (Dictionary<string, string>)args[1]!;
                return Task.FromResult(save(entityId, fields));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetItemHistoryWithStatusAsync), _ =>
                Task.FromResult((new List<MediaEngine.Web.Models.ViewDTOs.LibraryItemHistoryDto>(), (string?)null)));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorPreferencesAsync), _ =>
            {
                onProfilePreferenceRead?.Invoke();
                return Task.FromResult<MediaEngine.Contracts.Items.ItemEditorPreferencesResponse?>(null);
            });
            stub.SetHandler(nameof(IEngineApiClient.SaveItemEditorPreferencesAsync), _ =>
            {
                onProfilePreferenceWrite?.Invoke();
                return Task.FromResult(new MediaEngine.Web.Models.ViewDTOs.ItemEditorPreferencesSaveResultDto(false, false, null, null));
            });
        });
    }

    private void RegisterShellServices(IEngineApiClient api)
    {
        Services.AddSingleton(api);
        Services.AddSingleton(serviceProvider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            new ActiveProfileSessionService(serviceProvider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api),
            new ConfigurationBuilder().Build(),
            serviceProvider.GetRequiredService<ILogger<UIOrchestratorService>>()));
        Services.AddSingleton(_ => new ProviderCatalogueService(api, new MemoryCache(new MemoryCacheOptions())));
    }

    private static MediaEditorLaunchRequest Request(Guid? profileId = null) => new()
    {
        EntityIds = [ShowId],
        LaunchEntityId = ShowId,
        MediaType = "TV",
        InitialScope = "episode",
        InitialTab = "details",
        HeaderTitle = "Example show",
        ActiveProfileId = profileId,
    };

    private sealed class DetailsSaveShell : SharedMediaEditorShell
    {
        public MediaEditorDetailsFieldPresentation TitleField =>
            FindDetailsField("episode_title") ?? throw new InvalidOperationException("Episode title was not presented.");
        public MediaEditorDetailsFieldPresentation TagsField =>
            FindDetailsField("custom_tags") ?? throw new InvalidOperationException("Tags field was not presented.");
        public bool HasMatchDraftForTest => HasMatchDraft;
        public bool HasInlineDraftForTest => HasPendingDetailsInlineEdit;
        public bool HasPendingTargetSwitchForTest => HasPendingTargetSwitch;
        public string ActiveScopeIdForTest => ActiveScope!.ScopeId;
        public int ActiveTabIndexForTest => ActiveTabIndex;
        public string? InlineErrorForTest => DetailsInlineError;
        public string InlineDraftForTest => (string)typeof(SharedMediaEditorShell)
            .GetField("_detailsInlineDraft", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
        public string CanonicalEpisodeTitle => ActiveScope!.FieldSnapshot.CanonicalFields.Single(entry => entry.Key == "episode_title").Value;
        public Dictionary<string, string> ActiveFieldOverrides => ActiveScope!.FieldSnapshot.DisplayOverrides;

        protected override Task OnInitializedAsync()
        {
            SetField("_editorContext", new MediaEditorContextDto
            {
                LaunchEntityId = ShowId,
                MediaType = "TV",
                InitialScope = "episode",
                AvailableTabs = ["details", "artwork", "links", "history"],
                Scopes =
                [
                    new MediaEditorScopeDto
                    {
                        ScopeId = "series",
                        Label = "Series",
                        Order = 0,
                        FieldEntityId = ShowId,
                        DisplayTitle = "Example show",
                        CanEditFields = true,
                        AvailableTabs = ["details", "artwork", "links", "history"],
                        FieldSnapshot = new MediaEditorScopeFieldSnapshotDto(),
                    },
                    new MediaEditorScopeDto
                    {
                        ScopeId = "episode",
                        Label = "Episode",
                        Order = 1,
                        FieldEntityId = EpisodeId,
                        DisplayTitle = "The original episode",
                        CanEditFields = true,
                        AvailableTabs = ["details", "artwork", "links"],
                        FieldSnapshot = new MediaEditorScopeFieldSnapshotDto
                        {
                            CanonicalFields =
                            [
                                new MediaEditorScopedFieldValueDto
                                {
                                    Key = "episode_title",
                                    Value = "The original episode",
                                    ProviderName = "tvdb",
                                    IsUserLocked = true,
                                },
                            ],
                            AllowedOverrideKeys = ["title", "custom_tags"],
                            DisplayOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["custom_tags"] = "existing tag",
                            },
                        },
                    },
                ],
            });
            SetField("_activeScopeId", "episode");
            SetField("_loading", false);
            ((MediaEditorTabState)typeof(SharedMediaEditorShell)
                .GetField("_tabState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!).Activate("details");
            return Task.CompletedTask;
        }

        public Task BeginTitleEditAsync() => InvokeAsync(() => BeginDetailsInlineEditAsync(TitleField));
        public Task BeginTagsEditAsync() => InvokeAsync(() => BeginDetailsInlineEditAsync(TagsField));
        public Task UpdateInlineValueAsync(string value) => InvokeAsync(() => UpdateDetailsInlineDraftAsync(value));
        public Task SaveTitleAsync() => InvokeAsync(() => SaveDetailsInlineFieldAsync(TitleField));
        public Task SaveTagsAsync() => InvokeAsync(() => SaveDetailsInlineFieldAsync(TagsField));
        public Task CancelDetailsInlineEditForTestAsync() => InvokeAsync(CancelDetailsInlineEditAsync);
        public Task SynchronizeDialogEscapeForTestAsync() => InvokeAsync(SynchronizeDetailsDialogEscapeAsync);
        public Task RevertTitleAsync(MediaEditorDetailsFieldPresentation field) => InvokeAsync(() => RevertDetailsInlineFieldAsync(field));
        public Task SelectScopeForTest(string scopeId) => InvokeAsync(() => SelectScopeAsync(scopeId));
        public Task OpenDetailsLinkForTestAsync(string location) => InvokeAsync(() => NavigateToDetailsLinkAsync(location));
        public Task DiscardNavigationForTestAsync() => InvokeAsync(DiscardAndNavigate);
        public bool IsTabDisabledForTest(string tabId) => IsTabDisabled(tabId);
        public void SetDialogForTest(IMudDialogInstance dialog) =>
            typeof(SharedMediaEditorShell).GetProperty("MudDialog", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, dialog);
        public Task SelectArtworkTabAsync() => InvokeAsync(() => OnTabChanged(Tabs
            .Select((tab, index) => (tab, index))
            .First(item => item.tab.Id == "artwork").index));
        public void SetPendingMatchDraft() => SetField("_selectedRetailCandidateId", "pending-match");

        private void SetField(string name, object value) =>
            typeof(SharedMediaEditorShell).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, value);
    }

    public class MudDialogOptionsProxy : DispatchProxy
    {
        public DialogOptions CurrentOptions { get; set; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_Options")
                return CurrentOptions;
            if (targetMethod?.Name == "SetOptionsAsync")
            {
                CurrentOptions = (DialogOptions)args![0]!;
                return Task.CompletedTask;
            }
            if (targetMethod?.ReturnType == typeof(Task))
                return Task.CompletedTask;
            return targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }
}
