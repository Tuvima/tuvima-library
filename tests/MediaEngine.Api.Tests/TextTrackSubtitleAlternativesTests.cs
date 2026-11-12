using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Providers;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Video player follow-ups D9 to D12 on the Engine: the subtitle list is asked in the language the caller names (never silently "unknown"),
/// downloads nothing until one result is chosen, and answers every outcome with a status of its own; whether a source is switched on is
/// answered from settings alone. Real data store; the source is a hand-written spy that counts what it is asked.
/// </summary>
public sealed class TextTrackSubtitleAlternativesTests : IDisposable
{
    private const string LibraryId = "aaaaaaaa-0000-4000-8000-0000000000a1";
    private readonly LibraryMoveTestWorld _world = new(withFiles: true);
    private readonly SubtitleSource _source = new();
    private readonly Gate _gate = new();
    private readonly BridgeIdRepository _bridges;
    private readonly TextTrackRepository _tracks;
    private readonly string _central;
    private readonly Guid _movieWork;
    private readonly Guid _movieAsset;

    public TextTrackSubtitleAlternativesTests()
    {
        var files = _world.Files!;
        _bridges = new BridgeIdRepository(files.Database);
        _tracks = new TextTrackRepository(files.Database);
        _central = Path.Combine(files.Managed, ".library");
        var parent = _world.AddParent("Movies", "Films");
        var path = MoveFileWorld.WriteFile(files.Managed, "inbox/Film.mkv", "video");
        var movie = _world.AddChild(parent, "Movies", "Film", 1, filePath: path);
        _movieWork = movie.WorkId;
        _movieAsset = movie.AssetId!.Value;
        _world.Put(_movieWork, "media_type", "Movies");
        _world.Put(_movieWork, "title", "Film");
        _world.Put(_movieWork, "language", "de");
        SetBridge(_movieWork, BridgeIdKeys.TmdbId, "603");
    }

    public void Dispose() => _world.Dispose();

    private void SetBridge(Guid entity, string idType, string value) =>
        _bridges.UpsertAsync(new BridgeIdEntry { EntityId = entity, IdType = idType, IdValue = value }).GetAwaiter().GetResult();

    private TextTrackEnrichmentWorker Worker(bool gated = false, bool withLibraryLimits = false)
    {
        var files = _world.Files!;
        return new TextTrackEnrichmentWorker(
            new MediaAssetRepository(files.Database),
            new WorkRepository(files.Database),
            new CanonicalValueRepository(files.Database),
            _bridges,
            _tracks,
            [_source],
            new AssetPathService(_central),
            NullLogger<TextTrackEnrichmentWorker>.Instance,
            configurationLoader: withLibraryLimits ? files.Configuration : null,
            accessGate: gated ? _gate : null);
    }

    private static TextTrackCandidate Result(string id, double confidence = 0.9) =>
        new("spy", TextTrackKind.Subtitles, id, null, "en", "srt", confidence, false, null, ReleaseName: $"Film.{id}");

    // ── The list ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSubtitleList_DownloadsNothing_AndKeepsNoPreview()
    {
        _source.Results = [Result("a"), Result("b")];

        var list = await Worker().FindAlternativesAsync(_movieAsset, TextTrackKind.Subtitles, null);

        Assert.Equal("Found", list.Status);
        Assert.Equal(["a", "b"], list.Items.Select(item => item.SourceId).Order());
        Assert.All(list.Items, item => { Assert.Null(item.Preview); Assert.True(item.IsTimed); });
        Assert.Equal("Spy Source", list.SourceName);
        Assert.Equal("de", list.Language);
        Assert.Equal(0, _source.Downloads);
        Assert.Empty(await _tracks.GetByAssetAsync(_movieAsset, TextTrackKind.Subtitles));
    }

    [Fact]
    public async Task ALanguageGiven_ReachesTheSourceInsteadOfTheTitlesOwnLanguage()
    {
        var withLanguage = await Worker().FindAlternativesAsync(_movieAsset, TextTrackKind.Subtitles, null, language: "French");
        var without = await Worker().FindAlternativesAsync(_movieAsset, TextTrackKind.Subtitles, null);

        Assert.Equal(["French", "de"], _source.SearchedLanguages);
        Assert.Equal("fr", withLanguage.Language);
        Assert.Equal("de", without.Language);
    }

