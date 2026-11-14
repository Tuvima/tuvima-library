using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The "Your copy" row's facts (details-page-bottom spec D4, D5, section 4, section 5): the chips and File details groups for a movie, a show's,
/// an album's and a book's files, the show-level summary (the common quality, the exceptions and the totals), what a Kids profile and the Vault never
/// see, and that the file's place on disk goes only to someone who may edit.
/// </summary>
public sealed class YourCopyTests : IDisposable
{
    private static readonly DateTimeOffset Added = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private readonly SupportingFileTestWorld _world = new();

    public void Dispose() => _world.Dispose();

    // ---- The builder ------------------------------------------------------------------------------------------------------------

    private static MediaProbeResult Film(int width = 1920, int height = 1080, string codec = "h264", string transfer = "bt709", string audio = "aac", int channels = 6) => new()
    {
        Duration = TimeSpan.FromMinutes(109),
        VideoCodec = codec,
        Width = width,
        Height = height,
        FrameRate = 23.976,
        ColorTransfer = transfer,
        AudioCodec = audio,
        Channels = channels,
        SampleRate = 48000,
        AudioBitrate = 384,
        AudioLanguage = "eng",
        AudioStreams = [new MediaProbeAudioStream(0, audio, "eng", true)],
        SubtitleLanguages = ["eng", "spa"],
    };

    private static YourCopyAsset File(string path, MediaProbeResult? probe, Guid? edition = null, string? editionLabel = null, long size = 7_600_000_000, DateTimeOffset? added = null) =>
        new(Guid.NewGuid(), edition ?? Guid.NewGuid(), editionLabel, path, size, added ?? Added, null, probe?.Duration.TotalSeconds, null, probe);

    private static DetailPageViewModel Page(DetailEntityType type, DetailFactsViewModel? facts = null, ProgressViewModel? progress = null) =>
        new() { EntityType = type, Facts = facts, Progress = progress };

    private static DetailYourCopyBlockViewModel OnlyBlock(DetailYourCopyViewModel? copy) => Assert.Single(Assert.IsType<DetailYourCopyViewModel>(copy).Blocks);

    private static IReadOnlyList<string> Lines(DetailYourCopyBlockViewModel block, string group) =>
        Assert.Single(block.Groups, candidate => candidate.Key == group).Lines;

    [Fact]
    public void AMovie_GetsChipsForResolutionCodecAudioAndContainerAndThreeGroups()
    {
        var copy = YourCopyBuilder.Build(Page(DetailEntityType.Movie), [File("/m/Test (2011).mkv", Film())], includeLocation: false);

        var block = OnlyBlock(copy);
        Assert.Equal(["1080p", "H.264", "AAC 5.1", "MKV"], block.Chips);
        Assert.Equal([DetailYourCopyGroupKeys.Video, DetailYourCopyGroupKeys.AudioSubtitles, DetailYourCopyGroupKeys.File], block.Groups.Select(group => group.Key));
        Assert.Equal(["1920 × 1080 · 23.976 fps", "H.264 · 9.3 Mb/s", "SDR"], Lines(block, DetailYourCopyGroupKeys.Video));
        Assert.Equal(["English · AAC 5.1", "48 kHz · 384 kb/s", "Subtitles · English, Spanish"], Lines(block, DetailYourCopyGroupKeys.AudioSubtitles));
        Assert.Equal(["MKV · 7.1 GB", "Duration · 1h 49m", "Added · Nov 2, 2026"], Lines(block, DetailYourCopyGroupKeys.File));
        Assert.Equal("Test (2011).mkv", block.FileName);
        Assert.False(block.IsReadingFacts);
    }

    [Fact]
    public void AnHdrFile_GetsAnHdrChip_AndDolbyVisionOrAtmosNeverAppearBecauseNothingStoresThem()
    {
        var hdr = Film(3840, 2160, "hevc", "smpte2084", "eac3", 8);

        var block = OnlyBlock(YourCopyBuilder.Build(Page(DetailEntityType.Movie), [File("/m/A.mkv", hdr)], includeLocation: false));

        Assert.Equal(["4K", "HDR10", "HEVC", "Dolby Digital Plus 7.1", "MKV"], block.Chips);
        var everything = string.Join('|', block.Chips.Concat(block.Groups.SelectMany(group => group.Lines)));
        Assert.DoesNotContain("Dolby Vision", everything);
        Assert.DoesNotContain("Atmos", everything);
    }

