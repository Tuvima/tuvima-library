using MediaEngine.AI.Features;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.AI.Tests;

public sealed class TasteProfilerTests
{
    [Fact]
    public async Task GetProfileAsync_ReturnsExplicitInsufficientDataWithoutGlobalFallback()
    {
        var userId = Guid.NewGuid();
        var repository = new TasteRepositoryStub(
        [
            Signal("Books", "science fiction"),
            Signal("Movies", "drama"),
        ]);
        var profiler = new TasteProfiler(
            StubAiEngineRoom.ReturningJson("unused"),
            new FakeAiFeatureSwitches(),
            repository,
            NullLogger<TasteProfiler>.Instance);

        var result = await profiler.GetProfileAsync(userId);

        Assert.Equal(TasteProfileBuildStatus.InsufficientData, result.Status);
        Assert.Equal(userId, result.UserId);
        Assert.Equal(2, result.SignalCount);
        Assert.Null(result.Profile);
        Assert.Contains("At least 3", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetProfileAsync_WeightsOnlyRepositorySuppliedProfileSignals()
    {
        var userId = Guid.NewGuid();
        var repository = new TasteRepositoryStub(
        [
            Signal("Books", "science fiction", progress: 100),
            Signal("Books", "science fiction", progress: 50),
            Signal("Movies", "drama", progress: 10),
        ]);
        var profiler = new TasteProfiler(
            StubAiEngineRoom.ReturningJson("A tailored profile."),
            new FakeAiFeatureSwitches(AiFeature.TasteSummary),
            repository,
            NullLogger<TasteProfiler>.Instance);

        var result = await profiler.GetProfileAsync(userId);

        Assert.Equal(TasteProfileBuildStatus.Generated, result.Status);
        Assert.NotNull(result.Profile);
        Assert.True(result.Profile.GenreDistribution["science fiction"] > 0.90);
        Assert.Equal("A tailored profile.", result.Profile.Summary);
        Assert.NotEmpty(result.InputFingerprint);
    }

    [Fact]
    public async Task GetProfileAsync_WithTheTasteSummaryPluginOff_KeepsTheNumbersAndUsesAPlainSentenceWithoutAskingLocalAi()
    {
        var repository = new TasteRepositoryStub(
        [
            Signal("Books", "science fiction", progress: 100),
            Signal("Books", "science fiction", progress: 50),
            Signal("Movies", "drama", progress: 10),
        ]);
        var profiler = new TasteProfiler(
            StubAiEngineRoom.ReturningJson("A tailored profile."),
            new FakeAiFeatureSwitches(),
            repository,
            NullLogger<TasteProfiler>.Instance);

        var result = await profiler.GetProfileAsync(Guid.NewGuid());

        Assert.Equal(TasteProfileBuildStatus.Generated, result.Status);
        Assert.True(result.Profile!.GenreDistribution["science fiction"] > 0.90);
        Assert.DoesNotContain("tailored", result.Profile.Summary, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(result.Profile.Summary));
    }

    private static TasteSignal Signal(string mediaType, string genre, double progress = 25) => new(
        Guid.NewGuid(),
        progress,
        DateTimeOffset.UtcNow,
        mediaType,
        2020,
        [genre],
        ["hopeful"]);

    private sealed class TasteRepositoryStub(IReadOnlyList<TasteSignal> signals) : ITasteProfileRepository
    {
        public Task<TasteProfile?> GetAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult<TasteProfile?>(null);

        public Task<IReadOnlyList<TasteSignal>> GetSignalsAsync(
            Guid userId,
            int limit,
            CancellationToken ct = default) => Task.FromResult(signals);

        public Task<AiFeatureWriteResult> ReplaceAiProfileAsync(
            TasteProfilePersistenceRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
