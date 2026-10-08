using Dapper;

namespace MediaEngine.Storage.Tests;

public sealed class MediaFileWriteIntentRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_write_intent_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _asset = Guid.NewGuid();

    public MediaFileWriteIntentRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        using var connection = _database.CreateConnection();
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        connection.Execute("INSERT INTO works(id,media_type,work_kind) VALUES(@work,'Movies','child');", new { work });
        connection.Execute("INSERT INTO editions(id,work_id) VALUES(@edition,@work);", new { edition, work });
        connection.Execute("""
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root)
            VALUES(@asset,@edition,@hash,'C:/fixture/movie.mp4');
            INSERT INTO media_file_write_intents
                (asset_id,generation,operation_token,trigger,status,attempts,created_at,updated_at)
            VALUES(@asset,1,'op-1','editor_commit','pending',0,@now,@now);
            """, new
        {
            asset = _asset,
            edition,
            hash = Guid.NewGuid().ToString("N"),
            now = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    [Fact]
    public async Task ExpiredLeaseIsRecoveredAndCompletionIsGenerationGuarded()
    {
        var repository = new MediaFileWriteIntentRepository(_database);
        var first = await repository.ClaimNextAsync(TimeSpan.FromMinutes(1));
        Assert.NotNull(first);
        Assert.Equal("writing", first.Status);
        Assert.Equal(1, first.Attempts);

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                    UPDATE media_file_write_intents
                    SET generation=2, operation_token='op-2', status='pending', lease_expires_at=NULL
                    WHERE asset_id=@asset;
                    """, new { asset = _asset });
        }

        Assert.False(await repository.CompleteAsync(_asset, 1, "verified"));
        var latest = await repository.ClaimNextAsync(TimeSpan.FromMinutes(1));
        Assert.NotNull(latest);
        Assert.Equal(2, latest.Generation);
        Assert.Equal("op-2", latest.OperationToken);
        Assert.True(await repository.CompleteAsync(_asset, 2, "verified"));
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch { }
        try { File.Delete(_path); } catch { }
    }
}
