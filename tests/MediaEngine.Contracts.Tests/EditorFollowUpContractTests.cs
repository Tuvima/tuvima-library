using System.Text.Json;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of the saved ribbon's named steps and the show re-match preview (universal editor v3, WP6).</summary>
public sealed class EditorFollowUpContractTests
{
    [Fact]
    public void ShowChildRematchPreview_UsesSnakeCaseNames_AndRoundTrips()
    {
        var preview = new ShowChildRematchPreviewDto { EntityId = Guid.NewGuid(), SeriesId = "200", ShowName = "Show", EpisodeCount = 4, MatchableCount = 3, NotFoundCount = 1 };

        var json = JsonSerializer.Serialize(preview);
        var back = JsonSerializer.Deserialize<ShowChildRematchPreviewDto>(json)!;

        Assert.Contains("\"episode_count\":4", json, StringComparison.Ordinal);
        Assert.Contains("\"matchable_count\":3", json, StringComparison.Ordinal);
        Assert.Contains("\"not_found_count\":1", json, StringComparison.Ordinal);
        Assert.Equal("200", back.SeriesId);
    }

    [Fact]
    public void ChildRematchTarget_RoundTripsThroughTheChangeTargetString()
    {
        var token = EditSessionChildRematchTarget.Serialize(new ShowChildRematchTarget { SeriesId = "200" });

        Assert.True(EditSessionChildRematchTarget.TryParse(token, out var back));
        Assert.Equal("200", back!.SeriesId);
        Assert.False(EditSessionChildRematchTarget.TryParse("nonsense", out _));
        Assert.Contains(EditSessionChangeKinds.ChildRematch, EditSessionChangeKinds.All);
    }
}
