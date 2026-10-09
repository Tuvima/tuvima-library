using System.Text.Json;
using MediaEngine.Api.Services.Details;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Persons;
using MediaEngine.Domain;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

public sealed class DetailComposerServiceTests
{
    [Fact]
    public void OwnedManifestPositionAndFinalNormalizationPreserveEpisodeStateAndDistinctCounts()
    {
        var show = Guid.NewGuid(); var work = Guid.NewGuid(); var asset = Guid.NewGuid();
        var context = new MediaEngine.Contracts.Display.DisplayEpisodeContextDto(show, work, asset, "Show", "Episode", 1, 5, MediaEngine.Contracts.Display.DisplayContinuationState.Completed, 3000, 3000);
        var item = new SequenceItemViewModel
        {
            Id = work.ToString("D"),
            Title = "Episode",
            EntityType = DetailEntityType.TvEpisode,
            IsOwned = true,
            EpisodeContext = context,
            ProgressState = LibraryProgressState.Completed,
            ProgressPercent = 100,
            PositionSeconds = 3000,
            DurationSeconds = 3000,
            RemainingSeconds = 0,
            ProgressLabel = "Watched",
            EpisodeStillUrl = "/stream/artwork/actual",
            EpisodeStillWidthPx = 1920,
            EpisodeStillHeightPx = 1080
        };
        var items = new List<SequenceItemViewModel> { item };
        Assert.True(InvokePrivate<bool>("TryApplyManifestPositionToOwnedItem", items, new SeriesManifestItemRecord { SeriesQid = "QTestShow", ItemQid = "QTestEpisode", OrderSource = "SeriesOrdinal", LinkedWorkId = work, RawOrdinal = "5", ParsedOrdinal = 5, MembershipScope = "MainSequence" }, 5d, 5d, work, false));
        var normalized = InvokePrivate<List<SequenceItemViewModel>>("NormalizeSequenceItems", items, DetailEntityType.TvEpisode);
        var result = Assert.Single(normalized); Assert.Equal(5, result.PositionNumber); Assert.Same(context, result.EpisodeContext);
        Assert.Equal(item.EpisodeStillUrl, result.EpisodeStillUrl); Assert.Equal(1920, result.EpisodeStillWidthPx); Assert.Equal(1080, result.EpisodeStillHeightPx);
        Assert.Equal(100, result.ProgressPercent); Assert.Equal(3000, result.PositionSeconds); Assert.Equal(3000, result.DurationSeconds); Assert.Equal(0, result.RemainingSeconds); Assert.Equal("Watched", result.ProgressLabel);
        normalized.Add(result);
        var method = DetailComposerImplementationType.GetMethod("BuildSequenceGroups", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var groups = Assert.IsAssignableFrom<IReadOnlyList<SequenceGroupViewModel>>(method.Invoke(null, [normalized, "Season", null]));
        Assert.Equal(1, Assert.Single(groups).OwnedCount); Assert.Equal(1, groups[0].CompletedCount);
    }
    [Theory]
    [InlineData("/watch/player/123", "/watch/player/123?restart=true")]
    [InlineData("/watch/player/123?collectionId=456", "/watch/player/123?collectionId=456&restart=true")]
    public void RestartActionPreservesAValidPlayerRoute(string route, string expected)
    {
        var composer = typeof(DetailComposerService).Assembly.GetType(
            "MediaEngine.Api.Services.Details.Internals.DetailCompositionOrchestrator")!;
        var method = composer.GetMethod("BuildRestartAction",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var action = Assert.IsType<DetailAction>(method.Invoke(null, [route]));
        Assert.Equal("play-from-beginning", action.Key);
        Assert.Equal(expected, action.Route);
    }

    [Theory]
    [InlineData(DetailEntityType.BookSeries, "StructuralCollection")]
    [InlineData(DetailEntityType.ComicSeries, "StructuralCollection")]
    [InlineData(DetailEntityType.MovieSeries, "StructuralCollection")]
    [InlineData(DetailEntityType.TvShow, "Work")]
    [InlineData(DetailEntityType.MusicAlbum, "Work")]
    [InlineData(DetailEntityType.Collection, "Collection")]
    public void CollectionEditorTarget_SelectsShelfEditorWithoutRetargetingIndividualMedia(
        DetailEntityType entityType, string expectedKind)
    {
        var collectionId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var composer = typeof(DetailComposerService).Assembly.GetType(
            "MediaEngine.Api.Services.Details.Internals.DetailCompositionOrchestrator")!;
        var method = composer.GetMethod("BuildCollectionEditorTarget",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var target = Assert.IsType<DetailEditorTarget>(method.Invoke(null, [collectionId, entityType, rootId]));
        Assert.Equal(expectedKind, target.EntityKind);
        Assert.Equal((expectedKind == "Work" ? rootId : collectionId).ToString("D"), target.EntityId);
    }

    [Fact]
    public void SelectMusicAlbumManifestTracks_ScopesOversizedBoxSetToCanonicalDisc()
    {
        using var document = JsonDocument.Parse("""
            {
              "tracks": [
                { "title": "Other Album", "disc_number": 1, "track_number": 1 },
                { "title": "Album Opener", "disc_number": 2, "track_number": 1 },
                { "title": "Album Closer", "disc_number": 2, "track_number": 2 },
                { "title": "Live Bonus", "disc_number": 4, "track_number": 1 }
              ]
            }
            """);
        var canonicalValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.DiscCount] = "9",
            [MetadataFieldConstants.DiscNumber] = "2",
        };

        var selected = DetailComposerService.SelectMusicAlbumManifestTracks(
            document.RootElement.GetProperty("tracks"),
            canonicalValues,
            [2]);

        Assert.Equal(["Album Opener", "Album Closer"], selected.Select(track => track.GetProperty("title").GetString()));
    }

    [Fact]
    public void SelectMusicAlbumManifestTracks_PreservesNormalMultiDiscAlbum()
    {
        using var document = JsonDocument.Parse("""
            {
              "tracks": [
                { "title": "Disc One", "disc_number": 1, "track_number": 1 },
                { "title": "Disc Two", "disc_number": 2, "track_number": 1 }
              ]
            }
            """);
        var canonicalValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.DiscCount] = "2",
            [MetadataFieldConstants.DiscNumber] = "2",
        };

