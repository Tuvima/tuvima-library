using System.Net;
using System.Text.Json;
using MediaEngine.Plugin.ListenBrainz;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The ListenBrainz sharing plugin (Plugin architecture spec PA6): a finished play is queued only for a profile that linked its own
/// token, only when it counts as a listen, and only when the Library names the track (it does not for the Vault or for anything that is
/// not music). Delivery uses the profile's own token and tells the retry queue what to do with each answer.
/// </summary>
public sealed class ListenBrainzPluginTests
{
    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid Asset = Guid.NewGuid();
    private static readonly DateTimeOffset Finished = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeContext _context = new();

    [Fact]
    public async Task AFinishedPlay_ForALinkedProfile_IsQueuedForThatProfile()
    {
        _context.Linked.Add(Ana);
        _context.Track = Song();

        await Subscriber().HandleAsync(Play(Ana, played: 200, duration: 200), _context, CancellationToken.None);

        var (profile, json) = Assert.Single(_context.Queued);
        Assert.Equal(Ana, profile);
        using var listen = JsonDocument.Parse(json);
        var meta = listen.RootElement.GetProperty("track_metadata");
        Assert.Equal("Artist", meta.GetProperty("artist_name").GetString());
        Assert.Equal("Song", meta.GetProperty("track_name").GetString());
        Assert.Equal("Album", meta.GetProperty("release_name").GetString());
        Assert.Equal("rec-1", meta.GetProperty("additional_info").GetProperty("recording_mbid").GetString());
        Assert.Equal(200_000, meta.GetProperty("additional_info").GetProperty("duration_ms").GetInt64());
        Assert.Equal(Finished.AddSeconds(-200).ToUnixTimeSeconds(), listen.RootElement.GetProperty("listened_at").GetInt64());
    }

    [Fact]
    public async Task AFinishedPlay_ForAProfileThatHasNotLinked_QueuesNothing()
    {
        _context.Track = Song();

        await Subscriber().HandleAsync(Play(Ana, played: 200, duration: 200), _context, CancellationToken.None);

        Assert.Empty(_context.Queued);
        Assert.Equal(0, _context.TrackLookups);
    }

    [Fact]
    public async Task AFinishedPlay_TheLibraryWillNotName_QueuesNothing()
    {
        _context.Linked.Add(Ana);

        await Subscriber().HandleAsync(Play(Ana, played: 200, duration: 200), _context, CancellationToken.None);

        Assert.Empty(_context.Queued);
        Assert.Equal(1, _context.TrackLookups);
    }

