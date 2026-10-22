namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Told about each step of a library move as the organiser takes it, so the durable record always says what might be on disk
/// (batched write-back D10). The organiser keeps the file move in one place and stays free of the to-do list's storage; whoever asks
/// for the move supplies this to keep the record.
/// </summary>
public interface IRefileStepRecorder
{
    /// <summary>Called before the disk is touched: the file is at <paramref name="sourcePath"/> and is about to go to <paramref name="targetPath"/>.</summary>
    Task PlannedAsync(string sourcePath, string targetPath, CancellationToken ct);

    /// <summary>Called once the file is at <paramref name="actualPath"/> and the old place is empty, before the data store follows.</summary>
    Task MovedAsync(string actualPath, CancellationToken ct);

    /// <summary>Called once the data store holds the new path and the file has been checked there.</summary>
    Task CheckedAsync(CancellationToken ct);
}