        var selected = DetailComposerService.SelectMusicAlbumManifestTracks(
            document.RootElement.GetProperty("tracks"),
            canonicalValues,
            [2]);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void SelectMusicAlbumManifestTracks_UsesOwnedDiscWhenAlbumRootHasNoDiscNumber()
    {
        using var document = JsonDocument.Parse("""
            {
              "tracks": [
                { "title": "Other Album", "disc_number": 1, "track_number": 1 },
                { "title": "Owned Album", "disc_number": 2, "track_number": 1 },
                { "title": "Live Set", "disc_number": 4, "track_number": 1 }
              ]
            }
            """);
        var canonicalValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.DiscCount] = "9",
        };

        var selected = DetailComposerService.SelectMusicAlbumManifestTracks(
            document.RootElement.GetProperty("tracks"),
            canonicalValues,
            [2]);

        Assert.Equal("Owned Album", Assert.Single(selected).GetProperty("title").GetString());
    }

    [Fact]
    public void TryParseEntityType_DoesNotExposePodcastTypes()
    {
        Assert.False(DetailComposerService.TryParseEntityType("podcast", out _));
        Assert.False(DetailComposerService.TryParseEntityType("podcast-episode", out _));
    }

    [Fact]
    public void TryParseEntityType_ParsesSupportedKebabCaseTypes()
    {
        Assert.True(DetailComposerService.TryParseEntityType("tv-show", out var entityType));
        Assert.Equal(DetailEntityType.TvShow, entityType);
    }

    [Theory]
    [InlineData("listen", DetailPresentationContext.Listen)]
    [InlineData("watch", DetailPresentationContext.Watch)]
    [InlineData("read", DetailPresentationContext.Read)]
    [InlineData("unknown", DetailPresentationContext.Default)]
    public void ParseContext_UsesDefaultForUnknownValues(string value, DetailPresentationContext expected)
    {
        Assert.Equal(expected, DetailComposerService.ParseContext(value));
    }

    [Fact]
    public void ResolveArtworkPresentationMode_PrioritizesRealBackdrops()
    {
        var mode = DetailComposerService.ResolveArtworkPresentationMode(
            DetailEntityType.Movie,
            backdropUrl: "/backdrop.jpg",
            bannerUrl: null,
            coverUrl: "/cover.jpg",
            posterUrl: null,
            portraitUrl: null,
            relatedArtworkCount: 0,
            ownedFormatCount: 1);

        Assert.Equal(ArtworkPresentationMode.CinematicBackdrop, mode);
    }

    [Fact]
    public void ResolveArtworkPresentationMode_UsesCoverGradientWithoutBackdrop()
    {
        var mode = DetailComposerService.ResolveArtworkPresentationMode(
            DetailEntityType.Book,
            backdropUrl: null,
            bannerUrl: null,
            coverUrl: "/cover.jpg",
            posterUrl: null,
            portraitUrl: null,
            relatedArtworkCount: 0,
            ownedFormatCount: 1);

        Assert.Equal(ArtworkPresentationMode.ColorGradientFromArtwork, mode);
    }

    [Fact]
    public void ResolveArtworkPresentationMode_UsesPairedEditionGradientForMultiFormatWorks()
    {
        var mode = DetailComposerService.ResolveArtworkPresentationMode(
            DetailEntityType.Work,
            backdropUrl: null,
            bannerUrl: null,
            coverUrl: "/ebook.jpg",
            posterUrl: null,
            portraitUrl: null,
            relatedArtworkCount: 0,
            ownedFormatCount: 2);

        Assert.Equal(ArtworkPresentationMode.PairedEditionGradient, mode);
    }

    [Fact]
    public void ResolveArtworkPresentationMode_UsesPortraitEchoForPeople()
    {
        var mode = DetailComposerService.ResolveArtworkPresentationMode(
            DetailEntityType.Person,
            backdropUrl: null,
            bannerUrl: null,
            coverUrl: null,
            posterUrl: null,
            portraitUrl: "/portrait.jpg",
            relatedArtworkCount: 0,
            ownedFormatCount: 0);

        Assert.Equal(ArtworkPresentationMode.PortraitEcho, mode);
    }

    [Fact]
    public void ResolveHeroArtwork_PrioritizesBackgroundOverCover()
    {
        var artwork = HeroArtworkResolver.Resolve(
            DetailEntityType.Movie,
            backdropUrl: "/backdrop.jpg",
            bannerUrl: null,
            coverUrl: "/cover.jpg",
            posterUrl: null,
            portraitUrl: null,
            characterImageUrl: null,
            relatedArtworkUrls: []);

        Assert.Equal(HeroArtworkMode.BackdropWithRenderedTitle, artwork.Mode);
        Assert.True(artwork.HasImage);
        Assert.Equal("/backdrop.jpg", artwork.Url);
    }

    [Fact]
    public void ResolveHeroArtwork_UsesCoverFallbackWithoutBackground()
    {
        var artwork = HeroArtworkResolver.Resolve(
            DetailEntityType.Book,
            backdropUrl: null,
            bannerUrl: null,
            coverUrl: "/cover.jpg",
            posterUrl: null,
            portraitUrl: null,
            characterImageUrl: null,
            relatedArtworkUrls: []);

        Assert.Equal(HeroArtworkMode.ArtworkFallback, artwork.Mode);
        Assert.True(artwork.HasImage);
        Assert.Equal("/cover.jpg", artwork.Url);
    }

    [Fact]
    public void ResolveHeroArtwork_UsesPlaceholderWithoutImages()
    {
        var artwork = HeroArtworkResolver.Resolve(
            DetailEntityType.Collection,
            backdropUrl: null,
            bannerUrl: null,
            coverUrl: null,
            posterUrl: null,
            portraitUrl: null,
            characterImageUrl: null,
            relatedArtworkUrls: []);

        Assert.Equal(HeroArtworkMode.Placeholder, artwork.Mode);
        Assert.False(artwork.HasImage);
        Assert.Null(artwork.Url);
    }

    [Fact]
    public void DetailComposer_SourceKeepsMovieTabsOnCombinedOverviewAndUsesDirectEdit()
    {
        var source = ReadDetailComposerSource();

        Assert.Contains("DetailEntityType.Movie when hasUniverse => [\"overview\", \"cast\", \"universe\", \"related\", \"details\"]", source);
        Assert.Contains("DetailEntityType.Movie => [\"overview\", \"cast\", \"related\", \"details\"]", source);
        Assert.DoesNotContain("DetailEntityType.Movie => [\"overview\", \"people\"", source);
        Assert.Contains("DetailEntityType.TvShow => hasUniverse ? [\"overview\", \"cast\", \"universe\", \"related\", \"details\"] : [\"overview\", \"cast\", \"related\", \"details\"]", source);
        Assert.Contains("DetailEntityType.TvSeason => [\"overview\", \"cast\", \"related\", \"details\"]", source);
        Assert.Contains("DetailEntityType.Book when hasUniverse => [\"overview\", \"credits\", \"universe\", \"related\", \"details\"]", source);
        Assert.Contains("DetailEntityType.Book => [\"overview\", \"credits\", \"related\", \"details\"]", source);
        Assert.Contains("DetailEntityType.Audiobook => [\"overview\", \"tracks\", \"details\"]", source);
        Assert.DoesNotContain("DetailEntityType.Audiobook when hasChapters", source);
        Assert.DoesNotContain("DetailEntityType.Book or DetailEntityType.Audiobook => [\"overview\", \"credits\", \"chapters\", \"universe\", \"editions\", \"details\"]", source);
        Assert.Contains("DetailEntityType.ComicIssue when hasUniverse => [\"overview\", \"credits\", \"universe\", \"related\", \"details\"]", source);
        Assert.DoesNotContain("DetailEntityType.ComicIssue when hasUniverse => [\"overview\", \"credits\", \"universe\", \"editions\"", source);
        Assert.Contains("DetailEntityType.Person => [\"overview\"]", source);
        Assert.Contains("DetailEntityType.Collection => [\"overview\", \"details\"]", source);
        Assert.Contains("entityType == DetailEntityType.Collection", source);
        Assert.Contains("? []", source);
        Assert.Contains("BuildStandardCollectionMetadata(works)", source);
        Assert.Contains("c.collection_type AS CollectionType", source);
        Assert.Contains("CollectionTypeNames.Custom", source);
        Assert.Contains("EditorTarget = canEdit", source);
        Assert.Contains("DetailEntityType.BookSeries or DetailEntityType.ComicSeries or DetailEntityType.MovieSeries", source);
        Assert.Contains("Kind = $\"{lane}_count\"", source);
        Assert.DoesNotContain("BuildCollectionLaneActions", source);
        Assert.DoesNotContain("Key = $\"collection-{lane}\"", source);
        Assert.Contains("DetailEntityType.MusicAlbum => [\"overview\", \"details\"]", source);
        Assert.DoesNotContain("DetailEntityType.MusicTrack", source);
        Assert.Contains("HasUniverseRelationship(relationships)", source);
        Assert.DoesNotContain("sync-settings", source);
        Assert.Contains("BuildOverflowActions(workId, entityType, actionAuthorization,", source);
        Assert.Contains("authorization.Allows(action.Key)", source);
        Assert.Contains("Key = \"edit\", Label = \"Edit details\"", source);
        Assert.DoesNotContain("Label = \"Refresh Metadata\"", source);
        Assert.DoesNotContain("Label = \"View File Info\"", source);
    }

    [Fact]
    public void DetailComposer_UsesAlbumRootWorksForSharedMusicAlbumDetails()
    {
        var source = ReadDetailComposerSource();

        Assert.Contains("LoadMusicAlbumSystemViewGroupAsync(collectionId, ct)", source);
        Assert.Contains("LoadMusicAlbumSystemViewWorksAsync(musicAlbumGroup, ct)", source);
        Assert.Contains("GetSystemViewDetailWorksAsync(", source);
        Assert.Contains("MergeMusicAlbumManifestTracks(ownedWorks, values, row.CoverUrl)", source);
        Assert.Contains("AssetId = work.AssetId", source);
    }

    [Fact]
    public void DetailComposer_BuildsTheAlbumCompanionFromExactPrimaryArtistCredits()
    {
        var source = ReadDetailComposerSource();

        Assert.Contains("BuildMusicAlbumCompanionAsync(", source);
        Assert.Contains("group.GroupType == CreditGroupType.PrimaryArtists", source);
        Assert.Contains("Guid.TryParse(primaryArtist.EntityId, out primaryArtistId)", source);
        Assert.Contains("FROM primary_person_media_credits credit", source);
        Assert.Contains("credit.person_id = @personId", source);
        Assert.Contains("credit.credit_key = 'artist'", source);
        Assert.Contains("SELECT DISTINCT COALESCE(p.id, w.id)", source);
        Assert.DoesNotContain("SELECT DISTINCT COALESCE(gp.id, p.id, w.id)", source);
        Assert.Contains("ownedAlbumRootIds.Remove(currentAlbumRootWorkId)", source);
        var companionComposer = source[
            source.IndexOf("BuildMusicAlbumCompanionAsync(", StringComparison.Ordinal)..source.IndexOf("LoadOwnedMusicAlbumRootIdsForArtistAsync(", StringComparison.Ordinal)];
        Assert.DoesNotContain(".Take(3)", companionComposer);
        Assert.Contains("MusicAlbumCompanion = musicAlbumCompanion", source);
        Assert.Contains("DetailEntityType.MusicAlbum => BuildMusicAlbumActions()", source);
        Assert.Contains("Key = \"play-album\"", source);
        Assert.Contains("Key = \"shuffle\"", source);
    }

    [Fact]
    public void DetailComposer_MapsTvNetworkIntoHeroBrand()
    {
        var source = ReadDetailComposerSource();

        Assert.Contains("HeroBrand = BuildHeroBrand", source);
        Assert.Contains("DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode", source);
        Assert.Contains("network_logo_url", source);
        Assert.Contains("HeroBrandImageUrl", source);
    }

    [Fact]
    public void DetailComposer_BuildsTvShowHeroMetadataAndEpisodeAwareActions()
    {
        var source = ReadDetailComposerSource();

        Assert.Contains("if (entityType == DetailEntityType.TvShow)", source);
        Assert.Contains("GetValue(values, \"start_year\")", source);
        Assert.Contains("ownedEpisodeCount == 1 ? \"episode\" : \"episodes\"", source);
        Assert.DoesNotContain(")} owned\"", source);
        Assert.DoesNotContain("GetValue(values, \"episode_count\")", source);
        Assert.Contains("GetValue(values, \"content_rating\")", source);
        Assert.Contains("GetValue(values, \"video_quality\")", source);
        Assert.Contains("GetValue(tvPlaybackValues, MetadataFieldConstants.Rating)", source);
        Assert.Contains("FormatSeasonEpisode(detail.SeasonNumber, detail.EpisodeNumber)", source);
        Assert.Contains("LoadTvSeriesLogoForEpisodeAsync", source);
        Assert.Contains("episodeSeriesLogoUrl", source);
        Assert.Contains("DetailEntityType.TvEpisode => BuildWatchActions($\"/watch/player/{id}\"", source);
        Assert.Contains("SelectFirstOwnedTvEpisode", source);
        Assert.Contains("SelectInProgressTvEpisode", source);
        Assert.Contains("BuildTvShowWatchActions", source);
        Assert.Contains("/watch/player/{episodeId:D}", source);
        Assert.Contains("var tvPlaybackEpisode = tvInProgressEpisode ??", source);
        Assert.Contains("var tvInProgressEpisode = entityType == DetailEntityType.TvShow", source);
        Assert.Contains("LoadTvSeasonArtworkAsync", source);
        Assert.Contains("ArtworkSmallUrl = presentation?.ArtworkSmallUrl", source);
        Assert.Contains("Tagline = entityType == DetailEntityType.TvShow", source);
        Assert.Contains("tvInProgressEpisode?.Description", source);
        Assert.Contains("tvInProgressEpisode?.BackgroundUrl", source);
        Assert.Contains("entityType == DetailEntityType.TvShow ? heroSummary : longDescription", source);
        Assert.DoesNotContain("GetValue(values, \"wikipedia_extract\")", source);
        Assert.Contains("currentWorkId ?? tvPlaybackEpisodeId", source);
        Assert.Contains("FormatTrackDuration(StringHelpers.FirstNonBlankOr(", source);
        Assert.Contains("GetValue(tvPlaybackValues, MetadataFieldConstants.Rating)", source);
        Assert.Contains("LoadWorkAndAssetCanonicalMapAsync(tvPlaybackEpisodeId.Value, ct)", source);
        Assert.Contains("GetValue(tvPlaybackValues, MetadataFieldConstants.Runtime)", source);
        Assert.Contains("GetValue(tvPlaybackValues, \"duration\")", source);
        Assert.Contains("AddTechnicalClaimFallbacksAsync(conn, assetId, values, ct)", source);
        Assert.Contains("claim_key IN ('duration_sec', 'duration_seconds', 'genre')", source);
        Assert.Contains("FormatSecondsDuration(ParseDurationSeconds(StringHelpers.FirstNonBlankOr(", source);
        Assert.Contains("GetValue(tvPlaybackValues, MetadataFieldConstants.Genre)", source);
        Assert.Contains("SplitMetadataValues(playbackGenres).Take(2)", source);
        Assert.Contains("FROM canonical_value_arrays WHERE entity_id = @entityId ORDER BY key, ordinal", source);
        Assert.Contains("GetValue(canonicalValues, MetadataFieldConstants.Genre),\n                     detail.Genre)).Take(12)", source);
        Assert.Contains("LoadWorkCanonicalMapAsync(workId, detail, ct)", source);
        Assert.Contains("SELECT ma.id", source);
        Assert.Contains("entityType == DetailEntityType.TvEpisode ? \"background\" : \"cover\"", source);
        Assert.Contains("useEpisodeArtwork = entityType == DetailEntityType.TvEpisode ? 1 : 0", source);
        Assert.Contains("entityType == DetailEntityType.TvShow", source);
        Assert.Contains("? work.BackgroundUrl", source);
        Assert.Contains(": work.ArtworkUrl", source);
    }

    [Fact]
    public void DetailComposer_DetailAndChildListsPreferDisplayOverrides()
    {
        var source = ReadDetailComposerSource();

        Assert.Contains("LoadWorkDisplayOverridesAsync(workId", source, StringComparison.Ordinal);
        Assert.Contains("ResolveDisplayTitleOverride(displayOverrides, entityType)", source, StringComparison.Ordinal);
        Assert.Contains("w.display_overrides_json AS TEXT) AS WorkDisplayOverridesJson", source, StringComparison.Ordinal);
        Assert.Contains("ResolveDisplayTitleOverride(", source, StringComparison.Ordinal);
        Assert.Contains("overrides.TryGetValue(\"title\"", source, StringComparison.Ordinal);
        var resolverStart = source.IndexOf("ResolveDisplayTitleOverride(IReadOnlyDictionary", StringComparison.Ordinal);
        var resolverEnd = source.IndexOf("LoadCanonicalMapAsync", resolverStart, StringComparison.Ordinal);
        Assert.DoesNotContain("\"display_title\"", source[resolverStart..resolverEnd]);
        Assert.Contains("return $\"work:{work.Id}\";", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailComposer_DrivesHeroProgressFromPlaybackState()
    {
        var source = ReadDetailComposerSource();
        var contracts = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Contracts/Details/DetailDtos.cs"));

        Assert.Contains("Progress = heroProgress", source);
        Assert.Contains("BuildHeroProgress", source);
        Assert.Contains("BuildCollectionHeroProgress", source);
        Assert.Contains("LEFT JOIN user_states us ON us.asset_id = ma.id", source);
        Assert.Contains("var verb = progress is null ? \"Watch\" : \"Resume\"", source);
        Assert.Contains("$\"{verb} {episodePosition}\"", source);
        Assert.Contains("DetailEntityType.TvEpisode => BuildWatchActions($\"/watch/player/{id}\"", source);
        Assert.Contains("Key = \"play-from-beginning\"", source);
        Assert.Contains("Continue watching", source);
        Assert.Contains("public ProgressViewModel? Progress { get; init; }", contracts);
    }

    [Fact]
    public void DetailComposer_UsesAudiobookResumeForContinueActionsAndChapterProgress()
    {
        var source = ReadDetailComposerSource();

        Assert.Contains("ResolveAudiobookTotalDurationSeconds(row, chapters)", source);
        Assert.Contains("LoadAudiobookResumeAsync(conn, row.WorkId, row.AssetId, manifest?.Resume, totalDurationSeconds, ct)", source);
        Assert.Contains("NormalizeAudiobookResumePosition(fallback, durationSeconds)", source);
        Assert.Contains("durationSeconds.Value * Math.Clamp(progressPct.Value, 0, 100) / 100d", source);
        Assert.Contains("FROM audiobook_listen_active_segments", source);
        Assert.Contains("FROM audiobook_listen_history", source);
        Assert.Contains("IsMeaningfulAudiobookResume", source);
        Assert.Contains("BuildListenHeroProgressLabel", source);
        Assert.Contains("BuildAudiobookHeroProgress(entityType, detail.Runtime, mediaGroups)", source);
        Assert.Contains("current.ResumePositionSeconds.Value / totalSeconds * 100", source);
        Assert.Contains("Label = heroProgress is null ? \"Listen\" : \"Continue Listening\"", source);
        Assert.Contains("ResumePositionSeconds = IsPositionWithinChapter(resumeSeconds, chapter.StartSeconds, chapter.EndSeconds)", source);
        Assert.Contains("ProgressPercent = progressPercent", source);
        Assert.Contains("DurationSeconds = durationSeconds", source);
        Assert.Contains("var chapters = manifest?.Chapters ?? []", source);
        Assert.Contains("OwnedCount = items.Count", source);
        Assert.Contains("TotalCount = items.Count", source);
    }

    [Fact]
    public void DetailComposer_ComputesMediaGroupCompletionFromOwnedAssets()
    {
        var source = ReadDetailComposerSource();
        var contracts = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Contracts/Details/DetailDtos.cs"));

        Assert.Contains("CASE WHEN MAX(ma.id) IS NULL THEN 0 ELSE 1 END AS HasAsset", source);
        Assert.Contains("COALESCE(w.is_catalog_only, 0) AS IsCatalogOnly", source);
        Assert.Contains("ApplyMediaGroupCompletion", source);
        Assert.Contains("var expectedTotal = AuthoritativeManifestTotal(manifest)", source);
        Assert.Contains("BuildCollectionMediaGroups(entityType, displayWorks, favoriteWorkIds, expectedTotal)", source);
        Assert.Contains("BuildCollectionSequencePlacement", source);
        Assert.Contains("ApplyManifestPlacement(linkedWork, item)", source);
        Assert.Contains("SequenceLabel = StringHelpers.FirstNonBlankOr(string.Empty, item.RawOrdinal", source);
        Assert.Contains("PositionSort = positionSort", source);
        Assert.DoesNotContain("?? index + 1", source);
        Assert.Contains("ResolveTvSeriesRootWorkIdAsync(workId, ct)", source);
        Assert.Contains("if (episodeShowId is { } showId)", source);
        Assert.Contains("WHERE child.depth < 32", source);
        Assert.Contains("workId,\n                profileId,\n                authorizedWorks: authorizedWorks);", source);
        Assert.Contains(".Where(work => expectedTotal is > 0 || work.IsOwned)", source);
        Assert.Contains("selectedGroup?.HasAuthoritativeTotal == true", source);
        Assert.Contains("HasAuthoritativeTotal = mainSequenceExpectedTotal.HasValue", source);
        Assert.Contains("SequencePlacement = sequencePlacement", source);
        Assert.Contains("entityType == DetailEntityType.TvShow", source);
        Assert.Contains("var mediaGroups = entityType == DetailEntityType.TvShow", source);
        Assert.Contains("MediaGroups = mediaGroups", source);
        Assert.Contains("PrimaryModule = BuildPrimaryModule(entityType, sequencePlacement, mediaGroups)", source);
        Assert.Contains("var total = Math.Max(group.Items.Count, group.TotalCount)", source);
        Assert.Contains("DetailEntityType.MovieSeries => \"Films\"", source);
        Assert.Contains("DetailEntityType.BookSeries => \"Books\"", source);
        Assert.Contains("InitiallyCollapsed = total > 0 && owned == 0", source);
        Assert.Contains("Actions = work.IsOwned", source);
        Assert.Contains("DetailEntityType.TvEpisode ? \"play\" : \"open\"", source);
        Assert.Contains("public int OwnedCount { get; init; }", contracts);
        Assert.Contains("public double CompletionPercent { get; init; }", contracts);
    }

    [Fact]
    public void DetailComposer_BuildsWatchHeroActionsInMockupOrder()
    {
        var source = ReadDetailComposerSource();

        Assert.DoesNotContain("Key = \"watch-party\"", source);
        Assert.Contains("Label = isSelected ? \"In My List\" : \"Add to My List\"", source);
        Assert.Contains("Key = \"my-list\"", source);
        Assert.DoesNotContain("Label = \"Add to Collection\"", source);
        Assert.Contains("BuildReactionAction", source);
        Assert.Contains("Key = \"reaction-menu\"", source);
        Assert.Contains("Key = \"reaction-dislike\"", source);
        Assert.Contains("Key = \"reaction-like\"", source);
        Assert.DoesNotContain("Key = \"reaction-love\"", source);
        Assert.Contains("return actions;", source);
        Assert.DoesNotContain("SupportsWatchParty", source);
    }

    [Fact]
    public void DetailComposer_UsesChildArtworkFallbackExceptForTvShows()
    {
        var source = ReadDetailComposerSource();

        Assert.Contains("fallbackBackdrop", source);
        Assert.Contains("fallbackCover", source);
        Assert.Contains("var allowChildArtworkFallback = entityType != DetailEntityType.TvShow", source);
        Assert.Contains("allowChildArtworkFallback ? relatedArt : []", source);
        Assert.Contains("var collectionBackdrop = IsStructuralContainer(entityType)", source);
        Assert.Contains("DetailEntityType.TvShow => work.ArtworkUrl", source);
        Assert.Contains("var collectionCover = IsStructuralContainer(entityType)", source);
        Assert.Contains("'hero_url', 'hero'", source);
        Assert.Contains("SelectMany(w => new[] { w.BackgroundUrl, w.ArtworkUrl })", source);
        Assert.Contains("cv.entity_id = ma.id AND cv.key IN ('cover_url', 'cover')", source);
        Assert.DoesNotContain("LEFT JOIN canonical_values cover_asset", source);
        Assert.Contains("COALESCE(gp.id, p.id, w.id)", source);
        Assert.Contains("ResolveCollectionArtworkUrl", source);
        Assert.Contains("DisplayArtworkUrlResolver.Resolve(value, assetId, kind, state)", source);
        Assert.Contains("'episode_still_url', 'episode_still', 'still_url', 'still'", source);
        Assert.Contains("Description = row.Description", source);
        Assert.Contains("Duration = FormatTrackDuration(row.Duration)", source);
        Assert.Contains("Route = item.Route", source);
    }

    [Fact]
    public void DetailComposer_JoinsCollectionItemsWhenLoadingCollectionWorks()
    {
        var source = ReadDetailComposerSource();

        Assert.Contains("collectionId = GuidSql.ToBlob(collectionId)", source, StringComparison.Ordinal);
        Assert.Contains("rootWorkId = rootWorkId.HasValue ? GuidSql.ToBlob(rootWorkId.Value) : null", source, StringComparison.Ordinal);
        Assert.Contains("defaultOwnerUserId = GuidSql.ToBlob(profileId ?? DefaultOwnerUserId)", source, StringComparison.Ordinal);
        Assert.Contains("GuidSql.FromDb(bytes).ToString(\"D\")", source, StringComparison.Ordinal);
        Assert.Contains("(string?)StringValue(row.WorkDisplayOverridesJson)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("collectionId = collectionId.ToString()", source, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN collection_items ci ON ci.work_id = w.id AND ci.collection_id = @collectionId", source);
        Assert.Contains("OR ci.collection_id = @collectionId", source);
        Assert.Contains("_collectionCatalog.GetItemsAsync(", source);
        Assert.Contains("OR w.id IN @resolvedWorkIds", source);
        Assert.Contains("resolvedWorkIds.Select(GuidSql.ToBlob).ToArray()", source);
        Assert.Contains("work with { HasAsset = true, IsCatalogOnly = false, Ownership = \"Owned\" }", source);
        Assert.DoesNotContain("entityType = InferCollectionEntityType(works)", source);
        Assert.Contains("ORDER BY COALESCE(ci.sort_order, 9999)", source);
        var contributorStart = source.IndexOf("LoadContributorTargetIdsAsync", StringComparison.Ordinal);
        var contributorEnd = source.IndexOf("if (row is null)", contributorStart, StringComparison.Ordinal);
        Assert.DoesNotContain("collection_items ci", source[contributorStart..contributorEnd]);
    }

    [Fact]
    public void MediaEditorNavigator_UsesSettableRowsForSqliteMaterialization()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Api/Services/ReadServices/MediaEditorNavigationReadService.cs"));

        Assert.Contains("private sealed class NavigatorTreeRow", source);
        Assert.Contains("public long? Ordinal { get; init; }", source);
        Assert.Contains("public long IsCatalogOnly { get; init; }", source);
        Assert.Contains("private sealed class NavigatorValueRow", source);
        Assert.Contains("MIN(ma.id) AS AssetId", source);
        Assert.DoesNotContain("CAST(MIN(ma.id) AS TEXT) AS AssetId", source);
    }

    [Fact]
    public void DetailComposer_PrefersOwnedFormatArtworkForWorkHero()
    {
        var source = ReadDetailComposerSource();

        Assert.Contains("var foregroundArtworkUrl = StringHelpers.FirstNonBlankOr(string.Empty,", source);
        Assert.Contains("ownedCoverUrls.FirstOrDefault(),", source);
        Assert.Contains("detail.CoverUrl,", source);
        Assert.Contains("var managedCurrentArtworkUrl = await _reader.LoadManagedWorkCoverUrlAsync(", source);
        Assert.Contains("entityType,", source);
        Assert.Contains("row.WorkId == workId ? currentArtworkUrl : null", source);
        Assert.Contains("mc.claim_key IN ('cover_url', 'cover', 'poster_url', 'poster')", source);
        Assert.Contains("var detail = await _libraryItems.GetDetailAsync(workId, ct)", source);
        Assert.Contains("var managedArtworkUrl = await _reader.LoadManagedWorkCoverUrlAsync(", source);
        Assert.Contains("$\"/stream/artwork/{preferred.Id:D}\"", source);
        Assert.Contains("$\"/stream/entity/{ToDetailRouteEntityType(entityType)}/{entityId:D}/cover\"", source);
        Assert.Contains("artworkFallback.CoverUrl", source);
        Assert.Contains("WHERE entity_id = AssetId AND key IN ('cover_url', 'cover', 'poster_url', 'poster')", source);
        Assert.Contains("WHERE entity_id = WorkId AND key IN ('cover_url', 'cover', 'poster_url', 'poster')", source);
        Assert.Contains("WHERE entity_id = RootWorkId AND key IN ('cover_url', 'cover', 'poster_url', 'poster')", source);
    }

    [Fact]
    public void DetailComposer_PopulatesCastCharactersAndRelationshipsForCollectionSurfaces()
    {
        var source = ReadDetailComposerSource();
        var creditSource = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Api/Services/ReadServices/PersonCreditReadService.cs"));

        Assert.Contains("BuildCollectionCreditsAsync(collectionId, rootWorkId, works, entityType, values, ct)", source);
        Assert.Contains("IReadOnlyList<CreditGroupViewModel> contributorGroups = IsStructuralContainer(entityType)", source);
        Assert.Contains("var characterGroups = IsStructuralContainer(entityType)", source);
        Assert.Contains(": await BuildCollectionCharactersAsync(collectionId, row.WikidataQid, authorizedWorks, ct)", source);
        Assert.Contains("BuildUniverseCastGroupsAsync(row.WikidataQid, authorizedWorks, ct)", source);
        Assert.Contains("BuildUniverseRelationshipGroupsAsync(row.WikidataQid, ct)", source);
        Assert.Contains("ApiImageUrls.BuildCharacterPortraitUrl(row.PortraitId", source);
        Assert.Contains("private sealed class CollectionCharacterRow", source);
        Assert.Contains("private sealed class UniversePerformerRow", source);
        Assert.Contains("DetailEntityType.Movie => directors.Take(1).Concat(cast.Take(5)).ToList()", source);
        Assert.Contains("DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode => cast.Take(5).ToList()", source);
        Assert.Contains("Title = \"Actors\"", source);
        Assert.Contains(") AS RootWorkQid", creditSource);
        Assert.Contains("await BuildExplicitCastAsync(effectiveRootWorkQid, rootRankMap, _db, ct)", creditSource);
        Assert.Contains("workClaimCredits.Count == 0", creditSource);
        Assert.Contains("rootClaimCredits.Count > 0", creditSource);
        Assert.Contains("BuildFallbackCreditsFromCanonicalArrayAsync(work.RootWorkId.Value, _canonicalArrayRepo, _personRepo, ct)", creditSource);
        Assert.Contains("CastRankMap.BuildAsync", creditSource);
        Assert.Contains("ORDER BY cpl.rowid", creditSource);
    }

    [Fact]
    public void DetailComposer_SequencePlacementUsesWikidataPositionsWithoutRowOrderFallback()
    {
        var source = ReadDetailComposerSource();

        Assert.Contains("claim_key = 'series_position' AND provider_id = @wikidataProviderId", source);
        Assert.Contains("WellKnownProviders.Wikidata.ToString()", source);
        Assert.Contains("QueryAsync<SequenceRow>", source);
        Assert.Contains("PositionNumber = positionNumber", source);
        Assert.DoesNotContain("PositionNumber = positionNumber ?? index + 1", source);
    }

    [Fact]
    public void DetailComposer_SequencePlacementUsesKnownSeriesMembersForMissingSlots()
    {
        var source = ReadDetailComposerSource();

        Assert.Contains("series_qid", source);
        Assert.Contains("LoadManifestItemsForSequenceContainerAsync(normalizedContainerId, ct)", source);
        Assert.Contains("WHERE series_qid = @containerId", source);
        Assert.Contains("GetItemsBySeriesQidAsync(containerId, ct)", source);
        Assert.Contains("WHERE parent_collection_qid = @containerId", source);
        Assert.Contains("var exactManifestItems = manifestItems", source);
        Assert.Contains("SequenceContainerIdEquals(item.SeriesQid, normalizedContainerId)", source);
        Assert.Contains("manifestItems = exactManifestItems", source);
        Assert.Contains("MergeManifestItems(items, scopedManifestItems, currentWorkQid, currentWorkId, entityType)", source);
        Assert.Contains("FROM series_members", source);
        Assert.Contains("MergeSequenceManifestPlaceholdersAsync(items, manifestContainerId, detail.WikidataQid, workId, entityType, ct)", source);
        Assert.Contains("ApplyExactManifestPositionsAsync(items, manifestContainerId, entityType, ct)", source);
        Assert.Contains("SELECT linked_work_id AS LinkedWorkId", source);
        Assert.Contains("WHERE series_qid = @seriesQid", source);
        Assert.Contains("ToManifestSourcePosition", source);
        Assert.Contains("ResolveLinkedManifestSequenceContainerOptionsAsync(workId, entityType, detail.MediaType, ct)", source);
        Assert.Contains("ResolveLocalSequenceContainerOptionAsync(workId, entityType, detail.MediaType, ct)", source);
        Assert.DoesNotContain("smi.parent_collection_qid AS ContainerId", source);
        Assert.Contains("containerKind') AS TEXT), 'OrderedSeries') NOT IN ('Franchise', 'Universe', 'WikimediaList', 'PublisherOrProductionList')", source);
        Assert.Contains("IsParentSequenceContainer(scopedManifestItems, normalizedContainerId)", source);
        Assert.Contains("items.Count <= 1 && !hasExplicitSequenceEvidence", source);
        Assert.Contains("!hasExplicitSequenceEvidence && !hasPositionEvidence", source);
        Assert.Contains("LoadSequenceExpectedTotalAsync(containerId, ct)", source);
        Assert.Contains("?? await LoadSequenceExpectedTotalAsync(sourceContainerId, ct)", source);
        Assert.Contains("TotalKnownItems = totalKnownItems", source);
        Assert.Contains("HasAuthoritativeTotal = expectedTotal.HasValue", source);
        Assert.Contains("json_extract(api_metadata_json, '$.completeness') = 'Complete'", source);
        Assert.Contains("wikidata-manifest-rows", source);
        Assert.Contains("DeduplicateManifestMergeItems(merged)", source);
        Assert.Contains("BuildOwnedPositionSet(merged)", source);
        Assert.Contains("ManifestSourcePosition(manifestItem)", source);
        Assert.Contains("ManifestOrderingSort(manifestItem)", source);
        Assert.Contains("ManifestScopeGroup(manifestItem.MembershipScope)", source);
        Assert.DoesNotContain("BuildManifestDisplayPositions", source);
        Assert.DoesNotContain("NormalizeContiguousSequenceDisplayPositions", source);
        Assert.Contains("BuildManifestMergeKey", source);
        Assert.DoesNotContain("if (isLinkedOwned || (!string.IsNullOrWhiteSpace(manifestItem.ItemQid) && ownedQids.Contains(manifestItem.ItemQid)))", source);
        Assert.Contains("Missing from library", source);
    }

    [Fact]
    public void DetailComposer_PreservesOnlySourceOrdinalsAsDisplayedPositions()
    {
        var unnumbered = new SeriesManifestItemRecord
        {
            SeriesQid = "QSeries",
            ItemQid = "QShort",
            SortOrder = 10,
            OrderSource = "PublicationDate",
        };
        var decimalNumbered = new SeriesManifestItemRecord
        {
            SeriesQid = "QSeries",
            ItemQid = "QNovella",
            RawOrdinal = "0.3",
            ParsedOrdinal = 0.3,
            SortOrder = 2,
            OrderSource = "SeriesOrdinal",
        };

        var sourcePositionMethod = DetailComposerImplementationType.GetMethod(
            "ManifestSourcePosition",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(sourcePositionMethod);
        Assert.Null(sourcePositionMethod!.Invoke(null, [unnumbered]));
        var orderingSortMethod = DetailComposerImplementationType.GetMethod(
            "ManifestOrderingSort",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(orderingSortMethod);
        Assert.Equal(10d, (double?)orderingSortMethod!.Invoke(null, [unnumbered]));
        Assert.Equal(0.3d, (double?)sourcePositionMethod.Invoke(null, [decimalNumbered]));

        var anthologyNumbered = new SeriesManifestItemRecord
        {
            SeriesQid = "QSeries",
            ItemQid = "QShort",
            RawOrdinal = "4",
            ParsedOrdinal = 4,
            OrdinalScopeQid = "QAnthology",
            SortOrder = 10,
            OrderSource = "SeriesOrdinal",
        };
        Assert.Null(sourcePositionMethod.Invoke(null, [anthologyNumbered]));
    }

    [Fact]
    public void DetailComposer_CollapsesCurrentWorkFallbackAtTheSameStructuralSlot()
    {
        var linkedWorkId = Guid.NewGuid();
        var currentWorkId = Guid.NewGuid();
        var items = new List<SequenceItemViewModel>
        {
            new()
            {
                Id = linkedWorkId.ToString("D"),
                EntityType = DetailEntityType.Book,
                Title = "Example Volume",
                PositionNumber = 3,
                PositionSort = 3,
                PositionLabel = "3",
                IsOwned = true,
            },
            new()
            {
                Id = currentWorkId.ToString("D"),
                EntityType = DetailEntityType.Book,
                Title = "Example Volume",
                PositionNumber = 3,
                PositionSort = 3,
                PositionLabel = "3",
                IsCurrent = true,
                IsOwned = true,
            },
        };

        var method = DetailComposerImplementationType.GetMethod(
            "DeduplicateManifestMergeItems",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var deduplicated = Assert.IsAssignableFrom<IEnumerable<SequenceItemViewModel>>(
                method!.Invoke(null, [items]))
            .ToList();

        var item = Assert.Single(deduplicated);
        Assert.True(item.IsCurrent);
        Assert.True(item.IsOwned);
    }

    [Fact]
    public void DetailComposer_DoesNotConsumeAnotherFormatsManifestLinkAsTheCurrentOwnedItem()
    {
        var currentWorkId = Guid.NewGuid();
        var audiobookWorkId = Guid.NewGuid();
        var ownedItems = new List<SequenceItemViewModel>
        {
            new()
            {
                Id = currentWorkId.ToString("D"),
                EntityType = DetailEntityType.Book,
                Title = "Leviathan Wakes",
                PositionNumber = 1,
                PositionSort = 1,
                PositionLabel = "1",
                PositionText = "1",
                IsCurrent = true,
                IsOwned = true,
            },
        };
        var manifestItems = new List<SeriesManifestItemRecord>
        {
            new()
            {
                SeriesQid = "QSeries",
                ItemQid = "QLeviathan",
                ItemLabel = "Leviathan Wakes",
                RawOrdinal = "1",
                ParsedOrdinal = 1,
                OrdinalScopeQid = "QSeries",
                SortOrder = 1,
                OrderSource = "SeriesOrdinal",
                LinkedWorkId = currentWorkId,
            },
            new()
            {
                SeriesQid = "QSeries",
                ItemQid = "QCaliban",
                ItemLabel = "Caliban's War",
                RawOrdinal = "2",
                ParsedOrdinal = 2,
                OrdinalScopeQid = "QSeries",
                SortOrder = 2,
                OrderSource = "SeriesOrdinal",
                LinkedWorkId = audiobookWorkId,
            },
        };

        var merged = InvokePrivate<List<SequenceItemViewModel>>(
            "MergeManifestItems",
            ownedItems,
            manifestItems,
            "QLeviathan",
            currentWorkId,
            DetailEntityType.Book);

        Assert.Equal(2, merged.Count);
        Assert.Equal(1, Assert.Single(merged, item => item.IsCurrent).PositionNumber);
        var missingBook = Assert.Single(merged, item => item.Id == "missing-QCaliban");
        Assert.False(missingBook.IsOwned);
        Assert.Equal(2, missingBook.PositionNumber);
    }

    [Fact]
    public void DetailComposer_DoesNotDisplayAnOrdinalBorrowedFromAnotherContainer()
    {
        var manifestItems = new List<SeriesManifestItemRecord>
        {
            new()
            {
                SeriesQid = "QSeries",
                ItemQid = "QChurn",
                ItemLabel = "The Churn",
                RawOrdinal = "4",
                ParsedOrdinal = 4,
                OrdinalScopeQid = "QAnthology",
                SortOrder = 7,
                MembershipScope = "Supplementary",
                OrderSource = "SeriesOrdinal",
            },
        };

        var merged = InvokePrivate<List<SequenceItemViewModel>>(
            "MergeManifestItems",
            new List<SequenceItemViewModel>(),
            manifestItems,
            null,
            Guid.Empty,
            DetailEntityType.Book);

        var churn = Assert.Single(merged);
        Assert.Null(churn.PositionNumber);
        Assert.Null(churn.PositionLabel);
        Assert.Null(churn.PositionText);
        Assert.Equal(7, churn.PositionSort);
        Assert.Equal("supplementary", churn.GroupKey);
    }

    [Fact]
    public void DetailComposer_SeparatesMainSeriesFromShortFictionCounts()
    {
        var items = new List<SequenceItemViewModel>
        {
            new() { Title = "Leviathan Wakes", GroupKey = "main-sequence", GroupTitle = "Main Series", MembershipScope = "MainSequence" },
            new() { Title = "The Churn", GroupKey = "supplementary", GroupTitle = "Short Fiction & Extras", MembershipScope = "Supplementary", IsCurrent = true },
        };

        var groups = InvokePrivate<List<SequenceGroupViewModel>>(
            "BuildSequenceGroups",
            items,
            "Books",
            9);

        Assert.Equal(2, groups.Count);
        Assert.Equal(9, Assert.Single(groups, group => group.Key == "main-sequence").TotalKnownItems);
        Assert.Equal(1, Assert.Single(groups, group => group.Key == "supplementary").TotalKnownItems);
    }

    [Fact]
    public void DetailComposer_AppliesAuthoritativeTotalToUngroupedFiniteSeries()
    {
        var items = new List<SequenceItemViewModel>
        {
            new() { Title = "Film One", GroupKey = "all", GroupTitle = "Films", IsCurrent = true },
            new() { Title = "Film Two", GroupKey = "all", GroupTitle = "Films" },
        };

        var groups = InvokePrivate<List<SequenceGroupViewModel>>(
            "BuildSequenceGroups",
            items,
            "Films",
            5);

        Assert.Equal(5, Assert.Single(groups).TotalKnownItems);
    }

    [Fact]
    public void DetailComposer_ExposesWikidataAndSeriesSourceLinks()
    {
        var source = ReadDetailComposerSource();
        var resolver = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src/MediaEngine.Api/Services/Details/Internals/ProviderSourceLinkResolver.cs"));
        var contracts = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Contracts/Details/DetailDtos.cs"));

        Assert.Contains("SourceLinks = BuildExternalSourceLinks(detail.WikidataQid", source);
        Assert.Contains("\"wikidata-series\"", source);
        Assert.Contains("FirstText(sequence?.SourceContainerId, sequence?.ContainerId)", source);
        Assert.Contains("_providerSourceLinks.Resolve", source);
        Assert.Contains("UiMetadata?.ExternalLinks", resolver);
        Assert.Contains("linkConfig.UrlTemplate", resolver);
        Assert.DoesNotContain("https://www.themoviedb.org/", resolver, StringComparison.Ordinal);
        Assert.DoesNotContain("https://musicbrainz.org/", resolver, StringComparison.Ordinal);
        Assert.Contains("\"Series on Wikidata\"", source);
        Assert.Contains("public IReadOnlyList<ExternalSourceLinkViewModel> SourceLinks { get; init; } = [];", contracts);
        Assert.Contains("public sealed class ExternalSourceLinkViewModel", contracts);
        Assert.Contains("public string? SourceContainerId { get; init; }", contracts);
        Assert.Contains("public IReadOnlyList<string> EquivalentContainerIds { get; init; } = [];", contracts);
    }

    [Fact]
    public void DetailComposer_UsesIssueScopedComicDescriptionAndAttribution()
    {
        var source = ReadDetailComposerSource();

        Assert.Contains("MetadataFieldConstants.IssueDescription", source);
        Assert.Contains("IsComicIssueDescriptionSourceKey", source);
        Assert.Contains("\"issue_overview\"", source);
        Assert.Contains("BuildComicIssueFallbackDescription", source);
        Assert.Contains("selection.IsGeneratedFallback", source);
        Assert.Contains("SourceName = \"Comic Vine\"", source);
        Assert.Contains("SourceTitle = \"issue synopsis\"", source);
        Assert.Contains("LicenseName = \"Comic Vine API Terms\"", source);
        Assert.Contains("LicenseUrl = \"https://comicvine.gamespot.com/api/\"", source);
        Assert.Contains("if (!isComicVine)", source);
        Assert.Contains("GetCanonicalProviderId(detail, selection.SourceKey!)", source);
        Assert.Contains("normalizedTitle == normalizedSeries", source);
        Assert.DoesNotContain("Comic Vine: issue synopsis\";\n            SourceName = \"Wikipedia\"", source);
    }

    [Fact]
    public void DetailComposer_SequencePlacementUsesManifestOptionsAndHydratorRejectsMovieFranchiseFallback()
    {
        var source = ReadDetailComposerSource();
        var hydratorSource = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Providers/Services/WikidataSeriesManifestHydrationService.cs"));

        Assert.Contains("ResolveSequenceContainerOptions(detail, entityType)", source);
        Assert.Contains("current_work.collection_id AS CollectionId", source);
        Assert.Contains("w.collection_id = current.CollectionId", source);
        Assert.Contains("SourceContainerId = FirstText(qid, providerKey)", source);
        Assert.Contains("EquivalentContainerIds = BuildSequenceContainerAliases", source);
        Assert.Contains("smi.collection_id AS CollectionId", source);
        Assert.Contains("smi.membership_scope IN ('MainSequence', 'Supplementary', 'Unpositioned')", source);
        Assert.Contains("var wikidataLinkedContainers", source);
        Assert.Contains("IsProviderBackedSequenceContainer(option)", source);
        Assert.Contains("wikidataLinkedContainers.Any(wikidata => ShouldMergeSequenceContainerOptions(option, wikidata))", source);
        Assert.Contains("BuildManifestMemberFingerprint(manifestItems)", source);
        Assert.Contains("!linkedContainers.Any(linked => ShouldMergeSequenceContainerOptions(linked, option))", source);
        Assert.Contains("hasTrustedSequenceContainer && IsTitleOnlySequenceContainerOption(option)", source);
        Assert.Contains("availableContainers.Any(IsLocalOrProviderBackedSequenceContainer)", source);
        Assert.Contains("IsComicSequenceEntity(entityType)", source);
        Assert.Contains("IsWikidataOnlySequenceContainer(option)", source);
        Assert.Contains("if (!IsComicSequenceEntity(entityType))", source);
        Assert.Contains("availableContainers.RemoveAll(IsTitleOnlySequenceContainerOption)", source);
        Assert.Contains("equivalentContainerIds: collectionAliases", source);
        Assert.Contains("ShouldMergeSequenceContainerOptions", source);
        Assert.Contains("DeduplicateSequenceContainerOptions", source);
        Assert.Contains("CanChooseContainer = distinctContainers.Count > 1", source);
        Assert.Contains("SequenceContainerOptionMatches", source);
        Assert.Contains("PreferRoutableContainerId", source);
        Assert.DoesNotContain("key IN ('series', 'franchise')", source);
        Assert.DoesNotContain("GetDetailCanonicalValue(detail, MetadataFieldConstants.Franchise)", source);
        Assert.DoesNotContain("qid = GetDetailCanonicalValue(detail, \"franchise_qid\")", source);
        Assert.Contains("IsManifestItemInMediaScope", source);
        Assert.Contains("MediaType.Movies", hydratorSource);
        Assert.Contains("MediaType.Comics", hydratorSource);
        Assert.Contains("\"series_qid\"", hydratorSource);
        Assert.DoesNotContain("candidates.Count == 0 && mediaType is MediaType.Movies", hydratorSource);
        Assert.DoesNotContain("\"franchise_qid\"", hydratorSource);
        Assert.Contains("RelType, \"series\"", hydratorSource);
        Assert.DoesNotContain("context.MediaType is MediaType.Movies or MediaType.TV", hydratorSource);
        Assert.DoesNotContain("\"fictional_universe_qid\"", hydratorSource);
        Assert.Contains("LooksLikeEditionOrTranslation", hydratorSource);
    }

    [Fact]
    public void DetailComposer_DoesNotMergeSequenceContainersByDisplayTitleAlone()
    {
        var localCollection = new SequenceContainerOptionViewModel
        {
            ContainerId = Guid.NewGuid().ToString("D"),
            ContainerTitle = "Example Collection",
            MediaScope = "Watch",
        };
        var manifestSeries = new SequenceContainerOptionViewModel
        {
            ContainerId = "Q12345",
            SourceContainerId = "Q12345",
            ContainerTitle = "Example",
            MediaScope = "Watch",
        };

        Assert.False(InvokePrivate<bool>("ShouldMergeSequenceContainerOptions", localCollection, manifestSeries));
    }

    [Fact]
    public void DetailComposer_DropsTitleOnlyFallbackWhenTrustedSeriesIdentityExists()
    {
        var source = ReadDetailComposerSource();

        Assert.Contains("hasTrustedSequenceContainer", source);
        Assert.Contains("availableContainers.RemoveAll(IsTitleOnlySequenceContainerOption)", source);
        Assert.Contains("availableContainers.Any(IsLocalOrProviderBackedSequenceContainer)", source);
    }

    [Fact]
    public void DetailComposer_UsesComicProviderOrderAndKeepsWikidataAsIdentityOnly()
    {
        var source = ReadDetailComposerSource();

        Assert.Contains("IsComicSequenceEntity(entityType) && IsWikidataOnlySequenceContainer(option)", source);
        Assert.Contains("!availableContainers.Any(existing => ShouldMergeSequenceContainerOptions(existing, option))", source);
        Assert.Contains("if (!IsComicSequenceEntity(entityType))", source);
        Assert.Contains("MergeSequenceManifestPlaceholdersAsync", source);
        Assert.Contains("ApplyExactManifestPositionsAsync", source);
    }

    [Fact]
    public void SeriesProjection_DoesNotContainKnownTitleOrQidExceptions()
    {
        var root = FindRepoRoot();
        var source = string.Join(
            '\n',
            ReadDetailComposerSource(),
            File.ReadAllText(Path.Combine(root, "src/MediaEngine.Providers/Services/CollectionAssignmentService.cs")),
            File.ReadAllText(Path.Combine(root, "src/MediaEngine.Providers/Services/WikidataSeriesManifestHydrationService.cs")));

        foreach (var forbidden in new[]
        {
            "Dune",
            "Harry Potter",
            "Lord of the Rings",
            "Blade Runner",
            "Spider-Verse",
            "Dark Knight",
            "The Matrix",
            "Q2307877",
            "Q2111133",
            "Q99601314",
            "Q12859908",
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DetailComposer_PreservesDistinctSequenceSelectorIdentities()
    {
        var localCollectionId = Guid.NewGuid().ToString("D");
        var options = new List<SequenceContainerOptionViewModel>
        {
            new()
            {
                ContainerId = localCollectionId,
                ContainerTitle = "Example Collection",
                MediaScope = "Watch",
                EquivalentContainerIds = [localCollectionId],
            },
            new()
            {
                ContainerId = "Q12345",
                SourceContainerId = "Q12345",
                ContainerTitle = "Example",
                MediaScope = "Watch",
                EquivalentContainerIds = ["Q12345"],
            },
        };

        var distinct = InvokePrivate<List<SequenceContainerOptionViewModel>>(
            "DeduplicateSequenceContainerOptions",
            options);

        Assert.Equal(2, distinct.Count);
        Assert.Contains(distinct, option => option.ContainerId == localCollectionId);
        Assert.Contains(distinct, option => option.ContainerId == "Q12345");
    }

    [Fact]
    public void DetailComposer_MergesProviderAndLocalRepresentationsWithTheSameCollectionIdentity()
    {
        var collectionId = Guid.NewGuid().ToString("D");
        var options = new List<SequenceContainerOptionViewModel>
        {
            new()
            {
                ContainerId = collectionId,
                ContainerTitle = "Local Working Label",
                MediaScope = "Watch",
                EquivalentContainerIds = [collectionId],
            },
            new()
            {
                ContainerId = "catalog:collection:42",
                SourceContainerId = "catalog:collection:42",
                ContainerTitle = "Canonical Set Name",
                MediaScope = "Watch",
                EquivalentContainerIds = ["catalog:collection:42", collectionId],
            },
        };

        var distinct = InvokePrivate<List<SequenceContainerOptionViewModel>>(
            "DeduplicateSequenceContainerOptions",
            options);

        var merged = Assert.Single(distinct);
        Assert.Equal(collectionId, merged.ContainerId);
        Assert.Equal("catalog:collection:42", merged.SourceContainerId);
        Assert.Equal("Canonical Set Name", merged.ContainerTitle);
        Assert.Contains(collectionId, merged.EquivalentContainerIds);
        Assert.Contains("catalog:collection:42", merged.EquivalentContainerIds);
    }

    [Fact]
    public void DetailComposer_UsesOrderedMembersToIdentifyEquivalentCrossProviderContainers()
    {
        var firstManifest = new List<SeriesManifestItemRecord>
        {
            new() { ItemQid = "a1", ItemLabel = "First Film", MembershipScope = "MainSequence", ParsedOrdinal = 1, OrdinalScopeQid = "source-a", SeriesQid = "source-a", OrderSource = "SeriesOrdinal" },
            new() { ItemQid = "a2", ItemLabel = "Second Film", MembershipScope = "MainSequence", ParsedOrdinal = 2, OrdinalScopeQid = "source-a", SeriesQid = "source-a", OrderSource = "SeriesOrdinal" },
        };
        var equivalentManifest = new List<SeriesManifestItemRecord>
        {
            new() { ItemQid = "b1", ItemLabel = "First Film", MembershipScope = "MainSequence", RawOrdinal = "1", OrdinalScopeQid = "source-b", SeriesQid = "source-b", OrderSource = "SeriesOrdinal" },
            new() { ItemQid = "b2", ItemLabel = "Second Film", MembershipScope = "MainSequence", RawOrdinal = "2", OrdinalScopeQid = "source-b", SeriesQid = "source-b", OrderSource = "SeriesOrdinal" },
        };
        var differentManifest = new List<SeriesManifestItemRecord>
        {
            new() { ItemQid = "c1", ItemLabel = "First Film", MembershipScope = "MainSequence", ParsedOrdinal = 1, OrdinalScopeQid = "source-c", SeriesQid = "source-c", OrderSource = "SeriesOrdinal" },
            new() { ItemQid = "c2", ItemLabel = "Different Sequel", MembershipScope = "MainSequence", ParsedOrdinal = 2, OrdinalScopeQid = "source-c", SeriesQid = "source-c", OrderSource = "SeriesOrdinal" },
        };

        var first = InvokePrivate<string?>("BuildManifestMemberFingerprint", firstManifest);
        var equivalent = InvokePrivate<string?>("BuildManifestMemberFingerprint", equivalentManifest);
        var different = InvokePrivate<string?>("BuildManifestMemberFingerprint", differentManifest);

        Assert.NotNull(first);
        Assert.Equal(first, equivalent);
        Assert.NotEqual(first, different);
    }

    [Fact]
    public void DetailComposer_PrefersSemanticWikidataTitleForEquivalentProviderManifest()
    {
        var provider = new SequenceContainerOptionViewModel
        {
            ContainerId = "catalog:collection:42",
            SourceContainerId = "catalog:collection:42",
            ContainerTitle = "Example",
            MediaScope = "Watch",
            EquivalentContainerIds = ["catalog:collection:42", "sequence-fingerprint:shared"],
        };
        var wikidata = new SequenceContainerOptionViewModel
        {
            ContainerId = "Q12345",
            SourceContainerId = "Q12345",
            ContainerTitle = "Example Trilogy",
            MediaScope = "Watch",
            EquivalentContainerIds = ["Q12345", "sequence-fingerprint:shared"],
        };

        var distinct = InvokePrivate<List<SequenceContainerOptionViewModel>>(
            "DeduplicateSequenceContainerOptions",
            new List<SequenceContainerOptionViewModel> { provider, wikidata });

        var merged = Assert.Single(distinct);
        Assert.Equal("Example Trilogy", merged.ContainerTitle);
        Assert.Contains("catalog:collection:42", merged.EquivalentContainerIds);
        Assert.Contains("Q12345", merged.EquivalentContainerIds);
    }

    [Fact]
    public void DetailComposer_UsesProviderSeriesOnlyAsFallbackWhenWikidataManifestsExist()
    {
        var providerOnly = new SequenceContainerOptionViewModel
        {
            ContainerId = "catalog:collection:fallback",
            SourceContainerId = "catalog:collection:fallback",
            ContainerTitle = "Fallback Sequence",
            EquivalentContainerIds = ["catalog:collection:fallback"],
        };
        var wikidata = new SequenceContainerOptionViewModel
        {
            ContainerId = "Q12345",
            SourceContainerId = "Q12345",
            ContainerTitle = "Canonical Sequence",
            EquivalentContainerIds = ["Q12345", "sequence-fingerprint:shared"],
        };
        var equivalentProvider = new SequenceContainerOptionViewModel
        {
            ContainerId = "catalog:collection:equivalent",
            SourceContainerId = "catalog:collection:equivalent",
            ContainerTitle = "Provider Label",
            EquivalentContainerIds = ["catalog:collection:equivalent", "sequence-fingerprint:shared"],
        };

        var fallbackResult = InvokePrivate<List<SequenceContainerOptionViewModel>>(
            "PreferWikidataLinkedSequenceContainers",
            new List<SequenceContainerOptionViewModel> { providerOnly });
        var wikidataResult = InvokePrivate<List<SequenceContainerOptionViewModel>>(
            "PreferWikidataLinkedSequenceContainers",
            new List<SequenceContainerOptionViewModel> { providerOnly, wikidata, equivalentProvider });

        Assert.Single(fallbackResult);
        Assert.Contains(providerOnly, fallbackResult);
        Assert.Equal(2, wikidataResult.Count);
        Assert.Contains(wikidata, wikidataResult);
        Assert.Contains(equivalentProvider, wikidataResult);
        Assert.DoesNotContain(providerOnly, wikidataResult);
    }

    [Fact]
    public void DetailComposer_FormatsRatingsAndUsesCreditImageFallbacks()
    {
        var source = ReadDetailComposerSource();

        Assert.Contains("detail.Rating,\n            GetValue(canonicalValues, MetadataFieldConstants.Rating)", source);
        Assert.Contains("ToString(\"0.0\"", source);
        Assert.Contains("canonicalArrayKey + MetadataFieldConstants.CompanionQidSuffix", source);
        Assert.Contains("headshot_url", source);
        Assert.Contains("GetValue(canonicalValues, $\"{canonicalArrayKey}_profile_url\")", source);
        Assert.Contains("Guest Stars", source);
        Assert.Contains("MetadataFieldConstants.GuestStar", source);
    }

    [Fact]
    public void DetailComposer_ExposesStructuredFactsOnUnifiedDetails()
    {
        var source = ReadDetailComposerSource();
        var contracts = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Contracts/Details/DetailDtos.cs"));
        var client = ReadEngineApiClientSources();

        Assert.Contains("public DetailFactsViewModel? Facts { get; init; }", contracts);
        Assert.Contains("public sealed class DetailFactsViewModel", contracts);
        Assert.Contains("public IReadOnlyDictionary<string, string> Identifiers { get; init; }", contracts);
        Assert.Contains("Facts = BuildWorkFacts(factsDetail, entityType, factValues, contributorGroups)", source);
        Assert.Contains("Facts = BuildCollectionFacts(entityType, displayWorks, values, contributorGroups, row.WikidataQid)", source);
        Assert.Contains("Facts = BuildPersonFacts(person, displayRoles)", source);
        Assert.Contains("Actors = MergeNames(CreditNames(contributorGroups, CreditGroupType.Cast)", source);
        Assert.Contains("AlbumArtists = albumArtists", source);
        Assert.Contains("ShowName = StringHelpers.FirstNonBlankOr(string.Empty, detail.ShowName", source);
        Assert.Contains("TrackNumber = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, MetadataFieldConstants.TrackNumber)", source);
        Assert.Contains("Facts = detail.Facts", client);
    }

    [Fact]
    public void DetailComposer_UsesRootShowTitlesAndClaimFallbacksForPeople()
    {
        var source = ReadDetailComposerSource();
        var creditSource = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Api/Services/ReadServices/PersonCreditReadService.cs"));
        var scoringSource = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Providers/Services/ScoringHelper.cs"));
        var retailWorker = ReadProviderWorkerSource("RetailMatchWorker");
        var bridgeWorker = ReadProviderWorkerSource("WikidataBridgeWorker");
        var heroSource = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Details/DetailHero.razor"));

        Assert.Contains("ResolveCollectionTitle(entityType, row.DisplayName, rootValues, values)", source);
        Assert.Contains("GetValue(rootValues, MetadataFieldConstants.Title)", source);
        Assert.Contains("StripUniverseSuffix(displayName)", source);
        Assert.Contains("LoadContributorEntriesFromClaimsAsync", source);
        Assert.Contains("CreditGroupType.PrimaryArtists", source);
        Assert.Contains("CreditGroupType.MusicCredits", source);
        Assert.Contains("CreditGroupType.Illustrators", source);
        Assert.Contains("AddCreditLine(lines, \"Author\", CreditGroupType.Authors, CreditGroupType.Writers)", heroSource);
        Assert.Contains("AddCreditLine(lines, \"Illustrator\", CreditGroupType.Illustrators, CreditGroupType.CreativeTeam)", heroSource);
        Assert.Contains("BuildPersonCreditEntityId(credit.PersonId, credit.WikidataQid, credit.Name)", source);
        Assert.Contains(".GroupBy(CreditDisplayId, StringComparer.OrdinalIgnoreCase)", source);
        Assert.Contains("BuildPersonMediaItem(creditGroup.ToList(), context)", source);
        Assert.Contains("Roles = roles", source);
        Assert.Contains("Lane = DetailLane(entityType)", source);
        Assert.Contains("StringHelpers.FirstNonBlankOr(string.Empty, characterSummary, roleSummary)", source);
        Assert.Contains("ShouldShowContributorGroup(entityType, group)", source);
        Assert.Contains("return await BuildTvCreditsAsync(workId, ct);", source);
        Assert.Contains("textCredits.Concat(SplitCastGroups(credits))", source);
        Assert.Contains("Title = \"Actors\"", source);
        Assert.Contains("CreditGroupType.Directors", source);

        Assert.Contains("BuildFallbackCreditsFromMetadataClaimsAsync", creditSource);
        Assert.Contains("mc.claim_key IN ('cast_member', 'cast_member_qid')", creditSource);
        Assert.Contains(".Where(group => group.Count() == 1)", creditSource);
        Assert.Contains("ExtractQid(entry.ValueQid)", creditSource);

        Assert.Contains("BuildCanonicalArrayEntries(winningClaims, qidClaims)", scoringSource);
        Assert.Contains("ValueQid = parsed.Qid", scoringSource);
        Assert.Contains("arrayRepo: _arrayRepo", retailWorker);
        Assert.Contains("arrayRepo: _arrayRepo", bridgeWorker);
    }

    [Fact]
    public void PersonMediaSubtitle_UsesAValidMiddleDotSeparator()
    {
        var credits = new List<PersonLibraryCreditDto>
        {
            new()
            {
                WorkId = Guid.NewGuid(),
                MediaType = "Movies",
                Title = "Alien",
                Year = "1979",
                Role = "Director",
            },
        };

        var item = InvokePrivate<MediaGroupingItemViewModel>(
            "BuildPersonMediaItem",
            credits,
            DetailPresentationContext.Watch);

        Assert.Equal("Director · 1979", item.Subtitle);
    }

    [Fact]
    public void DetailComposer_RelationshipChipRoutesOnlyUseKnownDestinations()
    {
        Assert.Equal("/universe/Q12345/explore", InvokePrivateString("BuildUniverseExploreRoute", "Q12345"));
        Assert.Equal("/universe/Q12345/explore", InvokePrivateString("BuildUniverseExploreRoute", "https://www.wikidata.org/wiki/Q12345"));
        Assert.Null(InvokePrivateString("BuildUniverseExploreRoute", "Sony Pictures"));

        var containerId = Guid.NewGuid();
        var sequence = new SequencePlacementViewModel
        {
            ContainerId = containerId.ToString("D"),
            CurrentItem = new SequenceItemViewModel { EntityType = DetailEntityType.Movie },
        };

        Assert.Equal($"/details/movieseries/{containerId:D}?context=watch", InvokePrivateString("BuildSequenceContainerRoute", sequence));
        Assert.Null(InvokePrivateString("BuildSequenceContainerRoute", new SequencePlacementViewModel
        {
            ContainerId = "Q999",
            CurrentItem = new SequenceItemViewModel { EntityType = DetailEntityType.Movie },
        }));
    }

    [Fact]
    public void PersonEndpoints_DownloadsRemotePortraitsButDoesNotRedirectToRemoteImages()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Api/Endpoints/PersonEndpoints.cs"));

        Assert.Contains("IsLikelyImageFile", source);
        Assert.Contains("IsLikelyImageBytes", source);
        Assert.Contains("MediaMimeTypes.InferImageExtension(contentType)", source);
        Assert.Contains("return Results.File(bytes, contentType ?? GetImageMimeTypeOrJpeg(localPath), Path.GetFileName(localPath))", source);
        Assert.DoesNotContain("Results.Redirect(remoteUri.ToString())", source);
    }

    [Theory]
    [InlineData(DetailEntityType.Book)]
    [InlineData(DetailEntityType.Audiobook)]
    [InlineData(DetailEntityType.Movie)]
    [InlineData(DetailEntityType.ComicIssue)]
    public void BuildTabs_KeepsPrimarySeriesContentOutsideTheOverviewTabs(DetailEntityType entityType)
    {
        var tabs = InvokePrivate<List<DetailTab>>(
            "BuildTabs",
            entityType,
            DetailPresentationContext.Default,
            false,
            true,
            false,
            true);

        Assert.Equal("overview", tabs[0].Key);
        Assert.DoesNotContain(tabs, tab => tab.Key == "series");
        Assert.DoesNotContain(tabs, tab => tab.Key == "sequence");
    }

    [Fact]
    public void BuildTabs_MusicAlbumUsesOverviewForTracksAndCredits()
    {
        var tabs = InvokePrivate<List<DetailTab>>(
            "BuildTabs",
            DetailEntityType.MusicAlbum,
            DetailPresentationContext.Listen,
            false,
            false,
            false,
            true);

        Assert.Equal(["overview", "details"], tabs.Select(tab => tab.Key));
        Assert.DoesNotContain(tabs, tab => tab.Key == "tracks");
        Assert.DoesNotContain(tabs, tab => tab.Key == "credits");
    }

    [Fact]
    public void BuildTabs_AudiobookKeepsTracksOnTheirOwnTab()
    {
        var tabs = InvokePrivate<List<DetailTab>>(
            "BuildTabs",
            DetailEntityType.Audiobook,
            DetailPresentationContext.Listen,
            false,
            false,
            false,
            true);

        Assert.Equal(["overview", "tracks", "details"], tabs.Select(tab => tab.Key));
        Assert.DoesNotContain(tabs, tab => tab.Key == "chapters");
        Assert.DoesNotContain(tabs, tab => tab.Key == "credits");
    }

    [Fact]
    public void BuildTabs_DoesNotExposeTheDeprecatedOwnedFormatsSurface()
    {
        var tabs = InvokePrivate<List<DetailTab>>(
            "BuildTabs",
            DetailEntityType.Work,
            DetailPresentationContext.Default,
            false,
            false,
            false,
            true);

        Assert.DoesNotContain(tabs, tab => tab.Key == "formats");
    }

    [Fact]
    public void SequencePlacement_DropsAnUnpositionedContainerNamedAfterTheWork()
    {
        IReadOnlyList<SequenceItemViewModel> items =
        [
            new SequenceItemViewModel
            {
                Id = Guid.NewGuid().ToString("D"),
                Title = "Dungeon Crawler Carl: A LitRPG/Gamelit Adventure (Unabridged)",
                IsOwned = true,
                IsCurrent = true,
            },
        ];

        var isSelfNamed = InvokePrivate<bool>(
            "IsUnpositionedSelfNamedSequence",
            items,
            "Dungeon Crawler Carl: A Litrpg/Gamelit Adventure (Unabridged)",
            "Dungeon Crawler Carl: A LitRPG/Gamelit Adventure (Unabridged)",
            "Gamelit Adventure (Unabridged)",
            null);

        Assert.True(isSelfNamed);
    }

    [Theory]
    [InlineData(DetailEntityType.MusicAlbum, DetailPrimaryModuleKind.None)]
    [InlineData(DetailEntityType.Person, DetailPrimaryModuleKind.Works)]
    [InlineData(DetailEntityType.Collection, DetailPrimaryModuleKind.CollectionItems)]
    [InlineData(DetailEntityType.Universe, DetailPrimaryModuleKind.CollectionItems)]
    public void BuildPrimaryModule_UsesTheEntitySpecificOwnedContentPresentation(
        DetailEntityType entityType,
        DetailPrimaryModuleKind expectedKind)
    {
        IReadOnlyList<MediaGroupingViewModel> groups =
        [
            new MediaGroupingViewModel
            {
                Key = "owned",
                Title = "Owned",
                Items = [new MediaGroupingItemViewModel { Id = "owned-1", Title = "Owned item", IsOwned = true }],
            },
        ];

        var module = InvokePrivate<DetailPrimaryModuleViewModel>(
            "BuildPrimaryModule",
            entityType,
            null,
            groups);

        Assert.Equal(expectedKind, module.Kind);
        Assert.Equal(expectedKind == DetailPrimaryModuleKind.Works, module.SupportsRoleFilter);
    }

    [Fact]
    public void BuildPrimaryModule_DoesNotTreatTrackRecommendationsAsAlbumTracks()
    {
        IReadOnlyList<MediaGroupingViewModel> groups =
        [
            new MediaGroupingViewModel
            {
                Key = "more-like-this",
                Title = "More Like This",
                Items = [new MediaGroupingItemViewModel { Id = "recommended-1", Title = "Recommended track" }],
            },
        ];

        var module = InvokePrivate<DetailPrimaryModuleViewModel>(
            "BuildPrimaryModule",
            DetailEntityType.Work,
            null,
            groups);

        Assert.Equal(DetailPrimaryModuleKind.None, module.Kind);
    }

    [Fact]
    public void BuildPrimaryModule_AudiobookExcludesRelatedBooksFromItsTrackTable()
    {
        IReadOnlyList<MediaGroupingViewModel> groups =
        [
            new MediaGroupingViewModel
            {
                Key = "tracks",
                Title = "Tracks",
                Items = [new MediaGroupingItemViewModel { Id = "owned-track", Title = "001", IsOwned = true }],
            },
            new MediaGroupingViewModel
            {
                Key = "more-like-this",
                Title = "More Like This",
                Items = [new MediaGroupingItemViewModel { Id = "other-book", Title = "A related audiobook" }],
            },
        ];

        var module = InvokePrivate<DetailPrimaryModuleViewModel>(
            "BuildPrimaryModule",
            DetailEntityType.Audiobook,
            null,
            groups);

        Assert.Equal(DetailPrimaryModuleKind.Tracks, module.Kind);
        Assert.Equal(["tracks"], module.GroupKeys);
    }

    [Fact]
    public void BuildPrimaryModule_AudiobookPresentsItsSeriesBeforeItsTracks()
    {
        IReadOnlyList<MediaGroupingViewModel> groups =
        [
            new MediaGroupingViewModel
            {
                Key = "tracks",
                Title = "Tracks",
                Items = [new MediaGroupingItemViewModel { Id = "owned-track", Title = "001", IsOwned = true }],
            },
        ];
        var placement = new SequencePlacementViewModel
        {
            ContainerId = "dungeon-crawler-carl",
            ContainerTitle = "Dungeon Crawler Carl",
            ItemPluralLabel = "Books",
        };

        var module = InvokePrivate<DetailPrimaryModuleViewModel>(
            "BuildPrimaryModule",
            DetailEntityType.Audiobook,
            placement,
            groups);

        Assert.Equal(DetailPrimaryModuleKind.Sequence, module.Kind);
        Assert.Equal("Books", module.Title);
    }

    [Fact]
    public void ResolveWorkDisplayTitle_UsesTheConfirmedCanonicalTitleBeforeRetailFallback()
    {
        var detail = new LibraryItemDetail
        {
            Title = "Dungeon Crawler Carl: A LitRPG/Gamelit Adventure (Unabridged)",
            FileName = "Dungeon Crawler Carl.m4b",
        };
        IReadOnlyDictionary<string, string> canonicalValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.Title] = "Dungeon Crawler Carl",
        };

        var title = InvokePrivateString(
            "ResolveWorkDisplayTitle",
            null,
            detail,
            canonicalValues,
            DetailEntityType.Audiobook);

        Assert.Equal("Dungeon Crawler Carl", title);
    }

    [Theory]
    [InlineData("media", DetailEntityType.MovieSeries, "Films")]
    [InlineData("works", DetailEntityType.BookSeries, "Books")]
    public void BuildTabs_UsesContainerSpecificMediaLabels(string key, DetailEntityType entityType, string expected)
    {
        Assert.Equal(expected, InvokePrivateString("ToTabLabel", key, entityType));
    }

    [Fact]
    public void DetailComposer_StaysAThinFacadeWithBoundedInternalComponents()
    {
        var root = FindRepoRoot();
        var detailsRoot = Path.Combine(root, "src", "MediaEngine.Api", "Services", "Details");
        var facade = Path.Combine(detailsRoot, "DetailComposerService.cs");
        var implementationFiles = Directory
            .EnumerateFiles(Path.Combine(detailsRoot, "Internals"), "*.cs", SearchOption.TopDirectoryOnly)
            .ToList();

        Assert.True(File.ReadLines(facade).Count() < 500, "DetailComposerService must remain a thin public facade.");
        Assert.All(
            implementationFiles,
            path => Assert.True(
                File.ReadLines(path).Count() <= 1500,
                $"{Path.GetFileName(path)} exceeded the 1,500-line component limit."));
        Assert.Contains(
            implementationFiles,
            path => Path.GetFileName(path) == "DetailCompositionOrchestrator.Readers.cs");
        Assert.Contains(
            implementationFiles,
            path => Path.GetFileName(path) == "DetailProjectionReader.cs");
        Assert.Contains(
            implementationFiles,
            path => Path.GetFileName(path) == "DetailViewModelBuilder.cs");
        Assert.Contains(
            implementationFiles,
            path => Path.GetFileName(path) == "DetailPresentationPolicy.cs");
        var facadeSource = File.ReadAllText(facade);
        Assert.DoesNotContain("QueryAsync", facadeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateConnection", facadeSource, StringComparison.Ordinal);
    }

    private static string? InvokePrivateString(string methodName, params object?[] args)
    {
        var method = DetailComposerImplementationType.GetMethod(
            methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        return (string?)method!.Invoke(null, args);
    }

    private static T InvokePrivate<T>(string methodName, params object?[] args)
    {
        var method = DetailComposerImplementationType.GetMethod(
            methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        return Assert.IsType<T>(method!.Invoke(null, args));
    }

    private static Type DetailComposerImplementationType =>
        typeof(DetailComposerService).Assembly.GetType(
            "MediaEngine.Api.Services.Details.Internals.DetailCompositionOrchestrator",
            throwOnError: true)!;

    private static string ReadDetailComposerSource()
    {
        var detailsRoot = Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Services",
            "Details");
        var paths = new[]
            {
                Path.Combine(detailsRoot, "DetailComposerService.cs"),
            }
            .Concat(Directory.EnumerateFiles(
                Path.Combine(detailsRoot, "Internals"),
                "*.cs",
                SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        return string.Join(
            "\n",
            paths.Select(path => File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal)));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private static string ReadEngineApiClientSources()
    {
        var directory = Path.Combine(
            FindRepoRoot(),
            "src", "MediaEngine.Web", "Services", "Integration");

        return string.Join(
            "\n",
            Directory.EnumerateFiles(directory, "EngineApiClient*.cs")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllText));
    }

    private static string ReadProviderWorkerSource(string workerName)
    {
        var workersRoot = Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Providers",
            "Workers");
        var paths = new[] { Path.Combine(workersRoot, $"{workerName}.cs") }
            .Concat(Directory.EnumerateFiles(
                Path.Combine(workersRoot, "Internals"),
                $"{workerName}.*.cs",
                SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        return string.Join("\n", paths.Select(File.ReadAllText));
    }
}
