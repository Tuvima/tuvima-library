using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>"Not on this album" and its season and series twins (universal editor v3, WP4, spec 3a), and the before and after of a re-match in Review.</summary>
public sealed class EditorPlacementRulesTests
{
    private static readonly Guid Song = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static string Identity(string mediaType, params (string Key, string Value)[] fields)
    {
        var match = new ReplaceRetailMatchRequestDto { ProviderName = "musicbrainz", ProviderItemId = "x", ProviderId = Guid.NewGuid().ToString("D") };
        foreach (var (key, value) in fields)
        {
            match.RequiredFields[key] = value;
        }

        return EditSessionIdentityTarget.Serialize(match);
    }

    [Fact]
    public void MovingASongToTheAlbumItsMatchNames_IsNotFlagged()
    {
        var note = EditorPlacementRules.NotThereNote("Music", new("OK Computer", "Radiohead"), new("ok computer", null));

        Assert.Null(note);
    }

    [Fact]
    public void MovingASongToAnotherAlbum_IsFlaggedNotOnThisAlbum_NamingBothAlbums()
    {
        var note = EditorPlacementRules.NotThereNote("Music", new("The Bends", "Radiohead"), new("OK Computer", null));

        Assert.StartsWith("Not on this album", note, StringComparison.Ordinal);
        Assert.Contains("OK Computer", note, StringComparison.Ordinal);
        Assert.Contains("The Bends", note, StringComparison.Ordinal);
    }

    [Fact]
    public void ASongWithNoStagedMatch_IsFlaggedToo_BecauseNothingSaysItBelongsThere()
    {
        var note = EditorPlacementRules.NotThereNote("Music", new("The Bends", null), null);

        Assert.StartsWith("Not on this album", note, StringComparison.Ordinal);
        Assert.Contains("nothing you have matched", note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Books", "Not in this series")]
    [InlineData("Comics", "Not in this series")]
    [InlineData("TV", "Not in this season")]
    public void OtherKinds_UseTheirOwnPlaceWord(string mediaType, string heading)
    {
        var note = EditorPlacementRules.NotThereNote(mediaType, new("Somewhere", null), null);

        Assert.StartsWith(heading, note, StringComparison.Ordinal);
    }

    [Fact]
    public void ATvSeasonOfAnotherShow_IsNotTheSamePlaceAsTheSameSeasonNumberOfTheMatchedShow()
    {
        var note = EditorPlacementRules.NotThereNote("TV", new("Season 2", "Other Show"), new("Season 2", "Matched Show"));

        Assert.NotNull(note);
        Assert.Contains("Matched Show", note, StringComparison.Ordinal);
        Assert.Contains("Other Show", note, StringComparison.Ordinal);
    }

    [Fact]
    public void StagedMatchPlace_ReadsTheAlbumOrTheSeasonAndShowFromTheStagedMatch()
    {
        var pending = new PendingEditList();
        pending.SetTarget(EditSessionChangeKinds.Identity, Song, Identity("Music", (MetadataFieldConstants.Album, "OK Computer")));

        Assert.Equal(new EditorPlacementRules.Place("OK Computer", null), EditorPlacementRules.StagedMatchPlace(pending, Song, "Music"));

        var tv = new PendingEditList();
        tv.SetTarget(EditSessionChangeKinds.Identity, Song,
            Identity("TV", (MetadataFieldConstants.ShowName, "Show"), (MetadataFieldConstants.SeasonNumber, "0")));

        Assert.Equal(new EditorPlacementRules.Place("Specials", "Show"), EditorPlacementRules.StagedMatchPlace(tv, Song, "TV"));
        Assert.Null(EditorPlacementRules.StagedMatchPlace(new PendingEditList(), Song, "Music"));
    }

    [Fact]
    public void Review_ShowsTheNoteOnTheRelationshipRow_AndTheSiblingMatchAsAnIdentityRow()
    {
        var pending = new PendingEditList();
        pending.SetTarget(EditSessionChangeKinds.Relationship, Song, Guid.NewGuid().ToString("D"), label: "The Bends", note: "Not on this album: check it.");
        pending.SetTarget(EditSessionChangeKinds.SiblingMatch, Song,
            EditSessionSiblingMatchTarget.Serialize(new EpisodeSiblingMatchTarget { SeriesId = "200", SeasonNumber = 2 }),
            label: "3 other episodes of Show season 2, by number");

        var rows = EditReviewRows.Build(pending, _ => "Pilot", null);

        Assert.Equal("Not on this album: check it.", rows.Single(row => row.Kind == EditSessionChangeKinds.Relationship).Warning);
        var sibling = rows.Single(row => row.Kind == EditSessionChangeKinds.SiblingMatch);
        Assert.Equal(EditReviewGroup.Identity, sibling.Group);
        Assert.Equal("3 other episodes of Show season 2, by number", sibling.After);
        Assert.Equal(EditTool.Match, sibling.Tool);
    }

    [Fact]
    public void RemovingAChange_TakesItsNoteWithIt()
    {
        var pending = new PendingEditList();
        pending.SetTarget(EditSessionChangeKinds.Relationship, Song, Guid.NewGuid().ToString("D"), note: "Not on this album.");
        var id = pending.Changes.Single().Id;

        pending.Remove(id);

        Assert.Null(pending.Note(id));
    }
}
