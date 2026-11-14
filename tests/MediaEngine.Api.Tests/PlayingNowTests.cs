using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Playback;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>Playing now (spec playing-now-and-general-tabs): the live list, Stop, the grace period and the heaviness words.</summary>
public sealed class PlayingNowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_playing_now_{Guid.NewGuid():N}");
    private readonly TestClock _clock = new();
    private readonly PlaybackActivityRegistry _registry;
    private readonly ConfigurationDirectoryLoader _configuration;

    public PlayingNowTests()
    {
        Directory.CreateDirectory(_root);
        _registry = new PlaybackActivityRegistry(_clock);
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _configuration.SaveTranscoding(new TranscodingSettings { GraceSecondsAfterLastViewer = 120, PausedCountsAsWatchingMinutes = 10, MaxConcurrentTranscodes = 2 });
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(0.0, "light")]
    [InlineData(0.149, "light")]
    [InlineData(0.15, "medium")]
    [InlineData(0.449, "medium")]
    [InlineData(0.45, "heavy")]
    [InlineData(1.0, "heavy")]
    public void Classify_GivesTheWordForTheProcessorShare(double share, string word) =>
        Assert.Equal(word, EncodeHeaviness.Classify(share));

    [Fact]
    public async Task Tick_StopsAnEncodeTwoMinutesAfterTheLastViewerLeft()
    {
        var stopped = 0;
        var asset = Guid.NewGuid();
        var encode = Begin(asset, () => { stopped++; return Task.CompletedTask; });
        encode.WaitingForSlot = false;
        var service = Service();

        _clock.Advance(TimeSpan.FromSeconds(119));
        await service.TickAsync();
        Assert.Equal(0, stopped);

        _clock.Advance(TimeSpan.FromSeconds(2));
        await service.TickAsync();
        Assert.Equal(1, stopped);
    }

    [Fact]
    public async Task Tick_ResetsTheCountdownWhenSomeoneReturns()
    {
        var stopped = 0;
        var asset = Guid.NewGuid();
        Begin(asset, () => { stopped++; return Task.CompletedTask; });
        var service = Service();
        _clock.Advance(TimeSpan.FromSeconds(100));
        await service.TickAsync();

        _registry.ObserveWatch(Watch(asset, PlaybackTelemetryStates.Playing));
        await service.TickAsync();
        _clock.Advance(TimeSpan.FromSeconds(30));
        _registry.ObserveWatch(Watch(asset, PlaybackTelemetryStates.Playing));
        await service.TickAsync();
        _clock.Advance(TimeSpan.FromSeconds(100));
        await service.TickAsync();

        Assert.Equal(0, stopped);
    }

    [Fact]
    public async Task Tick_KeepsAnEncodeForAPausedPlayerForTenMinutes()
    {
        var stopped = 0;
        var asset = Guid.NewGuid();
        Begin(asset, () => { stopped++; return Task.CompletedTask; });
        var service = Service();
        _registry.ObserveWatch(Watch(asset, PlaybackTelemetryStates.Paused));

        _clock.Advance(TimeSpan.FromMinutes(9));
        await service.TickAsync();
        Assert.Equal(0, stopped);

        // The paused allowance ends at ten minutes; then the two-minute grace begins.
        _clock.Advance(TimeSpan.FromMinutes(2));
        await service.TickAsync();
        _clock.Advance(TimeSpan.FromMinutes(2));
        await service.TickAsync();
        Assert.Equal(1, stopped);
    }

    [Fact]
    public async Task Tick_NeverStopsAnOfflineCopyByItself()
    {
        var stopped = 0;
        _registry.BeginEncode(Guid.NewGuid(), PlayingNowKinds.OfflineEncode, Guid.NewGuid(), () => { stopped++; return Task.CompletedTask; });
        _clock.Advance(TimeSpan.FromHours(3));

        await Service().TickAsync();

        Assert.Equal(0, stopped);
    }

    [Fact]
    public async Task StopUnwatchedAsync_StopsOnlyTheEncodesNobodyWatches()
    {
        var stopped = new List<Guid>();
        var watched = Guid.NewGuid();
        var forgotten = Guid.NewGuid();
        var watchedId = Guid.NewGuid();
        var forgottenId = Guid.NewGuid();
        _registry.BeginEncode(watchedId, PlayingNowKinds.PlaybackEncode, watched, () => { stopped.Add(watchedId); return Task.CompletedTask; });
        _registry.BeginEncode(forgottenId, PlayingNowKinds.PlaybackEncode, forgotten, () => { stopped.Add(forgottenId); return Task.CompletedTask; });
        _registry.ObserveWatch(Watch(watched, PlaybackTelemetryStates.Playing));

        var count = await _registry.StopUnwatchedAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(1, count);
        Assert.Equal([forgottenId], stopped);
    }

    [Fact]
    public async Task StopAsync_ReturnsFalseForAnEncodeThatAlreadyFinished()
    {
        var id = Guid.NewGuid();
        _registry.BeginEncode(id, PlayingNowKinds.PlaybackEncode, Guid.NewGuid(), () => Task.CompletedTask);
        _registry.EndEncode(id);

        Assert.False(await _registry.StopAsync(id));
    }

    [Fact]
    public async Task GetAsync_NamesPersonProfileAndDevice_ForAWatchedEncode()
    {
        var asset = Guid.NewGuid();
        var encode = Begin(asset, () => Task.CompletedTask);
        encode.WaitingForSlot = false;
        encode.Step = PlayingNowSteps.EncodingPicture;
        encode.Heaviness = PlayingNowHeaviness.Heavy;
        encode.RanOn = PlaybackEncodePlaces.GraphicsCard;
        _registry.ObserveWatch(Watch(asset, PlaybackTelemetryStates.Playing) with { Title = "Dune", Subtitle = null });

        var list = await Read().GetAsync(CancellationToken.None);

        var row = Assert.Single(list.Rows);
        Assert.Equal("Dana", row.PersonName);
        Assert.Equal("Kids", row.ProfileName);
        Assert.True(row.IsKidsProfile);
        Assert.Equal("Living room TV", row.DeviceName);
        Assert.Equal("Graphics card", row.RanOn);
        Assert.Equal(PlayingNowHeaviness.Heavy, list.HeaviestLoad);
        Assert.Equal(2, list.MaxActiveEncodes);
    }

    [Fact]
    public async Task GetAsync_NamesNothingOfAVaultTitle()
    {
        var asset = Guid.NewGuid();
        Begin(asset, () => Task.CompletedTask);
        _registry.ObserveWatch(Watch(asset, PlaybackTelemetryStates.Playing) with { Title = "Secret", Subtitle = "Show · S1E1", CoverUrl = "/cover", IsVault = true });

        var row = Assert.Single((await Read().GetAsync(CancellationToken.None)).Rows);

        Assert.Equal(PlayingNowReadService.VaultTitle, row.Title);
        Assert.Null(row.Subtitle);
        Assert.Null(row.CoverUrl);
    }

    [Fact]
    public async Task GetAsync_ListsADirectPlaySessionWithNothingToStop()
    {
        _registry.ObserveWatch(Watch(Guid.NewGuid(), PlaybackTelemetryStates.Playing));

        var row = Assert.Single((await Read().GetAsync(CancellationToken.None)).Rows);

        Assert.Equal(PlayingNowKinds.Watching, row.Kind);
        Assert.Equal(PlayingNowSteps.PlayingDirectly, row.Step);
        Assert.False(row.CanStop);
        Assert.Equal(string.Empty, row.Id);
    }

    [Fact]
    public async Task GetAsync_ShowsAWaitingEncodeWithItsReason()
    {
        Begin(Guid.NewGuid(), () => Task.CompletedTask);

        var list = await Read().GetAsync(CancellationToken.None);

        var row = Assert.Single(list.Rows);
        Assert.True(row.WaitingForSlot);
        Assert.Equal("Waiting for a free slot", row.PreparingReason);
        Assert.Equal(0, list.ActiveEncodes);
    }

    [Fact]
    public async Task GetAsync_CountsDownToTheAutomaticStopForAnUnwatchedEncode()
    {
        Begin(Guid.NewGuid(), () => Task.CompletedTask);
        _clock.Advance(TimeSpan.FromSeconds(78));

        var row = Assert.Single((await Read().GetAsync(CancellationToken.None)).Rows);

        Assert.False(row.IsWatched);
        Assert.Equal(42, row.StopsInSeconds);
    }

    private EncodeEntry Begin(Guid asset, Func<Task> stop) =>
        _registry.BeginEncode(Guid.NewGuid(), PlayingNowKinds.PlaybackEncode, asset, stop);

    private WatchEntry Watch(Guid asset, string state) => new(
        Guid.NewGuid(), asset, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Living room TV", "Title", null, null, false, state, 10, 100,
        _clock.GetUtcNow(), _clock.GetUtcNow());

    private PlaybackActivityService Service() => new(
        _registry, new FixedLoad(0.6), _configuration, _clock, NullLogger<PlaybackActivityService>.Instance);

    private PlayingNowReadService Read() => new(_registry, new FixedNames(), _configuration, _clock);

    private sealed class FixedLoad(double share) : IEncodeLoadSampler
    {
        public double? Sample() => share;
    }

    private sealed class FixedNames : IPlayingNowNames
    {
        public Task<(string? Name, bool Kids)> ProfileAsync(Guid profileId, CancellationToken ct) => Task.FromResult<(string?, bool)>(("Kids", true));

        public Task<string?> PersonAsync(Guid accountId, CancellationToken ct) => Task.FromResult<string?>("Dana");
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
