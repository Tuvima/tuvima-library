using Bunit;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Plugin file card and its read-only viewer (Plugins and Integrations screens v5 WP27, D54): the card shows the file's name,
/// whether it is valid and whether it differs from Tuvima's version; View file opens a coloured viewer with line numbers in place on
/// a computer; a phone or tablet shows the name and state and says editing is available on a computer. No test names a real plugin.
/// </summary>
public sealed class PluginFileCardTests : AsyncBunitContext
{
    private const string Text = "{\n  \"id\": \"example\",\n  // a note\n  \"version\": 3,\n  \"on\": true,\n  \"name\": \"Example\"\n}\n";

    private PluginFileResponse? _file;
    private int _fileCalls;

    public PluginFileCardTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPluginFileAsync), _ =>
            {
                _fileCalls++;
                return Task.FromResult(_file);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetPluginSetupPageAsync), _ => Task.FromResult<PluginSetupPageResponse?>(new PluginSetupPageResponse
            {
                PluginId = "example",
                PluginName = "Example",
                Cards = [FileCard(State())],
            }));
        });
        Services.AddSingleton(api);
    }

    [Fact]
    public void TheCard_ShowsFileName_ValidState_AndThatItIsTheSameAsTuvimasVersion()
    {
        var cut = RenderCard(State(), canView: true);

        Assert.Equal("example.json", cut.Find("[data-testid=plugin-file-name]").TextContent);
        Assert.Equal("Valid", cut.Find("[data-testid=plugin-file-valid]").TextContent.Trim());
        Assert.Equal("Same as Tuvima's version", cut.Find("[data-testid=plugin-file-differs]").TextContent.Trim());
        Assert.NotNull(cut.Find("[data-testid=plugin-file-view]"));
    }

    [Fact]
    public void TheCard_ShowsTheProblemCount_AndWhenTheFileDiffersFromTuvimasVersion()
    {
        var cut = RenderCard(State(errors: 2, notes: 1, differs: true), canView: true);

        Assert.Equal("2 problems", cut.Find("[data-testid=plugin-file-valid]").TextContent.Trim());
        Assert.Equal("Differs from Tuvima's version", cut.Find("[data-testid=plugin-file-differs]").TextContent.Trim());
        Assert.Contains("1 thing to know", cut.Find("[data-testid=plugin-file-notes]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void APrivatePlugin_HasNoComparisonBadge()
    {
        var cut = RenderCard(State(shipped: false), canView: true);

        Assert.Empty(cut.FindAll("[data-testid=plugin-file-differs]"));
    }

    [Fact]
    public void ViewFile_OpensTheViewerInPlace_WithLineNumbers_AndColouredPieces()
    {
        _file = new PluginFileResponse { PluginId = "example", Text = Text, State = State() };
        var cut = RenderCard(State(), canView: true);

        cut.Find("[data-testid=plugin-file-view]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-viewer]")));
        var numbers = cut.FindAll(".plugin-file-viewer__number").Select(element => element.TextContent).ToList();
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7"], numbers);
        Assert.Equal("\"id\"", cut.Find("[data-line='2'] .plugin-json--key").TextContent);
        Assert.Equal("\"example\"", cut.Find("[data-line='2'] .plugin-json--string").TextContent);
        Assert.Contains("a note", cut.Find("[data-line='3'] .plugin-json--comment").TextContent, StringComparison.Ordinal);
        Assert.Equal("3", cut.Find("[data-line='4'] .plugin-json--number").TextContent);
        Assert.Equal("true", cut.Find("[data-line='5'] .plugin-json--literal").TextContent);
        Assert.Equal(1, _fileCalls);
        Assert.Empty(cut.FindAll("[data-testid=plugin-file-edit]"));
        Assert.Contains("Hide file", cut.Find("[data-testid=plugin-file-view]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheViewer_MarksTheLinesTheCheckPointsAt_AndListsTheirMessages()
    {
        _file = new PluginFileResponse
        {
            PluginId = "example",
            Text = Text,
            State = State(errors: 1),
            Problems = [new PluginFileProblemDto { Line = 4, Severity = "error", Code = "type", Message = "\"version\" should be text, but it is a number." }],
        };
        var cut = RenderCard(State(errors: 1), canView: true);

        cut.Find("[data-testid=plugin-file-view]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-viewer]")));
        Assert.Equal("true", cut.Find("[data-line='4']").GetAttribute("data-problem"));
        Assert.Null(cut.Find("[data-line='2']").GetAttribute("data-problem"));
        Assert.Contains("should be text", cut.Find("[data-testid=plugin-file-problems]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void HideFile_ClosesTheViewer()
    {
        _file = new PluginFileResponse { PluginId = "example", Text = Text, State = State() };
        var cut = RenderCard(State(), canView: true);
        cut.Find("[data-testid=plugin-file-view]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-viewer]")));

        cut.Find("[data-testid=plugin-file-view]").Click();

        Assert.Empty(cut.FindAll("[data-testid=plugin-file-viewer]"));
    }

    [Fact]
    public void WhenTheEngineDoesNotReturnTheFile_TheCardSaysSo()
    {
        _file = null;
        var cut = RenderCard(State(), canView: true);

        cut.Find("[data-testid=plugin-file-view]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-unavailable]")));
    }

    [Fact]
    public void OnAPhoneOrTablet_TheCardShowsNameAndState_AndSaysEditingIsAvailableOnAComputer()
    {
        var cut = RenderCard(State(errors: 1), canView: false);

        Assert.Equal("example.json", cut.Find("[data-testid=plugin-file-name]").TextContent);
        Assert.Equal("1 problem", cut.Find("[data-testid=plugin-file-valid]").TextContent.Trim());
        Assert.Equal("Editing is available on a computer.", cut.Find("[data-testid=plugin-file-computer-only]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=plugin-file-view]"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-file-viewer]"));
        Assert.Equal(0, _fileCalls);
    }

    [Fact]
    public async Task OnThePage_TheFileCardIsLast_AndFollowsTheDeviceRule()
    {
        var device = Services.GetRequiredService<DeviceContextService>();
        await device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", Touch: false, FinePointer: true, Hover: true));

        var desktop = RenderPage();
        desktop.WaitForAssertion(() => Assert.NotEmpty(desktop.FindAll("[data-card=file]")));
        Assert.Equal("file", desktop.FindAll("[data-card]").Last().GetAttribute("data-card"));
        Assert.NotEmpty(desktop.FindAll("[data-testid=plugin-file-view]"));

        await device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", Touch: true));
        var phone = RenderPage();
        phone.WaitForAssertion(() => Assert.NotEmpty(phone.FindAll("[data-card=file]")));
        Assert.Empty(phone.FindAll("[data-testid=plugin-file-view]"));
        Assert.NotEmpty(phone.FindAll("[data-testid=plugin-file-computer-only]"));
    }

    // ------------------------------------------------------------------------------------------------ The tokenizer

    [Fact]
    public void TheTokenizer_TellsKeysFromStrings_AndColoursEveryKindOfPiece()
    {
        var line = PluginJsonTokenizer.Lines("  \"a\": [\"b\", -1.5e3, true, null],  ").Single();

        Assert.Equal(PluginJsonTokenKind.Key, line.Single(token => token.Text == "\"a\"").Kind);
        Assert.Equal(PluginJsonTokenKind.String, line.Single(token => token.Text == "\"b\"").Kind);
        Assert.Equal(PluginJsonTokenKind.Number, line.Single(token => token.Text == "-1.5e3").Kind);
        Assert.Equal(PluginJsonTokenKind.Literal, line.Single(token => token.Text == "true").Kind);
        Assert.Equal(PluginJsonTokenKind.Literal, line.Single(token => token.Text == "null").Kind);
        Assert.Equal("  \"a\": [\"b\", -1.5e3, true, null],  ", string.Concat(line.Select(token => token.Text)));
    }

    [Fact]
    public void TheTokenizer_KeepsLineNumbersTheEngineCounts_AndColoursBlockCommentsAcrossLines()
    {
        var lines = PluginJsonTokenizer.Lines("{\r\n  /* one\r\n  two */ \"a\": 1\r\n}\r\n");

        Assert.Equal(4, lines.Count);
        Assert.Equal(PluginJsonTokenKind.Comment, lines[1][^1].Kind);
        Assert.Equal(new PluginJsonToken(PluginJsonTokenKind.Comment, "  two */"), lines[2][0]);
        Assert.Contains(lines[2], token => token.Kind == PluginJsonTokenKind.Key);
        Assert.Equal(1, PluginJsonTokenizer.LineCount(string.Empty));
    }

    [Fact]
    public void TheTokenizer_NeverThrows_OnBrokenText()
    {
        var lines = PluginJsonTokenizer.Lines("{ \"open\": \"never closed\n  ]]] ??? 12abc \\ \"");

        Assert.Equal(2, lines.Count);
    }

    // ------------------------------------------------------------------------------------------------ Helpers

    private IRenderedComponent<PluginFileCard> RenderCard(PluginFileStateDto state, bool canView) =>
        Render<PluginFileCard>(parameters => parameters
            .Add(component => component.PluginId, "example")
            .Add(component => component.Card, FileCard(state))
            .Add(component => component.CanView, canView));

    private IRenderedComponent<PluginSetupPage> RenderPage() =>
        Render<PluginSetupPage>(parameters => parameters
            .Add(component => component.Plugin, new PluginSummaryResponse { Id = "example", Name = "Example", Enabled = true }));

    private static PluginSetupCardDto FileCard(PluginFileStateDto state) => new() { Key = "file", Title = "Plugin file", File = state };

    private static PluginFileStateDto State(int errors = 0, int notes = 0, bool differs = false, bool shipped = true) => new()
    {
        FileName = "example.json",
        Kind = "manifest",
        State = errors == 0 ? "valid" : "problems",
        ErrorCount = errors,
        NoteCount = notes,
        DiffersFromShipped = differs,
        HasShippedCopy = shipped,
        Editable = true,
    };
}
