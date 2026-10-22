using System.Text.RegularExpressions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Every write into a media file goes through the durable file write list (batched write-back D1): callers put the file on the list with
/// <c>IFileWriteQueue</c>, and a scheduled run writes it. The media-asset writer is the one place that may write tags or embedded
/// artwork, so nothing else can write a file the moment something changes.
/// </summary>
public sealed class FileWriteQueueGuardrailTests
{
    private const string WriterFile = "MediaAssetFileWriter.cs";

    private static readonly Regex TagWrite = new(@"\.\s*WriteMetadata(WithOutcome)?Async\s*\(", RegexOptions.Compiled);
    private static readonly Regex TaggerTagWrite = new(@"\.\s*WriteTagsAsync\s*\(", RegexOptions.Compiled);
    private static readonly Regex TaggerCoverWrite = new(@"\.\s*WriteCoverArtAsync\s*\(", RegexOptions.Compiled);
    private static readonly Regex TagLibSave = new(@"\bfile\s*\.\s*Save\s*\(\s*\)", RegexOptions.Compiled);
    private static readonly Regex ArtworkWrite = new(@"\.\s*ProcessAsync\s*\(", RegexOptions.Compiled);

    [Fact]
    public void OnlyTheMediaAssetWriter_WritesTagsThroughTheWriteBackService()
    {
        var offenders = ActiveSources()
            .Where(file => file.Name != WriterFile && TagWrite.IsMatch(file.Text))
            .Select(file => file.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Put the file on the list with IFileWriteQueue instead of writing it now: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void OnlyTheMediaAssetWriter_EmbedsArtworkIntoAFile()
    {
        var offenders = ActiveSources()
            .Where(file => file.Name is not (WriterFile or "ArtworkWritebackService.cs")
                           && file.Text.Contains("ArtworkWritebackService", StringComparison.Ordinal)
                           && ArtworkWrite.IsMatch(file.Text))
            .Select(file => file.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Queue an artwork write with IFileWriteQueue instead of calling ProcessAsync: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void OnlyTheWriteBackService_CallsATaggerToWriteTags()
    {
        // Ingestion used to write tags straight into a file while it registered it; now it puts the file on the list like everything else.
        var offenders = ActiveSources()
            .Where(file => file.Name != "WriteBackService.cs" && TaggerTagWrite.IsMatch(file.Text))
            .Select(file => file.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Put the file on the list with IFileWriteQueue instead of calling a tagger: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void OnlyTheWriteBackServiceAndTheArtworkService_CallATaggerToEmbedACover()
    {
        var offenders = ActiveSources()
            .Where(file => file.Name is not ("WriteBackService.cs" or "ArtworkWritebackService.cs") && TaggerCoverWrite.IsMatch(file.Text))
            .Select(file => file.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Queue the cover with IFileWriteQueue instead of calling a tagger: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void OnlyTheTaggers_SaveATagLibFile()
    {
        var offenders = ActiveSources()
            .Where(file => !file.Name.EndsWith("MetadataTagger.cs", StringComparison.Ordinal) && TagLibSave.IsMatch(file.Text))
            .Select(file => file.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Only a tagger may save tags into a media file; queue the write instead: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheIngestionPipeline_PutsAFileOnTheListAndWritesNothingIntoIt()
    {
        var pipeline = ActiveSources().Single(file => file.Name == "IngestionEngine.Pipeline.cs").Text;

        Assert.Contains("EnqueueAssetAsync", pipeline, StringComparison.Ordinal);
        Assert.DoesNotMatch(TaggerTagWrite, pipeline);
        Assert.DoesNotMatch(TaggerCoverWrite, pipeline);
    }

    [Fact]
    public void TheWriteBackServicesContracts_AreNotInjectedAnywhereButTheWriterAndTheService()
    {
        // A caller that holds the write-back service can write a file at once; the list's writer is the only holder.
        var offenders = ActiveSources()
            .Where(file => file.Name is not (WriterFile or "WriteBackService.cs" or "IWriteBackService.cs"
                                               or "MediaEngineIngestionServiceCollectionExtensions.cs")
                           && Regex.IsMatch(file.Text, @"\bIWriteBack(Outcome)?Service\b"))
            .Select(file => file.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Inject IFileWriteQueue, not IWriteBackService: {string.Join(", ", offenders)}");
    }

    private static IEnumerable<(string Name, string Text)> ActiveSources()
    {
        var src = FindRepoDirectory("src");
        foreach (var path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var separator = Path.DirectorySeparatorChar;
            if (path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                || path.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (Path.GetFileName(path), File.ReadAllText(path));
        }
    }

    private static string FindRepoDirectory(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}");
    }
}
