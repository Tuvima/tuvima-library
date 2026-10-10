using System.Net;
using System.Net.Http.Json;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientSequenceNormalizationTests
{
    [Fact]
    public async Task DetailResponsePreservesEverySequencePositionStateAndOwnedCountThroughNormalization()
    {
        var show = Guid.NewGuid();
        var items = new[] { DisplayContinuationState.Unstarted, DisplayContinuationState.InProgress, DisplayContinuationState.Completed }.Select((state, index) =>
        {
            var work = Guid.NewGuid(); var asset = Guid.NewGuid();
            return new SequenceItemViewModel
            {
                Id = work.ToString("D"),
                EntityType = DetailEntityType.TvEpisode,
                Title = $"Episode {index + 1}",
                IsOwned = true,
                ArtworkUrl = $"/stream/artwork/{asset:D}?size=s",
                EpisodeStillUrl = index == 1 ? $"/stream/artwork/{asset:D}" : null,
                EpisodeStillWidthPx = index == 1 ? 1920 : null,
                EpisodeStillHeightPx = index == 1 ? 1080 : null,
                Route = TvEpisodeDetailRoute.Build(show, work),
                EpisodeContext = new(show, work, asset, "Show", $"Episode {index + 1}", 1, index + 1, state, index * 1500, 3000),
                ProgressState = index == 2 ? LibraryProgressState.Completed : index == 1 ? LibraryProgressState.InProgress : LibraryProgressState.Unstarted,
                ProgressPercent = index * 50,
                PositionSeconds = index * 1500,
                DurationSeconds = 3000,
                RemainingSeconds = 3000 - index * 1500,
                ProgressLabel = $"State {index}",
                CoveredWith = index == 0 ? [new SequenceCoverageLinkViewModel { Id = "sibling", PositionLabel = "2", Title = "Episode 2" }] : []
            };
        }).ToList();
        var detail = new DetailPageViewModel
        {
            Id = show.ToString("D"),
            EntityType = DetailEntityType.TvShow,
            Title = "Show",
            SequencePlacement = new()
            {
                ContainerId = show.ToString("D"),
                CurrentItem = items[1],
                PreviousItem = items[0],
                NextItem = items[2],
                OrderedItems = items,
                Groups = [new() { Key = "season-1", Title = "Season 1", Items = items, OwnedCount = 3, CompletedCount = 1 }]
            }
        };
        using var http = new HttpClient(new Handler(detail)) { BaseAddress = new Uri("http://localhost:61495") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        var result = await client.GetDetailPageAsync(DetailEntityType.TvShow, show); Assert.NotNull(result);
        var placement = Assert.IsType<SequencePlacementViewModel>(result.SequencePlacement);
        var copies = placement.OrderedItems.Concat(Assert.Single(placement.Groups).Items).Concat(new[] { placement.PreviousItem!, placement.CurrentItem, placement.NextItem! });
        foreach (var copy in copies)
        {
            var original = items.Single(i => i.Id == copy.Id);
            Assert.Equal(original.EpisodeContext, copy.EpisodeContext); Assert.Equal(original.ProgressState, copy.ProgressState);
            Assert.Equal(original.CoveredWith.Select(l => (l.Id, l.PositionLabel)), copy.CoveredWith.Select(l => (l.Id, l.PositionLabel))); Assert.Equal(original.ProgressPercent, copy.ProgressPercent); Assert.Equal(original.PositionSeconds, copy.PositionSeconds);
            Assert.Equal(original.DurationSeconds, copy.DurationSeconds); Assert.Equal(original.RemainingSeconds, copy.RemainingSeconds); Assert.Equal(original.ProgressLabel, copy.ProgressLabel);
            Assert.Equal(original.Route, copy.Route); Assert.StartsWith("/engine-image/stream/artwork/", copy.ArtworkUrl);
            Assert.Equal(original.EpisodeStillWidthPx, copy.EpisodeStillWidthPx); Assert.Equal(original.EpisodeStillHeightPx, copy.EpisodeStillHeightPx);
            if (original.EpisodeStillUrl is null)
            {
                Assert.Null(copy.EpisodeStillUrl);
            }
            else
            {
                Assert.StartsWith("/engine-image/stream/artwork/", copy.EpisodeStillUrl);
            }
        }
        Assert.Equal(3, placement.Groups[0].OwnedCount); Assert.Equal(1, placement.Groups[0].CompletedCount);
    }

    [Fact]
    public async Task SeasonWindowCountsAndOffsetsSurviveNormalizationAndTheWindowQueryIsSent()
    {
        var show = Guid.NewGuid();
        var detail = new DetailPageViewModel
        {
            Id = show.ToString("D"),
            EntityType = DetailEntityType.TvShow,
            SequencePlacement = new()
            {
                ContainerId = show.ToString("D"),
                Groups =
                [
                    new() { Key = "season-1", Title = "Season 1", ItemCount = 400, ItemOffset = 50, OwnedCount = 400, Items = [new() { Id = Guid.NewGuid().ToString("D"), IsOwned = true }] },
                    new() { Key = "season-2", Title = "Season 2", ItemCount = 12, OwnedCount = 12 },
                ],
            },
        };
        var handler = new Handler(detail);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:61495") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var current = Guid.NewGuid();
        var result = await client.GetDetailPageAsync(
            DetailEntityType.TvShow, show, sequence: SequenceWindowQuery.Page("season-1", 50, current));

        Assert.NotNull(result);
        var groups = result.SequencePlacement!.Groups;
        Assert.Equal((400, 50, 1), (groups[0].ItemCount, groups[0].ItemOffset, groups[0].Items.Count));
        Assert.Equal((12, 0, 0), (groups[1].ItemCount, groups[1].ItemOffset, groups[1].Items.Count));
        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("sequenceGroup=season-1", query);
        Assert.Contains("sequenceOffset=50", query);
        Assert.Contains("sequenceLimit=50", query);
        Assert.Contains($"sequenceCurrent={current:D}", query);
        Assert.Contains("sequenceOnly=true", query);

        await client.GetDetailPageAsync(DetailEntityType.TvEpisode, show, sequence: SequenceWindowQuery.WholeList);
        Assert.Contains("sequenceLimit=0", handler.LastRequest!.RequestUri!.Query);
        Assert.DoesNotContain("sequenceGroup", handler.LastRequest.RequestUri.Query);

        await client.GetDetailPageAsync(DetailEntityType.TvShow, show);
        Assert.DoesNotContain("sequence", handler.LastRequest!.RequestUri!.Query);
    }

    private sealed class Handler(DetailPageViewModel detail) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(detail) });
        }
    }
}
