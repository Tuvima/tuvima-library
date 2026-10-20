using MediaEngine.AI.Features;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.AI.Tests;

public sealed class SeriesAlignerTests
{
    [Fact]
    public async Task InferPositionAsync_PreservesModelConfidence()
    {
        var aligner = new SeriesAligner(
            StubAiEngineRoom.ReturningJson("""{"position":3,"confidence":0.87}"""),
            NullLogger<SeriesAligner>.Instance);

        var result = await aligner.InferPositionAsync(
            "The Third Book",
            "A Series",
            ["The First Book", "The Second Book", "The Third Book"]);

        Assert.NotNull(result);
        Assert.Equal(3, result.Position);
        Assert.Equal(0.87, result.Confidence);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public async Task InferPositionAsync_RejectsOutOfRangeConfidence(double confidence)
    {
        var aligner = new SeriesAligner(
            StubAiEngineRoom.ReturningJson($"{{\"position\":2,\"confidence\":{confidence}}}"),
            NullLogger<SeriesAligner>.Instance);

        var result = await aligner.InferPositionAsync("Book", "Series", ["Book"]);

        Assert.Null(result);
    }
}
