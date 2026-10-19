namespace MediaEngine.Domain.Contracts;

/// <summary>What the latest tools check found for the tools that decide whether media files can be inspected.</summary>
public sealed record MediaToolAvailability(bool FfmpegAvailable, bool TextRecognitionAvailable);

/// <summary>
/// Told after every tools check so work that was waiting on a tool can continue on its own.
/// A listener's failure never fails the check.
/// </summary>
public interface IMediaToolStatusListener
{
    Task OnMediaToolsCheckedAsync(MediaToolAvailability availability, CancellationToken ct);
}

/// <summary>Counts the files currently held back because a given tool is missing.</summary>
public interface IToolWaitingWorkCounter
{
    Task<int> CountWaitingAsync(string toolId, CancellationToken ct);
}

/// <summary>The default counter: nothing is held back until the work that holds files registers its own counter.</summary>
public sealed class NoToolWaitingWorkCounter : IToolWaitingWorkCounter
{
    public Task<int> CountWaitingAsync(string toolId, CancellationToken ct) => Task.FromResult(0);
}
