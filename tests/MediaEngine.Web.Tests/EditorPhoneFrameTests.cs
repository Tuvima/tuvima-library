using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The editor window's frame on a phone (universal editor v6 section 4a, D22): the window and footer carry the phone arrangement, Discard and Save
/// changes sit side by side, and Review changes is a full-screen step with Back and Save changes. The desktop window is unchanged. The device is a
/// desktop that may edit: whether a phone may open the editor is another packet's.
/// </summary>
public sealed class EditorPhoneFrameTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public EditorPhoneFrameTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), args =>
            {
                var request = (EditSessionRequest)args![0]!;
                return Task.FromResult(LibraryBatchEditOutcome<EditSessionResult>.Ok(Ok(request)));
            });
        }));
        Services.AddSingleton(new MediaEngine.Web.Services.Integration.DashboardSessionAccessor());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Render<MediaEngine.Web.Components.Shared.AppPopoverHost>();
    }

    private static EditSessionResult Ok(EditSessionRequest request) => new()
    {
        PreviewOnly = request.PreviewOnly,
        Valid = true,
        DataSaved = !request.PreviewOnly,
        Changes = request.Changes.Select(change => new EditSessionChangeResult
        {
            Id = change.Id,
            Kind = change.Kind,
            EntityId = change.EntityId,
            Status = EditSessionChangeStatuses.Ok,
        }).ToList(),
    };

    private async Task<(EditingModeContext Mode, EditSessionState Session, EditSessionFlow Flow)> StartAsync()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => fields.LoadAsync(MovieId, "Movies", "movie", null);
        var session = new EditSessionState(new PendingEditList());
        var flow = new EditSessionFlow(session, mode, new EditSessionSaver(api), () => [new EditTrailItem(MovieId, "Dune: Part One", "Movie")], () => Task.FromResult(false));
        Assert.True(await mode.EnterAsync());
        await flow.SelectToolAsync(EditTool.Details);
        return (mode, session, flow);
    }

    private IRenderedComponent<CascadingValue<EditingModeContext>> RenderWindow(EditingModeContext mode, EditSessionFlow flow, bool phone) =>
        Render<CascadingValue<EditingModeContext>>(parameters => parameters
            .Add(host => host.Value, mode)
            .Add(host => host.ChildContent, builder =>
            {
                builder.OpenComponent<EditorPanelHost>(0);
                builder.AddComponentParameter(1, nameof(EditorPanelHost.Mode), mode);
                builder.AddComponentParameter(2, nameof(EditorPanelHost.Flow), flow);
                builder.AddComponentParameter(3, nameof(EditorPanelHost.Phone), phone);
                builder.AddComponentParameter(4, nameof(EditorPanelHost.ShowHistory), true);
                builder.CloseComponent();
            }));

    [Fact]
    public async Task OnAPhone_TheWindowAndItsFooterCarryThePhoneArrangement_WithDiscardAndSaveChangesSideBySide()
    {
        var (mode, _, flow) = await StartAsync();

        var cut = RenderWindow(mode, flow, phone: true);

        var window = cut.Find(".tl-editor-window");
        Assert.Contains("tl-editor-window--phone", window.ClassList);
        Assert.Equal("true", window.GetAttribute("data-phone"));
        var actions = cut.FindAll(".tl-editor-window__footer .tl-editor-window__actions > button");
        // The plain title bar of this harness says Cancel where the framed header (EditorPhonePopoverTests) says Discard.
        Assert.Equal(["tl-editor-window__cancel", "tl-editor-window__save"], actions.Select(button => button.ClassList.First(name => name.StartsWith("tl-editor-window__", StringComparison.Ordinal))).ToArray());
        Assert.Equal("Save changes", actions[1].TextContent.Trim());
        // The Close button stays at the top right of the window.
        Assert.Single(cut.FindAll(".tl-editor-panel__close"));
    }

    [Fact]
    public async Task OnADesktop_TheWindowIsUnchanged()
    {
        var (mode, _, flow) = await StartAsync();

        var cut = RenderWindow(mode, flow, phone: false);

        var window = cut.Find(".tl-editor-window");
        Assert.DoesNotContain("tl-editor-window--phone", window.ClassList);
        Assert.False(window.HasAttribute("data-phone"));
    }

    [Fact]
    public async Task OnAPhone_ReviewChangesIsAStepWithBackAndSaveChanges_AndNoDiscardButton()
    {
        var (mode, session, flow) = await StartAsync();
        session.Pending.SetField(MovieId, "title", "Dune");
        var cut = RenderWindow(mode, flow, phone: true);

        cut.Find("button.tl-editor-window__save").Click();

        var review = cut.WaitForElement(".review-changes--phone");
        Assert.Equal("true", review.GetAttribute("data-phone"));
        cut.WaitForAssertion(() => Assert.False(cut.Find(".review-changes__footer button:last-child").HasAttribute("disabled")));
        var buttons = cut.FindAll(".review-changes__footer button");
        Assert.Equal(2, buttons.Count);
        Assert.Equal("Back to editing", buttons[0].TextContent.Trim());
        Assert.StartsWith("Save", buttons[1].TextContent.Trim(), StringComparison.Ordinal);
        Assert.DoesNotContain("Discard", cut.Find(".review-changes__footer").TextContent, StringComparison.Ordinal);
        // The step lists every change.
        Assert.Single(cut.FindAll(".review-changes__row"));
        // Back returns to the tool with everything still staged.
        buttons[0].Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".review-changes")));
        Assert.True(session.HasPendingChanges);
    }

    [Fact]
    public async Task OnADesktop_ReviewChangesStillOffersDiscardChanges()
    {
        var (mode, session, flow) = await StartAsync();
        session.Pending.SetField(MovieId, "title", "Dune");
        var cut = RenderWindow(mode, flow, phone: false);

        cut.Find("button.tl-editor-window__save").Click();

        cut.WaitForElement(".review-changes--page");
        Assert.Empty(cut.FindAll(".review-changes--phone"));
        Assert.Equal(3, cut.FindAll(".review-changes__footer button").Count);
        Assert.Contains("Discard changes", cut.Find(".review-changes__footer").TextContent, StringComparison.Ordinal);
    }
}
