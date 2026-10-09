namespace MediaEngine.Ingestion.Tests;

public sealed class IngestionPipelineCharacterizationTests
{
    [Fact]
    public void Restart_ResumesIncompleteRegistrationBeforeFingerprintShortcut()
    {
        var source = ReadRepoSource(@"src/MediaEngine.Ingestion/IngestionEngine.Watching.cs");
        var recovery = Find(source, "var trackedOperation = await GetTrackedIngestionOperationAsync");
        var fingerprint = Find(source, "if (fingerprintIsCurrent)");
        Assert.True(recovery < fingerprint);
        Assert.Contains("trackedOperation is null || IsTerminalMediaOperation(trackedOperation)", source);
    }

    [Fact]
    public void Restart_RepairsMissingIdentityWithoutChangingSourceOrBypassingLocalPolicy()
    {
        var source = ReadRepoSource(@"src/MediaEngine.Ingestion/IngestionEngine.Pipeline.cs");
        var recovery = Find(source, "var missingIdentityJob = context.Library?.BypassesExternalIdentity != true");
        var refresh = Find(source, "if (contentChanged || missingIdentityJob)", recovery);
        var fail = Find(source, "if (missingIdentityJob && !metadataRefreshed)", refresh);
        var completion = Find(source, "CompleteOperationAsync(durableOperation, \"same_path_redetected\"", fail);
        Assert.True(recovery < refresh && refresh < fail && fail < completion);
        Assert.Contains("queueIdentityRefresh: context.Library?.BypassesExternalIdentity != true", source);
    }

    [Fact]
    public void CandidatePipeline_HoldsHashLockAcrossEveryMutatingStage()
    {
        var source = ReadIngestionEngineSources();
        var stageOrder = Find(source, "new DelegateIngestionStage(\"settle/detect\"");
        var hashStage = Find(source, "new DelegateIngestionStage(\"hash/dedupe\"", stageOrder);
        var processStage = Find(source, "new DelegateIngestionStage(\"process\"", hashStage);
        var scoreStage = Find(source, "new DelegateIngestionStage(\"score/identify\"", processStage);
        var organizeStage = Find(source, "new DelegateIngestionStage(\"organize\"", scoreStage);
        var writeBackStage = Find(source, "new DelegateIngestionStage(\"write-back\"", organizeStage);
        var identityStage = Find(source, "new DelegateIngestionStage(\"identity-job creation\"", writeBackStage);

        Assert.True(stageOrder < hashStage);
        Assert.True(hashStage < processStage);
        Assert.True(processStage < scoreStage);
        Assert.True(scoreStage < organizeStage);
        Assert.True(organizeStage < writeBackStage);
        Assert.True(writeBackStage < identityStage);
        Assert.Contains("context.HashLock = _concurrencyGuard.GetHashLock", source, StringComparison.Ordinal);
        Assert.Contains("if (context.HashLock is not null && context.Hash is not null)", source, StringComparison.Ordinal);
        Assert.Contains("context.HashLock.Release()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidatePipeline_DefersOrganizationUntilRetailIdentityWork()
    {
        var source = ReadRepoSource(@"src/MediaEngine.Ingestion/IngestionEngine.Pipeline.cs");
        var pipelineStart = Find(source, "private async Task RunOrganizeStageAsync");
        var pipelineEnd = Find(source, "private async Task RunWriteBackStageAsync", pipelineStart);
        var pipeline = source[pipelineStart..pipelineEnd];

        Assert.Contains(
            "AutoOrganizeService moves them directly to the library after Stage 1 retail match.",
            pipeline,
            StringComparison.Ordinal);
        Assert.Contains("_gate.Evaluate(", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("_organizer.Organize", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("_organizer.Execute", pipeline, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidatePipeline_CreatesReviewBeforeIdentityJob()
    {
        var source = ReadRepoSource(@"src/MediaEngine.Ingestion/IngestionEngine.Pipeline.cs");
        var gate = Find(source, "_gate.Evaluate(");
        var review = Find(source, "CreateIngestionReviewItemAsync(", gate);
        var identityJob = Find(source, "_identityJobRepo.CreateAsync", review);

        Assert.True(gate < review);
        Assert.True(review < identityJob);
    }

    [Fact]
    public void CandidatePipeline_PreservesIdentityJobWhenWriteBackFails()
    {
        var source = ReadRepoSource(@"src/MediaEngine.Ingestion/IngestionEngine.Pipeline.cs");
        var deferredFailure = Find(source, "context.DeferredWriteBackFailure = ex;");
        var identityJob = Find(source, "_identityJobRepo.CreateAsync", deferredFailure);
        var rethrow = Find(source, ".Capture(context.DeferredWriteBackFailure)", identityJob);

        Assert.True(deferredFailure < identityJob);
        Assert.True(identityJob < rethrow);
    }

    [Fact]
    public void IngestionEngine_FacadeAndImplementationFilesStayWithinSizeRatchets()
    {
        var mainPath = FindRepoFile(@"src/MediaEngine.Ingestion/IngestionEngine.cs");
        Assert.True(
            File.ReadLines(mainPath).Count() < 500,
            "IngestionEngine.cs must remain a small public facade.");

        var oversized = Directory
            .EnumerateFiles(
                Path.GetDirectoryName(mainPath)!,
                "IngestionEngine*.cs",
                SearchOption.TopDirectoryOnly)
            .Select(path => new { Path = path, Lines = File.ReadLines(path).Count() })
            .Where(file => file.Lines > 1500)
            .Select(file => $"{Path.GetFileName(file.Path)}: {file.Lines}")
            .ToList();

        Assert.Empty(oversized);
    }

    private static string ReadIngestionEngineSources()
    {
        var mainPath = FindRepoFile(@"src/MediaEngine.Ingestion/IngestionEngine.cs");
        var pipelinePath = Path.Combine(
            Path.GetDirectoryName(mainPath)!,
            "IngestionEngine.Pipeline.cs");
        return File.ReadAllText(mainPath)
            + Environment.NewLine
            + File.ReadAllText(pipelinePath);
    }

    private static int Find(string source, string value, int start = 0)
    {
        var index = source.IndexOf(value, start, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Expected source to contain '{value}'.");
        return index;
    }

    private static string ReadRepoSource(
        string relativePath,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
        return File.ReadAllText(Path.Combine(root, relativePath));
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