    [Fact]
    public void AFactTheFileDoesNotSay_IsLeftOutInsteadOfGuessed()
    {
        var sparse = Film() with { FrameRate = null, ColorTransfer = null, SubtitleLanguages = [], SampleRate = null, AudioBitrate = null };

        var block = OnlyBlock(YourCopyBuilder.Build(Page(DetailEntityType.Movie), [File("/m/A.mkv", sparse)], includeLocation: false));

        Assert.Equal(["1920 × 1080", "H.264 · 9.3 Mb/s"], Lines(block, DetailYourCopyGroupKeys.Video));
        Assert.Equal(["English · AAC 5.1"], Lines(block, DetailYourCopyGroupKeys.AudioSubtitles));
    }

    [Fact]
    public void AFileNotScannedYet_ReadsAsReadingFileFactsWithNoChipsAndNoGroups()
    {
        var block = OnlyBlock(YourCopyBuilder.Build(Page(DetailEntityType.Movie), [File("/m/A.mkv", null)], includeLocation: true));

        Assert.True(block.IsReadingFacts);
        Assert.Empty(block.Chips);
        Assert.Empty(block.Groups);
        Assert.Null(block.FileLocation);
    }

    [Fact]
    public void TheFilesPlace_IsOnlyWrittenWhenTheViewerMayEdit()
    {
        var asset = File("/media/Movies/Test/Test.mkv", Film());

        Assert.Null(OnlyBlock(YourCopyBuilder.Build(Page(DetailEntityType.Movie), [asset], includeLocation: false)).FileLocation);
        Assert.Equal("/media/Movies/Test/Test.mkv", OnlyBlock(YourCopyBuilder.Build(Page(DetailEntityType.Movie), [asset], includeLocation: true)).FileLocation);
    }

    [Fact]
    public void TwoEditions_GiveOneBlockEachLabelledByEdition_AndOneEditionGivesNoLabel()
    {
        var theatrical = File("/m/Theatrical.mkv", Film(), editionLabel: "Theatrical cut");
        var directors = File("/m/Directors.mkv", Film(3840, 2160, "hevc"), editionLabel: "Director's cut");

        var two = YourCopyBuilder.Build(Page(DetailEntityType.Movie), [theatrical, directors], includeLocation: false)!;
        var one = YourCopyBuilder.Build(Page(DetailEntityType.Movie), [theatrical], includeLocation: false)!;

        Assert.Equal(["Theatrical cut", "Director's cut"], two.Blocks.Select(block => block.Label));
        Assert.Contains("4K", two.Blocks[1].Chips);
        Assert.Null(Assert.Single(one.Blocks).Label);
    }

    [Fact]
    public void AShow_ShowsTheCommonQualityInChipsAndTheExceptionsAndTotalsInThePanel()
    {
        var files = Enumerable.Range(1, 8).Select(number => File($"/tv/Show/S01E{number:00}.mkv", Film(3840, 2160, "hevc", "smpte2084", "eac3", 8)))
            .Concat(Enumerable.Range(9, 2).Select(number => File($"/tv/Show/S01E{number:00}.mkv", Film())))
            .ToList();

        var block = OnlyBlock(YourCopyBuilder.Build(Page(DetailEntityType.TvShow), files, includeLocation: true));

        Assert.Equal(["10 episodes", "4K", "HDR10", "HEVC", "Dolby Digital Plus 7.1", "MKV"], block.Chips);
        var video = Lines(block, DetailYourCopyGroupKeys.Video);
        Assert.Equal("Mostly 3840 × 2160 · HEVC", video[0]);
        Assert.Contains("2 episodes in 1080p", video);
        Assert.Contains("2 episodes in H.264", video);
        Assert.Contains("2 episodes in SDR", video);
        var sound = Lines(block, DetailYourCopyGroupKeys.AudioSubtitles);
        Assert.Contains("Most episodes", sound);
        Assert.Contains("2 episodes with AAC 5.1", sound);
        var totals = Lines(block, DetailYourCopyGroupKeys.Files);
        Assert.StartsWith("10 files · MKV · ", totals[0]);
        Assert.Contains("Total · 18h 10m", totals);
        // A show has no single file: no name, and its place is never written even for an editor.
        Assert.Null(block.FileName);
        Assert.Null(block.FileLocation);
    }

