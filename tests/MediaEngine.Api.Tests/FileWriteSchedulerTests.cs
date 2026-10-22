using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.FileWrites;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The batched write-back schedule over a real temp data store: changes only join the durable list, and a run (on the interval, or
/// Write now) writes each waiting file once. Only the file writer and the organiser are spies, and the clock is by hand.
/// </summary>
public sealed class FileWriteSchedulerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_file_write_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly FileWriteTestClock _clock = new(Start);
    private readonly SpyWriter _writer = new();
    private readonly SpyOrganizer _organizer = new();
    private readonly Guid _edition = Guid.NewGuid();

    public FileWriteSchedulerTests()
    {
        Directory.CreateDirectory(_root);
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        Intents = new MediaFileWriteIntentRepository(_database, _clock);
        Queue = new FileWriteQueue(Intents, _configuration, NullLogger<FileWriteQueue>.Instance);
        Scheduler = new FileWriteScheduler(
            Intents, [_writer],
            new LibraryMoveRunner(
                Intents, _organizer, new MediaAssetRepository(_database), new AssetHasher(), _configuration,
                NullLogger<LibraryMoveRunner>.Instance),
            _configuration, _clock, NullLogger<FileWriteScheduler>.Instance);
        var work = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works(id,media_type,work_kind) VALUES(@work,'Music','child');", new { work });
        connection.Execute("INSERT INTO editions(id,work_id) VALUES(@edition,@work);", new { edition = _edition, work });
    }

    private MediaFileWriteIntentRepository Intents { get; }
    private FileWriteQueue Queue { get; }
    private FileWriteScheduler Scheduler { get; }

    private Guid AddAsset(string? path = null)
    {
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root) VALUES(@asset,@edition,@hash,@path);
            """, new { asset, edition = _edition, hash = Guid.NewGuid().ToString("N"), path = path ?? $"C:/music/{asset:N}.flac" });
        return asset;
    }

    private void SaveSettings(Action<WriteBackConfiguration> change)
    {
        var settings = _configuration.LoadConfig<WriteBackConfiguration>(string.Empty, "writeback") ?? new WriteBackConfiguration();
        change(settings);
        _configuration.SaveConfig(string.Empty, "writeback", settings);
    }

    private (string Status, long Generation, int Attempts, string? MoveStep) Row(Guid asset)
    {
        using var connection = _database.CreateConnection();
        return connection.QuerySingle<(string, long, int, string?)>(
            "SELECT status, generation, attempts, move_step FROM media_file_write_intents WHERE asset_id=@asset;", new { asset });
    }

    private void RequestMove(Guid asset)
    {
        using var connection = _database.CreateConnection();
        using var transaction = connection.BeginTransaction();
        MediaFileWriteIntentRepository.UpsertAsset(
            connection, transaction, asset, "library_move", "move:" + Guid.NewGuid().ToString("N"), _clock.GetUtcNow(), requestMove: true);
        transaction.Commit();
    }

    private void PutTitle(Guid asset, string title)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO canonical_values(entity_id, key, value, last_scored_at) VALUES(@asset, 'title', @title, @now)
            ON CONFLICT(entity_id, key) DO UPDATE SET value = excluded.value;
            """, new { asset, title, now = Start.ToString("O") });
    }

    private string TitleOf(Guid asset)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<string>("SELECT value FROM canonical_values WHERE entity_id=@asset AND key='title';", new { asset })!;
    }

    private int RowCount()
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM media_file_write_intents;");
    }

    // ----- changes only join the list ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("quick_hydration")]
    [InlineData("universe_enrichment")]
    [InlineData("manual_override")]
    [InlineData("item_preferences")]
    [InlineData("config_change")]
    [InlineData("artwork")]
    public async Task AChange_AddsOneEntryAndWritesNothing(string trigger)
    {
        var asset = AddAsset();
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);

        await Queue.EnqueueAssetAsync(asset, trigger);
        await Scheduler.TickAsync();

        Assert.Equal(1, RowCount());
        Assert.Equal("pending", Row(asset).Status);
        Assert.Empty(_writer.Written);
    }

    [Fact]
    public async Task FiveChangesToOneFile_GiveOneEntry_AndAfterARunOneWrite()
    {
        var asset = AddAsset();
        foreach (var trigger in new[] { "auto_match", "quick_hydration", "universe_enrichment", "manual_override", "item_preferences" })
        {
            await Queue.EnqueueAssetAsync(asset, trigger);
        }

        Assert.Equal(1, RowCount());
        await Scheduler.RunBatchAsync();

        var write = Assert.Single(_writer.Written);
        Assert.Equal(asset, write.AssetId);
        Assert.Equal("item_preferences", write.Trigger);
        Assert.Equal("verified", Row(asset).Status);
    }

    [Fact]
    public async Task WritingBackOff_QueuesNothing()
    {
        var asset = AddAsset();
        SaveSettings(settings => settings.Enabled = false);

        await Queue.EnqueueAssetAsync(asset, "auto_match");
        await Queue.EnqueueAssetsAsync([asset], "manual_override");

        Assert.Equal(0, RowCount());
    }

    [Fact]
    public async Task ATriggerWhoseOwnSwitchIsOff_QueuesNothing_WhileOthersStillQueue()
    {
        var asset = AddAsset();
        SaveSettings(settings => settings.WriteOnManualOverride = false);

        await Queue.EnqueueAssetAsync(asset, "manual_override");
        Assert.Equal(0, RowCount());

        await Queue.EnqueueAssetAsync(asset, "auto_match");
        Assert.Equal(1, RowCount());
    }

    [Fact]
    public async Task AnIdThatIsNotAMediaFile_IsIgnored()
    {
        await Queue.EnqueueAssetAsync(Guid.NewGuid(), "quick_hydration");

        Assert.Equal(0, RowCount());
    }

    // ----- when a run starts ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARun_StartsOnlyOnceTheIntervalHasPassed_AndWritesEveryWaitingFile()
    {
        var first = AddAsset();
        var second = AddAsset();
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);
        await Queue.EnqueueAssetsAsync([first, second], "auto_match");

        _clock.Advance(TimeSpan.FromMinutes(59));
        await Scheduler.TickAsync();
        Assert.Empty(_writer.Written);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await Scheduler.TickAsync();

        Assert.Equal(2, _writer.Written.Count);
        Assert.Equal("verified", Row(first).Status);
        Assert.Equal("verified", Row(second).Status);
        var run = await Intents.GetRunStateAsync();
        Assert.Equal(2, run!.LastRunWritten);
        Assert.Equal(Start.AddMinutes(60), DateTimeOffset.Parse(run.LastRunFinishedAt!));
    }

    [Fact]
    public async Task TheNextRun_CountsFromWhenTheLastOneFinished()
    {
        var asset = AddAsset();
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);
        await Queue.EnqueueAssetAsync(asset, "auto_match");
        _clock.Advance(TimeSpan.FromMinutes(60));
        await Scheduler.TickAsync();
        Assert.Single(_writer.Written);

        await Queue.EnqueueAssetAsync(asset, "manual_override");
        _clock.Advance(TimeSpan.FromMinutes(59));
        await Scheduler.TickAsync();
        Assert.Single(_writer.Written);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await Scheduler.TickAsync();
        Assert.Equal(2, _writer.Written.Count);
    }

    [Fact]
    public async Task NothingWaiting_NoRunHappens_AndNoneIsRecorded()
    {
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromHours(5));
        await Scheduler.TickAsync();

        Assert.Empty(_writer.Written);
        Assert.Null((await Intents.GetRunStateAsync())!.LastRunFinishedAt);
    }

    [Fact]
    public async Task WriteNow_StartsARunBeforeTheInterval_AndAskingAgainWhileItRunsIsRefused()
    {
        var asset = AddAsset();
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);
        await Queue.EnqueueAssetAsync(asset, "manual_override");
        bool? secondAsk = null;
        _writer.WhileWriting = () => secondAsk = Scheduler.RequestRun();

        Assert.True(Scheduler.RequestRun());
        Assert.False(Scheduler.RequestRun());
        Assert.True(Scheduler.GetProgress().IsRunning);
        await Scheduler.TickAsync();

        Assert.Single(_writer.Written);
        Assert.False(secondAsk);
        Assert.False(Scheduler.GetProgress().IsRunning);
    }

    [Fact]
    public async Task WriteNow_WithNothingWaiting_DoesNothing()
    {
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);

        Assert.True(Scheduler.RequestRun());
        await Scheduler.TickAsync();

        Assert.Empty(_writer.Written);
        Assert.False(Scheduler.GetProgress().IsRunning);
        Assert.Null((await Intents.GetRunStateAsync())!.LastRunFinishedAt);
    }

    [Fact]
    public async Task ARunThatWasDueWhileTheEngineWasOff_RunsAfterTheStartupGrace()
    {
        var asset = AddAsset();
        await Intents.EnsureAnchorAsync(Start.AddHours(-5));
        await Intents.RecordRunAsync(Start.AddHours(-5), Start.AddHours(-5), 1, 0, 0);
        await Queue.EnqueueAssetAsync(asset, "auto_match");
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromSeconds(10));
        await Scheduler.TickAsync();
        Assert.Empty(_writer.Written);

        _clock.Advance(TimeSpan.FromSeconds(21));
        await Scheduler.TickAsync();
        Assert.Single(_writer.Written);
    }

    [Fact]
    public async Task AfterAnIdleSpell_ANewChangeWaitsForTheNextSlot_InsteadOfBeingWrittenWithinSeconds()
    {
        var early = AddAsset();
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(150));
        await Scheduler.TickAsync();

        await Queue.EnqueueAssetAsync(early, "manual_override");
        _clock.Advance(TimeSpan.FromSeconds(2));
        await Scheduler.TickAsync();
        Assert.Empty(_writer.Written);

        // The slots keep their rhythm from the first start: 60, 120, 180 minutes. The next one after 150 minutes is at 180.
        var next = await Scheduler.GetNextRunAtAsync();
        Assert.Equal(Start.AddMinutes(180), next);
        Assert.InRange(next - _clock.GetUtcNow(), TimeSpan.Zero, TimeSpan.FromMinutes(60));

        _clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(2) - TimeSpan.FromSeconds(1));
        await Scheduler.TickAsync();
        Assert.Empty(_writer.Written);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await Scheduler.TickAsync();
        Assert.Single(_writer.Written);
    }

    [Fact]
    public async Task AnIdleSpellNeverMovesTheNextSlotMoreThanOneIntervalAway()
    {
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);

        foreach (var minutes in new[] { 61, 119, 250, 1000 })
        {
            _clock.Advance(TimeSpan.FromMinutes(minutes));
            await Scheduler.TickAsync();
            var wait = await Scheduler.GetNextRunAtAsync() - _clock.GetUtcNow();
            Assert.InRange(wait, TimeSpan.Zero, TimeSpan.FromMinutes(60));
        }
    }

    [Fact]
    public async Task ARefreshLandingWhileAnEntryWaits_LeavesOneEntry_AndTheRunWritesTheRefreshedValues()
    {
        var asset = AddAsset();
        PutTitle(asset, "Old title");
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);
        await Queue.EnqueueAssetAsync(asset, "auto_match");
        var titleAtWriteTime = new List<string>();
        _writer.Next = intent =>
        {
            titleAtWriteTime.Add(TitleOf(intent.AssetId));
            return WriteBackOutcome.Verified();
        };

        // A metadata refresh lands while the file waits: Library saves the new values and asks again.
        PutTitle(asset, "Refreshed title");
        await Queue.EnqueueAssetAsync(asset, "universe_enrichment");
        Assert.Equal(1, RowCount());
        Assert.Equal(2, Row(asset).Generation);

        _clock.Advance(TimeSpan.FromMinutes(60));
        await Scheduler.TickAsync();

        Assert.Equal("Refreshed title", Assert.Single(titleAtWriteTime));
        Assert.Equal("universe_enrichment", Assert.Single(_writer.Written).Trigger);
        Assert.Equal("verified", Row(asset).Status);
    }

    [Fact]
    public async Task AFileChangedOutsideLibrary_IsReadAgainFirst_WritesNothing_AndWaitsWithoutSpendingAnAttempt_ThenIsWrittenOnTheNextRun()
    {
        var asset = AddAsset();
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);
        await Queue.EnqueueAssetAsync(asset, "manual_override");
        var firstTime = true;
        _writer.ReadAgain = _ =>
        {
            var again = firstTime;
            firstTime = false;
            return again;
        };

        _clock.Advance(TimeSpan.FromMinutes(60));
        await Scheduler.TickAsync();

        var waiting = Row(asset);
        Assert.Equal("pending", waiting.Status);
        Assert.Equal(0, waiting.Attempts);
        using (var connection = _database.CreateConnection())
        {
            Assert.Equal("The file changed outside Library; it will be read again before writing.", connection.ExecuteScalar<string>(
                "SELECT last_error FROM media_file_write_intents WHERE asset_id=@asset;", new { asset }));
        }
        Assert.True((await Intents.GetAssetStateAsync(asset)).Waiting);

        _clock.Advance(TimeSpan.FromMinutes(60));
        await Scheduler.TickAsync();

        Assert.Equal("verified", Row(asset).Status);
        Assert.Equal(2, _writer.Written.Count);
    }

    [Fact]
    public async Task WriteNow_WhileWriteBackIsOff_IsRefusedWithAPlainMessage_AndStartsNoRun()
    {
        var asset = AddAsset();
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);
        await Queue.EnqueueAssetAsync(asset, "auto_match");
        SaveSettings(settings => settings.Enabled = false);

        var refused = await SettingsEndpoints.StartFileWriteRunAsync(Scheduler, _configuration, CancellationToken.None);

        var problem = Assert.IsAssignableFrom<IStatusCodeHttpResult>(refused);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Contains("turned off", Assert.IsType<ProblemHttpResult>(refused).ProblemDetails.Detail, StringComparison.Ordinal);
        Assert.False(Scheduler.GetProgress().IsRunning);

        SaveSettings(settings => settings.Enabled = true);
        var accepted = await SettingsEndpoints.StartFileWriteRunAsync(Scheduler, _configuration, CancellationToken.None);
        Assert.Equal(StatusCodes.Status202Accepted, Assert.IsAssignableFrom<IStatusCodeHttpResult>(accepted).StatusCode);
    }

    [Fact]
    public async Task ASettingsChangeToTheInterval_AppliesOnTheNextPass()
    {
        var asset = AddAsset();
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);
        await Queue.EnqueueAssetAsync(asset, "auto_match");
        _clock.Advance(TimeSpan.FromMinutes(5));
        await Scheduler.TickAsync();
        Assert.Empty(_writer.Written);

        SaveSettings(settings => settings.RunIntervalMinutes = 5);
        await Scheduler.TickAsync();

        Assert.Single(_writer.Written);
    }

    [Fact]
    public void TheRunInterval_IsSixtyMinutesByDefault_IsSavedAndReloaded_AndStaysInRange()
    {
        Assert.Equal(TimeSpan.FromMinutes(60), new WriteBackConfiguration().RunInterval);

        SaveSettings(settings => settings.RunIntervalMinutes = 15);
        var reloaded = _configuration.LoadConfig<WriteBackConfiguration>(string.Empty, "writeback")!;
        Assert.Equal(15, reloaded.RunIntervalMinutes);
        Assert.Equal(TimeSpan.FromMinutes(15), reloaded.RunInterval);

        Assert.Equal(TimeSpan.FromMinutes(1), new WriteBackConfiguration { RunIntervalMinutes = -4 }.RunInterval);
        Assert.Equal(TimeSpan.FromMinutes(1440), new WriteBackConfiguration { RunIntervalMinutes = 99999 }.RunInterval);
        Assert.True(WriteBackConfiguration.IsValidRunInterval(1));
        Assert.True(WriteBackConfiguration.IsValidRunInterval(1440));
        Assert.False(WriteBackConfiguration.IsValidRunInterval(0));
        Assert.False(WriteBackConfiguration.IsValidRunInterval(1441));
    }

    [Fact]
    public async Task TheScheduleStatus_ReportsWaitingFailedAndNextRun()
    {
        var asset = AddAsset();
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);
        var empty = await Scheduler.GetScheduleAsync();
        Assert.True(empty.Enabled);
        Assert.Equal(60, empty.IntervalMinutes);
        Assert.Equal(0, empty.WaitingCount);
        Assert.Null(empty.NextRunAt);

        await Queue.EnqueueAssetAsync(asset, "auto_match");
        var waiting = await Scheduler.GetScheduleAsync();

        Assert.Equal(1, waiting.WaitingCount);
        Assert.Equal(Start.AddMinutes(60), waiting.NextRunAt);
        Assert.Null(waiting.LastRunAt);
        Assert.False(waiting.IsRunning);
    }

    // ----- crashes, overlapping changes and retries -------------------------------------------------------------------------

    [Fact]
    public async Task AnEntryInterruptedByAnEngineStop_IsWrittenOnTheNextRun()
    {
        var asset = AddAsset();
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);
        await Queue.EnqueueAssetAsync(asset, "auto_match");
        // The Engine claimed the row and stopped before finishing: it stays "writing" until its claim runs out.
        await Intents.ClaimBatchAsync("media_asset", 10, FileWriteScheduler.LeaseDuration, Start.AddHours(1));
        Assert.Equal("writing", Row(asset).Status);

        _clock.Advance(FileWriteScheduler.LeaseDuration + TimeSpan.FromMinutes(1));
        await Scheduler.RunBatchAsync();

        Assert.Single(_writer.Written);
        Assert.Equal("verified", Row(asset).Status);
    }

    [Fact]
    public async Task AChangeLandingWhileTheFileIsBeingWritten_LeavesTheEntryPendingAtTheNewerGeneration_AndTheNextRunWritesIt()
    {
        var asset = AddAsset();
        await Queue.EnqueueAssetAsync(asset, "auto_match");
        _writer.WhileWriting = () => Queue.EnqueueAssetAsync(asset, "manual_override").GetAwaiter().GetResult();

        await Scheduler.RunBatchAsync();

        // The older write finished, but it did not mark the newer change as done.
        var afterFirst = Row(asset);
        Assert.Equal("pending", afterFirst.Status);
        Assert.Equal(2, afterFirst.Generation);
        Assert.Single(_writer.Written);

        _writer.WhileWriting = null;
        _clock.Advance(TimeSpan.FromMinutes(60));
        await Scheduler.RunBatchAsync();

        Assert.Equal(2, _writer.Written.Count);
        Assert.Equal("manual_override", _writer.Written[1].Trigger);
        Assert.Equal("verified", Row(asset).Status);
    }

    [Fact]
    public async Task AWriteThatFailed_IsRetriedAtTheNextRun_NotStraightAway()
    {
        var asset = AddAsset();
        await Queue.EnqueueAssetAsync(asset, "auto_match");
        _writer.Next = _ => WriteBackOutcome.Failed("The file is in use.");

        await Scheduler.RunBatchAsync();

        Assert.Single(_writer.Written);
        var afterFailure = Row(asset);
        Assert.Equal("pending", afterFailure.Status);
        Assert.Equal(1, afterFailure.Attempts);

        _writer.Next = null;
        _clock.Advance(TimeSpan.FromMinutes(60));
        await Scheduler.RunBatchAsync();

        Assert.Equal(2, _writer.Written.Count);
        Assert.Equal("verified", Row(asset).Status);
    }

    [Fact]
    public async Task AWriteThatKeepsFailing_IsGivenUpAfterTheAttemptLimit_AndListedAsFailed()
    {
        var asset = AddAsset();
        await Queue.EnqueueAssetAsync(asset, "auto_match");
        _writer.Next = _ => WriteBackOutcome.Failed("The file is read-only.");

        for (var run = 0; run < FileWriteScheduler.MaxAttempts; run++)
        {
            await Scheduler.RunBatchAsync();
            _clock.Advance(TimeSpan.FromMinutes(60));
        }

        Assert.Equal("failed", Row(asset).Status);
        var failed = Assert.Single(await Intents.GetFailedAsync());
        Assert.Equal("The file is read-only.", failed.Reason);
        Assert.Equal(1, (await Scheduler.GetScheduleAsync()).FailedCount);
    }

    [Fact]
    public async Task AFileThatCannotHoldTags_IsKeptInLibraryOnly_AndNotRetried()
    {
        var asset = AddAsset();
        await Queue.EnqueueAssetAsync(asset, "auto_match");
        _writer.Next = _ => WriteBackOutcome.Unsupported("No metadata tagger supports this file format.");

        await Scheduler.RunBatchAsync();
        _clock.Advance(TimeSpan.FromMinutes(60));
        await Scheduler.RunBatchAsync();

        Assert.Single(_writer.Written);
        Assert.Equal("unsupported", Row(asset).Status);
        Assert.Equal(1, (await Intents.GetRunStateAsync())!.LastRunKept);
    }

    [Fact]
    public async Task AWriterThatThrows_FailsOnlyItsBatch_AndTheRunStillFinishes()
    {
        var asset = AddAsset();
        await Queue.EnqueueAssetAsync(asset, "auto_match");
        _writer.Throw = true;

        await Scheduler.RunBatchAsync();

        Assert.Equal("pending", Row(asset).Status);
        Assert.False(Scheduler.GetProgress().IsRunning);
        Assert.Equal(1, (await Intents.GetRunStateAsync())!.LastRunFailed);
    }

    [Fact]
    public async Task AKindWithNoWriter_StaysOnTheList_WhileMediaFilesAreStillWritten()
    {
        var asset = AddAsset();
        var item = await AddLocalItemAsync();
        await Queue.EnqueueAssetAsync(asset, "auto_match");
        await Queue.EnqueueViewItemAsync(item, "auto_match");

        await Scheduler.RunBatchAsync();
        await Scheduler.RunBatchAsync();

        Assert.Equal("verified", Row(asset).Status);
        using var connection = _database.CreateConnection();
        Assert.Equal("pending", connection.ExecuteScalar<string>(
            "SELECT status FROM media_file_write_intents WHERE local_item_id=@item;", new { item }));
    }

    [Fact]
    public async Task AFileTheRunJustWrote_IsNotWrittenAgainByTheNextRun()
    {
        var asset = AddAsset();
        await Queue.EnqueueAssetAsync(asset, "auto_match");
        await Scheduler.RunBatchAsync();

        _clock.Advance(TimeSpan.FromMinutes(60));
        await Scheduler.RunBatchAsync();

        Assert.Single(_writer.Written);
    }

    // ----- library moves ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALibraryMove_RunsStraightAway_AndItsTagsWaitForTheScheduledRun()
    {
        var asset = AddAsset();
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);
        using (var connection = _database.CreateConnection())
        {
            using var transaction = connection.BeginTransaction();
            MediaFileWriteIntentRepository.UpsertAsset(
                connection, transaction, asset, "library_move", "move:1", _clock.GetUtcNow(), requestMove: true);
            transaction.Commit();
        }

        _clock.Advance(TimeSpan.FromSeconds(2));
        await Scheduler.TickAsync();

        Assert.Equal([asset], _organizer.Refiled);
        Assert.Empty(_writer.Written);
        var afterMove = Row(asset);
        Assert.Equal("pending", afterMove.Status);
        Assert.Null(afterMove.MoveStep);

        _clock.Advance(TimeSpan.FromMinutes(60));
        await Scheduler.TickAsync();

        Assert.Equal("library_move", Assert.Single(_writer.Written).Trigger);
        Assert.Equal("verified", Row(asset).Status);
    }

    [Fact]
    public async Task AMoveThatFailsThisTime_IsTriedAgainOnTheNextPass_AndGivenUpAfterTheAttemptLimit()
    {
        var asset = AddAsset();
        _organizer.Failure = "The file is in use.";
        using (var connection = _database.CreateConnection())
        {
            using var transaction = connection.BeginTransaction();
            MediaFileWriteIntentRepository.UpsertAsset(
                connection, transaction, asset, "library_move", "move:1", _clock.GetUtcNow(), requestMove: true);
            transaction.Commit();
        }

        await Scheduler.RunMoveLaneAsync(CancellationToken.None);
        Assert.Single(_organizer.Refiled);
        Assert.Equal("pending", Row(asset).Status);
        Assert.Equal("requested", Row(asset).MoveStep);

        // The wait grows after each failure: a minute, five minutes, half an hour, two hours; then the move is given up.
        TimeSpan[] waits = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)];
        for (var pass = 1; pass < FileWriteScheduler.MaxAttempts; pass++)
        {
            var wait = waits[pass - 1];
            _clock.Advance(wait - TimeSpan.FromSeconds(1));
            await Scheduler.RunMoveLaneAsync(CancellationToken.None);
            Assert.Equal(pass, _organizer.Refiled.Count);

            _clock.Advance(TimeSpan.FromSeconds(1));
            await Scheduler.RunMoveLaneAsync(CancellationToken.None);
            Assert.Equal(pass + 1, _organizer.Refiled.Count);
        }

        Assert.Equal(FileWriteScheduler.MaxAttempts, _organizer.Refiled.Count);
        Assert.Equal("failed", Row(asset).Status);
        Assert.Null(Row(asset).MoveStep);
    }

    [Fact]
    public async Task AMoveThatFailed_IsNotTriedAgainWithinSeconds_SoAFileBrieflyInUseIsNotGivenUp()
    {
        var asset = AddAsset();
        _organizer.Failure = "The file is in use.";
        RequestMove(asset);

        for (var pass = 0; pass < 20; pass++)
        {
            await Scheduler.RunMoveLaneAsync(CancellationToken.None);
            _clock.Advance(FileWriteScheduler.TickInterval);
        }

        Assert.Single(_organizer.Refiled);
        Assert.Equal("pending", Row(asset).Status);
        Assert.Equal("requested", Row(asset).MoveStep);
    }

    [Fact]
    public async Task AMoveAskedForDuringALongRun_HappensBetweenChunks_NotAfterTheWholeRun()
    {
        var assets = Enumerable.Range(0, FileWriteScheduler.ChunkSize + 50).Select(_ => AddAsset()).ToList();
        var moving = AddAsset();
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);
        await Queue.EnqueueAssetsAsync(assets, "auto_match");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var refiledWhenTheSecondChunkStarted = -1;
        _writer.WhileWriting = () =>
        {
            if (_writer.Written.Count == 1)
            {
                // The person moves a file while the first chunk is being written.
                _clock.Advance(TimeSpan.FromSeconds(1));
                RequestMove(moving);
            }

            if (_writer.Written.Count == FileWriteScheduler.ChunkSize + 1)
            {
                refiledWhenTheSecondChunkStarted = _organizer.Refiled.Count;
            }
        };

        await Scheduler.RunBatchAsync();

        Assert.Equal(1, refiledWhenTheSecondChunkStarted);
        Assert.Equal([moving], _organizer.Refiled);
        Assert.Equal(assets.Count, _writer.Written.Count(write => assets.Contains(write.AssetId)));
    }

    // ----- a run that has nothing to take ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AFileStuckMidMove_DoesNotStartARunEveryPass_AndTheNextRunStaysInTheFuture()
    {
        var asset = AddAsset();
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);
        RequestMove(asset);
        var claimed = await Intents.ClaimNextMoveAsync(FileWriteScheduler.LeaseDuration);
        await Intents.RecordMovePlannedAsync(claimed!.Id, "C:/old.flac", "C:/new.flac", "hash");
        Assert.Equal(1, (await Scheduler.GetScheduleAsync()).WaitingCount);

        _clock.Advance(TimeSpan.FromMinutes(61));
        for (var pass = 0; pass < 10; pass++)
        {
            await Scheduler.TickAsync();
            _clock.Advance(FileWriteScheduler.TickInterval);
        }

        Assert.Empty(_writer.Written);
        Assert.Null((await Intents.GetRunStateAsync())!.LastRunFinishedAt);
        Assert.True(await Scheduler.GetNextRunAtAsync() > _clock.GetUtcNow());
        Assert.Equal(1, (await Scheduler.GetScheduleAsync()).WaitingCount);
    }

    [Fact]
    public async Task ARunThatFoundNothingToWrite_StillMovesTheScheduleOn()
    {
        await Scheduler.AnchorScheduleAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var wrote = await Scheduler.RunBatchAsync();

        Assert.False(wrote);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(60), await Scheduler.GetNextRunAtAsync());
    }

    // ----- deleting and restoring -------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeletingTheItem_DropsItsWaitingEntry()
    {
        var asset = AddAsset();
        await Queue.EnqueueAssetAsync(asset, "auto_match");

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM media_assets WHERE id=@asset;", new { asset });
        }

        Assert.Equal(0, RowCount());
        await Scheduler.RunBatchAsync();
        Assert.Empty(_writer.Written);
    }

    [Fact]
    public async Task RestoringAFileFromTheRecycleBin_QueuesAFreshEntry_WhenItsItemIsStillInTheLibrary()
    {
        var path = $"C:/music/{Guid.NewGuid():N}.flac";
        var asset = AddAsset(path);
        var restorer = new RestoredFileWriteQueuer(new MediaAssetRepository(_database), Queue);

        Assert.True(await restorer.QueueAsync(path));
        Assert.False(await restorer.QueueAsync("C:/music/not-in-the-library.flac"));

        Assert.Equal(1, RowCount());
        using var connection = _database.CreateConnection();
        Assert.Equal("restore", connection.ExecuteScalar<string>(
            "SELECT trigger FROM media_file_write_intents WHERE asset_id=@asset;", new { asset }));
    }

    // ----- results and the old rules ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(WriteBackOutcomeKind.Verified, 1, "verified")]
    [InlineData(WriteBackOutcomeKind.Blocked, 1, "blocked")]
    [InlineData(WriteBackOutcomeKind.Unsupported, 1, "unsupported")]
    [InlineData(WriteBackOutcomeKind.Unverified, 1, "pending")]
    [InlineData(WriteBackOutcomeKind.Failed, 1, "pending")]
    [InlineData(WriteBackOutcomeKind.Unverified, 5, "failed")]
    [InlineData(WriteBackOutcomeKind.Failed, 5, "failed")]
    public void MapCompletion_PreservesTruthfulOutcomeAndBoundsRetries(
        WriteBackOutcomeKind kind, int attempts, string expected)
    {
        var intent = new MediaFileWriteIntent { Attempts = attempts };
        var result = FileWriteScheduler.MapCompletion(intent, new WriteBackOutcome(kind, "evidence"));

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == "verified" ? null : "evidence", result.Error);
    }

    private async Task<Guid> AddLocalItemAsync()
    {
        var id = Guid.NewGuid();
        var shared = await new ViewSharedLibraryRepository(_database).GetAsync();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO local_items (id, scope_kind, library_id, media_kind, primary_file_name, primary_mime_type, created_at, updated_at)
            VALUES (@id, 'shared', @library, 'image', 'holiday.jpg', 'image/jpeg', @now, @now);
            """, new { id, library = shared.LibraryId, now = Start.ToString("O") });
        return id;
    }

    public void Dispose()
    {
        _configuration.Dispose();
        TestTemp.ReleaseSqlitePool(Path.Combine(_root, "library.db"));
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }

    /// <summary>Writes nothing; remembers each file it was asked to write, and answers as the test says.</summary>
    private sealed class SpyWriter : IFileWriteTargetWriter
    {
        public List<MediaFileWriteIntent> Written { get; } = [];

        public Func<MediaFileWriteIntent, WriteBackOutcome>? Next { get; set; }

        public Action? WhileWriting { get; set; }

        public Func<MediaFileWriteIntent, bool>? ReadAgain { get; set; }

        public bool Throw { get; set; }

        public string TargetKind => MediaFileWriteIntentRepository.MediaAssetKind;

        public Task<IReadOnlyList<FileWriteResult>> WriteBatchAsync(IReadOnlyList<MediaFileWriteIntent> intents, CancellationToken ct)
        {
            if (Throw)
            {
                throw new IOException("The writer fell over.");
            }

            var results = new List<FileWriteResult>();
            foreach (var intent in intents)
            {
                Written.Add(intent);
                WhileWriting?.Invoke();
                results.Add(ReadAgain?.Invoke(intent) == true
                    ? new FileWriteResult(intent.Id, WriteBackOutcome.Failed("The file changed outside Library; it will be read again before writing."), ReadAgainFirst: true)
                    : new FileWriteResult(intent.Id, Next?.Invoke(intent) ?? WriteBackOutcome.Verified()));
            }

            return Task.FromResult<IReadOnlyList<FileWriteResult>>(results);
        }
    }

    /// <summary>Records every re-file the move lane asks for; moves no file.</summary>
    private sealed class SpyOrganizer : IAutoOrganizeService
    {
        public List<Guid> Refiled { get; } = [];

        public string? Failure { get; set; }

        public Task TryAutoOrganizeAsync(Guid assetId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<AssetRefilePlan> PlanRefileAsync(Guid assetId, AssetRefilePlacement? placement, CancellationToken ct = default) =>
            Task.FromResult(AssetRefilePlan.Of(AssetRefileDisposition.AlreadyFits, string.Empty));

        public Task<AssetRefileOutcome> RefileAsync(Guid assetId, CancellationToken ct = default)
        {
            Refiled.Add(assetId);
            return Task.FromResult(Failure is null
                ? new AssetRefileOutcome(AssetRefileOutcomeKind.AlreadyFits, AssetRefileDisposition.AlreadyFits, string.Empty, null)
                : new AssetRefileOutcome(AssetRefileOutcomeKind.Failed, AssetRefileDisposition.AlreadyFits, string.Empty, Failure));
        }
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FileWriteTestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
