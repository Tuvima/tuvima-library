using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.WriteBack;
using MediaEngine.Web.Components.MediaEditor.Sections;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Formatting;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Settings > Libraries > Advanced: how often changes are written into files, what is waiting, Write now, and the files that
/// failed (batched-writeback spec, section 4); and the waiting note on the Editing Mode file card (D12).
/// </summary>
public sealed class LibraryWriteScheduleTests : AsyncBunitContext
{
    private static readonly DateTimeOffset LastRun = DateTimeOffset.UtcNow.AddMinutes(-30);
    private static readonly DateTimeOffset NextRun = DateTimeOffset.UtcNow.AddMinutes(30);

    private FileWriteScheduleDto? _schedule = Waiting(12);
    private ToolRequirementsDto? _tools = Tools(ToolRequirementStates.Ready);
    private IReadOnlyList<FailedFileWriteDto> _failed = [];
    private FileWriteRunResult _runResult = new(true, Waiting(12));
    private readonly List<int> _saved = [];
    private int _runs;
    private int _scheduleReads;

    public LibraryWriteScheduleTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetFileWriteScheduleAsync), _ =>
            {
                Interlocked.Increment(ref _scheduleReads);
                return Task.FromResult(_schedule);
            });
            stub.SetHandler(nameof(IEngineApiClient.UpdateFileWriteScheduleAsync), args =>
            {
                var minutes = (int)args![0]!;
                _saved.Add(minutes);
                _schedule = Copy(_schedule!, interval: minutes);
                return Task.FromResult<FileWriteScheduleDto?>(_schedule);
            });
            stub.SetHandler(nameof(IEngineApiClient.StartFileWriteRunAsync), _ =>
            {
                _runs++;
                if (_runResult.Started && _runResult.Schedule is not null)
                {
                    _schedule = _runResult.Schedule;
                }

                return Task.FromResult(_runResult);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetFailedFileWritesAsync), _ => Task.FromResult(_failed));
            stub.SetHandler(nameof(IEngineApiClient.GetToolRequirementsAsync), _ => Task.FromResult(_tools));
        }));
    }

    private static FileWriteScheduleDto Waiting(int count) => new()
    {
        Enabled = true,
        IntervalMinutes = 60,
        WaitingCount = count,
        LastRunAt = LastRun,
        NextRunAt = NextRun,
    };

    private static FileWriteScheduleDto Copy(FileWriteScheduleDto source, int? interval = null) => new()
    {
        Enabled = source.Enabled,
        IntervalMinutes = interval ?? source.IntervalMinutes,
        WaitingCount = source.WaitingCount,
        FailedCount = source.FailedCount,
        IsRunning = source.IsRunning,
        RunTotal = source.RunTotal,
        RunDone = source.RunDone,
        LastRunAt = source.LastRunAt,
        NextRunAt = source.NextRunAt,
    };

    private static ToolRequirementsDto Tools(string exifToolState) => new(
        "windows_installer", DateTimeOffset.UtcNow, exifToolState != ToolRequirementStates.Ready,
        [new ToolRequirementDto(ToolRequirementIds.ExifTool, "ExifTool", "tool", ToolRequirementLevels.Optional, exifToolState,
            "Photos, videos and PDFs in View wait for ExifTool.", null, "Download ExifTool.", null, 0, false, true, false, null)]);

    private IRenderedComponent<LibraryWriteSchedulePanel> Open(bool readOnly = false, TimeSpan? poll = null)
    {
        var cut = Render<LibraryWriteSchedulePanel>(parameters =>
        {
            parameters.Add(x => x.ReadOnly, readOnly);
            if (poll is { } interval)
            {
                parameters.Add(x => x.PollInterval, interval);
            }
        });
        cut.WaitForAssertion(() => Assert.DoesNotContain("Checking the file write schedule", cut.Markup));
        return cut;
    }

    private static string Text(IRenderedComponent<LibraryWriteSchedulePanel> cut) =>
        System.Text.RegularExpressions.Regex.Replace(cut.Find(".write-schedule").TextContent, "\\s+", " ").Trim();

    private static AngleSharp.Dom.IElement? WriteNow(IRenderedComponent<LibraryWriteSchedulePanel> cut) =>
        cut.FindAll("button").FirstOrDefault(button => button.TextContent.Trim() == "Write now");

    private static AngleSharp.Dom.IElement MinutesInput(IRenderedComponent<LibraryWriteSchedulePanel> cut) => cut.Find("input.app-text-field");

    [Fact]
    public void NormalState_ShowsTheIntervalTheWaitingCountBothRunTimesAndAnEnabledWriteNow()
    {
        var cut = Open();

        var text = Text(cut);
        Assert.Contains("Write changes into files every", text);
        Assert.Equal("60", MinutesInput(cut).GetAttribute("value"));
        Assert.Contains("minutes", text);
        Assert.Contains($"12 files waiting · Last run {FileWriteStatusText.FormatTime(LastRun)} · Next run {FileWriteStatusText.FormatTime(NextRun)}", text);
        Assert.False(WriteNow(cut)!.HasAttribute("disabled"));
    }

    [Fact]
    public void NothingWaiting_SaysFilesAreUpToDateAndDisablesWriteNow()
    {
        _schedule = Waiting(0);
        var cut = Open();

        Assert.Contains($"All files are up to date · Last run {FileWriteStatusText.FormatTime(LastRun)}", Text(cut));
        Assert.DoesNotContain("waiting", Text(cut));
        Assert.True(WriteNow(cut)!.HasAttribute("disabled"));
    }

    [Fact]
    public void RunInProgress_ShowsProgressWithThousandsSeparatorsAndDisablesWriteNow()
    {
        _schedule = new FileWriteScheduleDto { Enabled = true, IntervalMinutes = 60, WaitingCount = 960, IsRunning = true, RunTotal = 1200, RunDone = 240 };
        var cut = Open(poll: TimeSpan.FromMinutes(5));

        Assert.Contains("Writing 240 of 1,200 files…", Text(cut));
        Assert.True(WriteNow(cut)!.HasAttribute("disabled"));
    }

    [Fact]
    public void RunInProgress_WhenTheEngineHidesTheNumbers_SaysWritingFilesWithoutZeroOfZero()
    {
        _schedule = new FileWriteScheduleDto { Enabled = true, IntervalMinutes = 60, WaitingCount = 0, IsRunning = true, RunTotal = 0, RunDone = 0 };
        var cut = Open(poll: TimeSpan.FromMinutes(5));

        Assert.Contains("Writing files…", Text(cut));
        Assert.DoesNotContain("0 of 0", Text(cut));
        Assert.True(WriteNow(cut)!.HasAttribute("disabled"));
    }

    [Fact]
    public void RunInProgress_RefreshesTheStatusUntilTheRunEndsThenStopsAsking()
    {
        _schedule = new FileWriteScheduleDto { Enabled = true, IntervalMinutes = 60, WaitingCount = 5, IsRunning = true, RunTotal = 5, RunDone = 1 };
        var cut = Open(poll: TimeSpan.FromMilliseconds(30));
        Assert.Contains("Writing 1 of 5 files…", Text(cut));

        _schedule = Waiting(0);

        cut.WaitForAssertion(() => Assert.Contains("All files are up to date", Text(cut)));
        var readsWhenDone = Volatile.Read(ref _scheduleReads);
        Thread.Sleep(200);
        Assert.Equal(readsWhenDone, Volatile.Read(ref _scheduleReads));
    }

    [Fact]
    public void LeavingThePage_StopsTheRefreshWhileARunIsGoing()
    {
        _schedule = new FileWriteScheduleDto { Enabled = true, IntervalMinutes = 60, WaitingCount = 5, IsRunning = true, RunTotal = 5, RunDone = 1 };
        var cut = Open(poll: TimeSpan.FromMilliseconds(30));
        cut.WaitForAssertion(() => Assert.True(Volatile.Read(ref _scheduleReads) >= 2));

        cut.Instance.Dispose();
        var reads = Volatile.Read(ref _scheduleReads);
        Thread.Sleep(200);

        Assert.True(Volatile.Read(ref _scheduleReads) <= reads + 1);
    }

    [Fact]
    public void SomeFilesFailed_SaysHowManyAndOpensTheListWithTitlePathAndReason()
    {
        _schedule = new FileWriteScheduleDto { Enabled = true, IntervalMinutes = 60, WaitingCount = 0, FailedCount = 3, LastRunAt = LastRun };
        _failed =
        [
            new FailedFileWriteDto { TargetKind = "media_asset", Title = "Dune", Path = "E:\\Books\\Dune.epub", Reason = "The file is read-only." },
            new FailedFileWriteDto { TargetKind = "view_item", Title = "Holiday", Path = "E:\\View\\holiday.pdf", Reason = "The PDF is encrypted." },
            new FailedFileWriteDto { TargetKind = "media_asset", Title = "Alien", Path = "E:\\Movies\\Alien.mkv", Reason = "Another app has the file open." },
        ];
        var cut = Open();

        Assert.Contains("3 files couldn't be written", Text(cut));
        Assert.Empty(cut.FindAll(".write-schedule__failed-list"));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Show the files").Click();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".write-schedule__failed-list li").Count));
        var list = Text(cut);
        Assert.Contains("Dune", list);
        Assert.Contains("E:\\Books\\Dune.epub", list);
        Assert.Contains("The file is read-only.", list);
        Assert.Contains("The PDF is encrypted.", list);
    }

    [Fact]
    public void ExifToolMissing_SaysViewFilesAreWaitingAndLinksToTools()
    {
        _tools = Tools(ToolRequirementStates.Missing);
        var cut = Open();

        Assert.Contains("Photos, videos and PDFs in View are waiting for ExifTool", Text(cut));
        Assert.Equal("/settings/tools", cut.Find(".write-schedule a").GetAttribute("href"));
    }

    [Fact]
    public void ExifToolReady_ShowsNoExifToolMessage()
    {
        var cut = Open();

        Assert.DoesNotContain("ExifTool", Text(cut));
    }

    [Fact]
    public void WriteBackTurnedOff_HidesTheIntervalAndWriteNow()
    {
        _schedule = new FileWriteScheduleDto { Enabled = false, IntervalMinutes = 60 };
        var cut = Open();

        Assert.Empty(cut.FindAll("input"));
        Assert.Null(WriteNow(cut));
        Assert.DoesNotContain("Write changes into files", Text(cut));
    }

    [Fact]
    public void EngineUnreachable_ShowsTheUsualSettingsErrorAndNoControls()
    {
        _schedule = null;
        var cut = Open();

        Assert.Contains("The Engine is unavailable", cut.Find(".write-schedule").TextContent);
        Assert.Empty(cut.FindAll("input"));
        Assert.Null(WriteNow(cut));
    }

    [Fact]
    public void OnAPhone_ShowsTheStatusTextWithNoInputOrButtons()
    {
        _schedule = new FileWriteScheduleDto { Enabled = true, IntervalMinutes = 60, WaitingCount = 12, FailedCount = 2, LastRunAt = LastRun, NextRunAt = NextRun };
        var cut = Open(readOnly: true);

        Assert.Contains("12 files waiting", Text(cut));
        Assert.Contains("2 files couldn't be written", Text(cut));
        Assert.Empty(cut.FindAll("input"));
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public void ChangingTheInterval_SavesItAndShowsTheSavedValue()
    {
        var cut = Open();

        MinutesInput(cut).Change("30");

        cut.WaitForAssertion(() => Assert.Equal([30], _saved));
        cut.WaitForAssertion(() => Assert.Equal("30", MinutesInput(cut).GetAttribute("value")));
        Assert.Empty(cut.FindAll(".tl-field-error"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1441")]
    [InlineData("soon")]
    [InlineData("-5")]
    public void AnOutOfRangeInterval_ShowsAMessageAndSavesNothing(string typed)
    {
        var cut = Open();

        MinutesInput(cut).Change(typed);

        cut.WaitForAssertion(() => Assert.Contains("from 1 to 1,440", cut.Find(".tl-field-error").TextContent));
        Assert.Empty(_saved);
    }

    [Fact]
    public void WriteNow_AsksTheEngineToRunAndShowsTheRunThatStarted()
    {
        _runResult = new(true, new FileWriteScheduleDto { Enabled = true, IntervalMinutes = 60, WaitingCount = 12, IsRunning = true, RunTotal = 12, RunDone = 0 });
        var cut = Open(poll: TimeSpan.FromMinutes(5));

        WriteNow(cut)!.Click();

        cut.WaitForAssertion(() => Assert.Contains("Writing 0 of 12 files…", Text(cut)));
        Assert.Equal(1, _runs);
        Assert.True(WriteNow(cut)!.HasAttribute("disabled"));
    }

    [Fact]
    public void WriteNow_WhenARunIsAlreadyGoing_ShowsThePlainMessageNotAnError()
    {
        _runResult = new(false, Message: "A write run is already going.");
        var cut = Open(poll: TimeSpan.FromMinutes(5));

        WriteNow(cut)!.Click();

        cut.WaitForAssertion(() => Assert.Contains("A write run is already going.", cut.Find(".write-schedule__message").TextContent));
        Assert.Equal(1, _runs);
        Assert.Empty(cut.FindAll(".app-alert, [role=alert]"));
    }

    // ---- Editing Mode: the waiting note on the file card (D12) ----

    [Fact]
    public void EditingModeNote_WhenChangesAreWaiting_NamesTheNextRun()
    {
        var note = FileWriteStatusText.WaitingNote(new AssetFileWriteStatusDto { Waiting = true, NextRunAt = NextRun });

        Assert.Equal($"Changes waiting to be written to the file · next run {FileWriteStatusText.FormatTime(NextRun)}", note);
    }

    [Fact]
    public void EditingModeNote_WhenTheLastWriteFailed_GivesTheReason()
    {
        var status = new AssetFileWriteStatusDto { Waiting = false, LastError = "The file is read-only." };

        Assert.Equal("The last write to the file didn't work: The file is read-only.", FileWriteStatusText.FailureNote(status));
        Assert.Null(FileWriteStatusText.WaitingNote(status));
    }

    [Fact]
    public void EditingModeNote_WhenNothingIsWaitingAndNothingFailed_ShowsNothing()
    {
        var status = new AssetFileWriteStatusDto { Waiting = false, LastWrittenAt = LastRun };

        Assert.Null(FileWriteStatusText.WaitingNote(status));
        Assert.Null(FileWriteStatusText.FailureNote(status));
        Assert.Null(FileWriteStatusText.WaitingNote(null));
        Assert.Null(FileWriteStatusText.FailureNote(null));
    }

    [Fact]
    public void EditingModeFileCard_ShowsTheWaitingNoteAndTheFailureReasonOnlyWhenTheyApply()
    {
        var shown = RenderFileCard(
            FileWriteStatusText.WaitingNote(new AssetFileWriteStatusDto { Waiting = true, NextRunAt = NextRun }),
            FileWriteStatusText.FailureNote(new AssetFileWriteStatusDto { LastError = "Another app has the file open." }));

        Assert.Contains("Changes waiting to be written to the file · next run", shown.Markup);
        Assert.Contains("Another app has the file open.", shown.Find(".sme-details-file-note--failed").TextContent);

        var quiet = RenderFileCard(null, null);

        Assert.Empty(quiet.FindAll(".sme-details-file-note"));
    }

    private IRenderedComponent<EditorDetailsSection> RenderFileCard(string? waitingNote, string? failureNote) =>
        Render<EditorDetailsSection>(parameters => parameters
            .Add(x => x.IsSingleItem, true)
            .Add(x => x.HasActiveScope, true)
            .Add(x => x.ScopeId, "asset")
            .Add(x => x.DetailsImageShape, "poster")
            .Add(x => x.DetailsImageWidthClass, string.Empty)
            .Add(x => x.DetailsImageSizes, string.Empty)
            .Add(x => x.CurrentTargetTitle, "Dune")
            .Add(x => x.DetailsFiles, [new MediaEditorDetailsFilePresentation("Edition", "Dune.epub", "EPUB")])
            .Add(x => x.FileWriteWaitingNote, waitingNote)
            .Add(x => x.FileWriteFailureNote, failureNote));
}
