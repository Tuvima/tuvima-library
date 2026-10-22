namespace MediaEngine.Domain.Ingestion;

/// <summary>What the live ingestion numbers say about the queue as a whole.</summary>
public enum IngestionQueueRunState
{
    /// <summary>Nothing is being added.</summary>
    Idle = 0,

    /// <summary>At least one file is being worked on.</summary>
    Working = 1,

    /// <summary>Every unfinished file is waiting for a missing tool.</summary>
    Waiting = 2,
}

/// <summary>How many finished files ended each way.</summary>
public sealed record IngestionQueueOutcomes(int Added, int NeedsReview, int Failed, int Skipped, int Unmatched = 0);

/// <summary>How many files of one media type are in the queue.</summary>
public sealed record IngestionQueueTypeTally(string MediaType, int Files);

/// <summary>
/// The live ingestion numbers, worked out once from the per-file work rows. The Dashboard shows them and never
/// derives its own (see <see cref="IngestionQueueProgressCalculator"/>).
/// </summary>
public sealed record IngestionQueueProgressSnapshot
{
    /// <summary>Idle, working, or waiting for a tool.</summary>
    public IngestionQueueRunState State { get; init; }

    /// <summary>Files in the active period's set.</summary>
    public int FilesFound { get; init; }

    /// <summary>Files that are finished.</summary>
    public int FilesDone { get; init; }

    /// <summary>Files held back by a missing tool.</summary>
    public int FilesWaiting { get; init; }

    /// <summary>The distinct tools holding files, for example <c>ffmpeg</c>.</summary>
    public IReadOnlyList<string> WaitingFor { get; init; } = [];

    /// <summary>Files that joined after the period's first snapshot.</summary>
    public int FilesMoreFound { get; init; }

    /// <summary>How the finished files ended.</summary>
    public IngestionQueueOutcomes Outcomes { get; init; } = new(0, 0, 0, 0, 0);

    /// <summary>Files per media type, for the types with at least one file.</summary>
    public IReadOnlyList<IngestionQueueTypeTally> Tally { get; init; } = [];

    /// <summary>Files whose media type is not known yet.</summary>
    public int FilesUnsorted { get; init; }

    /// <summary>Steps possible: the number of steps times the files found.</summary>
    public long StepsTotal { get; init; }

    /// <summary>Steps completed: the sum of every file's step number.</summary>
    public long StepsCompleted { get; init; }

    /// <summary>The value the bar shows, 0 to 1. It never decreases within a period and is 1 only when idle.</summary>
    public double Progress { get; init; }

    /// <summary>When the active period started; <see langword="null"/> when no period has run.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>The newest operation or identity update seen.</summary>
    public DateTimeOffset? LastActivityAt { get; init; }

    /// <summary>Seconds left, only when the estimate is reliable.</summary>
    public int? EstimatedSecondsRemaining { get; init; }

    /// <summary>When this snapshot was worked out.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The snapshot for a library that has not been busy since the Engine started.</summary>
    public static IngestionQueueProgressSnapshot Empty(DateTimeOffset now) => new() { UpdatedAt = now };

    /// <summary>Whether two snapshots say the same thing, ignoring when they were worked out.</summary>
    public bool SameNumbersAs(IngestionQueueProgressSnapshot? other) =>
        other is not null
        && State == other.State
        && FilesFound == other.FilesFound
        && FilesDone == other.FilesDone
        && FilesWaiting == other.FilesWaiting
        && WaitingFor.SequenceEqual(other.WaitingFor, StringComparer.Ordinal)
        && FilesMoreFound == other.FilesMoreFound
        && Outcomes == other.Outcomes
        && Tally.SequenceEqual(other.Tally)
        && FilesUnsorted == other.FilesUnsorted
        && StepsTotal == other.StepsTotal
        && StepsCompleted == other.StepsCompleted
        && Progress.Equals(other.Progress)
        && StartedAt == other.StartedAt
        && LastActivityAt == other.LastActivityAt
        && EstimatedSecondsRemaining == other.EstimatedSecondsRemaining;
}
