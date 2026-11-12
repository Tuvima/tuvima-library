using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// An order that is half arranged in a header list's Reorder mode (universal editor v6 D23) when the window crosses 1024 wide (a tablet turned, a window
/// resized): the header is drawn again in its other arrangement, and the arrangement must not be thrown away without asking. The new header opens in the same
/// mode on the same list with the same order, and Save order and Cancel still behave as they do without a crossing.
/// </summary>
public sealed class EditorPhoneHeldOrderTests : AsyncBunitContext
{
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly EditSessionState _session = new(new PendingEditList());
    private EditSessionFlow _flow = null!;

    public EditorPhoneHeldOrderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton(_device);
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetVideoOrderAsync), _ => Task.FromResult<VideoOrderResponse?>(Lessons()))));
        Render<AppPopoverHost>();
        Render<AppDialogHost>();
        _session.Begin([new EditTrailItem(EditorHeaderTestData.EpisodeTwoId, "Graded wash", "Item")]);
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        _flow = new EditSessionFlow(_session, mode, new EditSessionSaver(api), () => [], () => Task.FromResult(true));
    }

    private static VideoOrderResponse Lessons() => new()
    {
        Groups =
        [
            new VideoOrderGroupDto
            {
                ParentId = EditorHeaderTestData.SeasonOneId,
                Title = "Washes",
                Entries =
                [
                    new VideoOrderEntryDto { Id = EditorHeaderTestData.EpisodeOneId, Title = "Flat wash" },
                    new VideoOrderEntryDto { Id = EditorHeaderTestData.EpisodeTwoId, Title = "Graded wash" },
                ],
            },
        ],
    };

    private IRenderedComponent<ArrangementSwap> Show(bool phone) =>
        Render<ArrangementSwap>(parameters => parameters
            .Add(swap => swap.Phone, phone)
            .Add(swap => swap.Header, EditorHeaderTestData.CourseLesson())
            .Add(swap => swap.Flow, _flow));

    private static string[] TitlesInOrder(IRenderedComponent<ArrangementSwap> cut) =>
        [.. cut.FindAll("[data-testid=reorder-videos] .tl-vorder__title").Select(title => title.TextContent.Trim())];

    private IRenderedComponent<ArrangementSwap> ArrangeOnADesktop()
    {
        var cut = Show(phone: false);
        cut.Find("#tl-hnav-item").Click();
        cut.Find("#tl-hnav-reorder").Click();
        cut.Find("button[aria-label='Move Flat wash down']").Click();
        Assert.Equal(["Graded wash", "Flat wash"], TitlesInOrder(cut));
        cut.WaitForAssertion(() => Assert.True(_session.HasHeldOrder));
        return cut;
    }

    [Fact]
    public void WhenTheWindowCrossesIntoThePhoneArrangement_TheOrderBeingArrangedIsStillHeldInTheSheet()
    {
        var cut = ArrangeOnADesktop();

        cut.Render(parameters => parameters.Add(swap => swap.Phone, true));

        cut.WaitForAssertion(() => Assert.Equal(["Graded wash", "Flat wash"], TitlesInOrder(cut)));
        Assert.NotNull(cut.Find("[data-testid=phone-nav-sheet] [data-testid=reorder-videos]"));
        Assert.True(_session.HasHeldOrder);
        Assert.Empty(_session.Pending.Changes);
    }

    [Fact]
    public void WhenTheWindowCrossesIntoTheDesktopArrangement_TheOrderBeingArrangedIsStillHeldInTheDropdown()
    {
        var cut = Show(phone: true);
        cut.Find("#tl-hnav-phone").Click();
        cut.Find("#tl-hnav-reorder").Click();
        cut.Find("button[aria-label='Move Flat wash down']").Click();
        cut.WaitForAssertion(() => Assert.True(_session.HasHeldOrder));

        cut.Render(parameters => parameters.Add(swap => swap.Phone, false));

        cut.WaitForAssertion(() => Assert.Equal(["Graded wash", "Flat wash"], TitlesInOrder(cut)));
        Assert.Empty(cut.FindAll("[data-testid=phone-nav-sheet]"));
        Assert.True(_session.HasHeldOrder);
        Assert.Empty(_session.Pending.Changes);
    }

    [Fact]
    public void SaveOrderAfterACrossing_StagesTheOneOrderChangeJustAsWithoutIt()
    {
        var cut = ArrangeOnADesktop();
        cut.Render(parameters => parameters.Add(swap => swap.Phone, true));
        cut.WaitForAssertion(() => Assert.Equal(["Graded wash", "Flat wash"], TitlesInOrder(cut)));

        cut.Find("[data-reorder-save]").Click();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.VideoOrder, staged.Kind);
        Assert.Equal([EditorHeaderTestData.EpisodeTwoId, EditorHeaderTestData.EpisodeOneId], staged.OrderedIds);
        Assert.False(_session.HasHeldOrder);
    }

    [Fact]
    public void CancelAfterACrossing_StagesNothingAndLeavesNothingHeldForTheNextHeader()
    {
        var cut = ArrangeOnADesktop();
        cut.Render(parameters => parameters.Add(swap => swap.Phone, true));
        cut.WaitForAssertion(() => Assert.Equal(["Graded wash", "Flat wash"], TitlesInOrder(cut)));

        cut.Find("[data-reorder-cancel]").Click();

        Assert.Empty(_session.Pending.Changes);
        Assert.False(_session.HasHeldOrder);
        // Crossing back finds nothing waiting: the dropdown is closed and the list as it was saved.
        cut.Render(parameters => parameters.Add(swap => swap.Phone, false));
        Assert.Empty(cut.FindAll("[data-testid=reorder-videos]"));
        Assert.False(_session.HasHeldOrder);
    }

    [Fact]
    public void ACrossingWithNoOrderBeingArranged_OpensNothingAndHoldsNothing()
    {
        var cut = Show(phone: false);
        cut.Find("#tl-hnav-item").Click();

        cut.Render(parameters => parameters.Add(swap => swap.Phone, true));

        Assert.Empty(cut.FindAll("[data-testid=phone-nav-sheet], [data-testid=reorder-videos]"));
        Assert.False(_session.HasHeldOrder);
    }

    /// <summary>
    /// Draws the header's navigation in either arrangement the way the header does: the two arrangements are different parts of the tree, so a change of
    /// arrangement removes one navigation and creates the other.
    /// </summary>
    private sealed class ArrangementSwap : ComponentBase
    {
        [Parameter] public bool Phone { get; set; }

        [Parameter] public EditorHeaderDto? Header { get; set; }

        [Parameter] public EditSessionFlow? Flow { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Phone)
            {
                builder.OpenElement(0, "div");
                builder.OpenComponent<EditorHeaderNav>(1);
                builder.AddComponentParameter(2, nameof(EditorHeaderNav.Header), Header);
                builder.AddComponentParameter(3, nameof(EditorHeaderNav.Flow), Flow);
                builder.AddComponentParameter(4, nameof(EditorHeaderNav.Phone), true);
                builder.CloseComponent();
                builder.CloseElement();
            }
            else
            {
                builder.OpenElement(10, "section");
                builder.OpenComponent<EditorHeaderNav>(11);
                builder.AddComponentParameter(12, nameof(EditorHeaderNav.Header), Header);
                builder.AddComponentParameter(13, nameof(EditorHeaderNav.Flow), Flow);
                builder.CloseComponent();
                builder.CloseElement();
            }
        }
    }
}