    [Fact]
    public async Task AChosenResult_IsSearchedInTheSameLanguageAndOnlyThenDownloaded()
    {
        _source.Results = [Result("a")];

        var chosen = await Worker().ChooseAlternativeAsync(_movieAsset, TextTrackKind.Subtitles, _source.Name, "a", null, language: "French");

        Assert.Equal("Updated", chosen.Status);
        Assert.Equal(["French"], _source.SearchedLanguages);
        Assert.Equal(1, _source.Downloads);
        Assert.Single(await _tracks.GetByAssetAsync(_movieAsset, TextTrackKind.Subtitles));
    }

    [Fact]
    public async Task ALanguageTheSourceCannotBeAskedFor_SaysSo_AndSearchesNothing()
    {
        var list = await Worker().FindAlternativesAsync(_movieAsset, TextTrackKind.Subtitles, null, language: "Klingon");

        Assert.Equal("LanguageUnknown", list.Status);
        Assert.Equal(0, _source.Searches);
    }

    // ── A status for every outcome ───────────────────────────────────────────

    [Fact]
    public async Task NothingFound_IsNoneFound_WithTheSourceAndLanguage()
    {
        var list = await Worker().FindAlternativesAsync(_movieAsset, TextTrackKind.Subtitles, null, language: "English");

        Assert.Equal("NoneFound", list.Status);
        Assert.Equal("Spy Source", list.SourceName);
        Assert.Equal("en", list.Language);
    }

    [Fact]
    public async Task ASourceSwitchedOff_IsNoSourceOn()
    {
        _source.Availability = "Disabled";

        Assert.Equal("NoSourceOn", (await Worker().FindAlternativesAsync(_movieAsset, TextTrackKind.Subtitles, null)).Status);
        Assert.Equal(0, _source.Searches);
    }

    [Fact]
    public async Task AMissingKey_IsSourceKeyRequired_WithoutContactingTheSource()
    {
        _source.Availability = "AuthenticationRequired";

        var list = await Worker().FindAlternativesAsync(_movieAsset, TextTrackKind.Subtitles, null);

        Assert.Equal("SourceKeyRequired", list.Status);
        Assert.Equal("Spy Source", list.SourceName);
        Assert.Equal(0, _source.Searches);
    }

    [Fact]
    public async Task AKeyTheSourceRefuses_IsSourceKeyRequired()
    {
        _source.Throws = new TextTrackLookupException("AuthenticationRequired", "bad key");

        var list = await Worker().FindAlternativesAsync(_movieAsset, TextTrackKind.Subtitles, null);

        Assert.Equal("SourceKeyRequired", list.Status);
        Assert.Equal("Spy Source", list.SourceName);
    }

    [Theory]
    [InlineData("QuotaExceeded")]
    [InlineData("RateLimited")]
    public async Task ADailyLimitOrTooManyRequests_IsLimitReached(string sourceStatus)
    {
        _source.Throws = new TextTrackLookupException(sourceStatus, "slow down");

        var list = await Worker().FindAlternativesAsync(_movieAsset, TextTrackKind.Subtitles, null);

        Assert.Equal("LimitReached", list.Status);
        Assert.Equal("Spy Source", list.SourceName);
    }

    [Theory]
    [InlineData("ProviderUnavailable")]
    [InlineData("ProviderError")]
    public async Task ASourceThatCannotBeReached_IsUnavailable(string sourceStatus)
    {
        _source.Throws = new TextTrackLookupException(sourceStatus, "down");

        var list = await Worker().FindAlternativesAsync(_movieAsset, TextTrackKind.Subtitles, null);

        Assert.Equal("Unavailable", list.Status);
        Assert.Equal("Spy Source", list.SourceName);
    }

    [Fact]
    public async Task ASourceKnownToBeDown_IsUnavailable_WithoutContactingIt()
    {
        _source.Availability = "ProviderUnavailable";

        Assert.Equal("Unavailable", (await Worker().FindAlternativesAsync(_movieAsset, TextTrackKind.Subtitles, null)).Status);
        Assert.Equal(0, _source.Searches);
    }

