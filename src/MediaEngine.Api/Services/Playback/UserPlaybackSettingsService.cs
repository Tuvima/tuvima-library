using System.Text.Json;
using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Playback;

public sealed class UserPlaybackSettingsService : IUserPlaybackSettingsService
{
    // WriteIndented = false is the JsonSerializerOptions default, so the previous
    // `new(JsonSerializerDefaults.Web) { WriteIndented = false }` local instance was
    // functionally identical to plain Web defaults.
    private static readonly JsonSerializerOptions JsonOptions = MediaEngineJson.Web;

    private static readonly HashSet<int> WatchingSkipBackValues = [5, 10, 15, 30];
    private static readonly HashSet<int> WatchingSkipForwardValues = [10, 30, 60, 90];
    private static readonly HashSet<int> ListeningSkipBackValues = [10, 15, 30];
    private static readonly HashSet<int> ListeningSkipForwardValues = [15, 30, 60];
    private static readonly HashSet<int> ListeningResumeRewindValues = [0, 5, 10, 15, 30];
    private static readonly HashSet<int> SyncFrequencyValues = [5, 15, 30, 60];
    private static readonly int[] DefaultSleepTimerOptionsMinutes = [5, 10, 15, 30, 45, 60];
    private static readonly HashSet<string> VideoQualityValues = BuildSet(
        PlaybackPreferenceValues.Auto,
        PlaybackPreferenceValues.Original,
        PlaybackPreferenceValues.High,
        PlaybackPreferenceValues.Balanced,
        PlaybackPreferenceValues.DataSaver);
    private static readonly HashSet<string> SleepTimerValues = BuildSet(
        PlaybackPreferenceValues.Off,
        "5",
        "10",
        "15",
        "30",
        "45",
        "60",
        PlaybackPreferenceValues.EndOfChapter,
        PlaybackPreferenceValues.EndOfEpisode);
    private static readonly HashSet<string> OutputValues = BuildSet(
        PlaybackPreferenceValues.Auto,
        PlaybackPreferenceValues.Headphones,
        PlaybackPreferenceValues.Speakers,
        PlaybackPreferenceValues.Bluetooth);
    private static readonly HashSet<string> ReadingModeValues = BuildSet(
        PlaybackPreferenceValues.Paginated,
        PlaybackPreferenceValues.Scroll);
    private static readonly HashSet<string> LineSpacingValues = BuildSet(
        PlaybackPreferenceValues.Compact,
        PlaybackPreferenceValues.Comfortable,
        PlaybackPreferenceValues.Spacious);
    private static readonly HashSet<string> MarginValues = BuildSet(
        PlaybackPreferenceValues.Narrow,
        PlaybackPreferenceValues.Medium,
        PlaybackPreferenceValues.Wide);
    private static readonly HashSet<string> ComicModeValues = BuildSet(
        PlaybackPreferenceValues.Page,
        PlaybackPreferenceValues.Webtoon,
        PlaybackPreferenceValues.DoublePage,
        PlaybackPreferenceValues.FitWidth);
    private static readonly HashSet<string> ForcedSubtitleValues = BuildSet(
        PlaybackPreferenceValues.Auto,
        PlaybackPreferenceValues.Always,
        PlaybackPreferenceValues.Never);
    private static readonly HashSet<string> SubtitleSizeValues = BuildSet(
        PlaybackPreferenceValues.Small,
        PlaybackPreferenceValues.Medium,
        PlaybackPreferenceValues.Large,
        PlaybackPreferenceValues.ExtraLarge);
    private static readonly HashSet<string> SubtitlePositionValues = BuildSet(
        PlaybackPreferenceValues.Bottom,
        PlaybackPreferenceValues.Top);
    private static readonly HashSet<string> SubtitleStyleValues = BuildSet(
        PlaybackPreferenceValues.Clean,
        PlaybackPreferenceValues.HighContrast,
        PlaybackPreferenceValues.Shadowed);

    private readonly IDatabaseConnection _db;
    private readonly IProfileRepository _profiles;

    public UserPlaybackSettingsService(IDatabaseConnection db, IProfileRepository profiles)
    {
        _db = db;
        _profiles = profiles;
    }

    public async Task<UserPlaybackSettingsDto> GetAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureProfileExistsAsync(profileId, ct);

        using var conn = _db.CreateConnection();
        EnsureTable(conn);
        var row = conn.QueryFirstOrDefault<SettingsRow>("""
            SELECT profile_id AS ProfileId,
                   settings_json AS SettingsJson,
                   updated_at AS UpdatedAt
            FROM user_playback_settings
            WHERE profile_id = @profileId
            LIMIT 1;
            """, new { profileId });

