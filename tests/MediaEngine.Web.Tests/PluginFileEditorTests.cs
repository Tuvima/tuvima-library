using Bunit;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Plugin file editor (Plugins and Integrations screens v5 WP28, D55, D56): Edit file is offered to an administrator on a computer
/// only; the editor has a sticky bar (Cancel, Reset to Tuvima's version, Save changes), line numbers, checks as you type with the
/// problems listed by line, and Save changes stays off until the file is valid and different; pressing it shows Review changes (a
/// line-by-line comparison) before anything is written. No test names a real plugin.
/// </summary>
public sealed class PluginFileEditorTests : AsyncBunitContext
{
    private const string Original = "{\n  \"id\": \"example\",\n  \"limit\": 5,\n  \"name\": \"Example\"\n}\n";
    private const string Edited = "{\n  \"id\": \"example\",\n  \"limit\": 9,\n  \"name\": \"Example\"\n}\n";

    private readonly List<string> _checked = [];
    private readonly List<string> _saved = [];
    private int _resets;
    private Func<string, PluginFileCheckResponse> _check;
    private Func<string, PluginFileSaveResponse> _save;
    private PluginFileResponse? _file;

    public PluginFileEditorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        _check = text => new PluginFileCheckResponse { PluginId = "example", State = State(), Problems = [] };
        _save = text => new PluginFileSaveResponse { PluginId = "example", Saved = true, Outcome = "saved", Message = "Saved.", Text = text, State = State(), Reloaded = true, Health = "working", HealthSummary = "It is working." };
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPluginFileAsync), _ => Task.FromResult(_file));
            stub.SetHandler(nameof(IEngineApiClient.CheckPluginFileAsync), args =>
            {
                var text = (string)args![1]!;
                _checked.Add(text);
                return Task.FromResult<PluginFileCheckResponse?>(_check(text));
            });
            stub.SetHandler(nameof(IEngineApiClient.SavePluginFileAsync), args =>
            {
                var text = (string)args![1]!;
                _saved.Add(text);
                return Task.FromResult<PluginFileSaveResponse?>(_save(text));
            });
            stub.SetHandler(nameof(IEngineApiClient.ResetPluginFileAsync), _ =>
            {
                _resets++;
                return Task.FromResult<PluginFileSaveResponse?>(new PluginFileSaveResponse { PluginId = "example", Saved = true, Outcome = "reset", Message = "Tuvima's version is back.", Text = Original, State = State(), Reloaded = true, Health = "working" });
            });
        }));
    }

    // ------------------------------------------------------------------------------------------------ The card offers Edit

    [Fact]
    public void OnAComputer_AnEditableFileOffersEditFile_AndAFileThatShipsInsideLibraryDoesNot()
    {
        var editable = RenderCard(State(), canEdit: true);
        var shipped = RenderCard(State(editable: false), canEdit: true);

        Assert.NotEmpty(editable.FindAll("[data-testid=plugin-file-edit]"));
        Assert.Empty(shipped.FindAll("[data-testid=plugin-file-edit]"));
        Assert.NotEmpty(shipped.FindAll("[data-testid=plugin-file-view]"));
    }

    [Fact]
    public void WhenTheDeviceCannotEdit_TheCardNeverOffersEditFile_OrTheEditor()
    {
        var cut = RenderCard(State(), canEdit: false, canView: false);

        Assert.Empty(cut.FindAll("[data-testid=plugin-file-edit]"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-file-editor]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-computer-only]"));
    }

    [Fact]
    public async Task OnAPhone_ThePagesFileCardShowsNoEditor()
    {
        _file = File(Original, State());
        Services.AddSingleton(EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetPluginSetupPageAsync), _ => Task.FromResult<PluginSetupPageResponse?>(new PluginSetupPageResponse
        {
            PluginId = "example",
            PluginName = "Example",
            Cards = [FileCard(State())],
        }))));
        var device = Services.GetRequiredService<DeviceContextService>();
        await device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", Touch: true));

        var page = Render<PluginSetupPage>(parameters => parameters.Add(component => component.Plugin, new PluginSummaryResponse { Id = "example", Name = "Example", Enabled = true }));

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-card=file]")));
        Assert.Empty(page.FindAll("[data-testid=plugin-file-edit]"));
        Assert.Empty(page.FindAll("[data-testid=plugin-file-editor]"));
    }

    [Fact]
    public void EditFile_TurnsTheCardIntoTheEditor_WithTheBar_LineNumbers_AndSaveOff()
    {
        _file = File(Original, State());
        var cut = RenderCard(State(), canEdit: true);

        cut.Find("[data-testid=plugin-file-edit]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-editor]")));
        Assert.Equal("example.json", cut.Find("[data-testid=plugin-file-editor-name]").TextContent);
        Assert.Equal(["1", "2", "3", "4", "5"], cut.FindAll("[data-testid=plugin-file-editor-gutter] .plugin-file-editor__line").Select(line => line.TextContent).ToList());
        Assert.Equal(Original, cut.Find("textarea").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-cancel]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-reset]"));
        Assert.True(cut.Find("[data-testid=plugin-file-save]").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-file-view]"));
    }

    [Fact]
    public void APrivatePlugin_HasNoResetButton_BecauseItHasNoTuvimaVersion()
    {
        var state = State(shipped: false);
        _file = File(Original, state);
        var cut = RenderCard(state, canEdit: true);

        cut.Find("[data-testid=plugin-file-edit]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-editor]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-file-reset]"));
    }

    // ------------------------------------------------------------------------------------------------ Checks as you type

    [Fact]
    public void Typing_ChecksTheText_ListsProblemsWithTheirLine_AndKeepsSaveOff()
    {
        _check = text => new PluginFileCheckResponse
        {
            PluginId = "example",
            State = State(errors: 1),
            Problems = [new PluginFileProblemDto { Line = 3, Severity = "error", Code = "range", Message = "\"limit\" must be at most 10." }],
        };
        var cut = Panel(File(Original, State()));

        cut.Find("textarea").Input(Edited.Replace("9", "99", StringComparison.Ordinal));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-severity=error]")));
        var problem = cut.Find("[data-testid=plugin-file-editor-problems] [data-severity=error]");
        Assert.Equal("3", problem.GetAttribute("data-line"));
        Assert.Contains("must be at most 10", problem.TextContent, StringComparison.Ordinal);
        Assert.Contains("Line 3", problem.TextContent, StringComparison.Ordinal);
        Assert.Contains("plugin-file-editor__line--problem", cut.Find("span[data-line='3']").ClassName, StringComparison.Ordinal);
        Assert.True(cut.Find("[data-testid=plugin-file-save]").HasAttribute("disabled"));
        Assert.Equal("1 problem", cut.Find("[data-testid=plugin-file-editor-status]").TextContent.Trim());
    }

    [Fact]
    public void AThingToKnow_IsListed_ButDoesNotBlockSaving()
    {
        _check = text => new PluginFileCheckResponse
        {
            PluginId = "example",
            State = State(notes: 1),
            Problems = [new PluginFileProblemDto { Line = 3, Severity = "note", Code = "unknown_word", Message = "\"colour\" is not a word Library knows, so it will be ignored." }],
        };
        var cut = Panel(File(Original, State()));

        cut.Find("textarea").Input(Edited);

        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=plugin-file-save]").HasAttribute("disabled")));
        Assert.Contains("will be ignored", cut.Find("[data-severity=note]").TextContent, StringComparison.Ordinal);
        Assert.Contains("To know", cut.Find("[data-severity=note]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveChanges_IsOffUntilTheFileIsValidAndDifferent()
    {
        var cut = Panel(File(Original, State()));
        Assert.True(cut.Find("[data-testid=plugin-file-save]").HasAttribute("disabled"));

        cut.Find("textarea").Input(Edited);
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=plugin-file-save]").HasAttribute("disabled")));

        cut.Find("textarea").Input(Original);
        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid=plugin-file-save]").HasAttribute("disabled")));
        Assert.NotEmpty(_checked);
    }

    [Fact]
    public void WhenTheEngineDoesNotAnswerTheCheck_SaveStaysOff_AndTheEditorSaysTheTextIsNotChecked()
    {
        var cut = Panel(File(Original, State()), answerChecks: false);

        cut.Find("textarea").Input(Edited);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-editor-check-failed]")));
        Assert.True(cut.Find("[data-testid=plugin-file-save]").HasAttribute("disabled"));
    }

    // ------------------------------------------------------------------------------------------------ Review changes, then save

    [Fact]
    public void SaveChanges_ShowsReviewChangesWithTheLinesThatDiffer_BeforeAnythingIsWritten()
    {
        var host = Render<AppDialogHost>();
        var done = new List<PluginFileSaveResponse?>();
        var cut = Panel(File(Original, State()), done);
        cut.Find("textarea").Input(Edited);
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=plugin-file-save]").HasAttribute("disabled")));

        cut.Find("[data-testid=plugin-file-save]").Click();

        host.WaitForElement("[data-testid=plugin-file-diff]");
        Assert.Contains("Review changes", host.Find(".app-dialog-shell__title").TextContent, StringComparison.Ordinal);
        Assert.Contains("1 line added, 1 line removed", host.Find("[data-testid=plugin-file-diff-summary]").TextContent, StringComparison.Ordinal);
        Assert.Contains("\"limit\": 5", host.Find("[data-kind=removed]").TextContent, StringComparison.Ordinal);
        Assert.Contains("\"limit\": 9", host.Find("[data-kind=added]").TextContent, StringComparison.Ordinal);
        Assert.Empty(_saved);
        Assert.Empty(done);

        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Save changes").Click();

        cut.WaitForAssertion(() => Assert.Single(done));
        Assert.Equal([Edited], _saved);
        Assert.True(done[0]!.Saved);
    }

    [Fact]
    public void KeepEditing_InReviewChanges_WritesNothing_AndStaysInTheEditor()
    {
        var host = Render<AppDialogHost>();
        var done = new List<PluginFileSaveResponse?>();
        var cut = Panel(File(Original, State()), done);
        cut.Find("textarea").Input(Edited);
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=plugin-file-save]").HasAttribute("disabled")));
        cut.Find("[data-testid=plugin-file-save]").Click();
        host.WaitForElement("[data-testid=plugin-file-diff]");

        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Keep editing").Click();

        host.WaitForAssertion(() => Assert.Empty(host.FindAll("[data-testid=plugin-file-diff]")));
        Assert.Empty(_saved);
        Assert.Empty(done);
        Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-editor]"));
    }

    [Fact]
    public void WhenTheEngineRefusesTheSave_TheEditorStaysOpen_AndShowsWhy()
    {
        _save = text => new PluginFileSaveResponse
        {
            PluginId = "example",
            Saved = false,
            Outcome = "invalid",
            Message = "Nothing was saved: the file has 1 problem.",
            State = State(errors: 1),
            Problems = [new PluginFileProblemDto { Line = 3, Severity = "error", Code = "range", Message = "\"limit\" must be at most 10." }],
        };
        var host = Render<AppDialogHost>();
        var done = new List<PluginFileSaveResponse?>();
        var cut = Panel(File(Original, State()), done);
        cut.Find("textarea").Input(Edited);
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=plugin-file-save]").HasAttribute("disabled")));
        cut.Find("[data-testid=plugin-file-save]").Click();
        host.WaitForElement("[data-testid=plugin-file-diff]");

        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Save changes").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-editor-message]")));
        Assert.Contains("Nothing was saved", cut.Find("[data-testid=plugin-file-editor-message]").TextContent, StringComparison.Ordinal);
        Assert.Empty(done);
    }

    [Fact]
    public void Cancel_WithEdits_AsksBeforeLeaving_AndWithoutEditsLeavesAtOnce()
    {
        var host = Render<AppDialogHost>();
        var done = new List<PluginFileSaveResponse?>();
        var untouched = Panel(File(Original, State()), done);

        untouched.Find("[data-testid=plugin-file-cancel]").Click();

        untouched.WaitForAssertion(() => Assert.Single(done));
        Assert.Null(done[0]);

        var edited = Panel(File(Original, State()), done);
        edited.Find("textarea").Input(Edited);
        edited.Find("[data-testid=plugin-file-cancel]").Click();
        host.WaitForElement(".app-confirm");
        Assert.Contains("Unsaved changes", host.Find(".app-dialog-shell__title").TextContent, StringComparison.Ordinal);
        Assert.Single(done);
    }

    [Fact]
    public void ResetToTuvimasVersion_AsksFirst_ThenAsksTheEngine()
    {
        var host = Render<AppDialogHost>();
        var done = new List<PluginFileSaveResponse?>();
        var cut = Panel(File(Original, State()), done);

        cut.Find("[data-testid=plugin-file-reset]").Click();
        host.WaitForElement(".app-confirm");
        Assert.Equal(0, _resets);
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Reset file").Click();

        cut.WaitForAssertion(() => Assert.Single(done));
        Assert.Equal(1, _resets);
        Assert.Equal("reset", done[0]!.Outcome);
    }

    // ------------------------------------------------------------------------------------------------ The card after a save

    [Fact]
    public void AfterASave_TheCardSaysWhatHappened_AndWhenThePluginDidNotLoad_OffersReset()
    {
        _file = File(Original, State());
        _save = text => new PluginFileSaveResponse
        {
            PluginId = "example",
            Saved = true,
            Outcome = "reload_failed",
            Message = "Library saved the file but could not load the plugin: it does nothing.",
            Text = text,
            State = State(),
            Reloaded = false,
            Health = "needs_attention",
            HealthSummary = "Library saved the file but could not load the plugin.",
        };
        var host = Render<AppDialogHost>();
        var cut = RenderCard(State(), canEdit: true);
        cut.Find("[data-testid=plugin-file-edit]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-editor]")));
        cut.Find("textarea").Input(Edited);
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=plugin-file-save]").HasAttribute("disabled")));
        cut.Find("[data-testid=plugin-file-save]").Click();
        host.WaitForElement("[data-testid=plugin-file-diff]");

        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Save changes").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-result]")));
        Assert.Equal("reload_failed", cut.Find("[data-testid=plugin-file-result]").GetAttribute("data-outcome"));
        Assert.Contains("could not load the plugin", cut.Find("[data-testid=plugin-file-result]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=plugin-file-editor]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-reset-after]"));

        cut.Find("[data-testid=plugin-file-reset-after]").Click();
        cut.WaitForAssertion(() => Assert.Equal(1, _resets));
    }

    [Fact]
    public void AfterAGoodSave_TheCardGlows_AndOffersNoReset()
    {
        _file = File(Original, State());
        var host = Render<AppDialogHost>();
        var cut = RenderCard(State(), canEdit: true);
        cut.Find("[data-testid=plugin-file-edit]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-editor]")));
        cut.Find("textarea").Input(Edited);
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=plugin-file-save]").HasAttribute("disabled")));
        cut.Find("[data-testid=plugin-file-save]").Click();
        host.WaitForElement("[data-testid=plugin-file-diff]");
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Save changes").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-file-result]")));
        Assert.Contains("plugin-file__result--saved", cut.Find("[data-testid=plugin-file-result]").ClassName, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=plugin-file-reset-after]"));
    }

    // ------------------------------------------------------------------------------------------------ The comparison

    [Fact]
    public void TheDiff_MarksRemovedAndAddedLines_WithTheirNumbers_AndFoldsLongUnchangedRuns()
    {
        var before = string.Join("\n", Enumerable.Range(1, 30).Select(n => $"line {n}"));
        var after = before.Replace("line 15", "line fifteen", StringComparison.Ordinal);

        var lines = PluginFileDiff.WithContext(PluginFileDiff.Compare(before, after));

        Assert.Equal(PluginFileDiffKind.Skipped, lines[0].Kind);
        Assert.Equal("12 unchanged lines", lines[0].Text);
        var removed = Assert.Single(lines, line => line.Kind == PluginFileDiffKind.Removed);
        var added = Assert.Single(lines, line => line.Kind == PluginFileDiffKind.Added);
        Assert.Equal((15, null, "line 15"), (removed.OldNumber, removed.NewNumber, removed.Text));
        Assert.Equal((null, 15, "line fifteen"), (added.OldNumber, added.NewNumber, added.Text));
        Assert.Equal(PluginFileDiffKind.Skipped, lines[^1].Kind);
    }

    [Fact]
    public void TheDiff_IgnoresLineEndings_AndSeesInsertedAndDeletedLines()
    {
        Assert.False(PluginFileDiff.Differ("a\r\nb\r\n", "a\nb\n"));
        Assert.True(PluginFileDiff.Differ("a\nb", "a\nc"));

        var lines = PluginFileDiff.Compare("a\nb\nc", "a\nx\nb\nc\nd");

        Assert.Equal(["x", "d"], lines.Where(line => line.Kind == PluginFileDiffKind.Added).Select(line => line.Text).ToArray());
        Assert.DoesNotContain(lines, line => line.Kind == PluginFileDiffKind.Removed);
    }

    // ------------------------------------------------------------------------------------------------ Helpers

    private IRenderedComponent<PluginFileEditorPanel> Panel(PluginFileResponse file, List<PluginFileSaveResponse?>? done = null, bool answerChecks = true)
    {
        if (!answerChecks)
        {
            Services.AddSingleton(EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.CheckPluginFileAsync), _ => Task.FromResult<PluginFileCheckResponse?>(null))));
        }

        return Render<PluginFileEditorPanel>(parameters => parameters
            .Add(component => component.PluginId, "example")
            .Add(component => component.Initial, file)
            .Add(component => component.CheckDelay, TimeSpan.Zero)
            .Add(component => component.Done, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<PluginFileSaveResponse?>(this, result => done?.Add(result))));
    }

    private IRenderedComponent<PluginFileCard> RenderCard(PluginFileStateDto state, bool canEdit, bool canView = true) =>
        Render<PluginFileCard>(parameters => parameters
            .Add(component => component.PluginId, "example")
            .Add(component => component.Card, FileCard(state))
            .Add(component => component.CanView, canView)
            .Add(component => component.CanEdit, canEdit));

    private static PluginSetupCardDto FileCard(PluginFileStateDto state) => new() { Key = "file", Title = "Plugin file", File = state };

    private static PluginFileResponse File(string text, PluginFileStateDto state) =>
        new() { PluginId = "example", Text = text, State = state, Problems = [] };

    private static PluginFileStateDto State(int errors = 0, int notes = 0, bool shipped = true, bool editable = true) => new()
    {
        FileName = "example.json",
        Kind = "manifest",
        State = errors == 0 ? "valid" : "problems",
        ErrorCount = errors,
        NoteCount = notes,
        HasShippedCopy = shipped,
        Editable = editable,
    };
}