    [Theory]
    [InlineData(10.0, 200.0, false)]
    [InlineData(99.0, 200.0, false)]
    [InlineData(100.0, 200.0, true)]
    [InlineData(240.0, 1000.0, true)]
    [InlineData(239.0, 1000.0, false)]
    [InlineData(20.0, 25.0, false)]
    [InlineData(250.0, null, true)]
    [InlineData(100.0, null, false)]
    public void ThePlayThreshold_FollowsListenBrainzsRule(double played, double? duration, bool counts) =>
        Assert.Equal(counts, ListenBrainzFinishedPlays.CountsAsListen(played, duration));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"subject":{"profile_id":"00000000-0000-0000-0000-000000000000"},"payload":{"asset_id":"x"}}""")]
    public async Task AnEnvelopeThatCannotBeRead_IsIgnored(string payload)
    {
        _context.Linked.Add(Ana);
        _context.Track = Song();

        await Subscriber().HandleAsync(new PluginEvent("playback.completed", "e1", 1, Finished, payload), _context, CancellationToken.None);

        Assert.Empty(_context.Queued);
    }

    [Fact]
    public async Task Delivery_SendsTheProfilesOwnToken_AndTheListen()
    {
        _context.Linked.Add(Ana);
        _context.Track = Song();
        await Subscriber().HandleAsync(Play(Ana, played: 200, duration: 200), _context, CancellationToken.None);
        _context.Answer = HttpStatusCode.OK;

        var result = await Delivery().DeliverAsync(Item(_context.Queued[0].Json, "secret-token"), _context, CancellationToken.None);

        Assert.Equal(PluginDeliveryOutcome.Delivered, result.Outcome);
        var request = Assert.Single(_context.Sent);
        Assert.Equal(ListenBrainzDelivery.SubmitListens, request.Uri);
        Assert.Equal("Token secret-token", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("single", body.RootElement.GetProperty("listen_type").GetString());
        Assert.Equal("Song", body.RootElement.GetProperty("payload")[0].GetProperty("track_metadata").GetProperty("track_name").GetString());
    }

    [Fact]
    public async Task Delivery_WithoutALinkedToken_GivesUpWithoutCallingOut()
    {
        var result = await Delivery().DeliverAsync(Item("{}", null), _context, CancellationToken.None);

        Assert.Equal(PluginDeliveryOutcome.GiveUp, result.Outcome);
        Assert.Empty(_context.Sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, PluginDeliveryOutcome.Delivered)]
    [InlineData(HttpStatusCode.TooManyRequests, PluginDeliveryOutcome.RetryLater)]
    [InlineData(HttpStatusCode.InternalServerError, PluginDeliveryOutcome.RetryLater)]
    [InlineData(HttpStatusCode.BadGateway, PluginDeliveryOutcome.RetryLater)]
    [InlineData(HttpStatusCode.Unauthorized, PluginDeliveryOutcome.GiveUp)]
    [InlineData(HttpStatusCode.BadRequest, PluginDeliveryOutcome.GiveUp)]
    public void Delivery_TurnsEachAnswerIntoWhatTheQueueShouldDo(HttpStatusCode answer, PluginDeliveryOutcome expected) =>
        Assert.Equal(expected, ListenBrainzDelivery.Classify(answer).Outcome);

    [Fact]
    public async Task Delivery_WhenOffline_AsksToTryAgainLater()
    {
        _context.Offline = true;

        var result = await Delivery().DeliverAsync(Item("{}", "t"), _context, CancellationToken.None);

        Assert.Equal(PluginDeliveryOutcome.RetryLater, result.Outcome);
    }

    [Fact]
    public async Task Delivery_NeverPutsTheTokenInItsReasons()
    {
        _context.Offline = true;

        var offline = await Delivery().DeliverAsync(Item("{}", "secret-token"), _context, CancellationToken.None);
        var refused = ListenBrainzDelivery.Classify(HttpStatusCode.Unauthorized);

        Assert.DoesNotContain("secret-token", offline.Reason);
        Assert.DoesNotContain("secret-token", refused.Reason);
    }

    [Fact]
    public void TheManifest_AsksForTheSmallestSetOfPermissions_AndKeepsTheTokenPersonal()
    {
        var manifest = new ListenBrainzPlugin().Manifest;

        Assert.Equal(
            new[] { "network.http", "play-details", "plugin-outbox", "secrets.plugin", "watch-activity" },
            manifest.Permissions.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("personal", manifest.Declaration!.CredentialScopes["user_token"]);
        Assert.Equal("never", manifest.Declaration.Vault);
        Assert.Equal(ListenBrainzPlugin.Host, Assert.Single(manifest.Network).Host);
    }

    private static ListenBrainzFinishedPlays Subscriber() => new();

    private static ListenBrainzDelivery Delivery() => new();

    private static PluginPlayedTrack Song() => new("Song", "Artist", "Album", 200, "rec-1", "rel-1", ["art-1"], 3);

    private static PluginEvent Play(Guid profile, double played, double duration)
    {
        var envelope = new
        {
            event_id = "e1",
            event_type = "playback.completed",
            version = 1,
            occurred_at = Finished,
            subject = new { type = "profile", id = profile, profile_id = profile },
            payload = new { asset_id = Asset, played_duration_seconds = played, duration_seconds = duration },
        };
        return new PluginEvent("playback.completed", "e1", 1, Finished, JsonSerializer.Serialize(envelope));
    }

    private static PluginOutboxItem Item(string payload, string? token) =>
        new(Guid.NewGuid(), Ana, payload, Finished, 0, (_, _) => Task.FromResult(token));

    private sealed record SentRequest(Uri? Uri, string? Authorization, string Body);

    private sealed class FakeContext : IPluginExecutionContext, IPluginOutbox, IPluginPlayDetails, IPluginProfileLinks, IPluginHttpClient
    {
        public HashSet<Guid> Linked { get; } = [];

        public PluginPlayedTrack? Track { get; set; }

        public int TrackLookups { get; private set; }

        public List<(Guid Profile, string Json)> Queued { get; } = [];

        public List<SentRequest> Sent { get; } = [];

        public HttpStatusCode Answer { get; set; } = HttpStatusCode.OK;

        public bool Offline { get; set; }

        public string PluginId => ListenBrainzPlugin.PluginId;

        public IReadOnlyDictionary<string, JsonElement> Settings => new Dictionary<string, JsonElement>();

        public IPluginMediaAccess Media => throw new NotSupportedException();

        public IPluginHttpClient Http => this;

        public IPluginStorage Storage => throw new NotSupportedException();

        public IPluginToolRuntime Tools => throw new NotSupportedException();

        public IPluginAiClient Ai => throw new NotSupportedException();

        public IPluginSecrets Secrets => throw new NotSupportedException();

        public IPluginRecordStore Records => throw new NotSupportedException();

        public IPluginOutbox Outbox => this;

        public IPluginPlayDetails PlayDetails => this;

        public IPluginProfileLinks ProfileLinks => this;

        public Task EnqueueAsync(Guid profileId, string payloadJson, CancellationToken cancellationToken = default)
        {
            Queued.Add((profileId, payloadJson));
            return Task.CompletedTask;
        }

        public Task<PluginPlayedTrack?> GetTrackAsync(Guid assetId, CancellationToken cancellationToken = default)
        {
            TrackLookups++;
            return Task.FromResult(Track);
        }

        public Task<bool> IsLinkedAsync(Guid profileId, string credentialName, CancellationToken cancellationToken = default) =>
            Task.FromResult(credentialName == ListenBrainzPlugin.TokenName && Linked.Contains(profileId));

        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
            CancellationToken cancellationToken = default)
        {
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            Sent.Add(new SentRequest(request.RequestUri, request.Headers.Authorization?.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Answer);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
