using System.Text.Json;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Contracts.Tests;

public sealed class ContractJsonRoundTripTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ViewGalleryShareTarget_RoundTripsOnlyCountFreeProfileIdentity()
    {
        var profileId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var dto = new ViewGalleryShareTargetDto(profileId, "Sarah", "#7457D9", "/avatars/sarah.jpg");

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ViewGalleryShareTargetDto>(json, JsonOptions);

        Assert.Equal(dto, roundTrip);
        Assert.Equal(
            "{\"profile_id\":\"11111111-2222-3333-4444-555555555555\",\"display_name\":\"Sarah\",\"avatar_color\":\"#7457D9\",\"avatar_url\":\"/avatars/sarah.jpg\"}",
            json);
        Assert.DoesNotContain("count", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("library", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("asset", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DetailsDto_RoundTripsRepresentativeShape()
    {
        var dto = new DetailPageViewModel
        {
            Id = "work-1",
            EntityType = DetailEntityType.Book,
            PresentationContext = DetailPresentationContext.Read,
            Title = "The Left Hand of Darkness",
            Subtitle = "Hainish Cycle",
            Artwork = new ArtworkSet
            {
                CoverUrl = "/images/cover.jpg",
                PrimaryColor = "#101820",
                DominantColors = ["#101820", "#f2aa4c"],
            },
            Progress = new ProgressViewModel
            {
                Percent = 42.5,
                Label = "Continue listening",
                ContextLabel = "Chapter 8 of 50",
                PercentLabel = "43%",
                RemainingLabel = "8h 15m left",
                SecondaryLabel = "42 chapters remaining",
            },
            Metadata =
            [
                new MetadataPill
                {
                    Label = "1969",
                    Kind = "year",
                    Tooltip = "Published in 1969",
                },
            ],
            Facts = new DetailFactsViewModel
            {
                MediaKind = "Book",
                Year = "1969",
                Rating = "4.8",
                Genres = ["Science Fiction"],
                Authors = ["Ursula K. Le Guin"],
                Series = "Hainish Cycle",
                PageCount = "304",
                Identifiers = new Dictionary<string, string>
                {
                    ["wikidata_qid"] = "Q754868",
                },
            },
            PrimaryActions =
            [
                new DetailAction
                {
                    Key = "read",
                    Label = "Read",
                    Route = "/read/work-1",
                    IsPrimary = true,
                },
            ],
            OwnedFormats =
            [
                new OwnedFormatViewModel
                {
                    Id = "edition-1",
                    FormatType = MediaFormatType.Ebook,
                    DisplayName = "EPUB",
                    PageCount = 304,
                },
            ],
            PrimaryModule = new DetailPrimaryModuleViewModel
            {
                Kind = DetailPrimaryModuleKind.Chapters,
                Title = "Chapters",
                GroupKeys = ["chapters"],
            },
            MediaGroups =
            [
                new MediaGroupingViewModel
                {
                    Key = "chapters",
                    Title = "Chapters",
                    Items =
                    [
                        new MediaGroupingItemViewModel
                        {
                            Id = "work-1",
                            EntityType = DetailEntityType.Audiobook,
                            Title = "Chapter 1",
                            AssetId = "asset-1",
                            ChapterIndex = 0,
                            StartSeconds = 12.5,
                            EndSeconds = 1800,
                            ResumePositionSeconds = 420.25,
                            Lane = "listen",
                            Roles = ["Narrator"],
                            IsFavorite = true,
                        },
                    ],
                },
            ],
            LibraryStatus = LibraryStatus.Owned,
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<DetailPageViewModel>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(dto.Id, roundTrip.Id);
        Assert.Equal(dto.EntityType, roundTrip.EntityType);
        Assert.Equal(dto.Artwork.CoverUrl, roundTrip.Artwork.CoverUrl);
        Assert.Equal("Chapter 8 of 50", roundTrip.Progress?.ContextLabel);
        Assert.Equal("8h 15m left", roundTrip.Progress?.RemainingLabel);
        Assert.Equal(dto.Metadata[0].Label, roundTrip.Metadata[0].Label);
        Assert.Equal("Book", roundTrip.Facts?.MediaKind);
        Assert.Equal("Ursula K. Le Guin", Assert.Single(roundTrip.Facts!.Authors));
        Assert.Equal("Q754868", roundTrip.Facts.Identifiers["wikidata_qid"]);
        Assert.Equal(dto.OwnedFormats[0].FormatType, roundTrip.OwnedFormats[0].FormatType);
        Assert.Equal("asset-1", roundTrip.MediaGroups[0].Items[0].AssetId);
        Assert.Equal(DetailPrimaryModuleKind.Chapters, roundTrip.PrimaryModule.Kind);
        Assert.Equal("listen", roundTrip.MediaGroups[0].Items[0].Lane);
        Assert.Equal("Narrator", Assert.Single(roundTrip.MediaGroups[0].Items[0].Roles));
        Assert.Equal(12.5, roundTrip.MediaGroups[0].Items[0].StartSeconds);
        Assert.Equal(420.25, roundTrip.MediaGroups[0].Items[0].ResumePositionSeconds);
        Assert.True(roundTrip.MediaGroups[0].Items[0].IsFavorite);
        Assert.Contains("\"entityType\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"EntityType\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayDto_RoundTripsRepresentativeShape()
    {
        var workId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var assetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var action = new DisplayActionDto("playAsset", "Play", WorkId: workId, AssetId: assetId);
        var card = new DisplayCardDto(
            Id: Guid.Parse("33333333-3333-3333-3333-333333333333"),
            WorkId: workId,
            AssetId: assetId,
            CollectionId: null,
            MediaType: "Movie",
            GroupingType: "work",
            Title: "Arrival",
            Subtitle: "2016",
            Facts: ["PG-13", "116 min"],
            Artwork: new DisplayArtworkDto(
                CoverUrl: "/cover.jpg",
                CoverSmallUrl: "/cover-s.jpg",
                CoverMediumUrl: "/cover-m.jpg",
                CoverLargeUrl: "/cover-l.jpg",
                SquareUrl: null,
                SquareSmallUrl: null,
                SquareMediumUrl: null,
                SquareLargeUrl: null,
                BannerUrl: null,
                BannerSmallUrl: null,
                BannerMediumUrl: null,
                BannerLargeUrl: null,
                BackgroundUrl: "/bg.jpg",
                BackgroundSmallUrl: "/bg-s.jpg",
                BackgroundMediumUrl: "/bg-m.jpg",
                BackgroundLargeUrl: "/bg-l.jpg",
                LogoUrl: null,
                CoverWidthPx: 1000,
                CoverHeightPx: 1500,
                SquareWidthPx: null,
                SquareHeightPx: null,
                BannerWidthPx: null,
                BannerHeightPx: null,
                BackgroundWidthPx: 1920,
                BackgroundHeightPx: 1080,
                AccentColor: "#112233"),
            PreferredShape: "portrait",
            Presentation: "default",
            TileTextMode: "coverOnly",
            PreviewPlacement: "bottom",
            Progress: new DisplayProgressDto(42, "42%", DateTimeOffset.Parse("2026-04-24T12:00:00Z"), action),
            Actions: [action],
            Flags: new DisplayCardFlagsDto(true, false, false, true),
            SortTimestamp: DateTimeOffset.Parse("2026-04-24T12:00:00Z"))
        {
            Description = "2016 science fiction film",
            Badges = [new DisplayCardBadgeDto("quality", "4K"), new DisplayCardBadgeDto("source", "Max")],
            PreviewItems =
            [
                new DisplayCardPreviewItemDto(
                    WorkId: workId,
                    AssetId: assetId,
                    Title: "Arrival",
                    ImageUrl: "/cover-s.jpg",
                    Shape: "portrait",
                    Position: "1",
                    MediaType: "Movie",
                    WebUrl: $"/watch/movie/{workId:D}",
                    Description: "A linguist meets visitors from another world.",
                    Facts: ["PG-13", "2016", "1h 56m", "★ 7.9"]),
            ],
            PreviewTotalCount = 3,
        };
        var hero = new DisplayHeroDto("Arrival", "2016", "Featured", card.Artwork, card.Progress, card.Actions)
        {
            Facts = card.Facts,
            Tagline = "Why are they here?",
        };
        var dto = new DisplayPageDto("home", "Home", null, hero, [new DisplayShelfDto("continue", "Continue", null, [card], "/display/continue")], [card]);

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<DisplayPageDto>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(dto.Key, roundTrip.Key);
        Assert.Equal(["PG-13", "116 min"], roundTrip.Hero?.Facts);
        Assert.Equal("Why are they here?", roundTrip.Hero?.Tagline);
        Assert.Equal(card.WorkId, roundTrip.Catalog[0].WorkId);
        Assert.Equal("2016 science fiction film", roundTrip.Catalog[0].Description);
        Assert.Equal(card.Actions[0].Type, roundTrip.Catalog[0].Actions[0].Type);
        Assert.Equal("4K", roundTrip.Catalog[0].Badges.Single(badge => badge.Kind == "quality").Label);
        Assert.Equal("Arrival", roundTrip.Catalog[0].PreviewItems.Single().Title);
        Assert.Equal("1", roundTrip.Catalog[0].PreviewItems.Single().Position);
        Assert.Equal("Movie", roundTrip.Catalog[0].PreviewItems.Single().MediaType);
        Assert.Equal($"/watch/movie/{workId:D}", roundTrip.Catalog[0].PreviewItems.Single().WebUrl);
        Assert.Equal("A linguist meets visitors from another world.", roundTrip.Catalog[0].PreviewItems.Single().Description);
        Assert.Equal(["PG-13", "2016", "1h 56m", "★ 7.9"], roundTrip.Catalog[0].PreviewItems.Single().Facts);
        Assert.Equal(3, roundTrip.Catalog[0].PreviewTotalCount);
        Assert.Contains("\"workId\":\"11111111-1111-1111-1111-111111111111\"", json, StringComparison.Ordinal);
        Assert.Contains("\"badges\":[{\"kind\":\"quality\",\"label\":\"4K\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"previewItems\":[{\"workId\":\"11111111-1111-1111-1111-111111111111\"", json, StringComparison.Ordinal);
        Assert.Contains("\"previewTotalCount\":3", json, StringComparison.Ordinal);
        Assert.Contains("\"description\":\"2016 science fiction film\"", json, StringComparison.Ordinal);
        Assert.Contains("\"tagline\":\"Why are they here?\"", json, StringComparison.Ordinal);
        Assert.Contains("\"facts\":[\"PG-13\",\"116 min\"]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PagingDto_RoundTripsRepresentativeShape()
    {
        var dto = new PagedResponse<string>(["one", "two"], Offset: 10, Limit: 2, HasMore: true, TotalCount: 42, NextCursor: "12");

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<PagedResponse<string>>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(dto.Items, roundTrip.Items);
        Assert.Equal(dto.NextCursor, roundTrip.NextCursor);
        Assert.Contains("\"has_more\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"total_count\":42", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackDto_RoundTripsRepresentativeShape()
    {
        var dto = new PlaybackManifestDto
        {
            AssetId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Client = "web",
            MediaType = "Movies",
            SourceExtension = ".mkv",
            RecommendedDelivery = PlaybackDeliveryModes.Hls,
            HlsUrl = "/playback/hls/manifest.m3u8",
            DirectPlaySupported = false,
            AudioTracks =
            [
                new PlaybackTrackDto
                {
                    Index = 1,
                    Language = "eng",
                    Codec = "aac",
                    IsDefault = true,
                    Channels = 2,
                },
            ],
            Segments =
            [
                new PlaybackSegmentDto
                {
                    Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    AssetId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    Kind = "intro",
                    StartSeconds = 12.5,
                    EndSeconds = 58.0,
                    Confidence = 0.91,
                    Source = "plugin",
                    IsSkippable = true,
                },
            ],
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<PlaybackManifestDto>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(dto.AssetId, roundTrip.AssetId);
        Assert.Equal(dto.RecommendedDelivery, roundTrip.RecommendedDelivery);
        Assert.Equal(dto.AudioTracks[0].Language, roundTrip.AudioTracks[0].Language);
        Assert.Equal(dto.Segments[0].Kind, roundTrip.Segments[0].Kind);
        Assert.Contains("\"assetId\":\"44444444-4444-4444-4444-444444444444\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerDto_RoundTripsQueueStateAndOpdsReadyMetadata()
    {
        var profileId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var queueItemId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var workId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var assetId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var state = new PlayerStateDto
        {
            ProfileId = profileId,
            SessionId = sessionId,
            DeviceId = "desktop-study",
            Client = "web",
            PlaybackState = PlayerPlaybackStates.Playing,
            Experience = PlayerExperienceModes.Audiobook,
            StateVersion = 42,
            CurrentQueueItemId = queueItemId,
            PositionSeconds = 1842.25,
            DurationSeconds = 7200,
            ProgressPct = 25.59,
            Volume = 0.72,
            PlaybackRate = 1.25,
            SourceLabel = "Audiobooks",
            Queue =
            [
                new PlayerQueueItemDto
                {
                    QueueItemId = queueItemId,
                    WorkId = workId,
                    AssetId = assetId,
                    MediaType = "Audiobooks",
                    Title = "Leviathan Wakes",
                    Author = "James S. A. Corey",
                    Narrator = "Jefferson Mays",
                    Series = "The Expanse",
                    CoverUrl = "/images/covers/leviathan.jpg",
                    DurationSeconds = 7200,
                    PositionSeconds = 1842.25,
                    ProgressPct = 25.59,
                    StreamUrl = "/playback/stream/44444444-4444-4444-4444-444444444444",
                    DownloadUrl = "/playback/download/44444444-4444-4444-4444-444444444444",
                    Chapters =
                    [
                        new PlaybackChapterDto
                        {
                            Index = 1,
                            Title = "Chapter 1",
                            OriginalTitle = "001",
                            Kind = PlaybackChapterKinds.Chapter,
                            TitleSource = PlaybackChapterTitleSources.Generated,
                            StartSeconds = 0,
                            EndSeconds = 1800,
                        },
                    ],
                },
            ],
            AudiobookHistory =
            [
                new AudiobookListenHistoryItemDto
                {
                    Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    ProfileId = profileId,
                    WorkId = workId,
                    AssetId = assetId,
                    Title = "Leviathan Wakes",
                    ChapterTitle = "Chapter 4",
                    ChapterIndex = 4,
                    PositionSeconds = 1842.25,
                    DurationSeconds = 7200,
                    ProgressPct = 25.59,
                    StartedAt = DateTimeOffset.Parse("2026-04-24T12:00:00Z"),
                    EndedAt = DateTimeOffset.Parse("2026-04-24T12:01:00Z"),
                },
            ],
        };

        var json = JsonSerializer.Serialize(state, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<PlayerStateDto>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(profileId, roundTrip.ProfileId);
        Assert.Equal(sessionId, roundTrip.SessionId);
        Assert.Equal(PlayerExperienceModes.Audiobook, roundTrip.Experience);
        Assert.Equal(queueItemId, roundTrip.CurrentQueueItemId);
        Assert.Equal(1842.25, roundTrip.PositionSeconds);
        Assert.Equal(25.59, roundTrip.ProgressPct);
        Assert.Equal("Audiobooks", roundTrip.Queue[0].MediaType);
        Assert.Equal(workId, roundTrip.Queue[0].WorkId);
        Assert.Equal(assetId, roundTrip.Queue[0].AssetId);
        Assert.Equal("James S. A. Corey", roundTrip.Queue[0].Author);
        Assert.Equal("Jefferson Mays", roundTrip.Queue[0].Narrator);
        Assert.Equal("The Expanse", roundTrip.Queue[0].Series);
        Assert.Equal("/playback/stream/44444444-4444-4444-4444-444444444444", roundTrip.Queue[0].StreamUrl);
        Assert.Equal("Chapter 1", roundTrip.Queue[0].Chapters[0].Title);
        Assert.Equal("001", roundTrip.Queue[0].Chapters[0].OriginalTitle);
        Assert.Equal(PlaybackChapterKinds.Chapter, roundTrip.Queue[0].Chapters[0].Kind);
        Assert.Equal(PlaybackChapterTitleSources.Generated, roundTrip.Queue[0].Chapters[0].TitleSource);
        Assert.Equal("Chapter 4", roundTrip.AudiobookHistory[0].ChapterTitle);
        Assert.Contains("\"positionSeconds\":1842.25", json, StringComparison.Ordinal);
        Assert.Contains("\"progressPct\":25.59", json, StringComparison.Ordinal);
        Assert.Contains("\"experience\":\"audiobook\"", json, StringComparison.Ordinal);
        Assert.Contains("\"streamUrl\":\"/playback/stream/44444444-4444-4444-4444-444444444444\"", json, StringComparison.Ordinal);
        Assert.Contains("\"originalTitle\":\"001\"", json, StringComparison.Ordinal);

        var mutation = new PlayerQueueMutationDto
        {
            StartIndex = 0,
            Items =
            [
                new PlayerQueueMutationItemDto
                {
                    WorkId = workId,
                    AssetId = assetId,
                    Title = "Chapter 4",
                    MediaType = "Audiobooks",
                    PositionSeconds = 1842.25,
                },
            ],
        };
        var mutationJson = JsonSerializer.Serialize(mutation, JsonOptions);
        var mutationRoundTrip = JsonSerializer.Deserialize<PlayerQueueMutationDto>(mutationJson, JsonOptions);

        Assert.NotNull(mutationRoundTrip);
        Assert.Equal(0, mutationRoundTrip.StartIndex);
        Assert.Equal(1842.25, mutationRoundTrip.Items[0].PositionSeconds);
        Assert.Contains("\"items\":[", mutationJson, StringComparison.Ordinal);
        Assert.Contains("\"startIndex\":0", mutationJson, StringComparison.Ordinal);
    }

    [Fact]
    public void AudiobookTrackTitleOverrideDto_RoundTripsRepresentativeShape()
    {
        var assetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var overrideRequest = new UpsertAudiobookChapterTitleOverrideRequestDto
        {
            AssetId = assetId,
            ChapterIndex = 1,
            Title = "Track Two",
            TitleSource = PlaybackChapterTitleSources.Override,
        };

        var overrideJson = JsonSerializer.Serialize(overrideRequest, JsonOptions);

        var overrideRoundTrip = JsonSerializer.Deserialize<UpsertAudiobookChapterTitleOverrideRequestDto>(overrideJson, JsonOptions);

        Assert.NotNull(overrideRoundTrip);
        Assert.Equal(assetId, overrideRoundTrip.AssetId);
        Assert.Equal("Track Two", overrideRoundTrip.Title);
        Assert.Equal(PlaybackChapterTitleSources.Override, overrideRoundTrip.TitleSource);
        Assert.Contains("\"titleSource\":\"Override\"", overrideJson, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsDto_RoundTripsRepresentativeShape()
    {
        var dto = new PipelineConfiguration
        {
            Pipelines =
            {
                ["Books"] = new MediaTypePipeline
                {
                    Strategy = ProviderStrategy.Waterfall,
                    Providers =
                    [
                        new PipelineProviderEntry
                        {
                            Rank = 1,
                            Name = "openlibrary",
                            Purpose = "identity",
                        },
                    ],
                    FieldPriorities =
                    {
                        ["title"] = ["openlibrary", "embedded"],
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<PipelineConfiguration>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.True(roundTrip.Pipelines.ContainsKey("Books"));
        Assert.Equal(dto.Pipelines["Books"].Strategy, roundTrip.Pipelines["Books"].Strategy);
        Assert.Equal("openlibrary", roundTrip.Pipelines["Books"].Providers[0].Name);
        Assert.Equal("identity", roundTrip.Pipelines["Books"].Providers[0].Purpose);
        Assert.Contains("\"field_priorities\":", json, StringComparison.Ordinal);
        Assert.Contains("\"purpose\":\"identity\"", json, StringComparison.Ordinal);
    }
}
