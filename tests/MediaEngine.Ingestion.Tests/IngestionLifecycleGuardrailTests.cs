namespace MediaEngine.Ingestion.Tests;

public sealed class IngestionLifecycleGuardrailTests
{
    [Fact]
    public void IngestionEngine_ReusesTrackedOperationsBeforeCreatingBatches()
    {
        var source = ReadIngestionEngineSource();
        var contract = ReadRepoSource(@"src/MediaEngine.Domain/Contracts/IMediaOperationRepository.cs");
        var watcher = ReadRepoSource(@"src/MediaEngine.Ingestion/FileWatcher.cs");

        Assert.Contains("GetByIdempotencyKeyAsync", contract, StringComparison.Ordinal);
        Assert.Contains("GetActiveBySourcePathAsync", contract, StringComparison.Ordinal);
        Assert.Contains("GetLatestBySourcePathAsync", contract, StringComparison.Ordinal);
        Assert.Contains("GetTrackedIngestionOperationAsync", source, StringComparison.Ordinal);
        Assert.Contains("BuildIngestionOperationKey(path)", source, StringComparison.Ordinal);
        Assert.Contains("GetActiveBySourcePathAsync(Path.GetFullPath(path), ct)", source, StringComparison.Ordinal);
        Assert.Contains("GetLatestBySourcePathAsync(Path.GetFullPath(path), ct)", source, StringComparison.Ordinal);
        Assert.Contains("RequeueTrackedIngestionOperationAsync", source, StringComparison.Ordinal);
        Assert.Contains("if (Directory.Exists(normalizedPath))", source, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(extension)", source, StringComparison.Ordinal);
        Assert.Contains("!File.Exists(normalizedPath)", source, StringComparison.Ordinal);
        Assert.Contains("var newEvents = new List<FileEvent>();", source, StringComparison.Ordinal);
        Assert.Contains("var resumedEvents = new List<FileEvent>();", source, StringComparison.Ordinal);
        Assert.Matches(@"FilesTotal\s*=\s*newEvents\.Count", source);
        Assert.Contains("ResolveBufferedBatchSourcePath(newEvents)", source, StringComparison.Ordinal);
        Assert.Contains("GetActiveAsync(ct)", source, StringComparison.Ordinal);
        Assert.Contains("IncrementCounterByAsync", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"FilesTotal\s*=\s*snapshot\.Count", source);
        Assert.DoesNotContain("_ = EnsureIngestionOperationAsync(normalizedEvent, MediaOperationStage.Discovered, CancellationToken.None);\r\n\r\n        // Events from ScanExistingFiles already have a batch", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NotifyFilters.DirectoryName", watcher, StringComparison.Ordinal);
    }

    private static string ReadRepoSource(
        string relativePath,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var directory = !string.IsNullOrWhiteSpace(sourceFile)
            ? new DirectoryInfo(Path.GetDirectoryName(sourceFile)!)
            : new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
            {
                directory = directory.Parent;
            }
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
        return File.ReadAllText(Path.Combine(root, relativePath));
    }

    private static string ReadIngestionEngineSource()
    {
        var enginePath = FindRepoFile(@"src/MediaEngine.Ingestion/IngestionEngine.cs");
        return string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                    Path.GetDirectoryName(enginePath)!,
                    "IngestionEngine*.cs",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(file => file, StringComparer.Ordinal)
                .Select(File.ReadAllText));
    }

    private static string FindRepoFile(
        string relativePath,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
        return Path.Combine(root, relativePath);
    }
}
