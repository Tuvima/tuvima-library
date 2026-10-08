using System.Reflection;
using System.Text;
using MediaEngine.Api.Services.Details;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class SequenceContainerMetadataTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public SequenceContainerMetadataTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_sequence_metadata_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    [Fact]
    public async Task LoadSequenceContainerMetadata_NormalizesSqliteValuesToText()
    {
        var collectionId = Guid.NewGuid();
        const string description = "A partially enriched series description.";
        const string wikipediaUrl = "https://en.wikipedia.org/wiki/Example_series";

        using (var conn = _db.CreateConnection())
        {
            using (var command = conn.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO collections (id, display_name, collection_type, description, created_at)
                    VALUES ($id, 'Example Series', 'Series', $description, datetime('now'));
    
                    INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES ($id, 'wikipedia_url', $wikipediaUrl, datetime('now'));
                    """;
                command.Parameters.AddWithValue("$id", GuidSql.ToBlob(collectionId));
                // SQLite permits BLOB storage in TEXT-affinity columns. This is the shape that
                // previously caused Dapper to seek a byte[] constructor on the metadata row.
                command.Parameters.AddWithValue("$description", Encoding.UTF8.GetBytes(description));
                command.Parameters.AddWithValue("$wikipediaUrl", Encoding.UTF8.GetBytes(wikipediaUrl));
                command.ExecuteNonQuery();
            }
        }

        var metadata = await InvokeLoadSequenceContainerMetadataAsync(collectionId.ToString("D"));

        Assert.NotNull(metadata);
        Assert.Equal(description, ReadStringProperty(metadata, "Description"));
        Assert.Equal(wikipediaUrl, ReadStringProperty(metadata, "WikipediaUrl"));
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task<object?> InvokeLoadSequenceContainerMetadataAsync(string containerId)
    {
        var composer = new DetailComposerService(
            _db,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            new DetailRecommendationService(_db));
        var implementation = typeof(DetailComposerService)
            .GetField("_composer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(composer)!;
        var method = implementation.GetType().GetMethod(
            "LoadSequenceContainerMetadataAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(DetailComposerService), "LoadSequenceContainerMetadataAsync");

        var task = (Task)method.Invoke(implementation, [containerId, null, CancellationToken.None])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private static string? ReadStringProperty(object instance, string propertyName) =>
        (string?)instance.GetType().GetProperty(propertyName)!.GetValue(instance);
}