        if (row is null)
        {
            return UserPlaybackSettingsDto.CreateDefaults(profileId);
        }

        var settings = JsonSerializer.Deserialize<UserPlaybackSettingsDto>(row.SettingsJson, JsonOptions)
            ?? UserPlaybackSettingsDto.CreateDefaults(profileId);
        settings.ProfileId = profileId;
        settings.UpdatedAt = DateTimeOffset.TryParse(row.UpdatedAt, out var updatedAt)
            ? updatedAt
            : DateTimeOffset.UtcNow;
        return settings;
    }

    public Task<UserPlaybackSettingsDto> GetOrCreateDefaultsAsync(Guid profileId, CancellationToken ct = default) =>
        GetAsync(profileId, ct);

    public async Task<UserPlaybackSettingsDto> UpdateAsync(
        Guid profileId,
        UserPlaybackSettingsDto settings,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(settings);
        await EnsureProfileExistsAsync(profileId, ct);

        if (settings.ProfileId != Guid.Empty && settings.ProfileId != profileId)
        {
            throw new ArgumentException("ProfileId must match the route profile id.", nameof(settings));
        }

        var normalized = Normalize(settings, profileId);
        Validate(normalized);

        normalized.UpdatedAt = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(normalized, JsonOptions);

        using var conn = _db.CreateConnection();
        EnsureTable(conn);
        conn.Execute("""
            INSERT INTO user_playback_settings (profile_id, settings_json, updated_at)
            VALUES (@profileId, @json, @updatedAt)
            ON CONFLICT(profile_id) DO UPDATE SET
                settings_json = excluded.settings_json,
                updated_at = excluded.updated_at;
            """, new
        {
            profileId,
            json,
            updatedAt = normalized.UpdatedAt.ToString("O"),
        });

        return normalized;
    }

    private async Task EnsureProfileExistsAsync(Guid profileId, CancellationToken ct)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("ProfileId is required.", nameof(profileId));
        }

        var profile = await _profiles.GetByIdAsync(profileId, ct);
        if (profile is null)
        {
            throw new KeyNotFoundException($"Profile '{profileId}' was not found.");
        }
    }

    private static void EnsureTable(System.Data.IDbConnection conn)
    {
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS user_playback_settings (
                profile_id    BLOB NOT NULL PRIMARY KEY REFERENCES profiles(id) ON DELETE CASCADE,
                settings_json TEXT NOT NULL,
                updated_at    TEXT NOT NULL
            );
            """);
    }

    private static UserPlaybackSettingsDto Normalize(UserPlaybackSettingsDto settings, Guid profileId)
    {
        settings.ProfileId = profileId;
        settings.General ??= new PlaybackGeneralSettingsDto();
        settings.Watching ??= new WatchingSettingsDto();
        settings.Listening ??= new ListeningSettingsDto();
        settings.Reading ??= new ReadingSettingsDto();
        settings.Subtitles ??= new SubtitleLanguageSettingsDto();
        settings.ContextWorkspaces ??= new Dictionary<string, ContextWorkspaceLayoutDto>(StringComparer.OrdinalIgnoreCase);
        settings.ContextWorkspaces = settings.ContextWorkspaces
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Key.Length <= 64 && entry.Value is not null)
            .Take(12)
            .ToDictionary(entry => entry.Key.Trim().ToLowerInvariant(), entry => NormalizeWorkspace(entry.Value), StringComparer.OrdinalIgnoreCase);

        settings.Watching.DefaultPlaybackSpeed = Math.Round(settings.Watching.DefaultPlaybackSpeed, 2);
        settings.Listening.AudiobookDefaultSpeed = Math.Round(settings.Listening.AudiobookDefaultSpeed, 2);
        settings.Listening.AudiobookScanRates ??= [2d, 4d, 8d, 16d];
        settings.Listening.AudiobookScanRates = settings.Listening.AudiobookScanRates
            .Where(rate => rate is >= 1d and <= 32d)
            .Select(rate => Math.Round(rate, 2))
            .Distinct()
            .Order()
            .ToList();
        if (settings.Listening.AudiobookScanRates.Count == 0)
        {
            settings.Listening.AudiobookScanRates = [2d, 4d, 8d, 16d];
        }
        settings.Listening.SleepTimerOptionsMinutes ??= [.. DefaultSleepTimerOptionsMinutes];
        settings.Listening.SleepTimerOptionsMinutes = settings.Listening.SleepTimerOptionsMinutes
            .Where(minutes => minutes is >= 1 and <= 240)
            .Distinct()
            .Order()
            .ToList();
        if (settings.Listening.SleepTimerOptionsMinutes.Count == 0)
        {
            settings.Listening.SleepTimerOptionsMinutes = [.. DefaultSleepTimerOptionsMinutes];
        }
        settings.Listening.AudiobookHistoryLimit = Math.Clamp(settings.Listening.AudiobookHistoryLimit, 1, 50);
        settings.Listening.AudiobookResumeRegressionGuardSeconds = Math.Clamp(settings.Listening.AudiobookResumeRegressionGuardSeconds, 0, 3600);
        settings.Listening.AudiobookNearStartGuardSeconds = Math.Clamp(settings.Listening.AudiobookNearStartGuardSeconds, 0, 300);
        settings.Listening.AudiobookHistoryActiveSegmentGapSeconds = Math.Clamp(settings.Listening.AudiobookHistoryActiveSegmentGapSeconds, 5, 300);
        settings.Listening.AudiobookHistoryPositionJumpToleranceSeconds = Math.Clamp(settings.Listening.AudiobookHistoryPositionJumpToleranceSeconds, 1, 120);
        settings.Watching.PreferredVideoQuality = NormalizeToken(settings.Watching.PreferredVideoQuality);
        settings.Listening.DefaultSleepTimer = NormalizeToken(settings.Listening.DefaultSleepTimer);
        settings.Listening.OutputPreference = NormalizeToken(settings.Listening.OutputPreference);
        settings.Reading.ReadingMode = NormalizeToken(settings.Reading.ReadingMode);
        settings.Reading.LineSpacing = NormalizeToken(settings.Reading.LineSpacing);
        settings.Reading.Margins = NormalizeToken(settings.Reading.Margins);
        settings.Reading.DefaultComicMode = NormalizeToken(settings.Reading.DefaultComicMode);
        settings.Subtitles.ForcedSubtitlesMode = NormalizeToken(settings.Subtitles.ForcedSubtitlesMode);
        settings.Subtitles.SubtitleSize = NormalizeToken(settings.Subtitles.SubtitleSize);
        settings.Subtitles.SubtitlePosition = NormalizeToken(settings.Subtitles.SubtitlePosition);
        settings.Subtitles.SubtitleStyle = NormalizeToken(settings.Subtitles.SubtitleStyle);
        settings.Subtitles.DefaultSubtitleLanguage = NormalizeLanguage(settings.Subtitles.DefaultSubtitleLanguage);
        settings.Subtitles.AudioLanguage = NormalizeLanguage(settings.Subtitles.AudioLanguage);
        return settings;
    }

    private static ContextWorkspaceLayoutDto NormalizeWorkspace(ContextWorkspaceLayoutDto layout)
    {
        layout.Width = Math.Clamp(layout.Width, 320, 640);
        layout.Panels = (layout.Panels ?? [])
            .Where(panel => panel is not null && !string.IsNullOrWhiteSpace(panel.Key) && panel.Key.Length <= 40)
            .DistinctBy(panel => panel.Key, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(panel => new ContextWorkspacePanelDto
            {
                Key = panel.Key.Trim().ToLowerInvariant(),
                Ratio = double.IsFinite(panel.Ratio) ? Math.Clamp(panel.Ratio, 0.1d, 1d) : 1d,
                Collapsed = panel.Collapsed,
            })
            .ToList();
        var total = layout.Panels.Sum(panel => panel.Ratio);
        if (total > 0)
        {
            foreach (var panel in layout.Panels)
                panel.Ratio = Math.Round(panel.Ratio / total, 4);
        }
        return layout;
    }

    private static void Validate(UserPlaybackSettingsDto settings)
    {
        RequireRange(settings.General.MinimumProgressToTrackPercent, 1, 25, nameof(settings.General.MinimumProgressToTrackPercent));
        RequireRange(settings.General.MarkCompleteThresholdPercent, 50, 100, nameof(settings.General.MarkCompleteThresholdPercent));
        RequireRange(settings.Reading.FontSizePercent, 80, 160, nameof(settings.Reading.FontSizePercent));
        RequireRange(settings.Watching.DefaultPlaybackSpeed, 0.5m, 2.0m, nameof(settings.Watching.DefaultPlaybackSpeed));
        RequireRange(settings.Listening.AudiobookDefaultSpeed, 0.5m, 3.0m, nameof(settings.Listening.AudiobookDefaultSpeed));
        RequireRange(settings.Listening.AudiobookListenQualificationSeconds, 15, 300, nameof(settings.Listening.AudiobookListenQualificationSeconds));
        RequireRange(settings.Listening.AudiobookHistoryLimit, 1, 50, nameof(settings.Listening.AudiobookHistoryLimit));
        RequireRange(settings.Listening.AudiobookResumeRegressionGuardSeconds, 0, 3600, nameof(settings.Listening.AudiobookResumeRegressionGuardSeconds));
        RequireRange(settings.Listening.AudiobookNearStartGuardSeconds, 0, 300, nameof(settings.Listening.AudiobookNearStartGuardSeconds));
        RequireRange(settings.Listening.AudiobookHistoryActiveSegmentGapSeconds, 5, 300, nameof(settings.Listening.AudiobookHistoryActiveSegmentGapSeconds));
        RequireRange(settings.Listening.AudiobookHistoryPositionJumpToleranceSeconds, 1, 120, nameof(settings.Listening.AudiobookHistoryPositionJumpToleranceSeconds));
        foreach (var minutes in settings.Listening.SleepTimerOptionsMinutes)
        {
            RequireRange(minutes, 1, 240, $"{nameof(settings.Listening.SleepTimerOptionsMinutes)}[]");
        }

        RequireAllowed(settings.Watching.SkipBackSeconds, WatchingSkipBackValues, nameof(settings.Watching.SkipBackSeconds));
        RequireAllowed(settings.Watching.SkipForwardSeconds, WatchingSkipForwardValues, nameof(settings.Watching.SkipForwardSeconds));
        RequireAllowed(settings.Listening.SkipBackSeconds, ListeningSkipBackValues, nameof(settings.Listening.SkipBackSeconds));
        RequireAllowed(settings.Listening.SkipForwardSeconds, ListeningSkipForwardValues, nameof(settings.Listening.SkipForwardSeconds));
        RequireAllowed(settings.Listening.ResumeRewindSeconds, ListeningResumeRewindValues, nameof(settings.Listening.ResumeRewindSeconds));
        RequireAllowed(settings.General.SyncFrequencyMinutes, SyncFrequencyValues, nameof(settings.General.SyncFrequencyMinutes));
        RequireAllowed(settings.Watching.PreferredVideoQuality, VideoQualityValues, nameof(settings.Watching.PreferredVideoQuality));
        RequireAllowed(settings.Listening.DefaultSleepTimer, SleepTimerValues, nameof(settings.Listening.DefaultSleepTimer));
        RequireAllowed(settings.Listening.OutputPreference, OutputValues, nameof(settings.Listening.OutputPreference));
        RequireAllowed(settings.Reading.ReadingMode, ReadingModeValues, nameof(settings.Reading.ReadingMode));
        RequireAllowed(settings.Reading.LineSpacing, LineSpacingValues, nameof(settings.Reading.LineSpacing));
        RequireAllowed(settings.Reading.Margins, MarginValues, nameof(settings.Reading.Margins));
        RequireAllowed(settings.Reading.DefaultComicMode, ComicModeValues, nameof(settings.Reading.DefaultComicMode));
        RequireAllowed(settings.Subtitles.ForcedSubtitlesMode, ForcedSubtitleValues, nameof(settings.Subtitles.ForcedSubtitlesMode));
        RequireAllowed(settings.Subtitles.SubtitleSize, SubtitleSizeValues, nameof(settings.Subtitles.SubtitleSize));
        RequireAllowed(settings.Subtitles.SubtitlePosition, SubtitlePositionValues, nameof(settings.Subtitles.SubtitlePosition));
        RequireAllowed(settings.Subtitles.SubtitleStyle, SubtitleStyleValues, nameof(settings.Subtitles.SubtitleStyle));

        if (settings.Listening.MusicCrossfade)
        {
            RequireRange(settings.Listening.CrossfadeSeconds, 1, 15, nameof(settings.Listening.CrossfadeSeconds));
        }
    }

    private static void RequireRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, $"{name} must be between {min} and {max}.");
        }
    }

    private static void RequireRange(decimal value, decimal min, decimal max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, $"{name} must be between {min:0.##} and {max:0.##}.");
        }
    }

    private static void RequireAllowed(int value, HashSet<int> allowed, string name)
    {
        if (!allowed.Contains(value))
        {
            throw new ArgumentException($"{name} must be one of: {string.Join(", ", allowed.Order())}.", name);
        }
    }

    private static void RequireAllowed(string value, HashSet<string> allowed, string name)
    {
        if (!allowed.Contains(value))
        {
            throw new ArgumentException($"{name} must be one of: {string.Join(", ", allowed)}.", name);
        }
    }

    private static HashSet<string> BuildSet(params string[] values) =>
        new(values, StringComparer.OrdinalIgnoreCase);

    private static string NormalizeToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeLanguage(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "English" : value.Trim();

    private sealed class SettingsRow
    {
        public Guid ProfileId { get; set; }
        public string SettingsJson { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
    }
}
