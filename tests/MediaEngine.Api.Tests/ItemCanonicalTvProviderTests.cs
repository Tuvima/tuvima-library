using MediaEngine.Api.Endpoints;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;

namespace MediaEngine.Api.Tests;

public sealed class ItemCanonicalTvProviderTests
{
    [Fact]
    public void TelevisionRetailApply_RequiresTvdbNameAndProviderId()
    {
        Assert.True(ItemCanonicalEndpoints.IsRetailProviderAllowed("TV", "tvdb", WellKnownProviders.Tvdb));
        Assert.False(ItemCanonicalEndpoints.IsRetailProviderAllowed("TV", "tmdb", WellKnownProviders.Tmdb));
        Assert.False(ItemCanonicalEndpoints.IsRetailProviderAllowed("TV", "tvdb", WellKnownProviders.Tmdb));
        Assert.False(ItemCanonicalEndpoints.IsRetailProviderAllowed("TV", "tmdb", WellKnownProviders.Tvdb));
        Assert.True(ItemCanonicalEndpoints.IsRetailProviderAllowed("Movies", "tmdb", WellKnownProviders.Tmdb));
        Assert.True(ItemCanonicalEndpoints.IsTvdbSeriesRecord(
            System.Text.Json.Nodes.JsonNode.Parse("{\"id\":389597,\"name\":\"Solo Leveling\"}"), "389597"));
        Assert.False(ItemCanonicalEndpoints.IsTvdbSeriesRecord(
            System.Text.Json.Nodes.JsonNode.Parse("{\"id\":5876034}"), "389597"));
    }

    [Fact]
    public void SeriesSearch_DoesNotTurnOwnedEpisodeNumbersIntoEpisodeCandidates()
    {
        var policy = new ItemCanonicalEndpoints.CanonicalTargetPolicy("TV", "container", "show",
            [MetadataFieldConstants.ShowName], [], [BridgeIdKeys.TvdbId], [], [], true, true, true);
        var draft = new Dictionary<string, string>
        {
            [MetadataFieldConstants.ShowName] = "Solo Leveling",
            [MetadataFieldConstants.SeasonNumber] = "2",
            [MetadataFieldConstants.EpisodeNumber] = "1",
        };

        var fields = ItemCanonicalEndpoints.BuildRetailSearchFields(policy, draft, null);

        Assert.Equal("Solo Leveling", fields?[MetadataFieldConstants.ShowName]);
        Assert.False(fields!.ContainsKey(MetadataFieldConstants.SeasonNumber));
        Assert.False(fields.ContainsKey(MetadataFieldConstants.EpisodeNumber));
        Assert.True(ItemCanonicalEndpoints.IsTvdbShowCandidate("389597",
            new Dictionary<string, string> { [BridgeIdKeys.TvdbId] = "389597" }));
        Assert.False(ItemCanonicalEndpoints.IsTvdbShowCandidate("5876034",
            new Dictionary<string, string>
            {
                [BridgeIdKeys.TvdbId] = "389597",
                [BridgeIdKeys.TvdbEpisodeId] = "5876034",
            }));
    }

    [Fact]
    public void ContainerSearch_ScoresTheContainerInsteadOfTheOpenedChild()
    {
        var policy = new ItemCanonicalEndpoints.CanonicalTargetPolicy("Music", "container", "album",
            [MetadataFieldConstants.Artist, MetadataFieldConstants.Album], [], [], [], [], true, true, true);
        var draft = new Dictionary<string, string>
        {
            [MetadataFieldConstants.Title] = "The Final Song",
            [MetadataFieldConstants.Artist] = "Kendrick Lamar",
            [MetadataFieldConstants.Album] = "GNX",
            [MetadataFieldConstants.TrackNumber] = "12",
        };

        var context = ItemCanonicalEndpoints.BuildRetailSearchContext(
            policy, draft, null, "The Final Song", "Kendrick Lamar", "2024");

        Assert.Equal("GNX", context.LocalTitle);
        Assert.Equal("Kendrick Lamar", context.LocalAuthor);
        Assert.Equal("GNX", context.FileHints?[MetadataFieldConstants.Title]);
        Assert.DoesNotContain(MetadataFieldConstants.TrackNumber, context.FileHints!.Keys);
        Assert.DoesNotContain(MetadataFieldConstants.TrackNumber, context.SearchFields!.Keys);
    }

