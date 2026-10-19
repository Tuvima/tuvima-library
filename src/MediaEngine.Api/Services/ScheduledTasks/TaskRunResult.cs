using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ScheduledTasks;

/// <summary>What one run of a recurring task reports back to <see cref="ScheduledTaskRunner"/>.</summary>
/// <param name="Outcome">One of <see cref="ScheduledTaskOutcomes"/> other than running or interrupted.</param>
/// <param name="ItemsProcessed">How many items the run handled, when that is meaningful.</param>
/// <param name="Message">Plain-English summary, skip reason or failure message.</param>
public sealed record TaskRunResult(string Outcome, int? ItemsProcessed = null, string? Message = null)
{
    /// <summary>The run did its work (including "nothing needed doing").</summary>
    public static TaskRunResult Succeeded(int? itemsProcessed = null, string? message = null) =>
        new(ScheduledTaskOutcomes.Succeeded, itemsProcessed, message);

    /// <summary>The run chose not to do any work, for example because the feature is off. <paramref name="reason"/> is shown to the person.</summary>
    public static TaskRunResult Skipped(string reason) =>
        new(ScheduledTaskOutcomes.Skipped, null, reason);

    /// <summary>The run handled its own error (already logged) and wants it recorded as a failure.</summary>
    public static TaskRunResult Failed(string message, int? itemsProcessed = null) =>
        new(ScheduledTaskOutcomes.Failed, itemsProcessed, message);
}
