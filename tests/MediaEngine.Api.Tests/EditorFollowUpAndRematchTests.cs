using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>"Re-match seasons and episodes" (universal editor v3, WP6, D8 item 3).</summary>
public sealed class EditorFollowUpAndRematchTests
{
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
