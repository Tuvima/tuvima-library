using Dapper;

namespace MediaEngine.Storage.Tests;

public sealed class IngestionAvailabilityTests
{
    [Fact]
    public async Task RecordingLocksAllPartsDuringIdentityAndUnlocksAtHumanReview()
    {
        var path = Path.Combine(Path.GetTempPath(), $"availability-{Guid.NewGuid():N}.db");
        using var db = new DatabaseConnection(path);
        db.InitializeSchema();
        DapperConfiguration.Configure();
        var work = Guid.NewGuid(); var edition = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        using (var conn = db.CreateConnection())
        {
            conn.Execute("INSERT INTO works(id,media_type) VALUES(@work,'Audiobooks'); INSERT INTO editions(id,work_id) VALUES(@edition,@work);", new { work, edition });
            foreach (var asset in new[] { first, second })
            {
                conn.Execute("INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,status) VALUES(@asset,@edition,@hash,@path,'Normal');", new { asset, edition, hash = asset.ToString(), path = asset.ToString() + ".mp3" });
            }
            conn.Execute("INSERT INTO identity_jobs(id,entity_id,entity_type,media_type,state) VALUES(@id,@first,'MediaAsset','Audiobooks','Queued');", new { id = Guid.NewGuid(), first });
        }
        Assert.True(await IngestionAvailability.IsUpdatingAsync(db, work));
        Assert.True(await IngestionAvailability.IsUpdatingAsync(db, second));
        using (var conn = db.CreateConnection())
        {
            conn.Execute("UPDATE identity_jobs SET state='QidNeedsReview'");
        }
        Assert.False(await IngestionAvailability.IsUpdatingAsync(db, work));
        Assert.False(await IngestionAvailability.IsUpdatingAsync(db, second));
        db.Dispose();
        try { File.Delete(path); } catch (IOException) { /* SQLite may retain a pooled handle until test-host exit. */ }
    }

    // After Save the editor shows no box (editor v7 D12): the item's own Updating state is the whole signal. A saved match queues the
    // item's identity job, so it reads as updating while the job works, and stops reading as updating the moment a step stops, whether
    // it finished or failed (a failure then waits in Needs Review, which is the item's normal warning).
    [Theory]
    [InlineData("Queued", true)]
    [InlineData("RetailSearching", true)]
    [InlineData("BridgeSearching", true)]
    [InlineData("Hydrating", true)]
    [InlineData("UniverseEnriching", true)]
    [InlineData("Ready", false)]
    [InlineData("ReadyWithoutUniverse", false)]
    [InlineData("Failed", false)]
    [InlineData("RetailNoMatch", false)]
    [InlineData("QidNoMatch", false)]
    [InlineData("QidNeedsReview", false)]
    public async Task AnItemShowsUpdatingWhileItsJobWorks_AndStopsWhenAStepFinishesOrFails(string state, bool updating)
    {
        var path = Path.Combine(Path.GetTempPath(), $"availability-{Guid.NewGuid():N}.db");
        using var db = new DatabaseConnection(path);
        db.InitializeSchema();
        DapperConfiguration.Configure();
        var work = Guid.NewGuid(); var edition = Guid.NewGuid(); var asset = Guid.NewGuid();
        using (var conn = db.CreateConnection())
        {
            conn.Execute("INSERT INTO works(id,media_type) VALUES(@work,'Movies'); INSERT INTO editions(id,work_id) VALUES(@edition,@work);", new { work, edition });
            conn.Execute("INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,status) VALUES(@asset,@edition,@hash,@path,'Normal');", new { asset, edition, hash = asset.ToString(), path = asset.ToString() + ".mkv" });
            conn.Execute("INSERT INTO identity_jobs(id,entity_id,entity_type,media_type,state) VALUES(@id,@asset,'MediaAsset','Movies',@state);", new { id = Guid.NewGuid(), asset, state });
        }

        Assert.Equal(updating, await IngestionAvailability.IsUpdatingAsync(db, work));
        db.Dispose();
        try { File.Delete(path); } catch (IOException) { /* SQLite may retain a pooled handle until test-host exit. */ }
    }
}