    [Fact]
    public void ContainerSearch_WithoutParentTitleDoesNotUseOpenedChildTitle()
    {
        var policy = new ItemCanonicalEndpoints.CanonicalTargetPolicy("Music", "container", "album",
            [MetadataFieldConstants.Album], [], [], [], [], true, true, true);
        var draft = new Dictionary<string, string>
        {
            [MetadataFieldConstants.Title] = "The Final Song",
        };

        var context = ItemCanonicalEndpoints.BuildRetailSearchContext(
            policy, draft, null, "The Final Song", null, "2024");

        Assert.Null(context.LocalTitle);
        Assert.Null(context.LocalYear);
        Assert.Null(context.FileHints);
    }

    [Fact]
    public void SeriesSearch_ScoresTheSeriesInsteadOfTheOpenedIssue()
    {
        var policy = new ItemCanonicalEndpoints.CanonicalTargetPolicy("Comics", "container", "series",
            [MetadataFieldConstants.Series], [], [], [], [], true, true, true);
        var draft = new Dictionary<string, string>
        {
            [MetadataFieldConstants.Title] = "Issue #7",
            [MetadataFieldConstants.Series] = "Saga",
            [MetadataFieldConstants.SeriesPosition] = "7",
            [MetadataFieldConstants.Author] = "Brian K. Vaughan",
        };

        var context = ItemCanonicalEndpoints.BuildRetailSearchContext(
            policy, draft, null, "Issue #7", "Brian K. Vaughan", "2014");

        Assert.Equal("Saga", context.LocalTitle);
        Assert.Equal("Saga", context.FileHints?[MetadataFieldConstants.Title]);
        Assert.DoesNotContain(MetadataFieldConstants.SeriesPosition, context.FileHints!.Keys);
    }

    [Fact]
    public void ComicRunSearch_UsesVolumeStrategyEvenWithAnEditedQuery()
    {
        var policy = new ItemCanonicalEndpoints.CanonicalTargetPolicy("Comics", "container", "series",
            [MetadataFieldConstants.Series], [], [BridgeIdKeys.ComicVineVolumeId], [], [], true, true, true);
        var draft = new Dictionary<string, string>
        {
            [MetadataFieldConstants.Series] = "Old run",
            [MetadataFieldConstants.Title] = "Issue #7",
            [MetadataFieldConstants.SeriesPosition] = "7",
        };

        var context = ItemCanonicalEndpoints.BuildRetailSearchContext(
            policy, draft, "New run", "Issue #7", null, null);

        Assert.Equal("New run", context.SearchFields?[MetadataFieldConstants.Series]);
        Assert.Equal("true", context.SearchFields?["container_search"]);
        Assert.DoesNotContain(MetadataFieldConstants.Title, context.SearchFields!.Keys);
    }

    [Fact]
    public void ComicIssueCandidate_RequiresVolumeWhenRunIsAlreadyConfirmed()
    {
        Assert.Null(ItemCanonicalEndpoints.ValidateComicIssueVolumeAlignment(null, null));
        Assert.Contains("does not identify its run", ItemCanonicalEndpoints.ValidateComicIssueVolumeAlignment("100", null));
        Assert.Contains("different run", ItemCanonicalEndpoints.ValidateComicIssueVolumeAlignment("100", "200"));
        Assert.Null(ItemCanonicalEndpoints.ValidateComicIssueVolumeAlignment("100", "100"));
    }

    [Fact]
    public void CandidateCompatibility_RejectsAnAlbumForATrackAndATrackForAnAlbum()
    {
        var album = new ItemCanonicalEndpoints.CanonicalTargetPolicy("Music", "container", "album",
            [MetadataFieldConstants.Artist, MetadataFieldConstants.Album], [], [], [], [], true, true, true);
        var track = new ItemCanonicalEndpoints.CanonicalTargetPolicy("Music", "item", "track",
            [MetadataFieldConstants.Title], [], [], [], [], true, true, true);

        Assert.False(ItemCanonicalEndpoints.IsRetailCandidateCompatible(
            album, "musicbrainz", "recording-1", new Dictionary<string, string>
            {
                [BridgeIdKeys.MusicBrainzRecordingId] = "recording-1",
            }, out var albumReason));
        Assert.Contains("individual track", albumReason);

        Assert.False(ItemCanonicalEndpoints.IsRetailCandidateCompatible(
            track, "musicbrainz", "release-1", new Dictionary<string, string>
            {
                [BridgeIdKeys.MusicBrainzReleaseId] = "release-1",
            }, out var trackReason));
        Assert.Contains("only identifies an album", trackReason);
    }
}
