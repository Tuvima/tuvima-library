using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>The saved ribbon's named steps and "Re-match seasons and episodes" (universal editor v3, WP6, D8 items 1 and 3).</summary>
public sealed class EditorFollowUpAndRematchTests
{
    private static IReadOnlyList<(string Key, string State)> Steps(string state, string? resolvedQid = null, string? error = null)
    {
        var dto = EditorFollowUpPlan.From(Guid.NewGuid(), new IdentityJob { State = state, ResolvedQid = resolvedQid, LastError = error });
        return dto.Steps.Select(step => (step.Key, step.State)).ToList();
    }

    [Fact]
    public void FollowUp_WithNoJob_HasNoStepsAndIsFinished()
    {
        var dto = EditorFollowUpPlan.From(Guid.NewGuid(), null);

        Assert.Empty(dto.Steps);
        Assert.True(dto.Finished);
    }

    [Theory]
    [InlineData("Queued", "waiting", "waiting")]
    [InlineData("RetailSearching", "running", "waiting")]
    [InlineData("BridgeSearching", "running", "waiting")]
    [InlineData("QidResolved", "done", "waiting")]
    [InlineData("Hydrating", "done", "running")]
    public void FollowUp_NamesTheStepTheJobIsOn(string state, string details, string pictures)
    {
        var steps = Steps(state);

        Assert.Equal(["matched", "details", "pictures_and_cast", "related", "files"], steps.Select(step => step.Key));
        Assert.Equal("done", steps[0].State);
        Assert.Equal(details, steps[1].State);
        Assert.Equal(pictures, steps[2].State);
    }

    [Fact]
    public void FollowUp_WhileUniverseEnrichmentRuns_HasPicturesDoneAndRelatedRunning()
    {
        var steps = Steps("UniverseEnriching");

        Assert.Equal(["done", "done", "done", "running", "waiting"], steps.Select(step => step.State));
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("ReadyWithoutUniverse")]
    public void FollowUp_ForAFinishedJob_IsEveryStepDoneAndFinished(string state)
    {
        var dto = EditorFollowUpPlan.From(Guid.NewGuid(), new IdentityJob { State = state });

        Assert.All(dto.Steps, step => Assert.Equal("done", step.State));
        Assert.True(dto.Finished);
    }

    [Fact]
    public void FollowUp_ForAJobStillWorking_IsNotFinished()
    {
        Assert.False(EditorFollowUpPlan.From(Guid.NewGuid(), new IdentityJob { State = "Hydrating" }).Finished);
    }

    [Theory]
    [InlineData("RetailNoMatch")]
    [InlineData("QidNeedsReview")]
    public void FollowUp_WhenTheJobStopsInNeedsReview_ShowsTheDetailsStepAmberWithAReason(string state)
    {
        var dto = EditorFollowUpPlan.From(Guid.NewGuid(), new IdentityJob { State = state });

        var details = dto.Steps.Single(step => step.Key == "details");
        Assert.Equal("failed", details.State);
        Assert.Contains("Needs Review", details.Reason);
        Assert.True(dto.Finished);
    }

    [Fact]
    public void FollowUp_WhenTheJobFailsAfterTheRecordWasFound_BlamesThePicturesStepAndKeepsTheError()
    {
        var dto = EditorFollowUpPlan.From(Guid.NewGuid(), new IdentityJob { State = "Failed", ResolvedQid = "Q42", LastError = "The picture host timed out." });

        Assert.Equal("done", dto.Steps.Single(step => step.Key == "details").State);
        var pictures = dto.Steps.Single(step => step.Key == "pictures_and_cast");
        Assert.Equal("failed", pictures.State);
        Assert.Equal("The picture host timed out.", pictures.Reason);
    }

    [Fact]
    public void FollowUp_WhenTheJobFailsBeforeAnyRecordWasFound_BlamesTheDetailsStep()
    {
        var dto = EditorFollowUpPlan.From(Guid.NewGuid(), new IdentityJob { State = "Failed" });

        var details = dto.Steps.Single(step => step.Key == "details");
        Assert.Equal("failed", details.State);
        Assert.False(string.IsNullOrWhiteSpace(details.Reason));
    }

    private static TvMatchBrowserEpisodeDto Slot(int season, int episode) => new(
        $"e{season}{episode}", "200", "New Show", $"s{season}", season, episode, $"Title {season}x{episode}", null, null, null, "https://thetvdb.test", false, false, null);

    private static ShowChildRematchPlan.Child Child(int? season, string? number, string file = "x.mkv") =>
        new(Guid.NewGuid(), Guid.NewGuid(), file, season, number);

    [Fact]
    public void Pair_GivesEachEpisodeTheSlotWithItsSeasonAndNumber_AndKeepsTheOnesTheShowLacks()
    {
        var slots = new Dictionary<(int, int), TvMatchBrowserEpisodeDto> { [(1, 1)] = Slot(1, 1), [(2, 3)] = Slot(2, 3) };
        var children = new[] { Child(2, "3", "b"), Child(1, "1", "a"), Child(1, "9", "c") };

        var rows = ShowChildRematchPlan.Pair(children, slots);

        Assert.Equal([(1, 1, true), (1, 9, false), (2, 3, true)], rows.Select(row => (row.Season, row.Episode, row.Found)));
        Assert.Equal("e23", rows[2].Target!.EpisodeId);
    }

