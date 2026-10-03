using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;

namespace MediaEngine.Api.Tests;

public sealed class AudiobookBookmarkValidationTests
{
    [Fact]
    public void RequestNormalization_ValidatesFinitePositionAndDurationAndPreservesLabelSeparately()
    {
        var assetId = Guid.NewGuid();
        var request = new CreateAudiobookBookmarkRequestDto
        {
            AssetId = assetId,
            PositionSeconds = 12.5,
            DurationSeconds = 20,
            Label = "A human label",
            Note = "  note text  ",
        };

        Assert.True(PlayerService.TryNormalizeAudiobookBookmarkRequest(request, out var normalized, out var error));
        Assert.Null(error);
        Assert.Equal("A human label", normalized!.Label);
        Assert.Equal("note text", normalized.Note);

        Assert.False(PlayerService.TryNormalizeAudiobookBookmarkRequest(
            request with { PositionSeconds = double.NaN }, out _, out _));
        Assert.False(PlayerService.TryNormalizeAudiobookBookmarkRequest(
            request with { PositionSeconds = -0.01 }, out _, out _));
        Assert.False(PlayerService.TryNormalizeAudiobookBookmarkRequest(
            request with { PositionSeconds = 21 }, out _, out _));
        Assert.False(PlayerService.TryNormalizeAudiobookBookmarkRequest(
            request with { DurationSeconds = double.PositiveInfinity }, out _, out _));
        Assert.False(PlayerService.TryNormalizeAudiobookBookmarkRequest(
            request with { Note = new string('x', 201) }, out _, out _));
    }
}