    [Fact]
    public async Task AMovieThatCannotBeIdentified_IsIdentityUnverified_WithoutContactingTheSource()
    {
        await _bridges.DeleteByEntityAsync(_movieWork);

        var list = await Worker().FindAlternativesAsync(_movieAsset, TextTrackKind.Subtitles, null);

        Assert.Equal("IdentityUnverified", list.Status);
        Assert.Equal(0, _source.Searches);
    }

    [Fact]
    public async Task AnEpisodeWithNoTmdbCrosswalk_IsIdentityUnverified()
    {
        var files = _world.Files!;
        var show = _world.AddParent("TV", "The Show");
        var episode = _world.AddChild(show, "TV", "Pilot", 1, filePath: MoveFileWorld.WriteFile(files.Managed, "inbox/Show.S01E01.mkv", "video"));
        _world.Put(episode.WorkId, "media_type", "TV");
        SetBridge(show, BridgeIdKeys.TvdbId, "81189");
        SetBridge(episode.WorkId, BridgeIdKeys.TvdbEpisodeId, "349232");

        var list = await Worker().FindAlternativesAsync(episode.AssetId!.Value, TextTrackKind.Subtitles, null);

        Assert.Equal("IdentityUnverified", list.Status);
        Assert.Equal(0, _source.Searches);
    }

    [Fact]
    public async Task AProfileThatMayNotUseTheSource_IsNotAllowed()
    {
        _gate.Outcome = PluginAccessOutcome.NotInAudience;

        Assert.Equal("NotAllowed", (await Worker(gated: true).FindAlternativesAsync(_movieAsset, TextTrackKind.Subtitles, null)).Status);
        Assert.Equal(0, _source.Searches);
    }

    [Fact]
    public async Task ALibraryThatNeverLooksAnythingUp_IsBlocked()
    {
        var files = _world.Files!;
        // A plain kind (Other videos) never contacts a source, whatever the file inside it is.
        files.Configuration.SaveLibraries(new LibrariesConfiguration
        {
            StorageLocations = [new() { Id = "media", Label = "Library data", Path = files.Managed, AllowWrite = true }],
            Libraries =
            [
                new LibraryFolderConfig
                {
                    Id = LibraryId, Name = "Clips", Category = "Videos", KindKey = "other_videos", Area = LibraryAreas.Watch,
                    MetadataPolicy = LibraryMetadataPolicies.LocalOnly, YoutubeLookup = false, MediaTypes = ["Videos"],
                },
            ],
        });
        _world.Execute("UPDATE media_assets SET library_id = @library WHERE id = @asset;", new { library = LibraryId, asset = _movieAsset });

        var list = await Worker(withLibraryLimits: true).FindAlternativesAsync(_movieAsset, TextTrackKind.Subtitles, null);

        Assert.Equal("Blocked", list.Status);
        Assert.Equal(0, _source.Searches);
    }

    [Fact]
    public async Task AFileThatIsNotKnown_IsAssetMissing()
    {
        Assert.Equal("AssetMissing", (await Worker().FindAlternativesAsync(Guid.NewGuid(), TextTrackKind.Subtitles, null)).Status);
    }

    [Fact]
    public async Task SubtitlesForASong_AreUnsupported()
    {
        var files = _world.Files!;
        var album = _world.AddParent("Music", "Album");
        var song = _world.AddChild(album, "Music", "Song", 1, filePath: MoveFileWorld.WriteFile(files.Managed, "inbox/Song.flac", "audio"));
        _world.Put(song.WorkId, "media_type", "Music");

        Assert.Equal("Unsupported", (await Worker().FindAlternativesAsync(song.AssetId!.Value, TextTrackKind.Subtitles, null)).Status);
    }

    // ── Is a source on, without asking one ───────────────────────────────────

    [Theory]
    [InlineData("Available", "Ready")]
    [InlineData("Disabled", "NoSourceOn")]
    [InlineData("AuthenticationRequired", "SourceKeyRequired")]
    [InlineData("ProviderUnavailable", "Ready")]
    public async Task WhetherASourceIsOn_IsAnsweredFromSettingsAlone(string availability, string expected)
    {
        _source.Availability = availability;
        _source.Results = [Result("a")];

        var status = await Worker().GetSourceStatusAsync(_movieAsset, TextTrackKind.Subtitles, null);

        Assert.Equal(expected, status);
        Assert.Equal(0, _source.Searches);
        Assert.Equal(0, _source.Downloads);
    }

