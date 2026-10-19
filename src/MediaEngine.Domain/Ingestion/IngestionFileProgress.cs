namespace MediaEngine.Domain.Ingestion;

/// <summary>Where one file is in the queue: its step, whether it is finished and how, and whether it is waiting for a tool.</summary>
/// <param name="Step">The step the file has reached (<see cref="IngestionFileStep.Done"/> when finished).</param>
/// <param name="IsDone">Whether the file is finished.</param>
/// <param name="Outcome">How the file ended; <see cref="IngestionFileOutcome.None"/> while it is not finished.</param>
/// <param name="WaitingForToolId">The missing tool holding the file, or <see langword="null"/>.</param>
public readonly record struct IngestionFileProgress(
    IngestionFileStep Step,
    bool IsDone,
    IngestionFileOutcome Outcome,
    string? WaitingForToolId)
{
    /// <summary>Whether the file is held back by a missing tool.</summary>
    public bool IsWaiting => WaitingForToolId is not null;
}
