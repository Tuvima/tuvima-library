using System.Globalization;
using System.Text.Json;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.LocalAssets;

/// <summary>
/// Turns the photos and personal videos a library takes in into counts for the Activity log. A library that gains
/// thousands of photos writes one row ("3,000 photos", "Added to Family photos"), never one row per file. A library's
/// counts are written when it has had nothing new for a short quiet period, or at the end of a scan.
/// </summary>
public sealed class PersonalMediaActivityRecorder : BackgroundService
{
    /// <summary>How long a library must go without a new item before its counts are written.</summary>
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private readonly ISystemActivityRepository _activity;
    private readonly TimeProvider _time;
    private readonly ILogger<PersonalMediaActivityRecorder> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Pending> _pending = [];

    public PersonalMediaActivityRecorder(
        ISystemActivityRepository activity,
        TimeProvider time,
        ILogger<PersonalMediaActivityRecorder> logger)
    {
        _activity = activity;
        _time = time;
        _logger = logger;
    }

    /// <summary>Counts one new photo or personal video for a library.</summary>
    public void Record(Guid libraryId, string libraryName, bool isVideo) =>
        Record(libraryId, () => libraryName, isVideo);

    /// <summary>
    /// Counts one new photo or personal video for a library. The name is looked up only when a new count starts, so
    /// adding thousands of photos reads it once.
    /// </summary>
    public void Record(Guid libraryId, Func<string> libraryName, bool isVideo)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(libraryId, out var pending))
            {
                pending = new Pending { LibraryName = libraryName() };
                _pending[libraryId] = pending;
            }

            pending.LastAddedAt = _time.GetUtcNow();
            if (isVideo)
            {
                pending.Videos++;
            }
            else
            {
                pending.Photos++;
            }
        }
    }

    /// <summary>Writes the library's counts as one row and starts counting again. Writes nothing when nothing was counted.</summary>
    public async Task FlushAsync(Guid libraryId, CancellationToken ct = default)
    {
        Pending? taken;
        lock (_gate)
        {
            taken = _pending.Remove(libraryId, out var pending) ? pending : null;
        }

        if (taken is null || taken.Photos + taken.Videos == 0)
        {
            return;
        }

        try
        {
            await _activity.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.PersonalMediaAdded,
                CollectionName = Describe(taken.Photos, taken.Videos),
                EntityId = libraryId,
                EntityType = "PersonalMedia",
                Detail = $"Added to {taken.LibraryName}",
                ChangesJson = JsonSerializer.Serialize(new
                {
                    photos = taken.Photos,
                    videos = taken.Videos,
                    count = taken.Photos + taken.Videos,
                }),
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep the counts so the next flush tries again instead of losing them.
            _logger.LogWarning(ex, "The Activity log could not record the media added to library {LibraryId}.", libraryId);
            Restore(libraryId, taken);
        }
    }

    /// <summary>Writes every library that has been quiet for <see cref="QuietPeriod"/>.</summary>
    internal async Task FlushQuietLibrariesAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        List<Guid> quiet;
        lock (_gate)
        {
            quiet = _pending.Where(pair => now - pair.Value.LastAddedAt >= QuietPeriod).Select(pair => pair.Key).ToList();
        }

        foreach (var libraryId in quiet)
        {
            await FlushAsync(libraryId, ct).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await FlushQuietLibrariesAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Writing the media added to personal libraries failed; it will be retried.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: StopAsync writes whatever is still counted.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        List<Guid> libraries;
        lock (_gate)
        {
            libraries = _pending.Keys.ToList();
        }

        foreach (var libraryId in libraries)
        {
            await FlushAsync(libraryId, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string Describe(int photos, int videos)
    {
        var parts = new List<string>(2);
        if (photos > 0)
        {
            parts.Add(Count(photos, "photo"));
        }

        if (videos > 0)
        {
            parts.Add(Count(videos, "video"));
        }

        return string.Join(" and ", parts);
    }

    private static string Count(int count, string noun) =>
        count.ToString("N0", CultureInfo.InvariantCulture) + " " + (count == 1 ? noun : noun + "s");

    private void Restore(Guid libraryId, Pending taken)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(libraryId, out var newer))
            {
                newer.Photos += taken.Photos;
                newer.Videos += taken.Videos;
            }
            else
            {
                _pending[libraryId] = taken;
            }
        }
    }

    private sealed class Pending
    {
        public string LibraryName { get; set; } = string.Empty;
        public int Photos { get; set; }
        public int Videos { get; set; }
        public DateTimeOffset LastAddedAt { get; set; }
    }
}