    [Fact]
    public async Task WhetherASourceIsOn_ForAnItemWithNoIdentity_StillContactsNothing()
    {
        await _bridges.DeleteByEntityAsync(_movieWork);

        Assert.Equal("Ready", await Worker().GetSourceStatusAsync(_movieAsset, TextTrackKind.Subtitles, null));
        Assert.Equal(0, _source.Searches);
    }

    [Fact]
    public async Task WhetherASourceIsOn_SaysNotAllowedBlockedUnsupportedAndMissing()
    {
        _gate.Outcome = PluginAccessOutcome.NotInAudience;
        Assert.Equal("NotAllowed", await Worker(gated: true).GetSourceStatusAsync(_movieAsset, TextTrackKind.Subtitles, null));
        Assert.Equal("AssetMissing", await Worker().GetSourceStatusAsync(Guid.NewGuid(), TextTrackKind.Subtitles, null));
        Assert.Equal("Unsupported", await Worker().GetSourceStatusAsync(_movieAsset, TextTrackKind.Lyrics, null));
        Assert.Equal(0, _source.Searches);
    }

    // ── The language the caller names ────────────────────────────────────────

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("English", true)]
    [InlineData("pt-BR", true)]
    [InlineData("Chinese Traditional", true)]
    [InlineData("en;drop", false)]
    [InlineData("en&x=1", false)]
    [InlineData("français1", false)]
    public void ALanguageToSearchIn_IsOnlyLettersSpacesAndHyphens(string? value, bool valid)
    {
        var check = typeof(MediaEngine.Api.Endpoints.StreamEndpoints)
            .GetMethod("IsValidSearchLanguage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        Assert.Equal(valid, (bool)check.Invoke(null, [value])!);
    }

    [Fact]
    public void ALanguageToSearchIn_IsAtMostFortyCharacters()
    {
        var check = typeof(MediaEngine.Api.Endpoints.StreamEndpoints)
            .GetMethod("IsValidSearchLanguage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        Assert.True((bool)check.Invoke(null, [new string('a', 40)])!);
        Assert.False((bool)check.Invoke(null, [new string('a', 41)])!);
    }

    private sealed class Gate : IPluginAccessGate
    {
        public PluginAccessOutcome Outcome { get; set; } = PluginAccessOutcome.Allowed;

        public Task<PluginAccessOutcome> CheckAsync(PluginAccessQuery query, CancellationToken ct = default) => Task.FromResult(Outcome);
    }

    private sealed class SubtitleSource : ITextTrackProvider
    {
        public string Name => "spy";

        public string DisplayName => "Spy Source";

        public TextTrackKind Kind => TextTrackKind.Subtitles;

        public bool IsEnabled => Availability != "Disabled";

        public string Availability { get; set; } = "Available";

        public IReadOnlyList<TextTrackCandidate> Results { get; set; } = [];

        public TextTrackLookupException? Throws { get; set; }

        public int Searches { get; private set; }

        public int Downloads { get; private set; }

        public List<string?> SearchedLanguages { get; } = [];

        public bool CanHandle(MediaType mediaType) => mediaType is MediaType.Movies or MediaType.TV;

        public TextTrackProviderAvailability GetAvailability(MediaType mediaType) => new(Availability, null);

        // The language names the source knows; anything else cannot be asked for.
        public string? NormalizeSearchLanguage(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "english" => "en",
            "french" => "fr",
            "de" => "de",
            "en" => "en",
            _ => null,
        };

        public Task<IReadOnlyList<TextTrackCandidate>> SearchAsync(TextTrackLookup lookup, CancellationToken ct = default)
        {
            Searches++;
            SearchedLanguages.Add(lookup.Language);
            return Throws is null ? Task.FromResult(Results) : throw Throws;
        }

        public Task<TextTrackDownload?> DownloadAsync(TextTrackCandidate candidate, CancellationToken ct = default)
        {
            Downloads++;
            return Task.FromResult<TextTrackDownload?>(new TextTrackDownload(candidate, "1\n00:00:01,000 --> 00:00:02,000\nHello\n", "srt", "vtt"));
        }
    }
}
