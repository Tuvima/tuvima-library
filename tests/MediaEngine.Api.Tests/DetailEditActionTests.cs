using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Contracts.Details;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The "..." menu's edit entry names what it edits (details overlay spec D2) and, while the item is still
/// processing, stays in the menu disabled with the reason (spec section 4, "Not allowed").
/// </summary>
public sealed class DetailEditActionTests
{
    public static TheoryData<DetailEntityType, string> ExpectedLabels => new()
    {
        { DetailEntityType.Work, "Edit this title" },
        { DetailEntityType.Movie, "Edit this movie" },
        { DetailEntityType.MovieSeries, "Edit this series" },
        { DetailEntityType.BookSeries, "Edit this series" },
        { DetailEntityType.ComicSeries, "Edit this series" },
        { DetailEntityType.TvShow, "Edit this show" },
        { DetailEntityType.TvSeason, "Edit this season" },
        { DetailEntityType.TvEpisode, "Edit this episode" },
        { DetailEntityType.Book, "Edit this book" },
        { DetailEntityType.Audiobook, "Edit this audiobook" },
        { DetailEntityType.ComicIssue, "Edit this comic" },
        { DetailEntityType.MusicAlbum, "Edit this album" },
        { DetailEntityType.Person, "Edit this person" },
        { DetailEntityType.Universe, "Edit this universe" },
        { DetailEntityType.Collection, "Edit this collection" },
    };

    [Theory]
    [MemberData(nameof(ExpectedLabels))]
    public void Build_LabelsTheEditEntryByTheTypeBeingEdited(DetailEntityType entityType, string expected)
    {
        var action = DetailEditAction.Build(entityType, updatingDetails: false);

        Assert.Equal("edit", action.Key);
        Assert.Equal(expected, action.Label);
    }

    [Fact]
    public void LabelFor_NamesEveryDetailEntityType()
    {
        foreach (var entityType in Enum.GetValues<DetailEntityType>())
        {
            Assert.StartsWith("Edit this ", DetailEditAction.LabelFor(entityType));
        }
    }

    [Fact]
    public void Build_KeepsTheEntryEnabledWithoutAReasonWhenNothingIsProcessing()
    {
        var action = DetailEditAction.Build(DetailEntityType.TvShow, updatingDetails: false);

        Assert.False(action.IsDisabled);
        Assert.Null(action.Tooltip);
    }

    [Fact]
    public void Build_KeepsTheEntryDisabledWithItsReasonWhileTheItemIsProcessing()
    {
        var action = DetailEditAction.Build(DetailEntityType.TvShow, updatingDetails: true);

        Assert.True(action.IsDisabled);
        Assert.Equal("Edit this show", action.Label);
        Assert.Equal(DetailEditAction.UpdatingReason, action.Tooltip);
        Assert.False(string.IsNullOrWhiteSpace(action.Tooltip));
    }

    [Fact]
    public void VideoShortcuts_OfferIdentifyAndChangeThumbnail_ForAVideoThatIsNotMatched()
    {
        var actions = DetailEditAction.BuildVideoShortcuts(matched: false, updatingDetails: false).ToList();

        Assert.Equal(["identify", "change-thumbnail"], actions.Select(action => action.Key));
        Assert.Equal(["Identify", "Change thumbnail"], actions.Select(action => action.Label));
        Assert.All(actions, action => Assert.False(action.IsDisabled));
    }

    [Fact]
    public void VideoShortcuts_LeaveOutIdentify_OnceTheVideoIsMatched()
    {
        var action = Assert.Single(DetailEditAction.BuildVideoShortcuts(matched: true, updatingDetails: false));

        Assert.Equal("change-thumbnail", action.Key);
    }

    [Fact]
    public void VideoShortcuts_StayInTheMenuDisabledWithTheReasonWhileTheItemIsProcessing()
    {
        var actions = DetailEditAction.BuildVideoShortcuts(matched: false, updatingDetails: true).ToList();

        Assert.All(actions, action =>
        {
            Assert.True(action.IsDisabled);
            Assert.Equal(DetailEditAction.UpdatingReason, action.Tooltip);
        });
    }

    [Theory]
    [InlineData("edit", true, true)]
    [InlineData("identify", true, true)]
    [InlineData("change-thumbnail", true, true)]
    [InlineData("identify", false, false)]
    [InlineData("change-thumbnail", false, false)]
    public void TheShortcuts_FollowTheSamePermissionAsEdit(string key, bool canManageMetadata, bool expected)
    {
        Assert.Equal(expected, new DetailActionAuthorizationContext(canManageMetadata).Allows(key));
    }
}
