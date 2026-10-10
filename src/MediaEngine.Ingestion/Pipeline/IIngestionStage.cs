using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Models;
using MediaEngine.Intelligence.Models;
using MediaEngine.Processors;
using MediaEngine.Processors.Models;

namespace MediaEngine.Ingestion.Pipeline;

/// <summary>
/// One ordered unit in the local file-ingestion pipeline.
/// A stage may mark the context complete when processing has reached a terminal outcome.
/// </summary>
internal interface IIngestionStage
{
    string Name { get; }

    Task ExecuteAsync(IngestionPipelineContext context, CancellationToken ct);
}

internal sealed class DelegateIngestionStage(
    string name,
    Func<IngestionPipelineContext, CancellationToken, Task> execute) : IIngestionStage
{
    public string Name { get; } = name;

    public Task ExecuteAsync(IngestionPipelineContext context, CancellationToken ct) =>
        execute(context, ct);
}

internal sealed class IngestionPipelineContext(
    IngestionCandidate candidate,
    Guid ingestionRunId)
{
    public IngestionCandidate Candidate { get; } = candidate;
    public LibraryFolderEntry? Library { get; set; }
    public Guid IngestionRunId { get; } = ingestionRunId;
    public MediaOperation? DurableOperation { get; set; }
    public Guid LogEntryId { get; set; }
    public System.Diagnostics.Stopwatch? PipelineStopwatch { get; set; }
    public HashResult? Hash { get; set; }
    public SemaphoreSlim? HashLock { get; set; }
    public ProcessorResult? ProcessorResult { get; set; }
    /// <summary>Sibling cover image (cover.jpg, folder.jpg, …) used only when the file has no embedded cover.</summary>
    public FolderCoverImage? FolderCover { get; set; }
    public Guid AssetId { get; set; }
    public IReadOnlyList<MetadataClaim> Claims { get; set; } = [];
    public ScoringResult? Scored { get; set; }
    public MediaType ResolvedMediaType { get; set; }
    public bool MediaTypeNeedsReview { get; set; }
    public IReadOnlyList<MediaTypeCandidate> MediaTypeCandidates { get; set; } = [];
    public string ResolvedTitle { get; set; } = "Unknown";
    public string ResolvedAuthor { get; set; } = string.Empty;
    public string CurrentPath { get; set; } = candidate.Path;
    public bool FileIsInLibrary { get; set; }
    public List<string> TagsWritten { get; set; } = [];
    public bool CoverWritten { get; set; }
    public Exception? DeferredWriteBackFailure { get; set; }
    public bool IsComplete { get; private set; }

    public void Complete() => IsComplete = true;
}
