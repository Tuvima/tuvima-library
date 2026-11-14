using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// What the Dashboard's browsers measured about themselves, and the limits the Engine learned for them (playback-path spec D12, D13).
/// A browser is not a paired device and has no device record, so this is kept in memory for the Engine's lifetime, keyed to the
/// signed-in profile and a random key the browser session makes up. When the Engine restarts the Dashboard reports again at the
/// next playback start; until then that browser falls back to the fixed web list. Nothing here is written to the data store.
/// </summary>
public sealed class BrowserPlaybackAbilityStore(TimeProvider timeProvider)
{
    private const int MaximumEntries = 256;
    private const int MaximumKeyLength = 64;
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(12);

    private readonly object _gate = new();
    private readonly Dictionary<(Guid Profile, string Browser), Entry> _entries = new();

    /// <summary>Keeps a browser's report. The limits learned for that browser so far stay.</summary>
    public bool Set(Guid profileId, string browser, ClientCapabilitiesDto capabilities)
    {
        if (!IsValidKey(browser))
        {
            return false;
        }

        lock (_gate)
        {
            Prune();
            var key = (profileId, browser);
            var learned = _entries.TryGetValue(key, out var existing) ? existing.Capabilities.LearnedLimits : [];
            _entries[key] = new Entry(ClientCapabilitiesNormalizer.Normalize(capabilities, learned), timeProvider.GetUtcNow());
            return true;
        }
    }

    /// <summary>What this browser reported (with its learned limits), or null when it has not reported.</summary>
    public ClientCapabilitiesDto? TryGet(Guid profileId, string? browser)
    {
        if (!IsValidKey(browser))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue((profileId, browser!), out var entry))
            {
                return null;
            }

            _entries[(profileId, browser!)] = entry with { LastUsed = timeProvider.GetUtcNow() };
            return entry.Capabilities;
        }
    }

    /// <summary>Remembers a failure for a browser that has reported; a browser that has not has nothing to attach it to.</summary>
    public bool RecordLearnedLimit(Guid profileId, string browser, string failedRung, string videoCodec, string container, bool tenBit)
    {
        if (!IsValidKey(browser))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue((profileId, browser), out var entry))
            {
                return false;
            }

            var now = timeProvider.GetUtcNow();
            var learned = LearnedPlaybackLimits.Learn(entry.Capabilities.LearnedLimits, failedRung, videoCodec, container, tenBit, now);
            _entries[(profileId, browser)] = new Entry(ClientCapabilitiesNormalizer.WithLearned(entry.Capabilities, learned), now);
            return true;
        }
    }

    private static bool IsValidKey(string? browser) =>
        !string.IsNullOrWhiteSpace(browser) && browser.Length <= MaximumKeyLength && browser.All(char.IsAsciiLetterOrDigit);

    private void Prune()
    {
        var cutoff = timeProvider.GetUtcNow() - IdleLifetime;
        foreach (var stale in _entries.Where(pair => pair.Value.LastUsed < cutoff).Select(pair => pair.Key).ToArray())
        {
            _entries.Remove(stale);
        }

        if (_entries.Count < MaximumEntries)
        {
            return;
        }

        foreach (var oldest in _entries.OrderBy(pair => pair.Value.LastUsed).Take(_entries.Count - MaximumEntries + 1).Select(pair => pair.Key).ToArray())
        {
            _entries.Remove(oldest);
        }
    }

    private sealed record Entry(ClientCapabilitiesDto Capabilities, DateTimeOffset LastUsed);
}
