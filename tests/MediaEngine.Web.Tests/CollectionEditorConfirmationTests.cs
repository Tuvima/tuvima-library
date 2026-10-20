using System.Reflection;
using Bunit;
using Bunit.TestDoubles;
using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The collection editor asks its two questions (leave with unsaved changes, delete the collection) in the shared confirmation
/// dialog (details overlay spec D17), never in a browser pop-up, and each answer does what the pop-up's did.
/// </summary>
public sealed class CollectionEditorConfirmationTests : AsyncBunitContext
{
    private static readonly Guid CollectionId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

    private readonly List<Guid> _deleted = [];
    private readonly List<bool> _closed = [];
    private bool _deleteResult = true;

    public CollectionEditorConfirmationTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton<ICollectionPersonalMediaClient>(new EmptyPersonalMediaClient());
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.DeleteCollectionAsync), args =>
            {
                _deleted.Add((Guid)args![0]!);
                return Task.FromResult(_deleteResult);
            })));
    }

    private IRenderedComponent<CollectionEditorShell> RenderEditor() =>
        Render<CollectionEditorShell>(parameters => parameters
            .Add(shell => shell.Inline, true)
            .Add(shell => shell.Closed, EventCallback.Factory.Create<bool>(this, applied => _closed.Add(applied)))
            .Add(shell => shell.Request, new CollectionEditorLaunchRequest
            {
                EditingCollection = new CollectionListItemViewModel
                {
                    Id = CollectionId,
                    Name = "Weekend picks",
                    CollectionType = "Custom",
                    MembershipMode = "Manual",
                    OwnerKind = "Library",
                    Audience = "Everyone",
                },
            }));

    private static void SetUnsaved(IRenderedComponent<CollectionEditorShell> editor) =>
        editor.Instance.GetType().GetField("_hasUnsavedChanges", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor.Instance, true);

    // These finish on the renderer's own schedule and may re-render nothing, so a render-driven wait could never look again.
    private static async Task UntilAsync(Func<bool> condition, string failure = "The navigation did not finish.")
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(10);
        }
    }

    private static IEnumerable<NavigationHistory> History(NavigationManager navigation) => ((BunitNavigationManager)navigation).History;

    private static IReadOnlyList<AngleSharp.Dom.IElement> FooterButtons(IRenderedComponent<AppDialogHost> host) =>
        host.FindAll(".app-dialog-shell__footer button");

    private static void Choose(IRenderedComponent<AppDialogHost> host, string label) =>
        FooterButtons(host).Single(button => button.TextContent.Trim() == label).Click();

    // -- Delete collection ---------------------------------------------------------------------

    [Fact]
    public void DeleteCollection_AsksInTheSharedDialogNamingTheCollectionWithARedDelete()
    {
        var host = Render<AppDialogHost>();
        var editor = RenderEditor();

        editor.FindAll("button").Single(button => button.TextContent.Trim() == "Delete collection").Click();

        host.WaitForElement(".app-confirm");
        Assert.Equal("Delete Weekend picks?", host.Find(".app-dialog-shell__title").TextContent.Trim());
        Assert.Equal("The titles in it stay in your library.", host.Find(".app-confirm__message").TextContent.Trim());
        Assert.Contains("app-tone--error", FooterButtons(host).Single(button => button.TextContent.Trim() == "Delete").ClassList);
        Assert.Contains(FooterButtons(host), button => button.TextContent.Trim() == "Keep");
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "confirm");
        Assert.Empty(_deleted);
    }

    [Fact]
    public async Task DeleteCollection_DeletesClosesAndSaysSoWhenTheAnswerIsDelete()
    {
        var host = Render<AppDialogHost>();
        var editor = RenderEditor();
        editor.FindAll("button").Single(button => button.TextContent.Trim() == "Delete collection").Click();
        host.WaitForElement(".app-confirm");

        Choose(host, "Delete");

        await UntilAsync(() => _closed.Count > 0, "The editor did not close.");
        Assert.Equal([true], _closed);
        Assert.Equal([CollectionId], _deleted);
        Assert.Contains(Services.GetRequiredService<AppToastService>().Toasts, toast => toast.Message == "Collection \"Weekend picks\" deleted.");
    }

    [Fact]
    public void DeleteCollection_ChangesNothingWhenTheAnswerIsKeep()
    {
        var host = Render<AppDialogHost>();
        var editor = RenderEditor();
        editor.FindAll("button").Single(button => button.TextContent.Trim() == "Delete collection").Click();
        host.WaitForElement(".app-confirm");

        Choose(host, "Keep");

        host.WaitForAssertion(() => Assert.Empty(host.FindAll(".app-confirm")));
        Assert.Empty(_deleted);
        Assert.Empty(_closed);
    }

    [Fact]
    public void DeleteCollection_ChangesNothingWhenTheDialogIsClosedWithoutAnAnswer()
    {
        var host = Render<AppDialogHost>();
        var editor = RenderEditor();
        editor.FindAll("button").Single(button => button.TextContent.Trim() == "Delete collection").Click();
        host.WaitForElement(".app-confirm");

        host.Find("button[aria-label='Close dialog']").Click();

        host.WaitForAssertion(() => Assert.Empty(host.FindAll(".app-confirm")));
        Assert.Empty(_deleted);
        Assert.Empty(_closed);
    }

    [Fact]
    public async Task DeleteCollection_StaysOpenWithAnErrorWhenTheEngineRefuses()
    {
        _deleteResult = false;
        var host = Render<AppDialogHost>();
        var editor = RenderEditor();
        editor.FindAll("button").Single(button => button.TextContent.Trim() == "Delete collection").Click();
        host.WaitForElement(".app-confirm");

        Choose(host, "Delete");

        await UntilAsync(() => Services.GetRequiredService<AppToastService>().Toasts.Any(toast => toast.Message == "The collection could not be deleted."),
            "The refusal was not shown.");
        Assert.Empty(_closed);
    }

    // -- Leaving with unsaved changes ----------------------------------------------------------

    [Fact]
    public async Task LeavingByNavigationWithUnsavedChanges_AsksInTheSharedDialogAndKeepEditingStaysPut()
    {
        var host = Render<AppDialogHost>();
        var editor = RenderEditor();
        SetUnsaved(editor);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var start = navigation.Uri;

        await editor.InvokeAsync(() => navigation.NavigateTo("/elsewhere"));
        host.WaitForElement(".app-confirm");
        Assert.Equal("Discard your changes?", host.Find(".app-dialog-shell__title").TextContent.Trim());
        Choose(host, "Keep editing");

        await UntilAsync(() => History(navigation).Any(entry => entry.State == NavigationState.Prevented));
        Assert.Equal(start, navigation.Uri);
        Assert.Empty(host.FindAll(".app-confirm"));
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "confirm");
    }

    [Fact]
    public async Task LeavingByNavigationWithUnsavedChanges_LeavesWhenTheAnswerIsDiscard()
    {
        var host = Render<AppDialogHost>();
        var editor = RenderEditor();
        SetUnsaved(editor);
        var navigation = Services.GetRequiredService<NavigationManager>();

        await editor.InvokeAsync(() => navigation.NavigateTo("/elsewhere"));
        host.WaitForElement(".app-confirm");
        Choose(host, "Discard");

        await UntilAsync(() => navigation.Uri.EndsWith("/elsewhere", StringComparison.Ordinal));
        Assert.DoesNotContain(History(navigation), entry => entry.State == NavigationState.Prevented);
    }

    [Fact]
    public async Task LeavingByNavigationWithNothingUnsaved_AsksNothing()
    {
        var host = Render<AppDialogHost>();
        var editor = RenderEditor();
        var navigation = Services.GetRequiredService<NavigationManager>();

        await editor.InvokeAsync(() => navigation.NavigateTo("/elsewhere"));

        await UntilAsync(() => navigation.Uri.EndsWith("/elsewhere", StringComparison.Ordinal));
        Assert.Empty(host.FindAll(".app-confirm"));
    }

    private sealed class EmptyPersonalMediaClient : ICollectionPersonalMediaClient
    {
        public string? LastError => null;
        public Task<IReadOnlyList<CollectionGalleryReferenceDto>> GetEligibleGalleriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionGalleryReferenceDto>>([]);
        public Task<IReadOnlyList<CollectionPersonalMediaSourceDto>> GetSourcesAsync(Guid collectionId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionPersonalMediaSourceDto>>([]);
        public Task<CollectionPersonalMediaSourceDto?> AddSourceAsync(Guid collectionId, CollectionPersonalMediaSourceWriteRequest request, CancellationToken ct = default) => Task.FromResult<CollectionPersonalMediaSourceDto?>(null);
        public Task<CollectionPersonalMediaSourceDto?> UpdateSourceAsync(Guid collectionId, Guid sourceId, CollectionPersonalMediaSourceWriteRequest request, CancellationToken ct = default) => Task.FromResult<CollectionPersonalMediaSourceDto?>(null);
        public Task<bool> RemoveSourceAsync(Guid collectionId, Guid sourceId, CancellationToken ct = default) => Task.FromResult(false);
    }
}