    [Fact]
    public void AShowWhereEveryEpisodeMatches_ListsNoExceptionsAndDropsMostly()
    {
        var files = Enumerable.Range(1, 3).Select(number => File($"/tv/Show/S01E{number:00}.mkv", Film())).ToList();

        var block = OnlyBlock(YourCopyBuilder.Build(Page(DetailEntityType.TvShow), files, includeLocation: false));

        var video = Lines(block, DetailYourCopyGroupKeys.Video);
        Assert.Equal("1920 × 1080 · H.264", video[0]);
        Assert.DoesNotContain(video, line => line.Contains(" in ", StringComparison.Ordinal));
        Assert.DoesNotContain("Most episodes", Lines(block, DetailYourCopyGroupKeys.AudioSubtitles));
    }

    [Fact]
    public void AOneEpisodeException_ReadsInTheSingular_AndEpisodesStillBeingReadAreSaidSo()
    {
        var files = new List<YourCopyAsset>
        {
            File("/tv/S01E01.mkv", Film()),
            File("/tv/S01E02.mkv", Film()),
            File("/tv/S01E03.mkv", Film(1280, 720)),
            File("/tv/S01E04.mkv", null),
        };

        var block = OnlyBlock(YourCopyBuilder.Build(Page(DetailEntityType.TvShow), files, includeLocation: false));

        Assert.Contains("1 episode in 720p", Lines(block, DetailYourCopyGroupKeys.Video));
        Assert.Contains("1 episode still being read", Lines(block, DetailYourCopyGroupKeys.Files));
        Assert.Equal("4 episodes", block.Chips[0]);
    }

    [Fact]
    public void AnAlbum_GetsFormatDepthAndRateTrackCountAndChannelsWithAudioTracksAndFilesGroups()
    {
        var track = new MediaProbeResult
        {
            Duration = TimeSpan.FromMinutes(4),
            AudioCodec = "flac",
            SampleRate = 96000,
            BitDepth = 24,
            Channels = 2,
            AudioBitrate = 2880,
            HasEmbeddedCover = true,
        };
        var files = Enumerable.Range(1, 12).Select(number => File($"/music/Aurora/Night Tape/{number:00}.flac", track, size: 34_000_000)).ToList();

        var block = OnlyBlock(YourCopyBuilder.Build(Page(DetailEntityType.MusicAlbum), files, includeLocation: false));

        Assert.Equal(["FLAC", "24-bit / 96 kHz", "12 tracks", "Stereo"], block.Chips);
        Assert.Equal([DetailYourCopyGroupKeys.Audio, DetailYourCopyGroupKeys.Tracks, DetailYourCopyGroupKeys.Files], block.Groups.Select(group => group.Key));
        Assert.Equal(["FLAC · 24-bit · 96 kHz", "Stereo", "2.9 Mb/s average"], Lines(block, DetailYourCopyGroupKeys.Audio));
        Assert.Equal(["12 tracks · 48 min", "Embedded cover art"], Lines(block, DetailYourCopyGroupKeys.Tracks));
        var disk = Lines(block, DetailYourCopyGroupKeys.Files);
        Assert.StartsWith("12 FLAC files · ", disk[0]);
        Assert.Contains("Folder · Aurora / Night Tape", disk);
    }

    [Fact]
    public void ABook_GetsFormatPagesAndLanguageWithBookReadingAndFileGroups_AndReadingShowsTheProfilesProgress()
    {
        var facts = new DetailFactsViewModel { PageCount = "320", Language = "English" };
        var progress = new ProgressViewModel { Kind = DetailProgressKind.Reading, Percent = 42, PercentLabel = "42%", RemainingLabel = "5h left" };

        var block = OnlyBlock(YourCopyBuilder.Build(Page(DetailEntityType.Book, facts, progress), [File("/books/Dune.epub", null, size: 1_300_000)], includeLocation: false));

        Assert.Equal(["EPUB", "320 pages", "English"], block.Chips);
        Assert.Equal([DetailYourCopyGroupKeys.Book, DetailYourCopyGroupKeys.Reading, DetailYourCopyGroupKeys.File], block.Groups.Select(group => group.Key));
        Assert.Equal(["Progress · 42%", "5h left"], Lines(block, DetailYourCopyGroupKeys.Reading));
        Assert.Equal(["EPUB · 1.2 MB", "Added · Nov 2, 2026"], Lines(block, DetailYourCopyGroupKeys.File));
        Assert.False(block.IsReadingFacts);
    }

