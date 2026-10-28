using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Plugins;

namespace MediaEngine.Plugin.ListenBrainz;

/// <summary>
/// Turns a finished play into a queued listen. Nothing is queued unless the profile linked its own token, the play counts as a listen
/// by ListenBrainz's rule, and the Library names the track (it does not for a Vault item or anything that is not music).
/// </summary>
internal sealed class ListenBrainzFinishedPlays : IPluginEventSubscriber
{
    /// <summary>ListenBrainz counts a play once half of the track, or four minutes, has been heard; tracks under 30 seconds do not count.</summary>
    internal const double MinimumTrackSeconds = 30;
    internal const double FourMinutes = 240;

    public string Kind => PluginCapabilityKinds.EventSubscriberKind;

    public IReadOnlyList<string> EventTypes { get; } = ["playback.completed"];

    public async Task HandleAsync(PluginEvent evt, IPluginExecutionContext context, CancellationToken cancellationToken)
    {
        if (!TryRead(evt.PayloadJson, out var play))
        {
            return;
        }

        // A profile that has not linked sends nothing, and nothing is left waiting for it.
        if (!await context.ProfileLinks.IsLinkedAsync(play.ProfileId, ListenBrainzPlugin.TokenName, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var track = await context.PlayDetails.GetTrackAsync(play.AssetId, cancellationToken).ConfigureAwait(false);
        if (track is null || !CountsAsListen(play.PlayedSeconds, track.DurationSeconds ?? play.DurationSeconds))
        {
            return;
        }

        var listenedAt = play.OccurredAt.AddSeconds(-Math.Max(0, play.PlayedSeconds)).ToUnixTimeSeconds();
        try
        {
            await context.Outbox.EnqueueAsync(play.ProfileId, BuildListen(track, listenedAt), cancellationToken).ConfigureAwait(false);
        }
        catch (PluginLimitExceededException)
        {
            // One unusable play must not count as a failure of the subscription, which would pause sharing for everyone.
        }
    }

    internal static bool CountsAsListen(double playedSeconds, double? durationSeconds)
    {
        if (durationSeconds is { } duration and > 0)
        {
            return duration >= MinimumTrackSeconds && playedSeconds >= Math.Min(FourMinutes, duration / 2);
        }

        // Without a length, only a long listen is certain to count.
        return playedSeconds >= FourMinutes;
    }

    internal static string BuildListen(PluginPlayedTrack track, long listenedAt)
    {
        var info = new JsonObject
        {
            ["media_player"] = "Library",
            ["submission_client"] = "Library",
        };
        if (track.RecordingMbid is not null)
        {
            info["recording_mbid"] = track.RecordingMbid;
        }

        if (track.ReleaseMbid is not null)
        {
            info["release_mbid"] = track.ReleaseMbid;
        }

        if (track.ArtistMbids.Count > 0)
        {
            info["artist_mbids"] = new JsonArray(track.ArtistMbids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        }

        if (track.TrackNumber is { } number)
        {
            info["tracknumber"] = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (track.DurationSeconds is { } seconds and > 0)
        {
            info["duration_ms"] = (long)Math.Round(seconds * 1000);
        }

        var metadata = new JsonObject
        {
            ["artist_name"] = Cap(track.Artist),
            ["track_name"] = Cap(track.Title),
            ["additional_info"] = info,
        };
        if (track.Album is not null)
        {
            metadata["release_name"] = Cap(track.Album);
        }

        return new JsonObject { ["listened_at"] = listenedAt, ["track_metadata"] = metadata }.ToJsonString();
    }

    // Names longer than any real track are cut, so one odd file can never make a listen too big to queue.
    private static string Cap(string value) => value.Length <= 512 ? value : value[..512];

    private static bool TryRead(string envelopeJson, out FinishedPlay play)
    {
        play = default;
        try
        {
            using var document = JsonDocument.Parse(envelopeJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("subject", out var subject)
                || !subject.TryGetProperty("profile_id", out var profile)
                || !Guid.TryParse(profile.GetString(), out var profileId)
                || profileId == Guid.Empty
                || !root.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("asset_id", out var asset)
                || !Guid.TryParse(asset.GetString(), out var assetId)
                || !root.TryGetProperty("occurred_at", out var occurred)
                || !occurred.TryGetDateTimeOffset(out var occurredAt))
            {
                return false;
            }

            play = new FinishedPlay(
                profileId,
                assetId,
                occurredAt,
                Number(payload, "played_duration_seconds") ?? 0,
                Number(payload, "duration_seconds"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    private readonly record struct FinishedPlay(Guid ProfileId, Guid AssetId, DateTimeOffset OccurredAt, double PlayedSeconds, double? DurationSeconds);
}
