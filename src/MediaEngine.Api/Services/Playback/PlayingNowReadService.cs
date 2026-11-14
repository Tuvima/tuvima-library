using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Builds the Playing now list for an administrator: one row per encode and per session nobody is encoding for, across every account
/// and profile, newest first. Vault titles are masked here, so nothing downstream can leak one (playing-now spec D15).
/// </summary>
public sealed class PlayingNowReadService(
    PlaybackActivityRegistry registry,
    IPlayingNowNames names,
    IConfigurationLoader configuration,
    TimeProvider clock)
{
    public const string VaultTitle = "Vault title";

    public async Task<PlayingNowDto> GetAsync(CancellationToken ct)
    {
        var settings = configuration.LoadTranscoding();
        var now = clock.GetUtcNow();
        var grace = TimeSpan.FromSeconds(Math.Max(1, settings.GraceSecondsAfterLastViewer));
        var pausedFor = TimeSpan.FromMinutes(Math.Max(1, settings.PausedCountsAsWatchingMinutes));
        var profileNames = new Dictionary<Guid, (string? Name, bool Kids)>();
        var people = new Dictionary<Guid, string?>();
        var rows = new List<PlayingNowRowDto>();
        var live = registry.Watches.Where(w => PlaybackActivityRegistry.IsLive(w, now, pausedFor)).ToArray();
        var encodedAssets = new HashSet<Guid>();

        foreach (var encode in registry.Encodes)
        {
            var watch = live.Where(w => w.AssetId == encode.AssetId).OrderByDescending(w => w.LastObservedAt).FirstOrDefault();
            var offline = encode.Kind == PlayingNowKinds.OfflineEncode;
            if (!offline)
            {
                encodedAssets.Add(encode.AssetId);
            }

            var watched = !offline && registry.HasViewer(encode.AssetId, now, pausedFor);
            int? stopsIn = !offline && !watched
                ? (int)Math.Max(0, Math.Ceiling((grace - (now - encode.LastViewerAt)).TotalSeconds))
                : null;
            var vault = watch?.IsVault == true;
            var (profileName, kids) = watch is null ? (null, false) : await ProfileAsync(watch.ProfileId, profileNames, ct).ConfigureAwait(false);
            var step = encode.WaitingForSlot ? PlayingNowSteps.Preparing : encode.Step;
            rows.Add(new PlayingNowRowDto
            {
                Id = encode.Id.ToString("D"),
                Kind = encode.Kind,
                Title = vault ? VaultTitle : watch?.Title ?? encode.Title ?? "Preparing a video",
                Subtitle = vault ? null : watch?.Subtitle,
                CoverUrl = vault ? null : watch?.CoverUrl,
                AccountId = watch?.AccountId.ToString("D"),
                ProfileId = watch?.ProfileId.ToString("D"),
                DeviceId = watch?.DeviceId?.ToString("D"),
                PersonName = watch is null ? encode.RequestedBy : await PersonAsync(watch.AccountId, people, ct).ConfigureAwait(false) ?? profileName,
                ProfileName = profileName,
                IsKidsProfile = kids,
                DeviceName = watch?.DeviceName,
                Step = step,
                PreparingReason = encode.WaitingForSlot ? "Waiting for a free slot" : step == PlayingNowSteps.Preparing ? "Reading the file" : null,
                WaitingForSlot = encode.WaitingForSlot,
                PercentPrepared = encode.WaitingForSlot ? null : encode.Percent?.Invoke(),
                PositionSeconds = watch?.PositionSeconds,
                DurationSeconds = watch?.DurationSeconds,
                Heaviness = encode.WaitingForSlot ? null : encode.Heaviness,
                RanOn = encode.WaitingForSlot ? null : EncodeHeaviness.PlaceWords(encode.RanOn),
                IsWatched = watched,
                StopsInSeconds = stopsIn,
                OfflineQuality = encode.Quality,
                CanStop = !encode.IsStopping,
                StartedAt = encode.StartedAt,
            });
        }

        // Sessions with no encode (playing directly) are listed too: no Stop, no extra load.
        foreach (var watch in live.Where(w => !encodedAssets.Contains(w.AssetId)))
        {
            var (profileName, kids) = await ProfileAsync(watch.ProfileId, profileNames, ct).ConfigureAwait(false);
            rows.Add(new PlayingNowRowDto
            {
                Id = string.Empty,
                Kind = PlayingNowKinds.Watching,
                Title = watch.IsVault ? VaultTitle : watch.Title,
                Subtitle = watch.IsVault ? null : watch.Subtitle,
                CoverUrl = watch.IsVault ? null : watch.CoverUrl,
                AccountId = watch.AccountId.ToString("D"),
                ProfileId = watch.ProfileId.ToString("D"),
                DeviceId = watch.DeviceId?.ToString("D"),
                PersonName = await PersonAsync(watch.AccountId, people, ct).ConfigureAwait(false) ?? profileName,
                ProfileName = profileName,
                IsKidsProfile = kids,
                DeviceName = watch.DeviceName,
                Step = PlayingNowSteps.PlayingDirectly,
                PositionSeconds = watch.PositionSeconds,
                DurationSeconds = watch.DurationSeconds,
                IsWatched = true,
                StartedAt = watch.StartedAt,
            });
        }

        var ordered = rows.OrderByDescending(r => r.StartedAt).ToArray();
        var running = ordered.Where(r => r.Kind != PlayingNowKinds.Watching && !r.WaitingForSlot).ToArray();
        var heaviest = running.Where(r => r.Heaviness is not null)
            .OrderByDescending(r => r.Heaviness == PlayingNowHeaviness.Heavy ? 2 : r.Heaviness == PlayingNowHeaviness.Medium ? 1 : 0)
            .FirstOrDefault();
        return new PlayingNowDto
        {
            Rows = ordered,
            MaxActiveEncodes = Math.Clamp(settings.MaxConcurrentTranscodes, 1, 8),
            ActiveEncodes = running.Length,
            WatchingCount = ordered.Count(r => r.IsWatched),
            PreparingCount = ordered.Count(r => r.Kind != PlayingNowKinds.Watching),
            HeaviestLoad = heaviest?.Heaviness,
            HeaviestRanOn = heaviest?.RanOn,
        };
    }

    private async Task<(string? Name, bool Kids)> ProfileAsync(Guid profileId, Dictionary<Guid, (string? Name, bool Kids)> cache, CancellationToken ct)
    {
        if (!cache.TryGetValue(profileId, out var found))
        {
            found = await names.ProfileAsync(profileId, ct).ConfigureAwait(false);
            cache[profileId] = found;
        }

        return found;
    }

    private async Task<string?> PersonAsync(Guid accountId, Dictionary<Guid, string?> cache, CancellationToken ct)
    {
        if (!cache.TryGetValue(accountId, out var name))
        {
            name = await names.PersonAsync(accountId, ct).ConfigureAwait(false);
            cache[accountId] = name;
        }

        return name;
    }
}

/// <inheritdoc cref="IPlayingNowNames"/>
public sealed class PlayingNowNames(IProfileRepository profiles, IAccountRepository accounts) : IPlayingNowNames
{
    public async Task<(string? Name, bool Kids)> ProfileAsync(Guid profileId, CancellationToken ct)
    {
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        return (profile?.DisplayName, profile?.IsKidsProfile ?? false);
    }

    public async Task<string?> PersonAsync(Guid accountId, CancellationToken ct)
    {
        var home = await accounts.GetDefaultProfileIdAsync(accountId, ct).ConfigureAwait(false);
        return home is { } id ? (await profiles.GetByIdAsync(id, ct).ConfigureAwait(false))?.DisplayName : null;
    }
}
