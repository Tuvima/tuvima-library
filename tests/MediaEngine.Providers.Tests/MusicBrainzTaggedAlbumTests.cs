using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS0618 // suppress obsolete warnings in test stubs

namespace MediaEngine.Providers.Tests;

/// <summary>
/// A music file's album tag outranks MusicBrainz's generic "earliest official release" rule:
/// the tagged album is preferred when the recording sits on it, and kept untouched (no
/// album-level claims) when the recording does not.
/// </summary>
public sealed class MusicBrainzTaggedAlbumTests
{
    private const string UninvitedRecording = """
        {
          "recordings": [
            {
              "id": "recording-uninvited",
              "title": "Uninvited",
              "artist-credit": [{ "name": "Alanis Morissette" }],
              "releases": [
                {
                  "id": "vh1-release",
                  "title": "VH1 Storytellers",
                  "status": "Official",
                  "date": "1998-01-01",
                  "release-group": { "id": "vh1-group", "primary-type": "Album" },
                  "cover-art-archive": { "artwork": true }
                },
                {
                  "id": "ballads-release",
                  "title": "90's Rock Ballads",
                  "status": "Official",
                  "date": "2003-05-05",
                  "release-group": { "id": "ballads-group", "primary-type": "Album" },
                  "cover-art-archive": { "artwork": true }
                }
              ]
            }
          ]
        }
        """;

