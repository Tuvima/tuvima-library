using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Theming;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Components.Reader;

public partial class ReaderFrame : IDisposable
{
    [Inject] private DeviceContextService DeviceContext { get; set; } = default!;
    [Inject] private IStringLocalizer<SharedStrings> L { get; set; } = default!;

    // ---- What the frame shows -------------------------------------------------------------------------------------------

    /// <summary>The opening spinner, the page, or "can't open". Tools, the panel and the bottom line draw only when Ready.</summary>
    [Parameter] public ReaderFrameState State { get; set; } = ReaderFrameState.Loading;

    /// <summary>Extra classes on the shell. A reader's own stage classes and its colour classes (a theme, a background) go here.</summary>
    [Parameter] public string? Class { get; set; }

    /// <summary>The title at the top left.</summary>
    [Parameter] public string? Title { get; set; }

    /// <summary>The line under the title: the author, the Series and issue, or the file kind and page count.</summary>
    [Parameter] public string? Subtitle { get; set; }

    /// <summary>The tools this reader has. Drawn in the fixed order (Search, Contents, Pages, Bookmark this page, Bookmarks, Reading settings) whatever order they are listed in. Close is always there.</summary>
    [Parameter] public IReadOnlyCollection<ReaderTool> Tools { get; set; } = ReaderTools.All;

    /// <summary>True once the bars have auto-hidden. The reader decides when (a tap, a pause, a key); the frame draws the state.</summary>
    [Parameter] public bool ChromeHidden { get; set; }

    // ---- Slots ----------------------------------------------------------------------------------------------------------

    /// <summary>What the reader draws into: the page view, the book view. Fills the shell behind the bars.</summary>
    [Parameter] public RenderFragment? Stage { get; set; }

    /// <summary>The bottom line's contents (chapter and percent, or the page slider). The frame keeps the line's space and hides it with the top line.</summary>
    [Parameter] public RenderFragment? Bottom { get; set; }

    /// <summary>The panel body for the open tool.</summary>
    [Parameter] public RenderFragment<ReaderTool>? PanelContent { get; set; }

    /// <summary>Anything else that sits over the page: the highlight tools, a short notice, the end-of-issue card.</summary>
    [Parameter] public RenderFragment? Overlays { get; set; }

    /// <summary>Replaces the opening spinner.</summary>
    [Parameter] public RenderFragment? LoadingContent { get; set; }

    /// <summary>Replaces the default "can't open" message.</summary>
    [Parameter] public RenderFragment? ErrorContent { get; set; }

    // ---- Panel ----------------------------------------------------------------------------------------------------------

    /// <summary>The tool whose panel is open, or null. Search, Contents, Pages, Bookmarks and Reading settings have panels; Bookmark this page does not.</summary>
    [Parameter] public ReaderTool? OpenPanel { get; set; }

    /// <summary>Overrides the panel heading, which is otherwise the tool's name.</summary>
    [Parameter] public string? PanelTitle { get; set; }

    /// <summary>Overrides the name of the Search tool (a book says "Search in this book", a PDF "Search in this file").</summary>
    [Parameter] public string? SearchLabel { get; set; }

    // ---- State the bars show --------------------------------------------------------------------------------------------

    /// <summary>True when the visitor has bookmarked the page they are on: the icon fills and pressing it again removes theirs.</summary>
    [Parameter] public bool IsPageBookmarked { get; set; }

    /// <summary>Spoken, not drawn, politely: the position ("Page 12 of 28", "42 percent").</summary>
    [Parameter] public string? LiveText { get; set; }

    /// <summary>Spoken, not drawn, politely: the result of the last action ("Bookmark added on page 12").</summary>
    [Parameter] public string? Announcement { get; set; }

    /// <summary>The words of the default "can't open" message.</summary>
    [Parameter] public string? ErrorMessage { get; set; }

    /// <summary>Shows Try again beside Close in the default "can't open" message.</summary>
    [Parameter] public bool CanRetry { get; set; }

    // ---- Events ---------------------------------------------------------------------------------------------------------

    /// <summary>The Close (X) at the top right: the reader saves the page and returns to where it was opened from.</summary>
    [Parameter] public EventCallback OnClose { get; set; }

    /// <summary>A tool with a panel was pressed (also from the phone "..." sheet). The reader opens, switches or closes the panel.</summary>
    [Parameter] public EventCallback<ReaderTool> OnToolClick { get; set; }

