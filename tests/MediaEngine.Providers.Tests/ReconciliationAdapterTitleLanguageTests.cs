namespace MediaEngine.Providers.Tests;

public sealed class ReconciliationAdapterTitleLanguageTests
{
    [Fact]
    public void ForeignLanguageLabelsRemainOriginalTitlesInsteadOfDisplayTitles()
    {
        var source = ReadAdapterSource("ReconciliationAdapter");

        Assert.Contains("emit it as \"original_title\" only", source, StringComparison.Ordinal);
        Assert.Contains("reconciliationLabel = await FetchDisplayLabelAsync(qid, displayLanguage, ct)", source, StringComparison.Ordinal);
        Assert.Contains("claims.Add(new ProviderClaim(MetadataFieldConstants.OriginalTitle, fileLangLabel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceLanguageTitleConfidence", source, StringComparison.Ordinal);
        Assert.DoesNotContain("claims.Add(new ProviderClaim(MetadataFieldConstants.Title, fileLangLabel", source, StringComparison.Ordinal);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));

    private static string ReadAdapterSource(string adapterName)
    {
        var facade = GetRepoFilePath($@"src/MediaEngine.Providers/Adapters/{adapterName}.cs");
        var internals = GetRepoFilePath(@"src/MediaEngine.Providers/Adapters/Internals");
        return string.Join(
            Environment.NewLine,
            new[] { facade }
                .Concat(Directory
                    .GetFiles(internals, $"{adapterName}.*.cs")
                    .Order(StringComparer.Ordinal))
                .Select(File.ReadAllText));
    }
}
