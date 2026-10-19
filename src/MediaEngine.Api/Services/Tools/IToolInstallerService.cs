using System.Runtime.InteropServices;

namespace MediaEngine.Api.Services.Tools;

/// <summary>What a tool's install job is doing right now.</summary>
public enum ToolInstallPhase
{
    /// <summary>Nothing is running and nothing is waiting.</summary>
    Idle,

    /// <summary>Downloading, checking and placing the tool; the percentage says how far.</summary>
    Downloading,

    /// <summary>Tesseract's setup program is downloaded and checked, waiting for the person to run it.</summary>
    InstallerReady,

    /// <summary>The last attempt did not finish; <see cref="ToolInstallSnapshot.FailureReason"/> says why in plain words.</summary>
    Failed,
}

/// <param name="Phase">What the job is doing.</param>
/// <param name="Percent">Progress while <see cref="ToolInstallPhase.Downloading"/>, 0 to 100.</param>
/// <param name="FailureReason">A short plain reason after a failed attempt, or a note for the person while the installer is ready.</param>
/// <param name="InstallerRunning">The setup program has been started and has not closed yet.</param>
public sealed record ToolInstallSnapshot(ToolInstallPhase Phase, int Percent, string? FailureReason, bool InstallerRunning)
{
    public static readonly ToolInstallSnapshot Idle = new(ToolInstallPhase.Idle, 0, null, false);
}

/// <summary>Whether the Library can fetch a tool here, and the fix wording to show when it cannot.</summary>
/// <param name="Offered">The Install for me or Download again button may be shown.</param>
/// <param name="FixText">Replaces the row's usual fix text when set.</param>
public sealed record ToolInstallOffer(bool Offered, string? FixText = null)
{
    public static readonly ToolInstallOffer None = new(false);
}

public enum ToolInstallOutcome
{
    /// <summary>The job started (or the installer was launched).</summary>
    Accepted,

    /// <summary>The id is not in the tools list.</summary>
    UnknownTool,

    /// <summary>This tool already has a job running.</summary>
    AlreadyRunning,

    /// <summary>The Library does not install this tool here; <see cref="ToolInstallResult.Message"/> says why in plain words.</summary>
    NotOffered,
}

public sealed record ToolInstallResult(ToolInstallOutcome Outcome, string? Message = null);

/// <summary>
/// Fetches FFmpeg and Tesseract on request, from the fixed addresses and with the checksums in
/// <c>config/tool_downloads.json</c>. One job per tool at a time; nothing ever starts by itself.
/// </summary>
public interface IToolInstallerService
{
    /// <summary>Whether this install kind and machine may be offered the tool, and any replacement fix wording.</summary>
    ToolInstallOffer GetOffer(string toolId, string installKind);

    /// <summary>The job's current state; <see cref="ToolInstallSnapshot.Idle"/> when there is none.</summary>
    ToolInstallSnapshot GetSnapshot(string toolId);

    /// <summary>Starts the download in the background and returns at once.</summary>
    Task<ToolInstallResult> StartAsync(string toolId, CancellationToken ct);

    /// <summary>Cancels a download in progress, waits for its temporary files to be removed, and does nothing if none is running.</summary>
    Task CancelAsync(string toolId, CancellationToken ct);

    /// <summary>Starts Tesseract's downloaded setup program (Windows asks the person to approve it) and returns at once.</summary>
    Task<ToolInstallResult> RunInstallerAsync(string toolId, CancellationToken ct);
}

/// <summary>
/// The one place a download is opened. The real source uses a named HTTP client; tests hand in a fake so no test ever
/// touches the network.
/// </summary>
public interface IToolDownloadSource
{
    Task<ToolDownload> OpenAsync(Uri address, CancellationToken ct);
}

/// <summary>An opened download: the bytes, their length when the host said, and whatever must be released with them.</summary>
public sealed class ToolDownload(Stream content, long? length, IDisposable? owner = null) : IAsyncDisposable
{
    public Stream Content { get; } = content;

    public long? Length { get; } = length;

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        owner?.Dispose();
    }
}

/// <summary>Starts a downloaded setup program and waits for it to close. A seam so tests never launch anything.</summary>
public interface IToolProcessLauncher
{
    /// <returns>The program's exit code.</returns>
    Task<int> RunAndWaitAsync(string programPath, CancellationToken ct);
}

/// <summary>What the machine running the Engine allows. A seam so tests can ask for any combination.</summary>
/// <param name="WindowsX64">The pinned builds are for 64-bit Windows.</param>
/// <param name="Interactive">The Engine runs in a signed-in desktop session (not as a Windows service), so a setup program can show its window.</param>
public sealed record ToolInstallPlatform(bool WindowsX64, bool Interactive)
{
    public static ToolInstallPlatform Current => new(
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64,
        Environment.UserInteractive);
}
