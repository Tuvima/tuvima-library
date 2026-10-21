using MediaEngine.AI.Configuration;
using MediaEngine.AI.EngineRoom;
using MediaEngine.AI.Features;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.AI.Tests;

/// <summary>
/// The Type helper, Vibe tags and Description insights plugins: with the plugin off nothing asks Local AI and the
/// Library carries on without it; with it on the answer is used, and a bad answer never turns into a made-up one.
/// </summary>
public sealed class AiFeatureOffAndOnTests
{
    private sealed class Words(params string[] books) : IVibeVocabularySource
    {
        public AiVibeVocabulary Current { get; } = new() { Books = [.. books] };
    }

    // ── Type helper ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TypeHelper_WhenItsPluginIsOff_DoesNotAskLocalAi_AndLeavesTheTypeUnknown()
    {
        var stub = StubAiEngineRoom.ReturningJson("""{"type":"Audiobooks","confidence":0.9,"reason":"long audio"}""");
        var advisor = new MediaTypeAdvisor(stub, new FakeAiFeatureSwitches(), NullLogger<MediaTypeAdvisor>.Instance);

        var result = await advisor.ClassifyAsync("track.m4b", "m4b", 36000, 64, null, true, null);

        Assert.Equal(MediaType.Unknown, result.Type);
        Assert.Equal(0, result.Confidence);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task TypeHelper_WhenOn_ClassifiesAsIngestion_AndClampsTheConfidence()
    {
        var stub = StubAiEngineRoom.ReturningJson("""{"type":"Audiobooks","confidence":7,"reason":"very long, chaptered audio"}""");
        var advisor = new MediaTypeAdvisor(stub, new FakeAiFeatureSwitches(AiFeature.TypeLogic), NullLogger<MediaTypeAdvisor>.Instance);

        var result = await advisor.ClassifyAsync("track.m4b", "m4b", 36000, 64, null, true, null);

        var request = Assert.Single(stub.Requests);
        Assert.Equal(AiCallerKind.Ingestion, request.Caller.Kind);
        Assert.Equal(AiAbility.Classify, request.Ability);
        Assert.Equal(MediaType.Audiobooks, result.Type);
        Assert.Equal(1.0, result.Confidence);
        Assert.Equal("very long, chaptered audio", result.Reason);
    }

    [Fact]
    public async Task TypeHelper_WhenLocalAiGivesNoAnswer_ReportsUnknownWithLowConfidence_SoTheFileCanGoToNeedsReview()
    {
        var stub = StubAiEngineRoom.Failing(AiRequestStatus.Unavailable, "Local AI is off");
        var advisor = new MediaTypeAdvisor(stub, new FakeAiFeatureSwitches(AiFeature.TypeLogic), NullLogger<MediaTypeAdvisor>.Instance);

        var result = await advisor.ClassifyAsync("track.m4b", "m4b", 36000, 64, null, true, null);

        Assert.Equal(MediaType.Unknown, result.Type);
        Assert.True(result.Confidence <= 0.1);
    }

    // ── Vibe tags ────────────────────────────────────────────────────────────

    [Fact]
    public async Task VibeTags_KeepOnlyWordsFromTheVocabulary_WithoutRepeats_AtMostFive()
    {
        var stub = StubAiEngineRoom.ReturningJson("""["cozy","Cozy","invented","dark","epic","intimate","witty","cerebral"]""");
        var tagger = new VibeTagger(stub, new Words("cozy", "dark", "epic", "intimate", "witty", "cerebral"), NullLogger<VibeTagger>.Instance);

        var tags = await tagger.TagAsync("Dune", "A desert planet story.", ["science fiction"], "books");

        Assert.Equal(["cozy", "dark", "epic", "intimate", "witty"], tags);
    }

    [Fact]
    public async Task VibeTags_AskAsALibraryBackgroundJob_WithTheVocabularyInThePrompt()
    {
        var stub = StubAiEngineRoom.ReturningJson("""["cozy"]""");
        var tagger = new VibeTagger(stub, new Words("cozy", "dark"), NullLogger<VibeTagger>.Instance);

        await tagger.TagAsync("Dune", "A desert planet story.", [], "books");

        var request = Assert.Single(stub.Requests);
        Assert.Equal(AiCallerKind.LibraryBackground, request.Caller.Kind);
        Assert.Contains("cozy, dark", request.Prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task VibeTags_WithNoSummaryToReadFrom_DoNotAskLocalAi(string? summary)
    {
        var stub = StubAiEngineRoom.ReturningJson("""["cozy"]""");
        var tagger = new VibeTagger(stub, new Words("cozy"), NullLogger<VibeTagger>.Instance);

        Assert.Empty(await tagger.TagAsync("Dune", summary, [], "books"));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task VibeTags_WithAnEmptyVocabularyForTheCategory_DoNotAskLocalAi()
    {
        var stub = StubAiEngineRoom.ReturningJson("""["cozy"]""");
        var tagger = new VibeTagger(stub, new Words(), NullLogger<VibeTagger>.Instance);

        Assert.Empty(await tagger.TagAsync("Dune", "A desert planet story.", [], "books"));
        Assert.Empty(stub.Requests);
    }

    // ── Description insights ─────────────────────────────────────────────────

    [Fact]
    public async Task DescriptionInsights_WhenItsPluginIsOff_ReturnsNothing_WithoutReadingTheLibraryOrAskingLocalAi()
    {
        var stub = StubAiEngineRoom.ReturningJson("{}");
        var service = new DescriptionIntelligenceService(
            stub,
            new FakeAiFeatureSwitches(),
            new Words("cozy"),
            canonicalRepo: null!,
            NullLogger<DescriptionIntelligenceService>.Instance);

        Assert.Null(await service.AnalyzeAsync(Guid.NewGuid(), "books"));
        Assert.Empty(stub.Requests);
    }
}