    [Fact]
    public void ABookNobodyHasStarted_SaysSo()
    {
        var block = OnlyBlock(YourCopyBuilder.Build(Page(DetailEntityType.Book), [File("/books/Dune.epub", null)], includeLocation: false));

        Assert.Equal(["Not started"], Lines(block, DetailYourCopyGroupKeys.Reading));
    }

    [Fact]
    public void APageThatIsNotATitle_GetsNothing()
    {
        Assert.Null(YourCopyBuilder.Build(Page(DetailEntityType.Person), [File("/m/A.mkv", Film())], includeLocation: true));
        Assert.Null(YourCopyBuilder.Build(Page(DetailEntityType.BookSeries), [File("/m/A.mkv", Film())], includeLocation: true));
    }

    // ---- Who may see it, and what is read --------------------------------------------------------------------------------------------

    private sealed class ProfilesSpy(params Profile[] profiles) : IProfileRepository
    {
        public Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Profile>>(profiles);

        public Task<Profile?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(profiles.FirstOrDefault(profile => profile.Id == id));

        public Task<bool> UpdateAsync(Profile profile, CancellationToken ct = default) => Task.FromResult(false);
    }

    private static readonly Profile Adult = new() { Id = Guid.NewGuid(), Role = ProfileRole.StandardUser };
    private static readonly Profile Kid = new() { Id = Guid.NewGuid(), Role = ProfileRole.RestrictedProfile };

    private DetailYourCopyService Service() => new(new YourCopyReadRepository(_world.Database), new ProfilesSpy(Adult, Kid));

