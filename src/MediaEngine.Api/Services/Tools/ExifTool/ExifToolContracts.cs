namespace MediaEngine.Api.Services.Tools.ExifTool;

/// <summary>How a single file's write ended.</summary>
public enum ExifToolWriteOutcome
{
    /// <summary>ExifTool wrote the tags (or the file already held those exact values).</summary>
    Succeeded,

    /// <summary>The file could not be written (damaged, locked, no room, or ExifTool stopped). <see cref="ExifToolFileResult.Reason"/> says why.</summary>
    Failed,

    /// <summary>ExifTool does not write this kind of file at all (for example plain text). <see cref="ExifToolFileResult.Reason"/> says which.</summary>
    Unsupported,
}

/// <summary>The result for one file in a batch. One file failing never changes the result of another.</summary>
/// <param name="Outcome">How the write ended.</param>
/// <param name="Reason">ExifTool's own words for a failure or an unsupported file, or a warning it gave on success; null when it said nothing.</param>
public sealed record ExifToolFileResult(ExifToolWriteOutcome Outcome, string? Reason = null)
{
    public bool IsSuccess => Outcome == ExifToolWriteOutcome.Succeeded;

    public static ExifToolFileResult Succeeded(string? warning = null) => new(ExifToolWriteOutcome.Succeeded, warning);

    public static ExifToolFileResult Failed(string reason) => new(ExifToolWriteOutcome.Failed, reason);

    public static ExifToolFileResult Unsupported(string reason) => new(ExifToolWriteOutcome.Unsupported, reason);
}

/// <summary>
/// Where a write goes. Exactly one of <see cref="OutputPath"/> and <see cref="OverwriteOriginal"/> is set, so ExifTool never
/// leaves a <c>_original</c> backup beside the file on its own.
/// </summary>
/// <param name="OutputPath">Write the changed copy here and leave the source file alone (ExifTool's <c>-o</c>); the path must not exist yet.</param>
/// <param name="OverwriteOriginal">Change the file in place without a backup copy (ExifTool's <c>-overwrite_original</c>).</param>
/// <param name="KeepFileTimes">Keep the file's modified time as it was (ExifTool's <c>-P</c>). Off by default: the file did change.</param>
public sealed record ExifToolWriteOptions(string? OutputPath = null, bool OverwriteOriginal = false, bool KeepFileTimes = false)
{
    /// <summary>Change the file where it is.</summary>
    public static ExifToolWriteOptions InPlace(bool keepFileTimes = false) => new(null, true, keepFileTimes);

    /// <summary>Write the changed copy to <paramref name="outputPath"/>.</summary>
    public static ExifToolWriteOptions ToCopy(string outputPath, bool keepFileTimes = false) => new(outputPath, false, keepFileTimes);
}

/// <summary>
/// One ExifTool process, started once and kept for a whole batch of files. Commands run one at a time. If ExifTool
/// exits or stops answering, the file being worked on is reported as failed and every later call returns
/// <see cref="ExifToolFileResult.Failed"/> (writes) or nothing (reads); nothing is thrown into the Engine for that.
/// </summary>
public interface IExifToolSession : IAsyncDisposable
{
    /// <summary>ExifTool's version, as it reported when the session started.</summary>
    string Version { get; }

    /// <summary>False once ExifTool has exited, hung, or been disposed.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Writes <paramref name="tags"/> (full tag names such as <c>XMP-tuvima:AddedBy</c>, mapped to their text value) into
    /// <paramref name="path"/>. An empty value removes the tag. The path must be absolute; values may not hold line breaks.
    /// </summary>
    Task<ExifToolFileResult> WriteAsync(
        string path, IReadOnlyDictionary<string, string> tags, ExifToolWriteOptions options, CancellationToken ct);

    /// <summary>
    /// Reads the named tags back. The result holds only the tags the file has, keyed by the name asked for. It is empty
    /// when the file has none of them or ExifTool could not read it.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ReadAsync(string path, IReadOnlyList<string> tags, CancellationToken ct);
}

/// <summary>What the tools list needs to know about ExifTool.</summary>
/// <param name="State">One of <c>ToolRequirementStates</c>: ready, missing or problem.</param>
/// <param name="Version">ExifTool's version when it is ready.</param>
/// <param name="PerlMissing">ExifTool's Perl build is what is available here but this machine has no Perl to run it with.</param>
public sealed record ExifToolStatus(string State, string? Version, bool PerlMissing);

/// <summary>Starts ExifTool for batches of View-file writes. The only code that launches it.</summary>
public interface IExifToolRunner
{
    /// <summary>Looks for ExifTool and asks it for its version, without keeping a process running.</summary>
    Task<ExifToolStatus> CheckAsync(CancellationToken ct);

    /// <summary>
    /// Starts one ExifTool process for a batch and returns it ready for commands, or <c>null</c> when ExifTool is not
    /// ready (not found, no Perl, or it did not answer). The caller disposes the session when the batch is done.
    /// </summary>
    Task<IExifToolSession?> OpenSessionAsync(CancellationToken ct);
}