    private const string UninvitedOnVh1Only = """
        {
          "recordings": [
            {
              "id": "recording-uninvited",
              "title": "Uninvited",
              "artist-credit": [{ "name": "Alanis Morissette" }],
              "releases": [
                {
                  "id": "vh1-release",
                  "title": "VH1 Storytellers",
                  "status": "Official",
                  "date": "1998-01-01",
                  "release-group": { "id": "vh1-group", "primary-type": "Album" },
                  "cover-art-archive": { "artwork": true }
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task FetchAsync_PrefersReleaseMatchingAlbumTagOverEarliestOfficialRelease()
    {
        var claims = await FetchAsync(UninvitedRecording, album: "90's Rock Ballads");

        Assert.Equal("90's Rock Ballads", ClaimValue(claims, MetadataFieldConstants.Album));
        Assert.Equal("ballads-release", ClaimValue(claims, BridgeIdKeys.MusicBrainzReleaseId));
        Assert.Equal("ballads-group", ClaimValue(claims, BridgeIdKeys.MusicBrainzReleaseGroupId));
        Assert.Equal(
            "https://coverartarchive.org/release/ballads-release/front-500",
            ClaimValue(claims, MetadataFieldConstants.CoverUrl));
        Assert.Equal("2003", ClaimValue(claims, MetadataFieldConstants.Year));
        Assert.Equal("recording-uninvited", ClaimValue(claims, BridgeIdKeys.MusicBrainzRecordingId));
    }

    [Fact]
    public async Task FetchAsync_KeepsTaggedAlbumWhenNoReleaseOfTheRecordingMatches()
    {
        var logger = new CapturingLogger<ConfigDrivenAdapter>();
        var entityId = Guid.NewGuid();

        var claims = await FetchAsync(
            UninvitedOnVh1Only,
            album: "City of Angels (Music from the Motion Picture)",
            entityId: entityId,
            logger: logger);

        // Recording-level claims still apply to the track.
        Assert.Equal("Uninvited", ClaimValue(claims, MetadataFieldConstants.Title));
        Assert.Equal("recording-uninvited", ClaimValue(claims, BridgeIdKeys.MusicBrainzRecordingId));
        Assert.Equal("Alanis Morissette", ClaimValue(claims, MetadataFieldConstants.Artist));

        // Nothing album-level may reach the parent album Work.
        Assert.DoesNotContain(claims, claim => claim.Key == MetadataFieldConstants.Album);
        Assert.DoesNotContain(claims, claim => claim.Key == MetadataFieldConstants.Year);
        Assert.DoesNotContain(claims, claim => claim.Key == MetadataFieldConstants.CoverUrl);
        Assert.DoesNotContain(claims, claim => claim.Key == MetadataFieldConstants.Cover);
        Assert.DoesNotContain(claims, claim => claim.Key == BridgeIdKeys.MusicBrainzReleaseId);
        Assert.DoesNotContain(claims, claim => claim.Key == BridgeIdKeys.MusicBrainzReleaseGroupId);

        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Information
            && entry.Message == $"Kept tagged album 'City of Angels (Music from the Motion Picture)' for {entityId}; no MusicBrainz release of the recording matches");
    }

    private const string JaggedLittlePillReleases = """
        {
          "recordings": [
            {
              "id": "recording-uninvited",
              "title": "Uninvited",
              "artist-credit": [{ "name": "Alanis Morissette" }],
              "releases": [
                {
                  "id": "jlp-original",
                  "title": "Jagged Little Pill",
                  "status": "Official",
                  "date": "1995-06-13",
                  "release-group": { "id": "jlp-group", "primary-type": "Album" },
                  "cover-art-archive": { "artwork": true }
                },
                {
                  "id": "jlp-collectors",
                  "title": "Jagged Little Pill",
                  "disambiguation": "collector's edition",
                  "status": "Official",
                  "date": "2015-10-30",
                  "release-group": { "id": "jlp-group", "primary-type": "Album" },
                  "cover-art-archive": { "artwork": true }
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task FetchAsync_EditionQualifiedTag_PrefersReleaseWhoseDisambiguationMatches_AndKeepsReleaseClaims()
    {
        var claims = await FetchAsync(JaggedLittlePillReleases, album: "Jagged Little Pill (Collector's Edition)");

        // The release data describes this album: id, group, cover and year all survive.
        Assert.Equal("jlp-collectors", ClaimValue(claims, BridgeIdKeys.MusicBrainzReleaseId));
        Assert.Equal("jlp-group", ClaimValue(claims, BridgeIdKeys.MusicBrainzReleaseGroupId));
        Assert.Equal(
            "https://coverartarchive.org/release/jlp-collectors/front-500",
            ClaimValue(claims, MetadataFieldConstants.CoverUrl));
        Assert.Equal("2015", ClaimValue(claims, MetadataFieldConstants.Year));
        Assert.Equal("recording-uninvited", ClaimValue(claims, BridgeIdKeys.MusicBrainzRecordingId));

        // The tag carries a label the release title lacks, so only the title claim is dropped.
        Assert.DoesNotContain(claims, claim => claim.Key == MetadataFieldConstants.Album);
    }

    [Fact]
    public async Task FetchAsync_EditionQualifiedTag_WithoutDisambiguation_StillKeepsReleaseClaims()
    {
        const string singleRelease = """
            {
              "recordings": [
                {
                  "id": "recording-uninvited",
                  "title": "Uninvited",
                  "artist-credit": [{ "name": "Alanis Morissette" }],
                  "releases": [
                    {
                      "id": "jlp-original",
                      "title": "Jagged Little Pill",
                      "status": "Official",
                      "date": "1995-06-13",
                      "release-group": { "id": "jlp-group", "primary-type": "Album" },
                      "cover-art-archive": { "artwork": true }
                    }
                  ]
                }
              ]
            }
            """;

        var claims = await FetchAsync(singleRelease, album: "Jagged Little Pill (Collector's Edition)");

        Assert.Equal("jlp-original", ClaimValue(claims, BridgeIdKeys.MusicBrainzReleaseId));
        Assert.Equal("jlp-group", ClaimValue(claims, BridgeIdKeys.MusicBrainzReleaseGroupId));
        Assert.Equal("1995", ClaimValue(claims, MetadataFieldConstants.Year));
        Assert.NotNull(ClaimValue(claims, MetadataFieldConstants.CoverUrl));
        Assert.DoesNotContain(claims, claim => claim.Key == MetadataFieldConstants.Album);
    }

    [Fact]
    public async Task FetchAsync_UnqualifiedTagMatchingReleaseTitle_KeepsProviderAlbumClaim()
    {
        var claims = await FetchAsync(JaggedLittlePillReleases, album: "Jagged Little Pill");

        Assert.Equal("Jagged Little Pill", ClaimValue(claims, MetadataFieldConstants.Album));
        Assert.NotNull(ClaimValue(claims, BridgeIdKeys.MusicBrainzReleaseId));
    }

    // Proof run: MusicBrainz returns several recordings that share one title. The first is often a
    // duplicate recording whose only release is elsewhere (a compilation, a live album, another
    // album), while a later recording sits on the tagged album. Selection used to take the first
    // recording, so the tagged album found no matching release and lost its release link.
    private static string RecordingsJson(params (string Id, string Title, string Artist, string[] Releases)[] recordings)
        => "{ \"recordings\": ["
           + string.Join(",", recordings.Select(recording =>
               $$"""
               { "id": "{{recording.Id}}", "title": "{{recording.Title}}",
                 "artist-credit": [{ "name": "{{recording.Artist}}" }],
                 "releases": [{{string.Join(",", recording.Releases)}}] }
               """))
           + "] }";

    private static string Release(
        string id, string title, string group, string? disambiguation = null, string date = "2005-08-16",
        string primaryType = "Album", string? secondaryType = null)
        => $$"""
           { "id": "{{id}}", "title": "{{title}}",
             {{(disambiguation is null ? string.Empty : $"\"disambiguation\": \"{disambiguation}\",")}}
             "status": "Official", "date": "{{date}}",
             "release-group": { "id": "{{group}}", "primary-type": "{{primaryType}}"{{(secondaryType is null ? string.Empty : $", \"secondary-types\": [\"{secondaryType}\"]")}} },
             "cover-art-archive": { "artwork": true } }
           """;

    [Fact]
    public async Task FetchAsync_BonusTrackVersionTag_PicksTheRecordingOnTheTaggedAlbumAndKeepsReleaseLink()
    {
        var json = RecordingsJson(
            ("rec-other-album", "Wasteland", "10 Years", [Release("killing-release", "Killing All That Holds You", "killing-group", date: "2004-03-22")]),
            ("rec-autumn", "Wasteland", "10 Years",
            [
                Release("autumn-release", "The Autumn Effect", "autumn-group"),
                Release("autumn-promo", "The Autumn Effect", "autumn-group", disambiguation: "BMG club edition"),
            ]));

        var claims = await FetchAsync(json, "The Autumn Effect (Bonus Track Version)", title: "Wasteland", artist: "10 Years");

        Assert.Equal("rec-autumn", ClaimValue(claims, BridgeIdKeys.MusicBrainzRecordingId));
        Assert.Equal("autumn-release", ClaimValue(claims, BridgeIdKeys.MusicBrainzReleaseId));
        Assert.Equal("autumn-group", ClaimValue(claims, BridgeIdKeys.MusicBrainzReleaseGroupId));
        Assert.Equal("2005", ClaimValue(claims, MetadataFieldConstants.Year));
        // The tagged edition label is not lost: the release title claim is withheld.
        Assert.DoesNotContain(claims, claim => claim.Key == MetadataFieldConstants.Album);
    }

    [Fact]
    public async Task FetchAsync_DeluxeTag_PicksTheRecordingOnTheTaggedAlbumNotACompilationDuplicate()
    {
        var json = RecordingsJson(
            ("rec-so-fresh", "Youngblood", "5 Seconds of Summer", [Release("so-fresh-release", "So Fresh: Best Ever", "so-fresh-group", date: "2020", secondaryType: "Compilation")]),
            ("rec-album", "Youngblood", "5 Seconds of Summer",
            [
                Release("youngblood-standard", "Youngblood", "youngblood-group", date: "2018-06-15"),
                Release("youngblood-deluxe", "Youngblood", "youngblood-group", disambiguation: "deluxe edition", date: "2018-06-15"),
            ]));

        var claims = await FetchAsync(json, "Youngblood (Deluxe)", title: "Youngblood", artist: "5 Seconds of Summer");

        Assert.Equal("rec-album", ClaimValue(claims, BridgeIdKeys.MusicBrainzRecordingId));
        Assert.Equal("youngblood-deluxe", ClaimValue(claims, BridgeIdKeys.MusicBrainzReleaseId));
        Assert.Equal("youngblood-group", ClaimValue(claims, BridgeIdKeys.MusicBrainzReleaseGroupId));
        Assert.DoesNotContain(claims, claim => claim.Key == MetadataFieldConstants.Album);
    }

    [Fact]
    public async Task FetchAsync_RemasterTag_PicksTheStudioRecordingOverALiveAlbumDuplicate_AndKeepsTaggedTitle()
    {
        var json = RecordingsJson(
            ("rec-live", "Hand in My Pocket", "Alanis Morissette", [Release("jlp-live", "Jagged Little Pill, Live", "jlp-live-group", date: "1997", secondaryType: "Live")]),
            ("rec-studio", "Hand in My Pocket", "Alanis Morissette", [Release("jlp-release", "Jagged Little Pill", "jlp-group", date: "1995-06-13")]));

        var claims = await FetchAsync(json, "Jagged Little Pill (2015 Remaster)", title: "Hand in My Pocket");

        Assert.Equal("rec-studio", ClaimValue(claims, BridgeIdKeys.MusicBrainzRecordingId));
        Assert.Equal("jlp-release", ClaimValue(claims, BridgeIdKeys.MusicBrainzReleaseId));
        Assert.Equal("jlp-group", ClaimValue(claims, BridgeIdKeys.MusicBrainzReleaseGroupId));
        // The strong (remaster-insensitive) match must not rename the album to "Jagged Little Pill".
        Assert.DoesNotContain(claims, claim => claim.Key == MetadataFieldConstants.Album);
    }

    [Fact]
    public async Task FetchAsync_LiveAlbumIsNotTheSameAlbumAsTheStudioAlbum()
    {
        var json = RecordingsJson(
            ("rec-live", "Hand in My Pocket", "Alanis Morissette", [Release("jlp-live", "Jagged Little Pill, Live", "jlp-live-group", date: "1997", secondaryType: "Live")]));

        var claims = await FetchAsync(json, "Jagged Little Pill (2015 Remaster)", title: "Hand in My Pocket");

        Assert.DoesNotContain(claims, claim => claim.Key == BridgeIdKeys.MusicBrainzReleaseId);
        Assert.DoesNotContain(claims, claim => claim.Key == BridgeIdKeys.MusicBrainzReleaseGroupId);
        Assert.DoesNotContain(claims, claim => claim.Key == MetadataFieldConstants.Album);
    }

    [Theory]
    [InlineData("Jagged Little Pill (2015 Remaster)", "Jagged Little Pill")]
    [InlineData("Youngblood (Deluxe)", "Youngblood")]
    [InlineData("The Autumn Effect (Bonus Track Version)", "The Autumn Effect")]
    [InlineData("Jagged Little Pill (25th Anniversary Deluxe Edition)", "Jagged Little Pill")]
    public void IsSameBaseAlbum_ProofRunEditionQualifiedAlbumsMatchTheirUnqualifiedRelease(string tagged, string release)
    {
        Assert.True(MediaEngine.Providers.Services.MusicAlbumIdentity.IsSameBaseAlbum(tagged, release));
        Assert.NotEqual(string.Empty, MediaEngine.Providers.Services.MusicAlbumIdentity.EditionLabel(tagged));
    }

    [Theory]
    [InlineData("Jagged Little Pill (2015 Remaster)", "Jagged Little Pill, Live")]
    [InlineData("Youngblood (Deluxe)", "So Fresh: Best Ever")]
    [InlineData("The Autumn Effect (Bonus Track Version)", "Killing All That Holds You")]
    public void IsSameBaseAlbum_ProofRunDifferentAlbumsStayDifferent(string tagged, string release)
    {
        Assert.False(MediaEngine.Providers.Services.MusicAlbumIdentity.IsSameBaseAlbum(tagged, release));
    }

    [Theory]
    [InlineData("Jagged Little Pill (Collector's Edition)", "Jagged Little Pill", true)]
    [InlineData("Jagged Little Pill (Deluxe Edition)", "Jagged Little Pill", true)]
    [InlineData("Jagged Little Pill - Expanded Edition", "Jagged Little Pill", true)]
    [InlineData("Jagged Little Pill [Remastered 2015]", "Jagged Little Pill", true)]
    [InlineData("Jagged Little Pill (Special Edition) [Explicit]", "Jagged Little Pill", true)]
    [InlineData("Jagged Little Pill (Bonus Track Version)", "Jagged Little Pill (Clean)", true)]
    [InlineData("Jagged Little Pill Deluxe Edition", "Jagged Little Pill", true)]
    [InlineData("90's Rock Ballads", "VH1 Storytellers", false)]
    [InlineData("Jagged Little Pill", "Supposed Former Infatuation Junkie", false)]
    [InlineData("Greatest Hits (Deluxe)", "Greatest Hits Live", false)]
    public void IsSameBaseAlbum_IgnoresEditionLabelsOnly(string tagged, string release, bool expected)
    {
        Assert.Equal(expected, MediaEngine.Providers.Services.MusicAlbumIdentity.IsSameBaseAlbum(tagged, release));
    }

    [Fact]
    public void EditionLabel_IsCoveredByAMatchingDisambiguation()
    {
        var label = MediaEngine.Providers.Services.MusicAlbumIdentity.EditionLabel("Jagged Little Pill (Collector's Edition)");

        Assert.Equal("collectors edition", label);
        Assert.True(MediaEngine.Providers.Services.MusicAlbumIdentity.LabelIsCoveredBy(label, "collector's edition"));
        Assert.False(MediaEngine.Providers.Services.MusicAlbumIdentity.LabelIsCoveredBy(label, "remastered"));
        Assert.False(MediaEngine.Providers.Services.MusicAlbumIdentity.LabelIsCoveredBy(label, null));
        Assert.Equal(string.Empty, MediaEngine.Providers.Services.MusicAlbumIdentity.EditionLabel("Jagged Little Pill"));
    }

    [Fact]
    public async Task FetchAsync_WithoutAlbumTag_KeepsEarliestOfficialRelease()
    {
        var claims = await FetchAsync(UninvitedRecording, album: null);

        Assert.Equal("VH1 Storytellers", ClaimValue(claims, MetadataFieldConstants.Album));
        Assert.Equal("vh1-release", ClaimValue(claims, BridgeIdKeys.MusicBrainzReleaseId));
        Assert.Equal("vh1-group", ClaimValue(claims, BridgeIdKeys.MusicBrainzReleaseGroupId));
        Assert.Equal("1998", ClaimValue(claims, MetadataFieldConstants.Year));
    }

    [Fact]
    public async Task FetchAsync_PrefersAlbumTagReleaseOnRecordingLookupToo()
    {
        var config = LoadMusicBrainzConfig();
        var factory = BuildFactory(config.Name, new RoutingStubHttpMessageHandler(_ => JsonResponse("""
            {
              "id": "recording-uninvited",
              "title": "Uninvited",
              "artist-credit": [{ "name": "Alanis Morissette" }],
              "releases": [
                { "id": "vh1-release", "title": "VH1 Storytellers", "status": "Official", "date": "1998-01-01",
                  "release-group": { "id": "vh1-group", "primary-type": "Album" } },
                { "id": "ballads-release", "title": "90's Rock Ballads", "status": "Official", "date": "2003-05-05",
                  "release-group": { "id": "ballads-group", "primary-type": "Album" } }
              ]
            }
            """)));
        var adapter = new ConfigDrivenAdapter(
            config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        var claims = await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Music,
            Title = "Uninvited",
            Artist = "Alanis Morissette",
            Album = "90's Rock Ballads",
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BridgeIdKeys.MusicBrainzRecordingId] = "recording-uninvited",
            },
            BaseUrl = "https://musicbrainz.org/ws/2",
        });

        Assert.Equal("ballads-release", ClaimValue(claims, BridgeIdKeys.MusicBrainzReleaseId));
        Assert.Equal("90's Rock Ballads", ClaimValue(claims, MetadataFieldConstants.Album));
    }

    /// <summary>
    /// Runs the real MusicBrainz strategy chain. Searches that name a release (the
    /// album-filtered strategies) find nothing, so selection reaches the artist search, which
    /// carries no album filter and used to take the earliest official release regardless of tag.
    /// </summary>
    private static async Task<IReadOnlyList<ProviderClaim>> FetchAsync(
        string recordingSearchJson,
        string? album,
        Guid? entityId = null,
        ILogger<ConfigDrivenAdapter>? logger = null,
        string title = "Uninvited",
        string artist = "Alanis Morissette")
    {
        var config = LoadMusicBrainzConfig();
        var factory = BuildFactory(
            config.Name,
            new RoutingStubHttpMessageHandler(request =>
            {
                var decodedUrl = Uri.UnescapeDataString(request.RequestUri?.ToString() ?? string.Empty);
                return decodedUrl.Contains("release:", StringComparison.Ordinal)
                    ? JsonResponse("""{ "recordings": [] }""")
                    : JsonResponse(recordingSearchJson);
            }));
        var adapter = new ConfigDrivenAdapter(
            config, factory, logger ?? NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);

        return await adapter.FetchAsync(new ProviderLookupRequest
        {
            EntityId = entityId ?? Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Music,
            Title = title,
            Artist = artist,
            Album = album,
            BaseUrl = "https://musicbrainz.org/ws/2",
            Country = "us",
            Language = "en",
        });
    }

    private static string? ClaimValue(IReadOnlyList<ProviderClaim> claims, string key)
        => claims.FirstOrDefault(claim => claim.Key == key)?.Value;

    private static ProviderConfiguration LoadMusicBrainzConfig()
    {
        var path = Path.Combine(FindRepoRoot(), "config", "providers", "musicbrainz.json");
        return JsonSerializer.Deserialize<ProviderConfiguration>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                })
            ?? throw new InvalidOperationException("Failed to deserialize config: musicbrainz");
    }

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(MusicBrainzTaggedAlbumTests).Assembly.Location);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Could not find repository root (MediaEngine.slnx).");
    }

    private static IHttpClientFactory BuildFactory(string clientName, HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(clientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class RoutingStubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