    [Fact]
    public void Pair_SkipsEpisodesWithNoSeasonOrNoUsableNumber_ButKeepsSeasonZero()
    {
        var slots = new Dictionary<(int, int), TvMatchBrowserEpisodeDto> { [(0, 1)] = Slot(0, 1) };

        var rows = ShowChildRematchPlan.Pair([Child(null, "2"), Child(1, null), Child(1, "0"), Child(1, "x"), Child(0, "1")], slots);

        var row = Assert.Single(rows);
        Assert.Equal((0, 1, true), (row.Season, row.Episode, row.Found));
    }

    [Fact]
    public void MissingSlotDetail_NamesTheShowSeasonAndEpisode()
    {
        Assert.Equal(
            "New Show has no season 2 episode 9, so this episode could not be matched to it by number.",
            ShowChildRematchPlan.MissingSlotDetail("New Show", 2, 9));
    }

    [Fact]
    public void ChildRematchKind_IsAKnownDataChange_AndItsTargetRoundTrips_AndRejectsOthers()
    {
        Assert.True(EditSessionChangeKinds.IsKnown(EditSessionChangeKinds.ChildRematch));
        Assert.True(EditSessionChangeKinds.IsDataChange(EditSessionChangeKinds.ChildRematch));

        var json = EditSessionChildRematchTarget.Serialize(new ShowChildRematchTarget { SeriesId = "200" });
        Assert.True(EditSessionChildRematchTarget.TryParse(json, out var target));
        Assert.Equal("200", target!.SeriesId);
        Assert.False(EditSessionChildRematchTarget.TryParse("{\"series_id\":\"abc\"}", out _));
        Assert.False(EditSessionChildRematchTarget.TryParse("nope", out _));
    }

    [Fact]
    public async Task Handler_ChecksAndAppliesThroughTheRematcher_AndRefusesAnUnreadableTarget()
    {
        var rematcher = new SpyRematcher();
        var services = new ServiceCollection().AddScoped<IShowChildRematcher>(_ => rematcher).BuildServiceProvider();
        var callers = new EditSessionCallerAccessor();
        var handler = new ChildRematchEditSessionChangeHandler(services.GetRequiredService<IServiceScopeFactory>(), callers);
        using var scope = callers.Use(new EditSessionCaller(LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything));
        var show = Guid.NewGuid();
        var change = new EditSessionChange
        {
            Id = "c",
            Kind = EditSessionChangeKinds.ChildRematch,
            EntityId = show,
            TargetId = EditSessionChildRematchTarget.Serialize(new ShowChildRematchTarget { SeriesId = "200" }),
        };

        Assert.Null(await handler.CheckAsync(change));
        Assert.Null(await handler.ApplyAsync(change));
        Assert.Equal([$"check:{show}:200", $"apply:{show}:200"], rematcher.Calls);

        var bad = new EditSessionChange { Id = "d", Kind = EditSessionChangeKinds.ChildRematch, EntityId = show, TargetId = "x" };
        Assert.Equal(EditSessionReasons.MatchUnreadable, await handler.CheckAsync(bad));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ApplyAsync(bad));
    }

    [Fact]
    public async Task Handler_RefusesWithoutACaller_AndRematchesNothing()
    {
        var rematcher = new SpyRematcher();
        var services = new ServiceCollection().AddScoped<IShowChildRematcher>(_ => rematcher).BuildServiceProvider();
        var handler = new ChildRematchEditSessionChangeHandler(services.GetRequiredService<IServiceScopeFactory>(), new EditSessionCallerAccessor());
        var change = new EditSessionChange
        {
            Id = "c",
            Kind = EditSessionChangeKinds.ChildRematch,
            EntityId = Guid.NewGuid(),
            TargetId = EditSessionChildRematchTarget.Serialize(new ShowChildRematchTarget { SeriesId = "200" }),
        };

        Assert.Equal(EditSessionReasons.SessionUnavailable, await handler.CheckAsync(change));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ApplyAsync(change));
        Assert.Empty(rematcher.Calls);
    }

    private sealed class SpyRematcher : IShowChildRematcher
    {
        public List<string> Calls { get; } = [];

        public Task<ShowChildRematchPreviewDto?> PreviewAsync(Guid showEntityId, string seriesId, LibraryMoveAccess canEdit, CancellationToken ct) =>
            Task.FromResult<ShowChildRematchPreviewDto?>(new ShowChildRematchPreviewDto { EntityId = showEntityId, SeriesId = seriesId });

        public Task<string?> CheckAsync(Guid showEntityId, ShowChildRematchTarget target, LibraryMoveAccess canEdit, CancellationToken ct)
        {
            Calls.Add($"check:{showEntityId}:{target.SeriesId}");
            return Task.FromResult<string?>(null);
        }

        public Task ApplyAsync(Guid showEntityId, ShowChildRematchTarget target, LibraryMoveAccess canEdit, CancellationToken ct)
        {
            Calls.Add($"apply:{showEntityId}:{target.SeriesId}");
            return Task.CompletedTask;
        }
    }
}
