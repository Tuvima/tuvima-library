namespace MediaEngine.Api.Tests;

/// <summary>
/// The Engine's read side, the wire contracts and the Dashboard know nothing about ingestion batches or runs
/// (spec ingestion-and-readiness D12 and D14). The pipeline's own batch rows are removed separately.
/// </summary>
public sealed class IngestionBatchRemovalGuardrailTests
{
    private static readonly string[] Roots =
    [
        "src/MediaEngine.Api/Endpoints",
        "src/MediaEngine.Api/Services/ReadServices",
        "src/MediaEngine.Contracts",
        "src/MediaEngine.Web",
    ];

    private static readonly string[] Forbidden =
    [
        "IngestionBatch",
        "ActivityBatch",
        "BatchId",
        "batch_id",
        "ingestion_run_id",
        "runId",
        "/batches",
    ];

    // The progress event goes with its emitter when the pipeline stops writing batches.
    private static readonly string[] Allowed =
    [
        "src/MediaEngine.Contracts/Realtime/IngestionEvents.cs",
    ];

    private static readonly string[] TextExtensions = [".cs", ".razor", ".js", ".mjs", ".json", ".css"];

    [Fact]
    public void ReadSideContractsAndDashboard_NameNoIngestionBatchOrRun()
    {
        var root = FindRepoRoot();
        var offences = new List<string>();
        foreach (var relativeRoot in Roots)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, relativeRoot), "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (IsBuildOutput(relative) || Allowed.Contains(relative, StringComparer.Ordinal)
                    || !TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var source = File.ReadAllText(file);
                offences.AddRange(Forbidden
                    .Where(token => source.Contains(token, StringComparison.Ordinal))
                    .Select(token => $"{relative} contains '{token}'"));
            }
        }

        Assert.True(offences.Count == 0, string.Join(Environment.NewLine, offences));
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
