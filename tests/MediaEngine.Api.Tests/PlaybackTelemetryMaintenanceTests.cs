using Dapper;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Activity;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The maintenance pass closes sessions that stopped reporting and never deletes history: playback telemetry and
/// each person's own activity record are kept until a server administrator removes them by hand.
/// </summary>
public sealed class PlaybackTelemetryMaintenanceTests
{
    private static readonly DateTimeOffset LongAgo = new(2020, 1, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AMaintenancePass_ClosesAStaleSession_ButKeepsEveryOldEndedRowForever()
    {
        await using var world = PersonalActivityWorld.Create();
        var (account, profile) = (world.MemberA.AccountId, world.MemberA.ProfileId);
        var ended = Guid.NewGuid();
        var stale = Guid.NewGuid();
        using (var connection = world.Database.CreateConnection())
        {
            // Well past the old 365-day limit: an ended session, and one that stopped reporting without closing.
            connection.Execute("""
                INSERT INTO playback_telemetry_sessions
                  (id,player_session_id,account_id,profile_id,library_id,media_type,feature_id,state,started_at,last_observed_at,ended_at,
                   started_position_seconds,last_position_seconds,played_duration_seconds,completion_reason)
                VALUES
                  (@ended,@endedPlayer,@account,@profile,@library,'Movies','watch','stopped',@start,@end,@end,0,100,100,'stopped'),
                  (@stale,@stalePlayer,@account,@profile,@library,'Movies','watch','playing',@start,@end,NULL,0,50,50,NULL);
                """, new
            {
                ended,
                stale,
                endedPlayer = Guid.NewGuid(),
                stalePlayer = Guid.NewGuid(),
                account,
                profile,
                library = Guid.NewGuid(),
                start = LongAgo.ToString("O"),
                end = LongAgo.AddMinutes(30).ToString("O"),
            });
        }

        await world.Repository.AddPlaybackAsync(profile, PersonalActivityKinds.Watch, false, stale, LongAgo,
            new PersonalActivityDevice("Dashboard", "web"),
            new PersonalActivitySnapshot("Movies", "Tremors", null, null, 1990, Guid.NewGuid(), Guid.NewGuid(), null),
            new PersonalActivityProgress(LongAgo.AddMinutes(30), 50, 10, false));

        var service = new PlaybackTelemetryMaintenanceService(
            new PlaybackTelemetryRepository(world.Database, new ApplicationEventOutboxWriter()),
            PersonalActivityTestKit.CreateRecorder(world.Database),
            TimeProvider.System,
            NullLogger<PlaybackTelemetryMaintenanceService>.Instance);
        await service.StartAsync(default);
        try
        {
            await WaitForAsync(() => IsClosed(world, stale));
        }
        finally
        {
            await service.StopAsync(default);
        }

        using var check = world.Database.CreateConnection();
        Assert.Equal(2, check.ExecuteScalar<int>("SELECT COUNT(*) FROM playback_telemetry_sessions;"));
        Assert.Equal(1, check.ExecuteScalar<int>("SELECT COUNT(*) FROM playback_telemetry_sessions WHERE id = @ended AND ended_at IS NOT NULL;", new { ended }));
        var activity = Assert.Single((await world.Repository.GetItemsAsync(
            new PersonalActivityFilter { ProfileIds = new HashSet<Guid> { profile } }, PersonalActivityGrouping.None, null, 10)).Items);
        Assert.Equal(LongAgo.AddMinutes(30), Assert.Single(activity.Entries).EndedAt);
    }

    private static bool IsClosed(PersonalActivityWorld world, Guid session)
    {
        using var connection = world.Database.CreateConnection();
        return connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM playback_telemetry_sessions WHERE id = @session AND ended_at IS NOT NULL;", new { session }) == 1;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The maintenance pass did not close the stale session in time.");
            await Task.Delay(50);
        }
    }
}
