namespace MediaEngine.Storage.Tests;

public sealed class Phase5EditorPersistenceTests
{
    [Fact]
    public void StorageLayerContainsPersistenceForPhase5EditorState()
    {
        var root = FindRepoRoot();
        var reviewRepo = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Storage/ReviewQueueRepository.cs"));
        var schema = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Storage/Schema/schema.snapshot.sql"));
        var databaseConnection = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Storage/DatabaseConnection.cs"));

        Assert.Contains("UpdateStatusAsync", reviewRepo, StringComparison.Ordinal);
        Assert.Contains("DataStoreUpgradeRunner", databaseConnection, StringComparison.Ordinal);
        Assert.Contains("review_queue", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("canonical_values", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("metadata_claims", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("entity_assets", schema, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
