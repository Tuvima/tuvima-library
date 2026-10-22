using System.Globalization;
using MediaEngine.Contracts.WriteBack;

namespace MediaEngine.Web.Services.Formatting;

/// <summary>
/// The plain words for the batched file write-back: the Settings panel and the Editing Mode File notes share one way of showing a time.
/// </summary>
public static class FileWriteStatusText
{
    /// <summary>The person's own time: just the time of day for today, the date as well for any other day.</summary>
    public static string FormatTime(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        return local.Date == DateTime.Now.Date
            ? local.ToString("h:mm tt", CultureInfo.CurrentCulture)
            : local.ToString("MMM d · h:mm tt", CultureInfo.CurrentCulture);
    }

    /// <summary>"Changes waiting to be written to the file · next run 11:00", or null when nothing is waiting.</summary>
    public static string? WaitingNote(AssetFileWriteStatusDto? status) => status is { Waiting: true }
        ? status.NextRunAt is { } next
            ? $"Changes waiting to be written to the file · next run {FormatTime(next)}"
            : "Changes waiting to be written to the file"
        : null;

    /// <summary>Why the last attempt to write the file did not work, or null when it did.</summary>
    public static string? FailureNote(AssetFileWriteStatusDto? status) => status is { LastError: { Length: > 0 } error }
        ? $"The last write to the file didn't work: {error}"
        : null;
}
