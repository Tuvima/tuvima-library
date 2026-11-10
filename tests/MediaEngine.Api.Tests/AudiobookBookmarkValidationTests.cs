using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;

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

    [Fact]
    public void RequestNormalization_TrimsTheLongerDescriptionAndRefusesMoreThanFourThousandCharacters()
    {
        var request = new CreateAudiobookBookmarkRequestDto
        {
            AssetId = Guid.NewGuid(),
            PositionSeconds = 750,
            DurationSeconds = 7200,
            Note = "short",
            Description = "  A longer description.\nWith two lines.  ",
        };

        Assert.True(PlayerService.TryNormalizeAudiobookBookmarkRequest(request, out var normalized, out var error));
        Assert.Null(error);
        Assert.Equal("A longer description.\nWith two lines.", normalized!.Description);
        Assert.False(normalized.IsGlobal);

        Assert.True(PlayerService.TryNormalizeAudiobookBookmarkRequest(
            request with { Description = new string('d', 4000) }, out _, out _));
        Assert.False(PlayerService.TryNormalizeAudiobookBookmarkRequest(
            request with { Description = new string('d', 4001) }, out _, out var tooLong));
        Assert.Contains("4000", tooLong);

        Assert.True(PlayerService.TryNormalizeAudiobookBookmarkRequest(
            request with { Description = "   " }, out var blank, out _));
        Assert.Null(blank!.Description);
    }

    [Fact]
    public void RequestNormalization_RefusesTheShareFlag_BecauseThePlayerOnlyMakesPersonalBookmarks()
    {
        var request = new CreateAudiobookBookmarkRequestDto { AssetId = Guid.NewGuid(), PositionSeconds = 10, DurationSeconds = 20, IsGlobal = true };

        Assert.False(PlayerService.TryNormalizeAudiobookBookmarkRequest(request, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.Contains("always your own", error);
    }

    [Fact]
    public void UpdateNormalization_TrimsTheNameAndNote_AndRefusesMoreThanTheNoteLimitOnBothSides()
    {
        var limit = AudiobookBookmarkNotePolicy.MaximumLength;

        Assert.True(PlayerService.TryNormalizeAudiobookBookmarkUpdate(
            new UpdateAudiobookBookmarkRequestDto { Label = "  The reveal ", Note = "   " }, out var normalized, out var error));
        Assert.Null(error);
        Assert.Equal("The reveal", normalized!.Label);
        Assert.Null(normalized.Note);

        Assert.True(PlayerService.TryNormalizeAudiobookBookmarkUpdate(
            new UpdateAudiobookBookmarkRequestDto { Label = new string('a', limit), Note = new string('b', limit) }, out _, out _));
        Assert.False(PlayerService.TryNormalizeAudiobookBookmarkUpdate(
            new UpdateAudiobookBookmarkRequestDto { Label = new string('a', limit + 1) }, out _, out var nameError));
        Assert.Contains(limit.ToString(System.Globalization.CultureInfo.InvariantCulture), nameError);
        Assert.False(PlayerService.TryNormalizeAudiobookBookmarkUpdate(
            new UpdateAudiobookBookmarkRequestDto { Note = new string('b', limit + 1) }, out _, out _));
    }
}
