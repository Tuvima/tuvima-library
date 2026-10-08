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
            conn.Execute("INSERT INTO works(id,media_type) VALUES(@work,'Audiobooks'); INSERT INTO editions(id,work_id) VALUES(@edition,@work);", new {work,edition});
            foreach (var asset in new[] {first,second})
            {
                conn.Execute("INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,status) VALUES(@asset,@edition,@hash,@path,'Normal');", new {asset,edition,hash=asset.ToString(),path=asset.ToString()+".mp3"});
            }
            conn.Execute("INSERT INTO identity_jobs(id,entity_id,entity_type,media_type,state) VALUES(@id,@first,'MediaAsset','Audiobooks','Queued');",new {id=Guid.NewGuid(),first});
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
}
