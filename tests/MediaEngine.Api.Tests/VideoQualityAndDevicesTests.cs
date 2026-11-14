using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using MediaEngine.Storage.Services;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>"Video quality and devices" (playback-path spec D15): the one list, what is saved per person and per device, and how the Engine applies it.</summary>
public sealed class VideoQualityAndDevicesTests : IAsyncLifetime
{
    private static readonly PlaybackConnectionContextDto MobileData = new() { ConnectionPath = PlaybackConnectionPaths.RemoteDirect };
    private static readonly PlaybackConnectionContextDto HomeNetwork = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_video_options_{Guid.NewGuid():N}");
    private DatabaseConnection _database = null!;
    private PlaybackCapabilitiesService _playback = null!;
    private PlaybackStateRepository _state = null!;
    private UserPlaybackSettingsService _settings = null!;
    private VideoOptionsService _options = null!;
    private readonly Guid _profileId = Guid.NewGuid();
    private readonly Guid _otherProfileId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        var configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        configuration.SaveCore(new CoreConfiguration { LibraryRoot = _root });
        configuration.SaveTranscoding(new TranscodingSettings { VariantCachePath = "variants" });

        foreach (var (id, name) in new[] { (_profileId, "Options Test"), (_otherProfileId, "Someone Else") })
        {
            await ProfileTestData.InsertAsync(_database, new Profile
            {
                Id = id,
                DisplayName = name,
                AvatarColor = "#7C4DFF",
                Role = ProfileRole.RestrictedProfile,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        _settings = new UserPlaybackSettingsService(_database, new ProfileRepository(_database));
        _options = new VideoOptionsService(_settings);

        var accounts = new AccountRepository(_database);
        var applications = new ApplicationRepository(_database);
        var identities = new IdentityRepository(_database);
        var clients = new ClientAuthorizationRepository(_database);
        var protection = new EphemeralDataProtectionProvider();
        var assets = new MediaAssetRepository(_database);
        var tracks = new TextTrackRepository(_database);
        var ffmpeg = new UnusedFfmpegService();
        var hls = new AdaptiveHlsService(
            new AdaptiveHlsPackageRepository(_database), assets, tracks, ffmpeg, configuration,
            new TestHostApplicationLifetime(), NullLogger<AdaptiveHlsService>.Instance);
        _state = new PlaybackStateRepository(_database);
        _playback = new PlaybackCapabilitiesService(
            assets, ffmpeg, _database, _state, new PlaybackSegmentRepository(_database), tracks,
            new AudiobookChapterTitleOverrideRepository(_database), new MediaTypeExtensionCatalog(), configuration, hls,
            new HlsAccessGrantService(protection, configuration, accounts, applications, identities, clients, new PermissionRegistry()),
            new HttpContextAccessor(), NullLogger<PlaybackCapabilitiesService>.Instance);
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_root, "library.db")}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    // ── The list ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheListHasTheSameEntriesInTheSameOrderForEveryClient()
    {
        var browser = await _options.BuildAsync(_profileId, null);
        var pairedPhone = await _options.BuildAsync(_otherProfileId, new ClientCapabilitiesDto
        {
            SchemaVersion = 2,
            VideoCodecs = ["h264", "hevc"],
            HardwareDecodeVideoCodecs = ["hevc"],
            SupportsHdr = true,
            MaxHeight = 2160,
        });
        var nobody = await _options.BuildAsync(null, null);

        Assert.Equal(VideoOptionCatalog.Ids, browser.Options.Select(option => option.Id));
        Assert.Equal(browser.Options.Select(option => option.Id), pairedPhone.Options.Select(option => option.Id));
        Assert.Equal(browser.Options.Select(option => option.Id), nobody.Options.Select(option => option.Id));
        Assert.Equal(browser.Options.Select(option => option.Label), pairedPhone.Options.Select(option => option.Label));
        Assert.Equal(browser.Options.Select(option => option.Choices.Select(choice => choice.Value).ToArray()).SelectMany(values => values),
            pairedPhone.Options.Select(option => option.Choices.Select(choice => choice.Value).ToArray()).SelectMany(values => values));
    }

    [Fact]
    public async Task TheListUsesTheAgreedWordsAndDefaultsEverythingToAutomatic()
    {
        var list = await _options.BuildAsync(_profileId, null);

        Assert.Equal("Video quality and devices", list.Title);
        Assert.Equal(
            ["Video quality", "Quality on mobile data", "Convert only when needed", "Allow HDR", "Maximum resolution",
             "Use hardware decoding", "Surround sound", "What this device can play", "Check again"],
            list.Options.Select(option => option.Label));
        Assert.Equal(["Automatic", "Highest", "Balanced", "Data saver"], list.Options[0].Choices.Select(choice => choice.Label));
        Assert.Equal(["Automatic", "On", "Off"], list.Options.Single(option => option.Id == VideoOptionIds.HardwareDecoding).Choices.Select(choice => choice.Label));
        Assert.Equal(["Automatic", "Pass through", "Stereo only"], list.Options.Single(option => option.Id == VideoOptionIds.SurroundSound).Choices.Select(choice => choice.Label));
        Assert.Equal("Automatic", Label(list, VideoOptionIds.VideoQuality));
        Assert.Equal("Automatic", Label(list, VideoOptionIds.MobileDataQuality));
        Assert.Equal("Automatic", Label(list, VideoOptionIds.MaxResolution));
        Assert.Equal("Automatic", Label(list, VideoOptionIds.HardwareDecoding));
        Assert.Equal("Automatic", Label(list, VideoOptionIds.SurroundSound));
        Assert.Equal(VideoOptionValues.On, list.Options.Single(option => option.Id == VideoOptionIds.ConvertOnlyWhenNeeded).Value);
        Assert.Equal(VideoOptionValues.On, list.Options.Single(option => option.Id == VideoOptionIds.AllowHdr).Value);
        Assert.Equal("Not measured yet", list.Options.Single(option => option.Id == VideoOptionIds.DeviceAbilities).Value);
    }

    [Fact]
    public async Task TheListScopesTheFourPersonRowsToTheProfileAndTheOthersToTheDevice()
    {
        var list = await _options.BuildAsync(_profileId, null);

        Assert.Equal(
            [VideoOptionScopes.Profile, VideoOptionScopes.Profile, VideoOptionScopes.Profile, VideoOptionScopes.Profile],
            list.Options.Take(4).Select(option => option.Scope));
        Assert.All(list.Options.Skip(4), option => Assert.Equal(VideoOptionScopes.Device, option.Scope));
        Assert.Equal(VideoOptionKinds.Summary, list.Options.Single(option => option.Id == VideoOptionIds.DeviceAbilities).Kind);
        Assert.Equal(VideoOptionKinds.Action, list.Options.Single(option => option.Id == VideoOptionIds.CheckAgain).Kind);
    }

    [Fact]
    public async Task TheDeviceSummaryAndLearnedLimitsComeFromWhatTheDeviceReported()
    {
        var list = await _options.BuildAsync(_profileId, new ClientCapabilitiesDto
        {
            VideoCodecs = ["h264", "hevc", "av1"],
            HardwareDecodeVideoCodecs = ["h264", "hevc", "av1"],
            SupportsHdr = true,
            MaxHeight = 2160,
            LearnedLimits = [new LearnedPlaybackLimitDto { Summary = "cannot play HEVC 10-bit MKV as it is" }],
        });

        Assert.Equal("Plays H.264, HEVC and AV1 in hardware · HDR · up to 4K", list.Options.Single(option => option.Id == VideoOptionIds.DeviceAbilities).Value);
        Assert.Equal(["Learned: cannot play HEVC 10-bit MKV as it is"], list.Learned);
    }

    // ── Saved values ────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePersonsFourChoices_AreSavedPerProfile_AndDoNotChangeAnotherProfile()
    {
        Assert.Equal(VideoOptionOutcome.Saved, await _options.SaveProfileOptionAsync(_profileId, VideoOptionIds.VideoQuality, PlaybackPreferenceValues.Balanced));
        Assert.Equal(VideoOptionOutcome.Saved, await _options.SaveProfileOptionAsync(_profileId, VideoOptionIds.MobileDataQuality, PlaybackPreferenceValues.DataSaver));
        Assert.Equal(VideoOptionOutcome.Saved, await _options.SaveProfileOptionAsync(_profileId, VideoOptionIds.ConvertOnlyWhenNeeded, "off"));
        Assert.Equal(VideoOptionOutcome.Saved, await _options.SaveProfileOptionAsync(_profileId, VideoOptionIds.AllowHdr, "off"));

        var mine = (await _settings.GetAsync(_profileId)).Watching;
        Assert.Equal(PlaybackPreferenceValues.Balanced, mine.PreferredVideoQuality);
        Assert.Equal(PlaybackPreferenceValues.DataSaver, mine.MobileDataVideoQuality);
        Assert.False(mine.ConvertOnlyWhenNeeded);
        Assert.False(mine.AllowHdr);

        var theirs = (await _settings.GetAsync(_otherProfileId)).Watching;
        Assert.Equal(PlaybackPreferenceValues.Auto, theirs.PreferredVideoQuality);
        Assert.Equal(PlaybackPreferenceValues.Auto, theirs.MobileDataVideoQuality);
        Assert.True(theirs.ConvertOnlyWhenNeeded);
        Assert.True(theirs.AllowHdr);

        var list = await _options.BuildAsync(_profileId, null);
        Assert.Equal(PlaybackPreferenceValues.DataSaver, list.Options.Single(option => option.Id == VideoOptionIds.MobileDataQuality).Value);
    }

    [Fact]
    public async Task APersonChoice_ThatIsNotInTheList_IsRefusedAndChangesNothing()
    {
        Assert.Equal(VideoOptionOutcome.InvalidValue, await _options.SaveProfileOptionAsync(_profileId, VideoOptionIds.VideoQuality, "ultra"));
        Assert.Equal(VideoOptionOutcome.InvalidValue, await _options.SaveProfileOptionAsync(_profileId, VideoOptionIds.AllowHdr, "maybe"));
        Assert.Equal(VideoOptionOutcome.UnknownOption, await _options.SaveProfileOptionAsync(_profileId, VideoOptionIds.MaxResolution, "720"));

        Assert.Equal(PlaybackPreferenceValues.Auto, (await _settings.GetAsync(_profileId)).Watching.PreferredVideoQuality);
    }

    [Fact]
    public void TheDeviceChoices_AreCheckedAgainstTheList_AndUnknownValuesBecomeAutomatic()
    {
        var changed = VideoOptionCatalog.TryApplyDeviceOption(null, VideoOptionIds.SurroundSound, VideoOptionValues.SurroundStereo);
        Assert.Equal(VideoOptionValues.SurroundStereo, changed!.SurroundSound);
        Assert.Equal(VideoOptionValues.Auto, changed.MaxResolution);

        changed = VideoOptionCatalog.TryApplyDeviceOption(changed, VideoOptionIds.MaxResolution, "1080");
        Assert.Equal("1080", changed!.MaxResolution);
        Assert.Equal(VideoOptionValues.SurroundStereo, changed.SurroundSound);

        Assert.Null(VideoOptionCatalog.TryApplyDeviceOption(changed, VideoOptionIds.MaxResolution, "999"));
        Assert.Null(VideoOptionCatalog.TryApplyDeviceOption(changed, VideoOptionIds.HardwareDecoding, "sometimes"));
        Assert.Null(VideoOptionCatalog.TryApplyDeviceOption(changed, VideoOptionIds.VideoQuality, "auto"));

        var cleaned = ClientCapabilitiesNormalizer.CleanOptions(new DeviceVideoOptionsDto { MaxResolution = "12", HardwareDecoding = "x", SurroundSound = "loud" });
        Assert.Equal([VideoOptionValues.Auto, VideoOptionValues.Auto, VideoOptionValues.Auto], [cleaned.MaxResolution, cleaned.HardwareDecoding, cleaned.SurroundSound]);
    }

    [Fact]
    public void ADevicesOwnReport_CannotSetItsVideoChoices_ButItsSavedChoicesSurviveARereport()
    {
        var saved = new ClientCapabilitiesDto { VideoOptions = new DeviceVideoOptionsDto { SurroundSound = VideoOptionValues.SurroundStereo } };
        var reported = new ClientCapabilitiesDto
        {
            VideoCodecs = ["h264"],
            VideoOptions = new DeviceVideoOptionsDto { MaxResolution = "480", SurroundSound = VideoOptionValues.SurroundPassthrough },
        };

        var kept = ClientCapabilitiesNormalizer.Normalize(reported, saved.LearnedLimits, saved.VideoOptions);

        Assert.Equal(VideoOptionValues.SurroundStereo, kept.VideoOptions.SurroundSound);
        Assert.Equal(VideoOptionValues.Auto, kept.VideoOptions.MaxResolution);
        Assert.Equal(["h264"], kept.VideoCodecs);
        // Recording what was learned keeps the choices too.
        var learned = ClientCapabilitiesNormalizer.WithLearned(kept, [new LearnedPlaybackLimitDto { Summary = "x" }]);
        Assert.Equal(VideoOptionValues.SurroundStereo, learned.VideoOptions.SurroundSound);
    }

    [Fact]
    public void TheBrowserStoreKeepsTheOptionsABrowserSendsAndLetsThemBeChanged()
    {
        var store = new BrowserPlaybackAbilityStore(TimeProvider.System);
        var key = "abc123";
        Assert.True(store.Set(_profileId, key, new ClientCapabilitiesDto
        {
            VideoCodecs = ["h264"],
            VideoOptions = new DeviceVideoOptionsDto { MaxResolution = "720" },
        }));
        Assert.Equal("720", store.TryGet(_profileId, key)!.VideoOptions.MaxResolution);

        Assert.True(store.SetVideoOptions(_profileId, key, new DeviceVideoOptionsDto { MaxResolution = "480", SurroundSound = VideoOptionValues.SurroundStereo }));

        var kept = store.TryGet(_profileId, key)!;
        Assert.Equal("480", kept.VideoOptions.MaxResolution);
        Assert.Equal(VideoOptionValues.SurroundStereo, kept.VideoOptions.SurroundSound);
        Assert.Equal(["h264"], kept.VideoCodecs);
        Assert.False(store.SetVideoOptions(_profileId, "unknown", new DeviceVideoOptionsDto()));
    }

    [Fact]
    public async Task TheChoicesAStartIsMadeUnder_ComeFromThePersonAndTheDevice()
    {
        await _options.SaveProfileOptionAsync(_profileId, VideoOptionIds.MobileDataQuality, PlaybackPreferenceValues.DataSaver);
        var device = new ClientCapabilitiesDto { VideoOptions = new DeviceVideoOptionsDto { MaxResolution = "720", SurroundSound = VideoOptionValues.SurroundStereo } };

        var choices = await _options.ResolveChoicesAsync(_profileId, device);

        Assert.Equal(PlaybackPreferenceValues.DataSaver, choices.MobileDataQuality);
        Assert.Equal(720, choices.MaxResolution);
        Assert.True(choices.StereoOnly);
        Assert.Equal(PlaybackChoices.Automatic, await _options.ResolveChoicesAsync(null, null));
    }

    // ── The rules ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PlaybackPreferenceValues.Auto, null)]
    [InlineData(PlaybackPreferenceValues.High, null)]
    [InlineData(PlaybackPreferenceValues.Original, null)]
    [InlineData(PlaybackPreferenceValues.Balanced, 720)]
    [InlineData(PlaybackPreferenceValues.DataSaver, 480)]
    public void EachQualityAllowsATallestPicture(string quality, int? expected) =>
        Assert.Equal(expected, PlaybackChoiceRules.QualityHeightCap(quality));

    [Fact]
    public void OnMobileData_TheMobileChoiceApplies_AtHomeTheVideoQualityDoes()
    {
        var choices = new PlaybackChoices(VideoQuality: PlaybackPreferenceValues.High, MobileDataQuality: PlaybackPreferenceValues.DataSaver);

        Assert.Equal(PlaybackPreferenceValues.DataSaver, PlaybackChoiceRules.EffectiveQuality(choices, MobileData));
        Assert.Equal(PlaybackPreferenceValues.High, PlaybackChoiceRules.EffectiveQuality(choices, HomeNetwork));
        Assert.Null(PlaybackChoiceRules.QualityCap(choices, HomeNetwork, 2160));
        Assert.Equal(480, PlaybackChoiceRules.QualityCap(choices, MobileData, 1080)!.Value.Height);
        Assert.Null(PlaybackChoiceRules.QualityCap(choices, MobileData, 480));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(2160, null, 2160)]
    [InlineData(null, 720, 720)]
    [InlineData(1080, 720, 720)]
    [InlineData(720, 1080, 720)]
    public void TheMaximumResolutionLowersThePlayersOwnLimit(int? player, int? chosen, int? expected) =>
        Assert.Equal(expected, PlaybackChoiceRules.CapPlayerHeight(player, new PlaybackChoices(MaxResolution: chosen)));

    // ── The ladder ──────────────────────────────────────────────────────────

    private static PlaybackDecision Decide(string extension, string audio, int channels, bool stereoOnly, string video = "h264", PlaybackProfileDto? profile = null) =>
        PlaybackLadder.Decide(new PlaybackLadderInput(
            extension,
            profile ?? PlaybackCapabilitiesService.ProfileFor("web"),
            new MediaProbeResult { VideoCodec = video, AudioCodec = audio, Channels = channels, Height = 1080 },
            StereoOnly: stereoOnly));

    [Theory]
    [InlineData(".mp4", "aac", 6)]
    [InlineData(".mkv", "aac", 6)]
    [InlineData(".mp4", "aac", 8)]
    public void StereoOnly_ForcesAudioConversion_InsteadOfCopyingSurround(string extension, string audio, int channels)
    {
        var automatic = Decide(extension, audio, channels, stereoOnly: false);
        var stereo = Decide(extension, audio, channels, stereoOnly: true);

        Assert.True(automatic.Rung is PlaybackRungs.Direct or PlaybackRungs.Repackage);
        Assert.Equal(PlaybackRungs.ConvertSound, stereo.Rung);
        Assert.True(stereo.CopyVideo);
        Assert.Empty(stereo.CopyAudioCodecs);
    }

    [Fact]
    public void StereoOnly_ForAPictureThatCannotBeCopied_EncodesThePictureAndNeverCopiesSound()
    {
        var stereo = Decide(".mp4", "aac", 6, stereoOnly: true, video: "vp9");

        Assert.Equal(PlaybackRungs.EncodePicture, stereo.Rung);
        Assert.Empty(stereo.CopyAudioCodecs);
    }

    [Fact]
    public void StereoOnly_LeavesStereoSoundAlone_AndSurroundIsKeptWhenAutomaticOrPassThrough()
    {
        Assert.Equal(PlaybackRungs.Direct, Decide(".mp4", "aac", 2, stereoOnly: true).Rung);
        Assert.Equal(PlaybackRungs.Direct, Decide(".mp4", "aac", 6, stereoOnly: false).Rung);
    }

    [Fact]
    public void StereoOnly_WhenTheSoundIsAlreadyConverted_StillCopiesNoSurround()
    {
        var stereo = Decide(".mp4", "ac3", 6, stereoOnly: true);

        Assert.Equal(PlaybackRungs.ConvertSound, stereo.Rung);
        Assert.Empty(stereo.CopyAudioCodecs);
    }

    [Fact]
    public void ConvertOnlyWhenNeededOff_PrefersAPreparedStreamWithItsOwnReason()
    {
        var decision = PlaybackLadder.Decide(new PlaybackLadderInput(
            ".mp4", PlaybackCapabilitiesService.ProfileFor("web"),
            new MediaProbeResult { VideoCodec = "h264", AudioCodec = "aac", Channels = 2, Height = 1080 },
            ForceStreamableCopy: true, StreamableCopyReason: "Convert only when needed is off for this profile"));

        Assert.Equal(PlaybackRungs.Repackage, decision.Rung);
        Assert.StartsWith("Convert only when needed is off", decision.Reason);
    }

    // ── The manifest ────────────────────────────────────────────────────────

    [Fact]
    public async Task DataSaverOnMobileData_PicksALowerQualityThanAutomatic()
    {
        var assetId = await SeedVideoAsync("film.mp4", "h264", "aac", 1080, channels: 2);
        var dataSaver = new PlaybackChoices(MobileDataQuality: PlaybackPreferenceValues.DataSaver);

        var automatic = await _playback.BuildManifestAsync(assetId, "web", connection: MobileData);
        var saved = await _playback.BuildManifestAsync(assetId, "web", connection: MobileData, choices: dataSaver);

        Assert.Equal(PlaybackDeliveryModes.DirectStream, automatic!.RecommendedDelivery);
        Assert.Equal(PlaybackDeliveryModes.Hls, saved!.RecommendedDelivery);
        Assert.Contains("480p", saved.ConversionReason);
        Assert.Contains("Data saver", saved.ConversionReason);
    }

    [Fact]
    public async Task DataSaverOnMobileData_DoesNotChangeStartsAtHome()
    {
        var assetId = await SeedVideoAsync("film.mp4", "h264", "aac", 1080, channels: 2);
        var dataSaver = new PlaybackChoices(MobileDataQuality: PlaybackPreferenceValues.DataSaver);

        var home = await _playback.BuildManifestAsync(assetId, "web", connection: HomeNetwork, choices: dataSaver);

        Assert.Equal(PlaybackDeliveryModes.DirectStream, home!.RecommendedDelivery);
    }

    [Fact]
    public async Task VideoQualityBalanced_CapsAHomeStartAt720p_AndASmallerPictureIsLeftAlone()
    {
        var big = await SeedVideoAsync("big.mp4", "h264", "aac", 1080, channels: 2);
        var small = await SeedVideoAsync("small.mp4", "h264", "aac", 720, channels: 2);
        var balanced = new PlaybackChoices(VideoQuality: PlaybackPreferenceValues.Balanced);

        Assert.Equal(PlaybackDeliveryModes.Hls, (await _playback.BuildManifestAsync(big, "web", choices: balanced))!.RecommendedDelivery);
        Assert.Equal(PlaybackDeliveryModes.DirectStream, (await _playback.BuildManifestAsync(small, "web", choices: balanced))!.RecommendedDelivery);
    }

    [Fact]
    public async Task StereoOnly_MovesASurroundFileOffDirectPlay()
    {
        var assetId = await SeedVideoAsync("surround.mp4", "h264", "aac", 1080, channels: 6);

        var automatic = await _playback.BuildManifestAsync(assetId, "web");
        var stereo = await _playback.BuildManifestAsync(assetId, "web", choices: new PlaybackChoices(StereoOnly: true));

        Assert.Equal(PlaybackDeliveryModes.DirectStream, automatic!.RecommendedDelivery);
        Assert.Equal(PlaybackDeliveryModes.Hls, stereo!.RecommendedDelivery);
    }

    [Fact]
    public async Task TheMaximumResolution_CapsThePictureTheDeviceIsGiven()
    {
        var assetId = await SeedVideoAsync("film.mp4", "h264", "aac", 1080, channels: 2);

        var capped = await _playback.BuildManifestAsync(assetId, "web", choices: new PlaybackChoices(MaxResolution: 720));

        Assert.Equal(PlaybackDeliveryModes.Hls, capped!.RecommendedDelivery);
        Assert.Equal(720, capped.Profile.MaxHeight);
    }

    [Fact]
    public async Task HdrOff_ConvertsAnHdrPicture_AndLeavesSdrAlone()
    {
        var hdr = await SeedVideoAsync("hdr.mp4", "h264", "aac", 1080, channels: 2, colorTransfer: "smpte2084");
        var sdr = await SeedVideoAsync("sdr.mp4", "h264", "aac", 1080, channels: 2, colorTransfer: "bt709");
        var noHdr = new PlaybackChoices(AllowHdr: false);

        var converted = await _playback.BuildManifestAsync(hdr, "web", choices: noHdr);

        Assert.Equal(PlaybackDeliveryModes.Hls, converted!.RecommendedDelivery);
        Assert.Contains("HDR", converted.ConversionReason);
        Assert.Equal(PlaybackDeliveryModes.DirectStream, (await _playback.BuildManifestAsync(sdr, "web", choices: noHdr))!.RecommendedDelivery);
        Assert.Equal(PlaybackDeliveryModes.DirectStream, (await _playback.BuildManifestAsync(hdr, "web"))!.RecommendedDelivery);
    }

    [Fact]
    public async Task ConvertOnlyWhenNeededOff_GivesAPlayableFileAPreparedStream()
    {
        var assetId = await SeedVideoAsync("film.mp4", "h264", "aac", 1080, channels: 2);

        var off = await _playback.BuildManifestAsync(assetId, "web", choices: new PlaybackChoices(ConvertOnlyWhenNeeded: false));

        Assert.Equal(PlaybackDeliveryModes.Hls, off!.RecommendedDelivery);
        Assert.Contains("Convert only when needed", off.ConversionReason);
    }

    [Fact]
    public async Task EverythingAutomatic_StartsExactlyAsBefore()
    {
        var assetId = await SeedVideoAsync("film.mp4", "h264", "aac", 1080, channels: 6);

        var without = await _playback.BuildManifestAsync(assetId, "web");
        var automatic = await _playback.BuildManifestAsync(assetId, "web", choices: PlaybackChoices.Automatic);

        Assert.Equal(without!.RecommendedDelivery, automatic!.RecommendedDelivery);
        Assert.Equal(PlaybackDeliveryModes.DirectStream, automatic.RecommendedDelivery);
    }

    private static string Label(VideoPlaybackOptionsDto list, string id)
    {
        var option = list.Options.Single(candidate => candidate.Id == id);
        return option.Choices.Single(choice => choice.Value == option.Value).Label;
    }

    private async Task<Guid> SeedVideoAsync(string fileName, string video, string audio, int height, int channels, string? colorTransfer = null)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var hash = assetId.ToString("N");
        var path = Path.Combine(_root, fileName);
        await File.WriteAllBytesAsync(path, [0, 1, 2, 3]);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO works (id, media_type) VALUES (@workId, 'Movies');", new { workId });
            connection.Execute("INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);", new { editionId, workId });
            connection.Execute("""
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@assetId, @editionId, @hash, @path, 'Normal');
                """, new { assetId, editionId, hash, path });
        }

        var probe = new MediaProbeResult
        {
            Duration = TimeSpan.FromSeconds(3600),
            VideoCodec = video,
            AudioCodec = audio,
            Channels = channels,
            Width = height * 16 / 9,
            Height = height,
            ColorTransfer = colorTransfer,
        };
        await _state.StoreInspectionAsync(
            assetId, hash, 4, 3600, Path.GetExtension(fileName).TrimStart('.'), JsonSerializer.Serialize(probe));
        return assetId;
    }

    private sealed class UnusedFfmpegService : IFFmpegService
    {
        public string? FfmpegPath => null;
        public string? FfprobePath => null;
        public bool IsAvailable => false;
        public HardwareCapabilities HardwareCapabilities { get; } = new();
        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<MediaProbeResult?>(null);
        public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