    /// <summary>Bookmark this page was pressed: the reader adds the visitor's bookmark, or removes it when the page already has one.</summary>
    [Parameter] public EventCallback OnBookmarkToggle { get; set; }

    /// <summary>The panel's Close, or a tap on the dim behind it.</summary>
    [Parameter] public EventCallback OnPanelClose { get; set; }

    /// <summary>Try again in the default "can't open" message.</summary>
    [Parameter] public EventCallback OnRetry { get; set; }

    /// <summary>Anything else set on the frame (a data attribute, an id) lands on the shell.</summary>
    [Parameter(CaptureUnmatchedValues = true)] public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    // ---- Derived --------------------------------------------------------------------------------------------------------

    // The form factor the Dashboard already tracks decides what fits; the frame adds no breakpoint of its own.
    private bool OnPhone => DeviceContext.IsPhone;
    private bool IsReady => State == ReaderFrameState.Ready;
    private string StateName => State.ToString().ToLowerInvariant();
    private string ShellClass => string.IsNullOrWhiteSpace(Class) ? "rf-shell" : $"rf-shell {Class}";
    private string BarLabel => string.IsNullOrWhiteSpace(Title) ? L["ReaderFrame_Reader"].Value : Title!;
    private string? BarHidden => ChromeHidden ? "true" : null;
    private bool HasPanel => IsReady && OpenPanel is { } tool && tool != ReaderTool.BookmarkThisPage;

    private IEnumerable<ReaderTool> OrderedTools => Enum.GetValues<ReaderTool>().Where(tool => Tools.Contains(tool));

    // On a phone only Bookmark this page stays on the line; the rest go in one "..." sheet (D16).
    private IEnumerable<ReaderTool> LineTools => OnPhone ? OrderedTools.Where(tool => tool == ReaderTool.BookmarkThisPage) : OrderedTools;
    private IEnumerable<ReaderTool> SheetTools => OnPhone ? OrderedTools.Where(tool => tool != ReaderTool.BookmarkThisPage) : [];

    private string LabelFor(ReaderTool tool) => tool switch
    {
        ReaderTool.Search => string.IsNullOrWhiteSpace(SearchLabel) ? L["ReaderFrame_Search"].Value : SearchLabel!,
        ReaderTool.Contents => L["ReaderFrame_Contents"].Value,
        ReaderTool.Pages => L["ReaderFrame_Pages"].Value,
        ReaderTool.BookmarkThisPage => IsPageBookmarked ? L["ReaderFrame_RemoveBookmark"].Value : L["ReaderFrame_BookmarkThisPage"].Value,
        ReaderTool.Bookmarks => L["ReaderFrame_Bookmarks"].Value,
        _ => L["ReaderFrame_ReadingSettings"].Value,
    };

    private string IconFor(ReaderTool tool) => tool switch
    {
        ReaderTool.Search => AppMaterialIcons.Filled.Search,
        ReaderTool.Contents => AppMaterialIcons.Filled.Toc,
        ReaderTool.Pages => AppMaterialIcons.Outlined.GridView,
        ReaderTool.BookmarkThisPage => IsPageBookmarked ? AppMaterialIcons.Filled.Bookmark : AppMaterialIcons.Outlined.BookmarkBorder,
        ReaderTool.Bookmarks => AppMaterialIcons.Outlined.Bookmarks,
        _ => AppMaterialIcons.Outlined.Tune,
    };

    private string ResolvedPanelTitle => !string.IsNullOrWhiteSpace(PanelTitle) ? PanelTitle! : OpenPanel is { } tool ? LabelFor(tool) : string.Empty;

    private string? ToolClass(ReaderTool tool) => tool == ReaderTool.BookmarkThisPage
        ? IsPageBookmarked ? "rf-ib rf-ib--on" : "rf-ib"
        : OpenPanel == tool ? "rf-ib rf-ib--on" : "rf-ib";

    private IReadOnlyList<AppRowMenuItem> SheetItems => SheetTools
        .Select(tool => new AppRowMenuItem
        {
            Label = LabelFor(tool),
            Icon = IconFor(tool),
            OnClick = EventCallback.Factory.Create(this, () => OnToolClick.InvokeAsync(tool)),
        })
        .ToList();

    protected override void OnInitialized() => DeviceContext.OnChanged += OnDeviceChanged;

    private void OnDeviceChanged() => _ = InvokeAsync(StateHasChanged);

    public void Dispose() => DeviceContext.OnChanged -= OnDeviceChanged;
}
