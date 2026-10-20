namespace MediaEngine.Ingestion.Models;

/// <summary>
/// The raw, unfiltered event emitted by <see cref="Contracts.IFileWatcher"/>
/// immediately when the OS reports a file system change.
///
/// This object is fed into <see cref="DebounceQueue"/> and is NOT yet safe
/// to process — the underlying file may still be locked by a writing process.
/// </summary>
public sealed class FileEvent
{
    /// <summary>Full absolute path of the affected file or directory.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// Previous full path, populated only when <see cref="EventType"/> is
    /// <see cref="FileEventType.Renamed"/>.
    /// </summary>
    public string? OldPath { get; init; }

    /// <summary>The kind of OS change that triggered this event.</summary>
    public required FileEventType EventType { get; init; }

    /// <summary>Wall-clock time the OS reported the event. Used as a version stamp
    /// inside <see cref="DebounceQueue"/> to detect superseded entries.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Optional routing context supplied by direct intake. This remains attached
    /// while the event is debounced so a known destination is not inferred again.
    /// </summary>
    public IntakeContext? Intake { get; set; }
}
