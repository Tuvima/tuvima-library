namespace MediaEngine.Api.Tests;

public sealed class AiBackgroundServiceGuardrailTests
{
    [Theory]
    [InlineData("VibeBatchService.cs")]
    [InlineData("SeriesAlignmentBackgroundService.cs")]
    [InlineData("DescriptionIntelligenceBatchService.cs")]
    public void AiWorkers_UseExplicitDependenciesAndDurableFeatureOutcomes(string fileName)
    {
        var source = File.ReadAllText(GetRepoFilePath($@"src/MediaEngine.Api/Services/{fileName}"));

        Assert.DoesNotContain("IServiceScopeFactory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetRequiredService<", source, StringComparison.Ordinal);
        Assert.Contains("IAiFeaturePersistenceRepository", source, StringComparison.Ordinal);
        Assert.Contains("OperationCanceledException", source, StringComparison.Ordinal);
        Assert.Contains("RecordAiFeatureFailureAsync", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("VibeBatchService.cs")]
    [InlineData("SeriesAlignmentBackgroundService.cs")]
    [InlineData("DescriptionIntelligenceBatchService.cs")]
    public void LaunchAiWorkers_GateBeforeScanning(string fileName)
    {
        var source = File.ReadAllText(GetRepoFilePath($@"src/MediaEngine.Api/Services/{fileName}"));
        var gate = source.IndexOf("_featureGate.CanExecute", StringComparison.Ordinal);
        var scan = source.IndexOf("GetPageAsync", StringComparison.Ordinal);
        if (scan < 0)
        {
            scan = source.IndexOf("GetEntitiesNeedingEnrichmentAsync", StringComparison.Ordinal);
        }

        Assert.True(gate >= 0, $"{fileName} must use the shared feature gate.");
        Assert.True(scan < 0 || gate < scan, $"{fileName} must gate before its first scan.");
    }

    [Fact]
    public void CanonicalAiWorkers_UseAtomicFeatureReplacement()
    {
        foreach (var fileName in new[]
                 {
                     "VibeBatchService.cs",
                     "SeriesAlignmentBackgroundService.cs",
                     "DescriptionIntelligenceBatchService.cs",
                 })
        {
            var source = File.ReadAllText(GetRepoFilePath($@"src/MediaEngine.Api/Services/{fileName}"));
            Assert.Contains("ReplaceAiFeatureAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("new CanonicalValue", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EngineRegistersFeatureAndTastePersistenceContracts()
    {
        var source = File.ReadAllText(GetRepoFilePath(
            @"src/MediaEngine.Api/DependencyInjection/TuvimaStorageServiceCollectionExtensions.cs"));

        Assert.Contains("IAiFeaturePersistenceRepository", source, StringComparison.Ordinal);
        Assert.Contains("ITasteProfileRepository, TasteProfileRepository", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupDownloadsOnlySelectedTextAndExplicitAudioPack()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/ModelAutoDownloadService.cs"));

        Assert.Contains("DownloadIfNeededAsync(AiModelRole.TextQuality", source, StringComparison.Ordinal);
        Assert.Contains("if (_settings.AudioPackEnabled)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AiModelRole.TextFast,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AiModelRole.TextScholar,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldDownloadCjkModel", source, StringComparison.Ordinal);
    }

    private static string GetRepoFilePath(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}");
    }
}
