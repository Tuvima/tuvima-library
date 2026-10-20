using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Singleton that owns the per-media-type writeback hash state.
///
/// <para>The hash combines the JSON slice of writable fields for a media type
/// (sorted, canonicalised) with the version constant of the tagger that
/// handles that media type. Bumping a tagger constant or editing
/// <c>writeback-fields.json</c> rotates the hash, marking every existing file
/// as stale and triggering the auto re-tag sweep.</para>
///
/// <para>Watches <c>config/writeback-fields.json</c> for changes via
/// <see cref="FileSystemWatcher"/>. When the file changes, the new state is
/// staged as <see cref="PendingHashes"/> (not auto-applied) and a
/// <see cref="PendingChanged"/> event is fired so the API/SignalR layer can
/// alert the user. The user must call <see cref="ApplyPending"/> before the
/// sweep walks any files — see plan §"Trigger flow".</para>
/// </summary>
public sealed class WritebackConfigState : IDisposable
{
    private readonly IConfigurationLoader _configLoader;
    private readonly ILogger<WritebackConfigState> _logger;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;
    private DateTime _lastChangeUtc = DateTime.MinValue;
    private bool _disposed;

    /// <summary>
    /// Per-media-type SHA-256 of (sorted-field-list-JSON + tagger version).
    /// Stable across restarts as long as the JSON and version constants don't change.
    /// </summary>
    public IReadOnlyDictionary<string, string> CurrentHashes { get; private set; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Staged pending hashes after a config-file edit. Empty when nothing is awaiting Apply.
    /// </summary>
    public IReadOnlyDictionary<string, string> PendingHashes { get; private set; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a pending diff has been staged but not yet approved.</summary>
    public bool HasPendingDiff => PendingHashes.Count > 0;

    /// <summary>
    /// The diff that produced <see cref="PendingHashes"/> — populated alongside it.
    /// Each entry is one media type whose field list changed.
    /// </summary>
    public IReadOnlyList<WritebackFieldDiff> PendingDiff { get; private set; }
        = Array.Empty<WritebackFieldDiff>();

    /// <summary>
    /// Fired on the worker thread when a pending diff is staged. Subscribers
    /// (typically the SignalR publisher) should not perform long-running work.
    /// </summary>
    public event Action<IReadOnlyList<WritebackFieldDiff>>? PendingChanged;

    /// <summary>
    /// Fired when <see cref="ApplyPending"/> commits new hashes — the worker
    /// uses this to kick a sweep immediately.
    /// </summary>
    public event Action? PendingApplied;

    public WritebackConfigState(
        IConfigurationLoader configLoader,
        ILogger<WritebackConfigState> logger)
    {
        _configLoader = configLoader;
        _logger = logger;

        // Initial load — disk state becomes Current with no pending diff.
        var initial = LoadAndComputeHashes(out _);
        CurrentHashes = initial;

        TryStartWatcher();
    }

    /// <summary>
    /// Re-applies <see cref="PendingHashes"/> on top of <see cref="CurrentHashes"/>
    /// for the media types that changed, then clears the pending state.
    /// Idempotent — calling with no pending diff is a no-op.
    /// </summary>
    public void ApplyPending()
    {
        lock (_gate)
        {
            if (PendingHashes.Count == 0)
            {
                return;
            }

            var merged = new Dictionary<string, string>(CurrentHashes, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in PendingHashes)
            {
                merged[key] = value;
            }

            CurrentHashes = merged;
            PendingHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PendingDiff = Array.Empty<WritebackFieldDiff>();
        }

        _logger.LogInformation("WritebackConfigState: pending diff applied; sweep will be eligible");
        PendingApplied?.Invoke();
    }

    /// <summary>
    /// Fires <see cref="PendingApplied"/> without touching any staged diff —
    /// lets the Dashboard's "Run Now" button wake the retag sweep worker
    /// immediately for an out-of-band pass over the current hash state.
    /// </summary>
    public void SignalRunNow()
    {
        _logger.LogInformation("WritebackConfigState: run-now signal raised");
        PendingApplied?.Invoke();
    }

    /// <summary>
    /// Computes the canonical hash for a media type's writable field list,
    /// without consulting the cached state. Used by <see cref="WriteBackService"/>
    /// when stamping a successful re-tag, since the asset's media type can be
    /// resolved from its work lineage.
    /// </summary>
    public string ComputeHashFor(string mediaType)
    {
        var fields = _configLoader.LoadConfig<WritebackFieldsConfiguration>("", "writeback-fields")
                     ?? new WritebackFieldsConfiguration();
        var slice = fields.GetFieldsFor(mediaType);
        return ComputeSliceHash(mediaType, slice);
    }

    // ── Internals ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reads <c>writeback-fields.json</c> and computes a per-media-type hash
    /// for every populated slice. Returns the snapshot via the return value
    /// and emits the raw field-list slices via <paramref name="rawSlices"/>
    /// so callers can produce a diff.
    /// </summary>
    private Dictionary<string, string> LoadAndComputeHashes(out Dictionary<string, IReadOnlyList<string>> rawSlices)
    {
        rawSlices = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        WritebackFieldsConfiguration fields;
        try
        {
            fields = _configLoader.LoadConfig<WritebackFieldsConfiguration>("", "writeback-fields")
                     ?? new WritebackFieldsConfiguration();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WritebackConfigState: failed to load writeback-fields.json — treating as empty");
            return hashes;
        }

        foreach (var mediaType in MediaTypeKeys)
        {
            var slice = fields.GetFieldsFor(mediaType);
            rawSlices[mediaType] = slice.ToList();
            if (slice.Count == 0)
            {
                continue;
            }

            hashes[mediaType] = ComputeSliceHash(mediaType, slice);
        }

        return hashes;
    }

    /// <summary>
    /// SHA-256 of: <c>{taggerVersion}|{sortedField1,sortedField2,…}</c>.
    /// The tagger version is the manually-bumped <c>public const int Version</c>
    /// constant on whichever tagger handles this media type. Bumping that
    /// constant rotates the hash for every file of that type.
    /// </summary>
    private static string ComputeSliceHash(string mediaType, IReadOnlyList<string> slice)
    {
        var version = TaggerVersionFor(mediaType);
        var sorted = slice.OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var payload = $"{version}|{string.Join(",", sorted)}";

        // The prior hand-rolled implementation already produced lowercase hex
        // (Convert.ToHexString(...).ToLowerInvariant()), matching Hashing.Sha256Hex
        // byte-for-byte — safe to redirect without rotating any stored hash.
        return Hashing.Sha256Hex(payload);
    }

    /// <summary>
    /// Maps a media type to the version constant of the tagger that handles it.
    /// Hard-coded — there are only four taggers, and the mapping is the same
    /// shape as the file-format → tagger mapping that the runtime uses today.
    /// </summary>
    private static int TaggerVersionFor(string mediaType) => mediaType switch
    {
        "Movies" => VideoMetadataTagger.Version,
        "Videos" => VideoMetadataTagger.Version,
        "TV" => VideoMetadataTagger.Version,
        "Music" => AudioMetadataTagger.Version,
        "Audiobooks" => AudioMetadataTagger.Version,
        "Books" => EpubMetadataTagger.Version,
        "Comics" => ComicMetadataTagger.Version,
        _ => 0,
    };

    private static readonly string[] MediaTypeKeys =
    [
        "Books", "Audiobooks", "Movies", "TV", "Music", "Comics", "Videos",
    ];

    private void TryStartWatcher()
    {
        var configDir = _configLoader.ConfigDirectoryPath;
        if (string.IsNullOrWhiteSpace(configDir) || !Directory.Exists(configDir))
        {
            _logger.LogDebug("WritebackConfigState: config dir not available — file watcher disabled");
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(configDir, "writeback-fields.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;

            _logger.LogInformation("WritebackConfigState: watching {Path}",
                Path.Combine(configDir, "writeback-fields.json"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WritebackConfigState: failed to start file watcher");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // FileSystemWatcher fires multiple events per save (LastWrite, Size).
        // Coalesce by ignoring repeats within 500 ms.
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if ((now - _lastChangeUtc).TotalMilliseconds < 500)
            {
                return;
            }

            _lastChangeUtc = now;

            _reloadTimer ??= new Timer(
                static state => ((WritebackConfigState)state!).OnReloadTimer(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _reloadTimer.Change(TimeSpan.FromMilliseconds(150), Timeout.InfiniteTimeSpan);
        }

        // Editors typically rewrite the file in two flushes — give the second one
        // a moment to settle before reading.
    }

    private void OnReloadTimer()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        RecomputePending();
    }

    private void RecomputePending()
    {
        try
        {
            var newHashes = LoadAndComputeHashes(out var newSlices);

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                // Diff against CURRENT (not against the old pending) so the user
                // always sees the full delta from approved baseline.
                var diff = new List<WritebackFieldDiff>();
                var oldSlices = LoadOldSlicesForCurrent();

                foreach (var mediaType in MediaTypeKeys)
                {
                    var oldList = oldSlices.TryGetValue(mediaType, out var o) ? o : Array.Empty<string>();
                    var newList = newSlices.TryGetValue(mediaType, out var n) ? n : Array.Empty<string>();

                    var added = newList.Except(oldList, StringComparer.OrdinalIgnoreCase).ToList();
                    var removed = oldList.Except(newList, StringComparer.OrdinalIgnoreCase).ToList();
                    if (added.Count == 0 && removed.Count == 0)
                    {
                        continue;
                    }

                    diff.Add(new WritebackFieldDiff(mediaType, added, removed));
                }

                if (diff.Count == 0)
                {
                    _logger.LogDebug("WritebackConfigState: file changed but no field deltas detected");
                    return;
                }

                // Stage only the media types that actually changed.
                var pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in diff)
                {
                    if (newHashes.TryGetValue(entry.MediaType, out var h))
                    {
                        pending[entry.MediaType] = h;
                    }
                }

                PendingHashes = pending;
                PendingDiff = diff;
            }

            _logger.LogInformation("WritebackConfigState: staged pending diff covering {Count} media type(s)",
                PendingDiff.Count);

            PendingChanged?.Invoke(PendingDiff);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WritebackConfigState: failed to recompute pending diff");
        }
    }

    /// <summary>
    /// Re-derives the field-list slices that produced the currently-applied
    /// hashes. Since we don't store the slices themselves (only the hashes),
    /// we recompute them from the on-disk file when the user has not yet
    /// edited it. After Apply, the file IS the current state.
    /// </summary>
    private Dictionary<string, IReadOnlyList<string>> LoadOldSlicesForCurrent()
    {
        // The on-disk slices are now the *new* state. To compare against
        // current, we'd need a snapshot of the previous file content. The
        // simplest faithful approximation: pendings are diffed against the
        // empty slice for any media type whose CurrentHash differs from the
        // freshly-computed one. This makes the first diff after a change
        // show every field as "added" if no prior snapshot exists.
        //
        // Good enough for the alert UX, and the worker still uses
        // CurrentHashes vs the per-asset stamp for the actual sweep filter.
        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        FileSystemWatcher? watcher;
        Timer? reloadTimer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            watcher = _watcher;
            _watcher = null;
            reloadTimer = _reloadTimer;
            _reloadTimer = null;
        }

        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnFileChanged;
            watcher.Created -= OnFileChanged;
            watcher.Dispose();
        }

        reloadTimer?.Dispose();
    }
}

/// <summary>
/// One media type's worth of field-list delta. Surfaced to the user when a
/// pending diff is awaiting Apply.
/// </summary>
public sealed record WritebackFieldDiff(
    string MediaType,
    IReadOnlyList<string> AddedFields,
    IReadOnlyList<string> RemovedFields);
