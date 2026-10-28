using Dapper;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class UserPlaybackSettingsServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-playback-settings-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly ProfileRepository _profiles;
    private readonly UserPlaybackSettingsService _service;
    private readonly Guid _profileId = Guid.NewGuid();
    private readonly Guid _otherProfileId = Guid.NewGuid();

    public UserPlaybackSettingsServiceTests()
    {
        _db = new DatabaseConnection(_dbPath);
        _db.Open();
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _profiles = new ProfileRepository(_db);
        ProfileTestData.InsertAsync(_db, new Profile
        {
            Id = _profileId,
            DisplayName = "Playback Test",
            AvatarColor = "#7C4DFF",
            Role = ProfileRole.RestrictedProfile,
            CreatedAt = DateTimeOffset.UtcNow,
        }).GetAwaiter().GetResult();
        ProfileTestData.InsertAsync(_db, new Profile
        {
            Id = _otherProfileId,
            DisplayName = "Other Profile",
            AvatarColor = "#C9922E",
            Role = ProfileRole.RestrictedProfile,
            CreatedAt = DateTimeOffset.UtcNow,
        }).GetAwaiter().GetResult();
        _service = new UserPlaybackSettingsService(_db, _profiles);
    }

    [Fact]
    public async Task GetAsync_ReturnsDefaults_WhenSettingsDoNotExist()
    {
        var settings = await _service.GetAsync(_profileId);

        Assert.Equal(_profileId, settings.ProfileId);
        Assert.True(settings.General.ResumePlayback);
        Assert.Equal(5, settings.General.MinimumProgressToTrackPercent);
        Assert.True(settings.General.SyncAcrossDevices);
        Assert.Equal(15, settings.General.SyncFrequencyMinutes);
        Assert.Equal(1.0m, settings.Watching.DefaultPlaybackSpeed);
        Assert.Equal(1.25m, settings.Listening.AudiobookDefaultSpeed);
        Assert.Equal(10, settings.Listening.ResumeRewindSeconds);
        Assert.Equal(60, settings.Listening.AudiobookListenQualificationSeconds);
        Assert.Equal([2d, 4d, 8d, 16d], settings.Listening.AudiobookScanRates);
        Assert.Equal(100, settings.Reading.FontSizePercent);
        Assert.True(settings.Subtitles.SubtitleBackground);
    }

    [Fact]
    public async Task TheSpoilerLevel_IsUpToWhereIAm_UntilThePersonChoosesAnother()
    {
        Assert.Equal(SpoilerLevel.UpToProgress, await _service.GetSpoilerLevelAsync(_profileId));
        Assert.Equal("up_to_progress", (await _service.GetAsync(_profileId)).General.SpoilerLevel);
    }

    [Fact]
    public async Task ChoosingASpoilerLevel_IsSavedForThatProfileOnly()
    {
        var chosen = await _service.SetSpoilerLevelAsync(_profileId, "everything");

        Assert.Equal(SpoilerLevel.Everything, chosen);
        Assert.Equal(SpoilerLevel.Everything, await _service.GetSpoilerLevelAsync(_profileId));
        Assert.Equal(SpoilerLevel.UpToProgress, await _service.GetSpoilerLevelAsync(_otherProfileId));
        await _service.SetSpoilerLevelAsync(_profileId, "up_to_progress");
        Assert.Equal(SpoilerLevel.UpToProgress, await _service.GetSpoilerLevelAsync(_profileId));
    }

    [Fact]
    public async Task ChoosingASpoilerLevel_KeepsTheProfilesOtherPlaybackSettings()
    {
        var settings = await _service.GetAsync(_profileId);
        settings.Watching.DefaultPlaybackSpeed = 1.5m;
        await _service.UpdateAsync(_profileId, settings);

        await _service.SetSpoilerLevelAsync(_profileId, "everything");

        Assert.Equal(1.5m, (await _service.GetAsync(_profileId)).Watching.DefaultPlaybackSpeed);
    }

    [Theory]
    [InlineData("whole_work")]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ChoosingAnUnknownSpoilerLevel_IsRefused_AndKeepsTheSavedOne(string level)
    {
        await _service.SetSpoilerLevelAsync(_profileId, "everything");

        await Assert.ThrowsAsync<ArgumentException>(() => _service.SetSpoilerLevelAsync(_profileId, level));

        Assert.Equal(SpoilerLevel.Everything, await _service.GetSpoilerLevelAsync(_profileId));
    }

    [Fact]
    public async Task ASpoilerLevelForAnUnknownProfile_IsNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.SetSpoilerLevelAsync(Guid.NewGuid(), "everything"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.GetSpoilerLevelAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ABulkSaveRunningBesideAChosenSpoilerLevel_NeverPutsTheOldLevelBack()
    {
        var stale = await _service.GetAsync(_profileId);
        var saves = Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => _service.UpdateAsync(_profileId, CopyOf(stale))))
            .ToList();
        var choice = Task.Run(() => _service.SetSpoilerLevelAsync(_profileId, "everything"));
        saves.AddRange(Enumerable.Range(0, 12).Select(_ => Task.Run(() => _service.UpdateAsync(_profileId, CopyOf(stale)))));

        await Task.WhenAll(saves);
        await choice;

        Assert.Equal(SpoilerLevel.Everything, await _service.GetSpoilerLevelAsync(_profileId));
    }

    private static UserPlaybackSettingsDto CopyOf(UserPlaybackSettingsDto source) =>
        System.Text.Json.JsonSerializer.Deserialize<UserPlaybackSettingsDto>(
            System.Text.Json.JsonSerializer.Serialize(source, MediaEngine.Domain.Services.MediaEngineJson.Web),
            MediaEngine.Domain.Services.MediaEngineJson.Web)!;

    [Fact]
    public async Task TheVaultNavigationSwitch_IsOffUntilThePersonTurnsItOn_AndIsPerProfile()
    {
        Assert.False(await _service.GetShowVaultInNavigationAsync(_profileId));

        await _service.SetShowVaultInNavigationAsync(_profileId, true);

        Assert.True(await _service.GetShowVaultInNavigationAsync(_profileId));
        Assert.False(await _service.GetShowVaultInNavigationAsync(_otherProfileId));
        await _service.SetShowVaultInNavigationAsync(_profileId, false);
        Assert.False(await _service.GetShowVaultInNavigationAsync(_profileId));
    }

    [Fact]
    public async Task TheVaultNavigationSwitch_KeepsOtherSettings_AndABulkSaveNeverChangesIt()
    {
        var settings = await _service.GetAsync(_profileId);
        settings.Watching.DefaultPlaybackSpeed = 1.5m;
        await _service.UpdateAsync(_profileId, settings);
        await _service.SetShowVaultInNavigationAsync(_profileId, true);

        var stale = await _service.GetAsync(_profileId);
        stale.General.ShowVaultInNavigation = false;
        var saved = await _service.UpdateAsync(_profileId, stale);

        Assert.True(saved.General.ShowVaultInNavigation);
        Assert.True(await _service.GetShowVaultInNavigationAsync(_profileId));
        Assert.Equal(1.5m, (await _service.GetAsync(_profileId)).Watching.DefaultPlaybackSpeed);
    }

    [Fact]
    public async Task TheVaultNavigationSwitchForAnUnknownProfile_IsNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.SetShowVaultInNavigationAsync(Guid.NewGuid(), true));
    }

    [Fact]
    public async Task ABulkPlaybackSave_NeverChangesTheSpoilerLevel()
    {
        await _service.SetSpoilerLevelAsync(_profileId, "everything");
        var settings = await _service.GetAsync(_profileId);
        settings.General.SpoilerLevel = "up_to_progress";
        settings.Watching.DefaultPlaybackSpeed = 1.25m;

        var saved = await _service.UpdateAsync(_profileId, settings);

        Assert.Equal("everything", saved.General.SpoilerLevel);
        Assert.Equal(SpoilerLevel.Everything, await _service.GetSpoilerLevelAsync(_profileId));
        Assert.Equal(1.25m, (await _service.GetAsync(_profileId)).Watching.DefaultPlaybackSpeed);
    }

    [Fact]
    public async Task UpdateAsync_PersistsProfileScopedSettings()
    {
        var settings = await _service.GetAsync(_profileId);
        settings.Watching.DefaultPlaybackSpeed = 1.5m;
        settings.General.MinimumProgressToTrackPercent = 10;
        settings.General.SyncFrequencyMinutes = 30;

        await _service.UpdateAsync(_profileId, settings);

        var saved = await _service.GetAsync(_profileId);
        var other = await _service.GetAsync(_otherProfileId);
        Assert.Equal(1.5m, saved.Watching.DefaultPlaybackSpeed);
        Assert.Equal(10, saved.General.MinimumProgressToTrackPercent);
        Assert.Equal(30, saved.General.SyncFrequencyMinutes);
        Assert.Equal(1.0m, other.Watching.DefaultPlaybackSpeed);
    }

    [Fact]
    public async Task UpdateAsync_PreservesFractionalPlaybackRatesWithoutRounding()
    {
        var settings = await _service.GetAsync(_profileId);
        settings.Watching.DefaultPlaybackSpeed = 1.75m;
        settings.Listening.AudiobookDefaultSpeed = 0.75m;

        await _service.UpdateAsync(_profileId, settings);
        var saved = await _service.GetAsync(_profileId);

        Assert.Equal(1.75m, saved.Watching.DefaultPlaybackSpeed);
        Assert.Equal(0.75m, saved.Listening.AudiobookDefaultSpeed);

        saved.Watching.DefaultPlaybackSpeed = 1.333m;
        await _service.UpdateAsync(_profileId, saved);
        Assert.Equal(1.333m, (await _service.GetAsync(_profileId)).Watching.DefaultPlaybackSpeed);
    }

    [Fact]
    public async Task UpdateAsync_PersistsSingleSidebarLayoutPerProfileAndNormalizesWidthAndPanelKey()
    {
        var settings = await _service.GetAsync(_profileId);
        settings.ContextSidebars["desktop:music"] = new ContextSidebarLayoutDto
        {
            Open = true,
            Width = 900,
            ActivePanelKey = " Lyrics ",
        };

        await _service.UpdateAsync(_profileId, settings);

        var saved = await _service.GetAsync(_profileId);
        var other = await _service.GetAsync(_otherProfileId);
        var layout = saved.ContextSidebars["desktop:music"];
        Assert.True(layout.Open);
        Assert.Equal(480, layout.Width);
        Assert.Equal("lyrics", layout.ActivePanelKey);
        Assert.Empty(other.ContextSidebars);
    }

    [Fact]
    public async Task UpdateAsync_PreservesExactValidSpeedAndRejectsInvalidThresholdAndEnumValues()
    {
        var settings = await _service.GetAsync(_profileId);

        settings.Watching.DefaultPlaybackSpeed = 2.5m;
        settings.Listening.AudiobookDefaultSpeed = 1.75m;
        await _service.UpdateAsync(_profileId, settings);
        var exact = await _service.GetAsync(_profileId);
        Assert.Equal(2.5m, exact.Watching.DefaultPlaybackSpeed);
        Assert.Equal(1.75m, exact.Listening.AudiobookDefaultSpeed);

        settings = exact;
        settings.Watching.DefaultPlaybackSpeed = 3.01m;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.UpdateAsync(_profileId, settings));

        settings = await _service.GetAsync(_profileId);
        settings.General.MarkCompleteThresholdPercent = 25;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.UpdateAsync(_profileId, settings));

        settings = await _service.GetAsync(_profileId);
        settings.General.MinimumProgressToTrackPercent = 30;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.UpdateAsync(_profileId, settings));

        settings = await _service.GetAsync(_profileId);
        settings.General.SyncFrequencyMinutes = 45;
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateAsync(_profileId, settings));

        settings = await _service.GetAsync(_profileId);
        settings.Listening.ResumeRewindSeconds = 11;
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateAsync(_profileId, settings));

        settings = await _service.GetAsync(_profileId);
        settings.Listening.AudiobookListenQualificationSeconds = 10;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.UpdateAsync(_profileId, settings));

    }

    [Fact]
    public async Task GetAsync_ReturnsReaderDefaults_WhenSettingsDoNotExist()
    {
        var reading = (await _service.GetAsync(_profileId)).Reading;

        Assert.Equal("system", reading.Theme);
        Assert.Equal("book", reading.Font);
        Assert.Equal(18, reading.FontSizePx);
        Assert.Equal(1.5m, reading.LineHeight);
        Assert.Equal("standard", reading.PageWidth);
        Assert.Equal(PlaybackPreferenceValues.Paginated, reading.ReadingMode);
    }

    [Fact]
    public async Task GetAsync_FillsReaderDefaults_WhenStoredJsonPredatesTheReaderFields()
    {
        // The first read creates the table; the row is then written the way an older build stored it.
        await _service.GetAsync(_profileId);
        using (var conn = _db.CreateConnection())
        {
            conn.Execute(
                "INSERT INTO user_playback_settings (profile_id, settings_json, updated_at) VALUES (@profileId, @json, @updatedAt);",
                new
                {
                    profileId = _profileId,
                    json = "{\"reading\":{\"readingMode\":\"Scroll\",\"fontSizePercent\":120}}",
                    updatedAt = DateTimeOffset.UtcNow.ToString("O"),
                });
        }

        var reading = (await _service.GetAsync(_profileId)).Reading;

        Assert.Equal(PlaybackPreferenceValues.Scroll, reading.ReadingMode);
        Assert.Equal(120, reading.FontSizePercent);
        Assert.Equal("system", reading.Theme);
        Assert.Equal("book", reading.Font);
        Assert.Equal(18, reading.FontSizePx);
        Assert.Equal(1.5m, reading.LineHeight);
        Assert.Equal("standard", reading.PageWidth);
    }

    [Fact]
    public async Task UpdateAsync_PersistsReaderDefaultsAndNormalizesCase()
    {
        var settings = await _service.GetAsync(_profileId);
        settings.Reading.Theme = " Sepia ";
        settings.Reading.Font = "SERIF";
        settings.Reading.FontSizePx = 24;
        settings.Reading.LineHeight = 1.8m;
        settings.Reading.PageWidth = "Wide";
        settings.Reading.ReadingMode = PlaybackPreferenceValues.Scroll;

        await _service.UpdateAsync(_profileId, settings);
        var saved = (await _service.GetAsync(_profileId)).Reading;
        var other = (await _service.GetAsync(_otherProfileId)).Reading;

        Assert.Equal("sepia", saved.Theme);
        Assert.Equal("serif", saved.Font);
        Assert.Equal(24, saved.FontSizePx);
        Assert.Equal(1.8m, saved.LineHeight);
        Assert.Equal("wide", saved.PageWidth);
        Assert.Equal(PlaybackPreferenceValues.Scroll, saved.ReadingMode);
        Assert.Equal("system", other.Theme);
        Assert.Equal(18, other.FontSizePx);
    }

    [Theory]
    [InlineData(2, 12)]
    [InlineData(11, 12)]
    [InlineData(33, 32)]
    [InlineData(500, 32)]
    public async Task UpdateAsync_ClampsReaderTextSize(int sent, int expected)
    {
        var settings = await _service.GetAsync(_profileId);
        settings.Reading.FontSizePx = sent;

        await _service.UpdateAsync(_profileId, settings);

        Assert.Equal(expected, (await _service.GetAsync(_profileId)).Reading.FontSizePx);
    }

    [Theory]
    [InlineData(0.5, 1.2)]
    [InlineData(1.1, 1.2)]
    [InlineData(2.5, 2.4)]
    [InlineData(9, 2.4)]
    public async Task UpdateAsync_ClampsReaderLineHeight(double sent, double expected)
    {
        var settings = await _service.GetAsync(_profileId);
        settings.Reading.LineHeight = (decimal)sent;

        await _service.UpdateAsync(_profileId, settings);

        Assert.Equal((decimal)expected, (await _service.GetAsync(_profileId)).Reading.LineHeight);
    }

    [Fact]
    public async Task UpdateAsync_RejectsUnknownReaderThemeTypefaceAndPageWidth()
    {
        var settings = await _service.GetAsync(_profileId);
        settings.Reading.Theme = "neon";
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateAsync(_profileId, settings));

        settings = await _service.GetAsync(_profileId);
        settings.Reading.Font = "comic";
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateAsync(_profileId, settings));

        settings = await _service.GetAsync(_profileId);
        settings.Reading.PageWidth = "huge";
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateAsync(_profileId, settings));
    }

    [Fact]
    public async Task UpdateAsync_RejectsMismatchedProfileId()
    {
        var settings = await _service.GetAsync(_profileId);
        settings.ProfileId = _otherProfileId;

        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateAsync(_profileId, settings));
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.ReleaseSqlitePool(_dbPath);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