    private (Guid Work, Guid Asset) Seed(string path, MediaProbeResult? probe, string? library = null, string mediaType = "Movies", Guid? parent = null)
    {
        var work = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var hash = $"hash-{asset:N}";
        using var connection = _world.Database.CreateConnection();
        connection.Execute("INSERT INTO works (id, media_type, parent_work_id) VALUES (@work, @mediaType, @parent)", new { work, mediaType, parent });
        connection.Execute("INSERT INTO editions (id, work_id) VALUES (@edition, @work)", new { edition, work });
        connection.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id) VALUES (@asset, @edition, @hash, @path, @added, @library)",
            new { asset, edition, hash, path, added = Added.ToString("O"), library });
        if (probe is not null)
        {
            connection.Execute(
                "INSERT INTO playback_inspection_cache (asset_id, source_hash, inspected_at, file_size, duration_secs, metadata_json) VALUES (@asset, @hash, datetime('now'), 7600000000, @seconds, @json)",
                new { asset, hash, seconds = probe.Duration.TotalSeconds, json = JsonSerializer.Serialize(probe) });
        }

        return (work, asset);
    }

    private static DetailActionAuthorizationContext Editor => new(CanManageMetadata: true);

    private static DetailActionAuthorizationContext Viewer => default;

    [Fact]
    public async Task AnAdultProfile_GetsTheRowFromTheStoredFacts_AndOnlyAnEditorGetsTheFilesPlace()
    {
        var (work, asset) = Seed("/media/Movies/Test/Test.mkv", Film());
        var viewerPage = new DetailPageViewModel { Id = work.ToString("D"), EntityType = DetailEntityType.Movie };
        var editorPage = new DetailPageViewModel { Id = work.ToString("D"), EntityType = DetailEntityType.Movie };

        await Service().ApplyAsync(viewerPage, [asset], null, Adult.Id, Viewer, CancellationToken.None);
        await Service().ApplyAsync(editorPage, [asset], null, Adult.Id, Editor, CancellationToken.None);

        var viewerBlock = OnlyBlock(viewerPage.YourCopy);
        Assert.Equal(["1080p", "H.264", "AAC 5.1", "MKV"], viewerBlock.Chips);
        Assert.Equal("Test.mkv", viewerBlock.FileName);
        Assert.Null(viewerBlock.FileLocation);
        Assert.Equal("/media/Movies/Test/Test.mkv", OnlyBlock(editorPage.YourCopy).FileLocation);
    }

    [Fact]
    public async Task AKidsProfile_AndARequestWithNoProfile_GetNoRowAtAll()
    {
        var (work, asset) = Seed("/media/Movies/Test/Test.mkv", Film());
        var kidPage = new DetailPageViewModel { Id = work.ToString("D"), EntityType = DetailEntityType.Movie };
        var anonymousPage = new DetailPageViewModel { Id = work.ToString("D"), EntityType = DetailEntityType.Movie };

        await Service().ApplyAsync(kidPage, [asset], null, Kid.Id, Editor, CancellationToken.None);
        await Service().ApplyAsync(anonymousPage, [asset], null, null, Editor, CancellationToken.None);

        Assert.Null(kidPage.YourCopy);
        Assert.Null(anonymousPage.YourCopy);
    }

    [Fact]
    public async Task AFileInTheVault_IsNeverReadSoTheRowNeverNamesIt()
    {
        var vaultLibrary = Guid.NewGuid().ToString("D");
        using (var connection = _world.Database.CreateConnection())
        {
            var admin = Guid.NewGuid();
            connection.Execute("INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@admin,'Admin','#7C4DFF','Administrator',@now);", new { admin, now = Added.ToString("O") });
            connection.Execute(
                "INSERT INTO vault_libraries(library_id, lookup_source, lookup_server_id, adults_confirmed_at, created_by_profile_id, created_at, updated_at) VALUES(@id,'none',NULL,@now,@admin,@now,@now);",
                new { id = vaultLibrary, admin, now = Added.ToString("O") });
        }

        var (work, vaultAsset) = Seed("/vault/clip.mkv", Film(), library: vaultLibrary);
        var page = new DetailPageViewModel { Id = work.ToString("D"), EntityType = DetailEntityType.Movie };

        await Service().ApplyAsync(page, [vaultAsset], null, Adult.Id, Editor, CancellationToken.None);

        Assert.Null(page.YourCopy);
    }

    [Fact]
    public async Task AShowPage_SummarisesOnlyTheEpisodesUnderThatShowThatTheViewerMaySee()
    {
        var show = Guid.NewGuid();
        var otherShow = Guid.NewGuid();
        var rows = new List<DisplayWorkRow>();
        var visible = new List<(Guid Work, Guid Asset)>();
        for (var number = 1; number <= 3; number++)
        {
            var (work, asset) = Seed($"/tv/Show/S01E{number:00}.mkv", Film(number == 3 ? 1280 : 1920, number == 3 ? 720 : 1080), mediaType: "TV");
            rows.Add(new DisplayWorkRow { WorkId = work, AssetId = asset, RootWorkId = show });
            visible.Add((work, asset));
        }

        var (_, foreign) = Seed("/tv/Other/S01E01.mkv", Film(3840, 2160), mediaType: "TV");
        rows.Add(new DisplayWorkRow { WorkId = Guid.NewGuid(), AssetId = foreign, RootWorkId = otherShow });
        var page = new DetailPageViewModel { Id = show.ToString("D"), EntityType = DetailEntityType.TvShow };

        await Service().ApplyAsync(page, null, rows, Adult.Id, Editor, CancellationToken.None);

        var block = OnlyBlock(page.YourCopy);
        Assert.Equal(["3 episodes", "1080p", "H.264", "AAC 5.1", "MKV"], block.Chips);
        Assert.Contains("1 episode in 720p", Lines(block, DetailYourCopyGroupKeys.Video));
        Assert.Null(block.FileLocation);
    }

    [Fact]
    public async Task APersonPage_IsLeftAlone()
    {
        var page = new DetailPageViewModel { Id = Guid.NewGuid().ToString("D"), EntityType = DetailEntityType.Person };

        await Service().ApplyAsync(page, [Guid.NewGuid()], null, Adult.Id, Editor, CancellationToken.None);

        Assert.Null(page.YourCopy);
    }
}
