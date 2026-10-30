using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Watch;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// Bookmarks in the video player (Videos spec WP15, D43): the audiobook Add and Saved panel, driven by the same bookmark owner, with a
// longer description, the Share switch for server administrators only, shared bookmarks marked "Everyone", and ticks with a
// quick-look card on the progress rail.
public sealed class VideoBookmarkTests : AsyncBunitContext
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid SomeoneElse = Guid.NewGuid();
    private static readonly Guid WorkId = Guid.NewGuid();
    private static readonly Guid AssetId = Guid.NewGuid();

    public VideoBookmarkTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/audiobook-bookmark-dialog.js");
        JSInterop.SetupModule("./js/playback-tool-sheet.js");
        JSInterop.SetupModule("./js/playback-seek-rail.js");
        Services.AddLogging();
    }

    // ---------------------------------------------------------------- the owner service

    [Fact]
    public async Task Save_SendsTheLongerDescriptionAndTheShareChoice_ToTheEngine()
    {
        CreateAudiobookBookmarkRequestDto? sent = null;
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.CreateAudiobookBookmarkWithOutcomeAsync), args =>
        {
            sent = (CreateAudiobookBookmarkRequestDto)args![1]!;
            return Task.FromResult(AudiobookBookmarkOperationResult<AudiobookBookmarkDto>.Succeeded(
                Dto(Guid.NewGuid(), Me, 750, global: sent.IsGlobal) with { Note = sent.Note, Description = sent.Description }));
        }));
        var service = new AudiobookBookmarkActionService(api);
        var context = Context();
        service.Open(context);
        Assert.True(service.TryCapture(context, AssetId, new HashSet<Guid> { AssetId }, 750, 7200, null, out _));
        var generation = service.GetSnapshot(context.DialogId).Draft!.Generation;

        var saved = await service.SaveWithFieldsAsync(context, generation, new HashSet<Guid> { AssetId },
            new AudiobookBookmarkSaveFields("  The reveal ", "  Watch the window.  ", ShareWithEveryone: true));

        Assert.Equal(AudiobookBookmarkOperationOutcome.Success, saved.Outcome);
        Assert.Equal("The reveal", sent!.Note);
        Assert.Equal("Watch the window.", sent.Description);
        Assert.True(sent.IsGlobal);
        Assert.Equal(750, sent.PositionSeconds);
        Assert.Equal(AssetId, sent.AssetId);
    }

    [Fact]
    public async Task Save_WithoutTheShareSwitch_IsPersonal_AndTheNoteOnlyOverloadStillWorks()
    {
        CreateAudiobookBookmarkRequestDto? sent = null;
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.CreateAudiobookBookmarkWithOutcomeAsync), args =>
        {
            sent = (CreateAudiobookBookmarkRequestDto)args![1]!;
            return Task.FromResult(AudiobookBookmarkOperationResult<AudiobookBookmarkDto>.Succeeded(Dto(Guid.NewGuid(), Me, 90)));
        }));
        var service = new AudiobookBookmarkActionService(api);
        var context = Context();
        service.Open(context);
        service.TryCapture(context, AssetId, new HashSet<Guid> { AssetId }, 90, 7200, null, out _);

        await service.SaveAsync(context, service.GetSnapshot(context.DialogId).Draft!.Generation, new HashSet<Guid> { AssetId }, "just a note");

        Assert.False(sent!.IsGlobal);
        Assert.Null(sent.Description);
        Assert.Equal("just a note", sent.Note);
    }

    [Fact]
    public async Task EveryBookmarkTheEngineReturns_IsListedAndCanBeReplayed_WhateverProfileIdTheDashboardHolds()
    {
        // The Engine already returns only the signed-in profile's own bookmarks plus the shared ones, so the
        // Dashboard must not second-guess ownership with its own copy of the profile id.
        var shared = Dto(Guid.NewGuid(), SomeoneElse, 300, global: true);
        var personal = Dto(Guid.NewGuid(), SomeoneElse, 400);
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetAudiobookBookmarksWithOutcomeAsync), _ =>
            Task.FromResult(AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>.Succeeded([personal, shared]))));
        AudiobookBookmarkDto? replayed = null;
        var service = new AudiobookBookmarkActionService(api, null, (bookmark, _) => { replayed = bookmark; return Task.CompletedTask; });
        var context = Context();
        service.Open(context);
        var assets = new HashSet<Guid> { AssetId };

        await service.LoadSavedAsync(context, assets);

        Assert.Equal([personal.Id, shared.Id], service.GetSnapshot(context.DialogId).Saved.Select(item => item.Id).ToArray());
        var result = await service.ReplayAsync(context, personal.Id, assets);
        Assert.Equal(AudiobookBookmarkOperationOutcome.Success, result.Outcome);
        Assert.Equal(personal.Id, replayed!.Id);
    }

    // ---------------------------------------------------------------- the dialog

    [Fact]
    public void Add_ForAnAdministrator_OffersTheLongerDescriptionAndAShareSwitchThatIsOffByDefault()
    {
        var (service, context) = OpenedService();
        var cut = RenderDialog(service, context, canShare: true, allowDescription: true);

        Assert.Equal(2, cut.FindAll("textarea").Count);
        Assert.Contains("0 / 200", cut.Markup);
        Assert.Contains("0 / 4000", cut.Markup);
        var share = cut.Find("input[role=switch]");
        Assert.Contains("Share with everyone", cut.Markup);
        Assert.False(share.HasAttribute("checked"));
        Assert.Contains("Position in video", cut.Markup);
    }

    [Fact]
    public void Add_ForAnyoneElse_NeverShowsTheShareSwitch()
    {
        var (service, context) = OpenedService();
        var cut = RenderDialog(service, context, canShare: false, allowDescription: true);

        Assert.Empty(cut.FindAll("input[role=switch]"));
        Assert.DoesNotContain("Share with everyone", cut.Markup);
        Assert.Equal(2, cut.FindAll("textarea").Count);
    }

    [Fact]
    public void TheAudiobookDialog_KeepsTheShortNoteAlone_AsBefore()
    {
        var (service, context) = OpenedService();
        var cut = Render<AudiobookBookmarkDialog>(parameters => parameters
            .Add(component => component.Context, context)
            .Add(component => component.Actions, service)
            .Add(component => component.AuthorizedAssetIds, new HashSet<Guid> { AssetId })
            .Add(component => component.WorkTitle, "A book"));

        Assert.Single(cut.FindAll("textarea"));
        Assert.Empty(cut.FindAll("input[role=switch]"));
        Assert.Contains("Listening position", cut.Markup);
    }

    [Fact]
    public async Task Saving_PassesTheTypedNoteDescriptionAndSwitchToTheOwner()
    {
        CreateAudiobookBookmarkRequestDto? sent = null;
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.CreateAudiobookBookmarkWithOutcomeAsync), args =>
        {
            sent = (CreateAudiobookBookmarkRequestDto)args![1]!;
            return Task.FromResult(AudiobookBookmarkOperationResult<AudiobookBookmarkDto>.Succeeded(
                Dto(Guid.NewGuid(), Me, 750, global: sent.IsGlobal) with { Note = sent.Note, Description = sent.Description }));
        }));
        var (service, context) = OpenedService(api);
        var cut = RenderDialog(service, context, canShare: true, allowDescription: true);

        cut.FindAll("textarea")[0].Input("The reveal");
        cut.FindAll("textarea")[1].Input("Watch the reflection in the window.");
        cut.Find("input[role=switch]").Change(true);
        await cut.Find("form").SubmitAsync();

        Assert.Equal("The reveal", sent!.Note);
        Assert.Equal("Watch the reflection in the window.", sent.Description);
        Assert.True(sent.IsGlobal);
        Assert.Equal("Saved", cut.Find("button.audiobook-bookmark-dialog__tab.is-selected").TextContent.Trim());
    }

    [Fact]
    public async Task Saved_ShowsSharedBookmarksAsEveryone_AndOnlyAnAdministratorCanDeleteThem()
    {
        var shared = Dto(Guid.NewGuid(), SomeoneElse, 300, global: true) with { Note = "For all", Description = "The long story." };
        var mine = Dto(Guid.NewGuid(), Me, 500) with { Note = "Mine" };
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetAudiobookBookmarksWithOutcomeAsync), _ =>
            Task.FromResult(AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>.Succeeded([mine, shared]))));

        var (service, context) = OpenedService(api);
        var asViewer = RenderDialog(service, context, canShare: false, allowDescription: true);
        await asViewer.Find("button.audiobook-bookmark-dialog__tab.is-selected ~ button").ClickAsync();
        Assert.Equal(2, asViewer.FindAll("[data-bookmark-swipe-row]").Count);
        Assert.Contains("Everyone", asViewer.Markup);
        Assert.Single(asViewer.FindAll("button.audiobook-bookmark-dialog__delete-request"));
        Assert.Single(asViewer.FindAll("button.audiobook-bookmark-dialog__swipe-delete"));

        // Opening the longer text shows the short note and the description.
        await asViewer.FindAll("button.audiobook-bookmark-dialog__note-toggle")[1].ClickAsync();
        Assert.Contains("The long story.", asViewer.Markup);

        var (adminService, adminContext) = OpenedService(api);
        var asAdmin = RenderDialog(adminService, adminContext, canShare: true, allowDescription: true);
        await asAdmin.Find("button.audiobook-bookmark-dialog__tab.is-selected ~ button").ClickAsync();
        Assert.Equal(2, asAdmin.FindAll("button.audiobook-bookmark-dialog__delete-request").Count);
    }

    // ---------------------------------------------------------------- the progress rail

    [Fact]
    public async Task TheRail_DrawsATickPerBookmark_AndTheQuickLookShowsTheNoteThenTheDescriptionUnderMore()
    {
        var jumped = new List<PlaybackRailBookmark>();
        var bookmarks = PlaybackRailBookmark.From(
        [
            Dto(Guid.NewGuid(), Me, 750) with { Note = "The reveal", Description = "Watch the reflection." },
            Dto(Guid.NewGuid(), SomeoneElse, 1800, global: true) with { Note = "For everyone" },
        ], 7200);
        var cut = Render<PlaybackSeekRail>(parameters => parameters
            .Add(component => component.Position, 0d)
            .Add(component => component.Duration, 7200d)
            .Add(component => component.Bookmarks, bookmarks)
            .Add(component => component.BookmarkJump, EventCallback.Factory.Create<PlaybackRailBookmark>(this, jumped.Add)));

        var ticks = cut.FindAll("button.playback-seek-rail__bookmark");
        Assert.Equal(2, ticks.Count);
        Assert.Contains("12:30", ticks[0].GetAttribute("aria-label"));
        Assert.Contains("Shared bookmark", ticks[1].GetAttribute("aria-label"));
        Assert.Contains("left:10.417%", cut.FindAll(".playback-seek-rail__bookmark-wrap")[0].GetAttribute("style"));
        Assert.Empty(cut.FindAll(".playback-seek-rail__quicklook"));

        // Pointing at it (desktop) shows the short note; More shows the longer description.
        cut.FindAll(".playback-seek-rail__bookmark-wrap")[0].MouseEnter();
        Assert.Contains("The reveal", cut.Find(".playback-seek-rail__quicklook").TextContent);
        Assert.DoesNotContain("Watch the reflection.", cut.Markup);
        cut.Find(".playback-seek-rail__quicklook button.playback-seek-rail__more").Click();
        Assert.Contains("Watch the reflection.", cut.Find(".playback-seek-rail__quicklook").TextContent);

        cut.FindAll(".playback-seek-rail__bookmark-wrap")[0].MouseLeave();
        Assert.Empty(cut.FindAll(".playback-seek-rail__quicklook"));

        // A tap (touch) keeps the card open until it is closed or jumped from.
        cut.FindAll("button.playback-seek-rail__bookmark")[1].Click();
        cut.FindAll(".playback-seek-rail__bookmark-wrap")[1].MouseLeave();
        var card = cut.Find(".playback-seek-rail__quicklook");
        Assert.Contains("Everyone", card.TextContent);
        Assert.Contains("For everyone", card.TextContent);
        Assert.DoesNotContain(cut.FindAll(".playback-seek-rail__quicklook button.playback-seek-rail__more"), button => button.TextContent == "More");

        var jump = cut.FindAll(".playback-seek-rail__quicklook button.playback-seek-rail__more").First(button => button.TextContent == "Jump here");
        await jump.ClickAsync();
        Assert.Equal(1800, Assert.Single(jumped).Seconds);
        Assert.Empty(cut.FindAll(".playback-seek-rail__quicklook"));
    }

    [Fact]
    public void TheRail_WithNoBookmarks_DrawsNothingExtra()
    {
        var cut = Render<PlaybackSeekRail>(parameters => parameters
            .Add(component => component.Position, 0d)
            .Add(component => component.Duration, 600d));

        Assert.Empty(cut.FindAll("[data-playback-bookmark-ticks]"));
    }

    [Fact]
    public void TheRailTicks_LeaveOutABookmarkBeyondTheEndOfTheFile_AndComeInTimeOrder()
    {
        var ticks = PlaybackRailBookmark.From(
        [
            Dto(Guid.NewGuid(), Me, 5000),
            Dto(Guid.NewGuid(), Me, 100),
            Dto(Guid.NewGuid(), Me, 900),
        ], 1000);

        Assert.Equal([100d, 900d], ticks.Select(tick => tick.Seconds).ToArray());
    }

    // ---------------------------------------------------------------- the video player panel

    [Fact]
    public async Task ThePanel_CapturesThePositionWhenItOpens_ShowsTheChapter_AndPreviewJumpsThere()
    {
        var jumps = new List<(double Seconds, bool Play)>();
        var api = EngineApiClientStub.Create(_ => { });
        Services.AddSingleton(api);
        var cut = Render<VideoBookmarkPanel>(parameters => parameters
            .Add(component => component.ProfileId, Me)
            .Add(component => component.WorkId, WorkId)
            .Add(component => component.AssetId, AssetId)
            .Add(component => component.Title, "The Film")
            .Add(component => component.Position, 750d)
            .Add(component => component.Duration, 7200d)
            .Add(component => component.Chapters, new[]
            {
                new PlaybackChapterDto { Index = 0, Title = "Opening", StartSeconds = 0 },
                new PlaybackChapterDto { Index = 1, Title = "The Bridge", StartSeconds = 600 },
                new PlaybackChapterDto { Index = 2, Title = "Finale", StartSeconds = 3000 },
            })
            .Add(component => component.JumpTo, (Func<double, bool, Task>)((seconds, play) => { jumps.Add((seconds, play)); return Task.CompletedTask; })));

        cut.WaitForAssertion(() => Assert.Contains("00:12:30", cut.Markup));
        Assert.Contains("The Bridge", cut.Markup);
        Assert.Equal("Add", cut.Find("button.audiobook-bookmark-dialog__tab.is-selected").TextContent.Trim());
        Assert.Empty(cut.FindAll("input[role=switch]"));

        await cut.Find("button.audiobook-bookmark-dialog__preview").ClickAsync();
        Assert.Equal((750d, true), Assert.Single(jumps));
    }

    [Fact]
    public async Task ThePanel_SavesAtTheCapturedMomentEvenIfTheVideoHasMovedOn_AndReportsTheListForTheRail()
    {
        CreateAudiobookBookmarkRequestDto? sent = null;
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.CreateAudiobookBookmarkWithOutcomeAsync), args =>
        {
            sent = (CreateAudiobookBookmarkRequestDto)args![1]!;
            return Task.FromResult(AudiobookBookmarkOperationResult<AudiobookBookmarkDto>.Succeeded(
                Dto(Guid.NewGuid(), Me, sent.PositionSeconds) with { Note = sent.Note, Description = sent.Description, ChapterTitle = sent.ChapterTitle }));
        }));
        Services.AddSingleton(api);
        IReadOnlyList<AudiobookBookmarkDto>? reported = null;
        var cut = Render<VideoBookmarkPanel>(parameters => parameters
            .Add(component => component.ProfileId, Me)
            .Add(component => component.WorkId, WorkId)
            .Add(component => component.AssetId, AssetId)
            .Add(component => component.Position, 750d)
            .Add(component => component.Duration, 7200d)
            .Add(component => component.CanShare, true)
            .Add(component => component.SavedChanged, EventCallback.Factory.Create<IReadOnlyList<AudiobookBookmarkDto>>(this, saved => reported = saved)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("textarea")));

        // The video keeps playing while the person types: the new position must not replace the captured one.
        cut.Render(parameters => parameters.Add(component => component.Position, 9000d));
        cut.FindAll("textarea")[0].Input("The reveal");
        cut.FindAll("textarea")[1].Input("Watch the window.");
        await cut.Find("form").SubmitAsync();

        Assert.Equal(750, sent!.PositionSeconds);
        Assert.Equal("The reveal", sent.Note);
        Assert.Equal("Watch the window.", sent.Description);
        Assert.False(sent.IsGlobal);
        cut.WaitForAssertion(() => Assert.NotNull(reported));
        Assert.Contains(reported!, bookmark => bookmark.PositionSeconds == 750);
    }

    // ---------------------------------------------------------------- the control catalogue

    [Fact]
    public void TheVideoControlCatalogue_OffersBookmarksOnlyWhenTheHostCanBookmark()
    {
        var without = PlaybackControlCatalog.Build(PlaybackExperience.Video, PlaybackControlSurface.PrimaryVideo, new PlaybackControlState());
        var with = PlaybackControlCatalog.Build(PlaybackExperience.Video, PlaybackControlSurface.PrimaryVideo, new PlaybackControlState(CanBookmark: true));

        Assert.DoesNotContain(without, control => control.Key == PlaybackControlKey.Bookmarks);
        var bookmarks = Assert.Single(with, control => control.Key == PlaybackControlKey.Bookmarks);
        Assert.Equal("Bookmarks", bookmarks.Label);
    }

    // ---------------------------------------------------------------- helpers

    private static AudiobookBookmarkActionContext Context() => new(Guid.NewGuid(), Me, WorkId, Guid.NewGuid(), 0, AssetId);

    private static AudiobookBookmarkDto Dto(Guid id, Guid profile, double seconds, bool global = false) => new()
    {
        Id = id,
        ProfileId = profile,
        WorkId = WorkId,
        AssetId = AssetId,
        PositionSeconds = seconds,
        DurationSeconds = 7200,
        IsGlobal = global,
        CreatedAt = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero).AddSeconds(seconds),
    };

    private static (AudiobookBookmarkActionService Service, AudiobookBookmarkActionContext Context) OpenedService(IEngineApiClient? api = null)
    {
        var service = new AudiobookBookmarkActionService(api ?? EngineApiClientStub.Create(_ => { }));
        var context = Context();
        service.Open(context);
        Assert.True(service.TryCapture(context, AssetId, new HashSet<Guid> { AssetId }, 750, 7200, null, out _));
        return (service, context);
    }

    private IRenderedComponent<AudiobookBookmarkDialog> RenderDialog(
        AudiobookBookmarkActionService service, AudiobookBookmarkActionContext context, bool canShare, bool allowDescription) =>
        Render<AudiobookBookmarkDialog>(parameters => parameters
            .Add(component => component.Context, context)
            .Add(component => component.Actions, service)
            .Add(component => component.AuthorizedAssetIds, new HashSet<Guid> { AssetId })
            .Add(component => component.WorkTitle, "The Film")
            .Add(component => component.CanShare, canShare)
            .Add(component => component.AllowDescription, allowDescription)
            .Add(component => component.PositionLabel, "Position in video"));
}
