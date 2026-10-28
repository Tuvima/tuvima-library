using System.Text.RegularExpressions;

namespace MediaEngine.Plugins;

// Host services a plugin can use beyond its own capabilities: its own records, recurring jobs, Library events and
// always-on background work. Each sits behind its own declared permission (see PluginPermissionIds) and is bounded by
// limits the Library enforces; none of these types references the Library's storage or domain.

/// <summary>One record a plugin keeps in the Library's data store.</summary>
/// <param name="Collection">The plugin's own grouping name, for example <c>watched-titles</c>.</param>
/// <param name="Key">The record's key inside the collection.</param>
/// <param name="Json">The record's value: a JSON document written by the plugin.</param>
/// <param name="UpdatedAt">When the record was last written (UTC).</param>
public sealed record PluginRecord(string Collection, string Key, string Json, DateTimeOffset UpdatedAt);

/// <summary>
/// A plugin's own records. Always scoped to the calling plugin: there is no way to name another plugin, and one plugin
/// can never read another's records. Needs the <c>plugin-records</c> permission. Names are 1 to 128 printable characters.
/// </summary>
public interface IPluginRecordStore
{
    /// <summary>The record, or null when there is none.</summary>
    Task<PluginRecord?> GetAsync(string collection, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a record, replacing any record with the same collection and key. <paramref name="json"/> must be valid JSON.
    /// Throws <see cref="PluginLimitExceededException"/> when the value, the record count or the plugin's total space
    /// is over its limit.
    /// </summary>
    Task PutAsync(string collection, string key, string json, CancellationToken cancellationToken = default);

    /// <summary>Removes a record. Returns false when there was none.</summary>
    Task<bool> DeleteAsync(string collection, string key, CancellationToken cancellationToken = default);

    /// <summary>Records in a collection ordered by key, optionally only those whose key starts with <paramref name="keyPrefix"/>. At most <paramref name="limit"/> (1 to 1,000).</summary>
    Task<IReadOnlyList<PluginRecord>> ListAsync(
        string collection,
        string? keyPrefix = null,
        int limit = 100,
        CancellationToken cancellationToken = default);
}

/// <summary>A plugin asked for more than the Library allows it. <see cref="Exception.Message"/> is plain English.</summary>
public sealed class PluginLimitExceededException(string message) : Exception(message);

/// <summary>
/// When a recurring job runs: every so often (an interval of at least five minutes) or on a five-field cron schedule
/// evaluated in the machine's local time zone. The Library checks a cron text when the plugin loads.
/// </summary>
public sealed partial record PluginSchedule
{
    /// <summary>The shortest interval a plugin job may use.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

    private PluginSchedule(TimeSpan? interval, string? cron)
    {
        Interval = interval;
        Cron = cron;
    }

    /// <summary>Set for an interval schedule.</summary>
    public TimeSpan? Interval { get; }

    /// <summary>Set for a cron schedule.</summary>
    public string? Cron { get; }

    /// <summary>Runs every <paramref name="interval"/>, which must be at least <see cref="MinimumInterval"/>.</summary>
    public static PluginSchedule Every(TimeSpan interval)
    {
        if (interval < MinimumInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "A plugin job cannot run more often than every 5 minutes.");
        }

        return new PluginSchedule(interval, null);
    }

