namespace MediaEngine.Api.Tests;

/// <summary>
/// Nothing in <c>src/</c> keeps ingestion batches or runs (spec ingestion-and-readiness D12 and D14): no batch tables or
/// repositories, no batch counters or progress events, no run / batch link on a work row, and no batch gate. The upgrade
/// steps are the one place the old names stay, because they are what removed them.
/// </summary>
public sealed class IngestionBatchRemovalSourceGuardrailTests
{
    private static readonly string[] Forbidden =
    [
        "IngestionBatch",
        "BatchProgress",
        "BatchCounter",
        "SafeIncrementBatch",
        "ingestion_batch",
        "ingestion_run_id",
        "IngestionRunId",
        "ingestionRunId",
        "batch_id",
        "BatchId",
        "BatchGate",
        "batch_gate",
    ];

    private static readonly string[] TextExtensions = [".cs", ".razor", ".js", ".mjs", ".json", ".css", ".sql"];

    private const string UpgradeSteps = "src/MediaEngine.Storage/Schema/Upgrades/";

    // Its header names the latest upgrade step; the structure itself is checked by the second test.
    private const string Snapshot = "src/MediaEngine.Storage/Schema/schema.snapshot.sql";

    [Fact]
    public void Source_NamesNoIngestionBatchRunOrBatchGate()
    {
        var root = FindRepoRoot();
        var offences = new List<string>();
        // config/ too, so the removed Wikidata batch gate cannot come back as a setting.
        foreach (var file in new[] { "src", "config" }.SelectMany(folder =>
                     Directory.EnumerateFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories)))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsBuildOutput(relative)
                || relative.StartsWith(UpgradeSteps, StringComparison.Ordinal)
                || relative == Snapshot
                || !TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            offences.AddRange(Forbidden
                .Where(token => source.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{relative} contains '{token}'"));
        }

        Assert.True(offences.Count == 0, string.Join(Environment.NewLine, offences));
    }

    [Fact]
    public void DataStoreStructure_HasNoBatchTablesOrRunLinks()
    {
        var root = FindRepoRoot();
        var snapshot = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Storage/Schema/schema.snapshot.sql"));

        Assert.DoesNotContain("CREATE TABLE ingestion_batch", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("ingestion_run_id", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("batch_id", snapshot, StringComparison.Ordinal);
    }

    private static bool IsBuildOutput(string relativePath) =>
        relativePath.Contains("/bin/", StringComparison.Ordinal)
        || relativePath.Contains("/obj/", StringComparison.Ordinal)
        || relativePath.Contains("/node_modules/", StringComparison.Ordinal);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
