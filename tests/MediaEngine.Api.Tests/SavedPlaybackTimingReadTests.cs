using System.Collections;
using System.Globalization;
using System.Reflection;
using Dapper;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class SavedPlaybackTimingReadTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"saved_timing_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;

    public SavedPlaybackTimingReadTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_path);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    [Theory]
    [InlineData("300.5", "900.5", 300.5, 900.5)]
    [InlineData("0", "900", 0d, 900d)]
    [InlineData("-1", "900", null, 900d)]
    [InlineData("broken", "900", null, 900d)]
    [InlineData("300", "broken", 300d, null)]
    [InlineData("300", "0", 300d, null)]
    [InlineData("NaN", "Infinity", null, null)]
    [InlineData("1,000", "1,500", null, null)]
    public async Task ActualUserStateSaveIsReadByJourneyAndDetailSequence(
        string position, string duration, double? expectedPosition, double? expectedDuration)
    {
        var work = Guid.NewGuid(); var edition = Guid.NewGuid(); var asset = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute("""
            INSERT INTO works(id,media_type,work_kind,curator_state) VALUES (@work,'Movies','standalone','accepted');
            INSERT INTO editions(id,work_id) VALUES (@edition,@work);
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root) VALUES (@asset,@edition,'saved-timing','C:/qa/saved.mp4');
            INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES (@work,'runtime','120',CURRENT_TIMESTAMP);
            """, new { work, edition, asset });
        await new UserStateRepository(_db).SaveAsync(new UserState
        {
            UserId = Profile.SeedProfileId, AssetId = asset, ContentHash = "saved-timing",
            ProgressPct = 42, LastAccessed = DateTimeOffset.UtcNow,
            ExtendedProperties = new() { ["position_seconds"] = position, ["duration_seconds"] = duration },
        });
        var saved = connection.QuerySingle<string>("SELECT extended_properties FROM user_states WHERE asset_id=@asset", new { asset });
        Assert.Contains("\"position_seconds\":\"", saved);
        await AssertReaders(work, asset, expectedPosition, expectedDuration);
    }

    [Theory]
    [InlineData("{\"position_seconds\":300,\"duration_seconds\":900}", 300d, 900d)]
    [InlineData("{\"position_seconds\":300.5,\"duration_seconds\":900.5}", 300.5, 900.5)]
    [InlineData("{}", null, null)]
    [InlineData("{\"position_seconds\":true,\"duration_seconds\":[]}", null, null)]
    public async Task NumericJsonAndMissingValuesUseTheSameReadContract(string json, double? position, double? duration)
    {
        var work = Guid.NewGuid(); var edition = Guid.NewGuid(); var asset = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute("""
            INSERT INTO works(id,media_type,work_kind,curator_state) VALUES (@work,'Movies','standalone','accepted');
            INSERT INTO editions(id,work_id) VALUES (@edition,@work);
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root) VALUES (@asset,@edition,'numeric-timing','C:/qa/numeric.mp4');
            INSERT INTO user_states(user_id,asset_id,progress_pct,last_accessed,extended_properties) VALUES (@profile,@asset,42,CURRENT_TIMESTAMP,@json);
            """, new { work, edition, asset, profile = Profile.SeedProfileId, json });
        await AssertReaders(work, asset, position, duration);
    }

    private async Task AssertReaders(Guid work, Guid asset, double? position, double? duration)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var journey = Assert.Single(await new DisplayJourneyProjectionReader(_db).LoadAsync(Profile.SeedProfileId, "watch", default));
            Assert.Equal(asset, journey.AssetId);
            Assert.Equal(position, journey.PositionSeconds);
            Assert.Equal(duration, journey.DurationSeconds);
            // Exercise the actual SQLite reader used by collection/detail sequence composition.
            var composer = new DetailCompositionOrchestrator(_db, null!, null!, null!, null!, null!, null!, null!);
            var method = typeof(DetailCompositionOrchestrator).GetMethod("LoadCollectionWorksAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var task = (Task)method.Invoke(composer, [Guid.Empty, null, CancellationToken.None, new[] { work }, Profile.SeedProfileId, new[] { asset }])!;
            await task;
            var result = (IEnumerable)task.GetType().GetProperty("Result")!.GetValue(task)!;
            var row = Assert.Single(result.Cast<object>());
            Assert.Equal(position, row.GetType().GetProperty("PositionSeconds")!.GetValue(row));
            Assert.Equal(duration, row.GetType().GetProperty("DurationSeconds")!.GetValue(row));
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    public void Dispose()
    {
        _db.Dispose();
        // Only this fixture's pool is released; other concurrently running
        // SQLite tests retain their own connections and pools.
        using var fixturePool = new SqliteConnection($"Data Source={_path}");
        SqliteConnection.ClearPool(fixturePool);
        if (File.Exists(_path)) File.Delete(_path);
    }
}