    /// <summary>Runs on a five-field cron schedule such as <c>0 3 * * *</c>.</summary>
    public static PluginSchedule OnCron(string cron)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cron);
        return new PluginSchedule(null, cron.Trim());
    }

    /// <summary>
    /// Reads schedule text: a cron expression, or an interval such as <c>30m</c>, <c>2h</c>, <c>1d</c> or
    /// <c>every 30 minutes</c>. Returns false for an interval under five minutes. A cron text is only checked for its
    /// shape here; the Library checks it fully before running anything.
    /// </summary>
    public static bool TryParse(string? text, out PluginSchedule? schedule)
    {
        schedule = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = IntervalPattern().Match(text.Trim());
        if (match.Success)
        {
            if (!int.TryParse(match.Groups["n"].ValueSpan, out var count) || count <= 0)
            {
                return false;
            }

            var unit = char.ToLowerInvariant(match.Groups["unit"].Value[0]);
            var interval = unit switch
            {
                'm' => TimeSpan.FromMinutes(count),
                'h' => TimeSpan.FromHours(count),
                _ => TimeSpan.FromDays(count),
            };
            if (interval < MinimumInterval || interval > TimeSpan.FromDays(366))
            {
                return false;
            }

            schedule = new PluginSchedule(interval, null);
            return true;
        }

        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
        {
            return false;
        }

        schedule = new PluginSchedule(null, string.Join(' ', fields));
        return true;
    }

    [GeneratedRegex(@"^(?:every\s+)?(?<n>\d+)\s*(?<unit>m(?:in(?:ute)?s?)?|h(?:(?:ou)?rs?)?|d(?:ays?)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntervalPattern();
}

/// <summary>
/// A recurring job a plugin runs. Needs the <c>scheduled-work</c> permission. Each run is a run record listed in
/// Scheduled tasks. A job never overlaps itself, and a run longer than ten minutes is cancelled and recorded as failed.
/// </summary>
public interface IPluginScheduledJob : IPluginCapability
{
    /// <summary>A short stable name for the job: lower-case letters, digits, hyphens and underscores (1 to 64 characters).</summary>
    string Key { get; }

    /// <summary>The name shown in Scheduled tasks after the plugin's name, for example "Refresh the reading list".</summary>
    string DisplayName { get; }

    /// <summary>When the job runs unless the administrator overrides it in the plugin's configuration file.</summary>
    PluginSchedule DefaultSchedule { get; }

    Task RunAsync(IPluginExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// A Library event delivered to a plugin. <paramref name="PayloadJson"/> is the same envelope JSON that webhooks
/// receive (event id, type, version, time, server, subject and payload).
/// </summary>
/// <param name="Type">The event type, for example <c>library.item_added</c>.</param>
/// <param name="EventId">The event's unique id.</param>
/// <param name="Sequence">The event's position in the Library's event list; always rising.</param>
/// <param name="OccurredAt">When it happened (UTC).</param>
/// <param name="PayloadJson">The webhook envelope JSON for this event.</param>
public sealed record PluginEvent(string Type, string EventId, long Sequence, DateTimeOffset OccurredAt, string PayloadJson);

/// <summary>
/// A plugin that is told when things happen in the Library. Needs <c>library-events</c>, plus <c>watch-activity</c> for the
/// <c>playback.*</c> events. A type the Library does not know is a load error. Each event is tried once with a 30-second
/// limit; five failures in a row pause the subscription.
/// </summary>
public interface IPluginEventSubscriber : IPluginCapability
{
    /// <summary>The event types to receive, for example <c>library.item_added</c>.</summary>
    IReadOnlyList<string> EventTypes { get; }

    Task HandleAsync(PluginEvent evt, IPluginExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Always-on work a plugin runs while it is switched on and approved. Needs <c>background-work</c>. The Library starts it,
/// cancels the token to stop it (then waits ten seconds), and restarts it with a growing pause if it crashes or returns.
/// The context stays usable for the whole run.
/// </summary>
public interface IPluginBackgroundService : IPluginCapability
{
    Task RunAsync(IPluginExecutionContext context, CancellationToken stoppingToken);
}

/// <summary>
/// The retry queue for a plugin that sends things to an outside service on a person's behalf (for example scrobbling a finished play).
/// Queueing never waits on the service and never fails because it is offline; the Library delivers each item through the plugin's
/// <see cref="IPluginOutboxDelivery"/> and tries again with a growing pause. Items belong to one profile and go if that profile
/// unlinks its account, is removed, or the plugin is removed. Needs <c>plugin-outbox</c>.
/// </summary>
public interface IPluginOutbox
{
    /// <summary>Queues <paramref name="payloadJson"/> (valid JSON, at most 64 KB) for one profile.</summary>
    Task EnqueueAsync(Guid profileId, string payloadJson, CancellationToken cancellationToken = default);
}

/// <summary>One queued item handed to the plugin for delivery.</summary>
/// <param name="Id">The item's id.</param>
/// <param name="ProfileId">The profile it belongs to.</param>
/// <param name="PayloadJson">What the plugin queued.</param>
/// <param name="CreatedAt">When it was queued.</param>
/// <param name="Attempts">How many times delivery has already been tried.</param>
/// <param name="ReadCredentialAsync">Reads one of this profile's own personal credentials by name; there is no way to read another profile's.</param>
public sealed record PluginOutboxItem(
    Guid Id,
    Guid ProfileId,
    string PayloadJson,
    DateTimeOffset CreatedAt,
    int Attempts,
    Func<string, CancellationToken, Task<string?>> ReadCredentialAsync);

public enum PluginDeliveryOutcome
{
    /// <summary>Sent. The item is removed.</summary>
    Delivered,

    /// <summary>The service could not take it now. Try again later.</summary>
    RetryLater,

    /// <summary>It can never be sent (for example the account was unlinked). The item is removed.</summary>
    GiveUp,
}

/// <summary>How a delivery ended.</summary>
/// <param name="Outcome">What to do with the item.</param>
/// <param name="Reason">For a retry or a give-up: a short plain note kept in Activity. Never put keys or tokens in it.</param>
public sealed record PluginDeliveryResult(PluginDeliveryOutcome Outcome, string? Reason = null)
{
    public static PluginDeliveryResult Delivered() => new(PluginDeliveryOutcome.Delivered);

    public static PluginDeliveryResult RetryLater(string? reason = null) => new(PluginDeliveryOutcome.RetryLater, reason);

    public static PluginDeliveryResult GiveUp(string? reason = null) => new(PluginDeliveryOutcome.GiveUp, reason);
}

/// <summary>Sends one queued item. The Library calls it from the retry queue, never from the player or the browser.</summary>
public interface IPluginOutboxDelivery : IPluginCapability
{
    Task<PluginDeliveryResult> DeliverAsync(PluginOutboxItem item, IPluginExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>The Library surfaces a plugin may add a line, chip, block or badge to. The list is fixed; a plugin cannot add a surface.</summary>
public enum PluginDisplaySlot
{
    /// <summary>A short line or badge on a Home slide, such as "Intro skippable".</summary>
    HomeHero,

    /// <summary>A suggestion for the Home slide, with a reason and a strength. The Home rules still decide; it can suggest but never override.</summary>
    HomeHeroCandidate,

    /// <summary>A chip or one-line fact on an item's details hero.</summary>
    ItemHero,

    /// <summary>A titled block on an item's details page, such as Lore or Mood and themes.</summary>
    ItemSection,

    /// <summary>A small icon with a tooltip on a tile or card.</summary>
    TileBadge,

    /// <summary>A status line on the Settings overview.</summary>
    SettingsOverview,
}

/// <summary>
/// One thing a plugin wants shown. It is plain data: the Library draws it with its own components, so nothing a plugin supplies can
/// run in the browser or change the look of a page.
/// </summary>
/// <param name="Slot">Where it is shown.</param>
/// <param name="SubjectId">The item it is about (a work id), or <c>null</c> when it is not about one item.</param>
/// <param name="Key">The plugin's own name for this contribution, at most 64 characters. Publishing the same key again replaces it.</param>
/// <param name="Text">The line or body, at most 280 characters.</param>
/// <param name="Title">An optional heading (for a block), at most 80 characters.</param>
/// <param name="Icon">An optional icon name from the Library's own icon set, at most 40 characters.</param>
/// <param name="Strength">0 to 100: how strongly the plugin suggests it. Only used to order contributions in the same slot.</param>
/// <param name="FromLocalAi">True when Local AI produced the text, so the Library marks it "AI".</param>
public sealed record PluginDisplayContribution(
    PluginDisplaySlot Slot,
    string? SubjectId,
    string Key,
    string Text,
    string? Title = null,
    string? Icon = null,
    int Strength = 0,
    bool FromLocalAi = false);

/// <summary>
/// Publishes what a plugin wants shown on the Library's own surfaces (see <see cref="PluginDisplayContribution"/>). Publishing is a quick
/// local write; the Library reads the result later and never waits on the plugin. Needs <c>display-slots</c>.
/// </summary>
public interface IPluginDisplay
{
    /// <summary>Adds the contribution, or replaces the plugin's earlier one with the same slot, subject and key.</summary>
    Task PublishAsync(PluginDisplayContribution contribution, CancellationToken cancellationToken = default);

    /// <summary>Takes one of the plugin's contributions down. Does nothing when it is not there.</summary>
    Task WithdrawAsync(PluginDisplaySlot slot, string? subjectId, string key, CancellationToken cancellationToken = default);
}

/// <summary>What a person finished playing, for a plugin that shares plays with an outside service.</summary>
/// <param name="Title">The track title.</param>
/// <param name="Artist">The artist's name.</param>
/// <param name="Album">The album or release name, when known.</param>
/// <param name="DurationSeconds">The track length, when known.</param>
/// <param name="RecordingMbid">The MusicBrainz recording id, when the Library has matched it.</param>
/// <param name="ReleaseMbid">The MusicBrainz release id, when the Library has matched it.</param>
/// <param name="ArtistMbids">The MusicBrainz artist ids the Library has matched.</param>
/// <param name="TrackNumber">The track's position on the release, when known.</param>
public sealed record PluginPlayedTrack(
    string Title,
    string Artist,
    string? Album,
    double? DurationSeconds,
    string? RecordingMbid,
    string? ReleaseMbid,
    IReadOnlyList<string> ArtistMbids,
    int? TrackNumber);

/// <summary>
/// Reads the details of a track that was played (title, artist, album, MusicBrainz ids). Needs <c>play-details</c>. The Library
/// answers only for music the plugin may work on: a Vault item, a library the plugin is limited away from, or anything that is not
/// music gets no answer, so a plugin cannot share what it must not.
/// </summary>
public interface IPluginPlayDetails
{
    /// <returns>The track, or null when the plugin may not see it or the Library knows too little about it to name it.</returns>
    Task<PluginPlayedTrack?> GetTrackAsync(Guid assetId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Says whether a profile has linked one of the plugin's own personal credentials (never what it is). A plugin that shares on a
/// person's behalf asks this before it queues anything, so a profile that has not linked sends nothing and nothing waits for it.
/// </summary>
public interface IPluginProfileLinks
{
    Task<bool> IsLinkedAsync(Guid profileId, string credentialName, CancellationToken cancellationToken = default);
}
