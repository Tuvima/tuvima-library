using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>"Match the other episodes in this season by number" (universal editor v3, WP4, D4): the pairing rules and the edit-session kind.</summary>
public sealed class EpisodeSiblingMatchTests
{
    private static TvMatchBrowserEpisodeDto Episode(int number) => new(
        $"e{number}", "200", "Other Show", "s2", 2, number, $"Title {number}", null, null, "https://img.test/e.jpg", "https://thetvdb.test", false, false, null);

    private static IReadOnlyDictionary<int, TvMatchBrowserEpisodeDto> Season(params int[] numbers) =>
        numbers.ToDictionary(number => number, Episode);

    [Fact]
    public void Pair_GivesEachOtherEpisodeTheEpisodeWithItsOwnNumber_InNumberOrder()
    {
        var chosen = Guid.NewGuid();
        var four = Guid.NewGuid();
        var three = Guid.NewGuid();

        var rows = EpisodeSiblingMatchPlan.Pair(
            chosen,
            [new(chosen, "Show.S02E01.mkv", "1"), new(four, "Show.S02E04.mkv", "4"), new(three, "Show.S02E03.mkv", "3")],
            Season(1, 3, 4));

        Assert.Equal([three, four], rows.Select(row => row.EntityId));
        Assert.All(rows, row => Assert.True(row.Found));
        Assert.Equal(["e3", "e4"], rows.Select(row => row.TargetEpisodeId));
        Assert.Equal("Title 3", rows[0].TargetTitle);
    }

    [Fact]
    public void Pair_KeepsAnEpisodeWhoseNumberTheSeasonLacks_AsNotFound_AndSkipsOnesWithNoNumber()
    {
        var missing = Guid.NewGuid();

        var rows = EpisodeSiblingMatchPlan.Pair(
            Guid.NewGuid(),
            [new(missing, "Show.S02E09.mkv", "9"), new(Guid.NewGuid(), "bonus.mkv", null), new(Guid.NewGuid(), "zero.mkv", "0"), new(Guid.NewGuid(), "odd.mkv", "x")],
            Season(1, 2));

        var row = Assert.Single(rows);
        Assert.Equal(missing, row.EntityId);
        Assert.False(row.Found);
        Assert.Null(row.TargetEpisodeId);
    }

    [Fact]
    public void RequestFor_CarriesTheFullShowSeasonAndEpisodeIdentity_AsTheMatchToolStagesIt()
    {
        var request = EpisodeSiblingMatchPlan.RequestFor(Episode(3));

        Assert.Equal("show_episode", request.TargetFieldGroup);
        Assert.Equal("tvdb", request.ProviderName);
        Assert.Equal("e3", request.ProviderItemId);
        Assert.Equal("200", request.BridgeIds[BridgeIdKeys.TvdbId]);
        Assert.Equal("e3", request.BridgeIds[BridgeIdKeys.TvdbEpisodeId]);
        Assert.Equal("Other Show", request.RequiredFields[MetadataFieldConstants.ShowName]);
        Assert.Equal("2", request.RequiredFields[MetadataFieldConstants.SeasonNumber]);
        Assert.Equal("3", request.RequiredFields[MetadataFieldConstants.EpisodeNumber]);
    }

    [Fact]
    public async Task Handler_ChecksAndAppliesThroughTheMatcher_WithTheStagedTarget()
    {
        var matcher = new SpyMatcher();
        var handler = HandlerWith(matcher);
        var item = Guid.NewGuid();
        var change = new EditSessionChange
        {
            Id = "c",
            Kind = EditSessionChangeKinds.SiblingMatch,
            EntityId = item,
            TargetId = EditSessionSiblingMatchTarget.Serialize(new EpisodeSiblingMatchTarget { SeriesId = "200", SeasonNumber = 2 }),
        };

        Assert.Null(await handler.CheckAsync(change));
        var undo = await handler.ApplyAsync(change);

        Assert.Null(undo);
        Assert.Equal([$"check:{item}:200:2", $"apply:{item}:200:2"], matcher.Calls);
    }

    [Fact]
    public async Task Handler_RefusesATargetItCannotRead_AndWritesNothing()
    {
        var matcher = new SpyMatcher();
        var handler = HandlerWith(matcher);
        var change = new EditSessionChange { Id = "c", Kind = EditSessionChangeKinds.SiblingMatch, EntityId = Guid.NewGuid(), TargetId = "not json" };

        Assert.Equal(EditSessionReasons.MatchUnreadable, await handler.CheckAsync(change));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ApplyAsync(change));
        Assert.Empty(matcher.Calls);
    }

    [Fact]
    public async Task Handler_PassesTheMatcherReasonBack_WhenTheCheckFails()
    {
        var matcher = new SpyMatcher { Reason = "Connect TheTVDB in Settings before matching the other episodes." };
        var change = new EditSessionChange
        {
            Id = "c",
            Kind = EditSessionChangeKinds.SiblingMatch,
            EntityId = Guid.NewGuid(),
            TargetId = EditSessionSiblingMatchTarget.Serialize(new EpisodeSiblingMatchTarget { SeriesId = "1", SeasonNumber = 0 }),
        };

        Assert.Equal(matcher.Reason, await HandlerWith(matcher).CheckAsync(change));
    }

    private static SiblingMatchEditSessionChangeHandler HandlerWith(SpyMatcher matcher)
    {
        var services = new ServiceCollection().AddScoped<IEpisodeSiblingMatcher>(_ => matcher).BuildServiceProvider();
        return new SiblingMatchEditSessionChangeHandler(services.GetRequiredService<IServiceScopeFactory>(), new EditSessionCallerAccessor());
    }

    private sealed class SpyMatcher : IEpisodeSiblingMatcher
    {
        public List<string> Calls { get; } = [];

        public string? Reason { get; init; }

        public Task<EpisodeSiblingMatchPreviewDto?> PreviewAsync(Guid entityId, string seriesId, int seasonNumber, LibraryMoveAccess? canEdit, CancellationToken ct) =>
            Task.FromResult<EpisodeSiblingMatchPreviewDto?>(new EpisodeSiblingMatchPreviewDto { EntityId = entityId, SeriesId = seriesId, SeasonNumber = seasonNumber });

        public Task<string?> CheckAsync(Guid entityId, EpisodeSiblingMatchTarget target, LibraryMoveAccess? canEdit, CancellationToken ct)
        {
            Calls.Add($"check:{entityId}:{target.SeriesId}:{target.SeasonNumber}");
            return Task.FromResult(Reason);
        }

        public Task ApplyAsync(Guid entityId, EpisodeSiblingMatchTarget target, LibraryMoveAccess? canEdit, CancellationToken ct)
        {
            Calls.Add($"apply:{entityId}:{target.SeriesId}:{target.SeasonNumber}");
            return Task.CompletedTask;
        }
    }
}
