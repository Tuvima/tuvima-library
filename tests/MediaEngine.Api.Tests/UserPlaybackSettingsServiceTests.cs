using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
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
    public async Task UpdateAsync_RejectsMismatchedProfileId()
    {
        var settings = await _service.GetAsync(_profileId);
        settings.ProfileId = _otherProfileId;

        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateAsync(_profileId, settings));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
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
