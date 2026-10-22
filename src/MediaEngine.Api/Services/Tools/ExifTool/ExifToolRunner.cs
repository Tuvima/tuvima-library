using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MediaEngine.Contracts.Settings;

namespace MediaEngine.Api.Services.Tools.ExifTool;

/// <summary>
/// Starts ExifTool. A batch opens one session (one ExifTool process, started once with <c>-stay_open True -@ -</c>) and sends
/// every file through it, so writing the profile name into hundreds of files pays ExifTool's start-up cost once, not once per file.
/// </summary>
public sealed class ExifToolRunner : IExifToolRunner
{
    /// <summary>How long one command may take before ExifTool is treated as hung.</summary>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    private readonly IExifToolLocator _locator;
    private readonly ILogger<ExifToolRunner> _logger;
    private readonly TimeSpan _commandTimeout;
    private int _processesStarted;

    public ExifToolRunner(IExifToolLocator locator, ILogger<ExifToolRunner> logger, TimeSpan? commandTimeout = null)
    {
        _locator = locator;
        _logger = logger;
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
    }

    /// <summary>How many long-lived ExifTool processes this runner has started (the version check does not count).</summary>
    public int ProcessesStarted => Volatile.Read(ref _processesStarted);

    public async Task<ExifToolStatus> CheckAsync(CancellationToken ct)
    {
        var command = _locator.Resolve();
        if (command is null)
        {
            return new ExifToolStatus(ToolRequirementStates.Missing, null, PerlMissing: !OperatingSystem.IsWindows() && _locator.FindPerl() is null);
        }

        try
        {
            using var process = Process.Start(CreateStartInfo(command, configFile: null, ["-ver"], redirectInput: false))
                ?? throw new InvalidOperationException("ExifTool did not start.");
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CheckTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Kill(process);
                _logger.LogWarning("ExifTool did not answer its version check in time.");
                return new ExifToolStatus(ToolRequirementStates.Problem, null, false);
            }

            var version = (await output.ConfigureAwait(false)).Trim();
            if (process.ExitCode == 0 && version.Length > 0 && char.IsDigit(version[0]))
            {
                return new ExifToolStatus(ToolRequirementStates.Ready, version.Split('\n')[0].Trim(), false);
            }

            _logger.LogWarning("ExifTool was found but its version check failed (exit {ExitCode}): {Detail}",
                process.ExitCode, (await errors.ConfigureAwait(false)).Trim());
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "ExifTool was found but could not be started.");
        }

        return new ExifToolStatus(ToolRequirementStates.Problem, null, false);
    }

    public async Task<IExifToolSession?> OpenSessionAsync(CancellationToken ct)
    {
        var command = _locator.Resolve();
        if (command is null)
        {
            return null;
        }

        Process process;
        try
        {
            var startInfo = CreateStartInfo(command, _locator.ResolveConfigFile(), ["-stay_open", "True", "-@", "-"], redirectInput: true);
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("ExifTool did not start.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "ExifTool could not be started for a batch.");
            return null;
        }

        Interlocked.Increment(ref _processesStarted);
        var session = new ExifToolSession(process, _commandTimeout, _logger);
        if (await session.StartAsync(ct).ConfigureAwait(false))
        {
            return session;
        }

        await session.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// ExifTool's <c>-config</c> must be its first option, so it goes right after the arguments that name the script. The
    /// working folder is left alone: every path handed to ExifTool is absolute.
    /// </summary>
    private static ProcessStartInfo CreateStartInfo(
        ExifToolCommand command, string? configFile, IReadOnlyList<string> arguments, bool redirectInput)
    {
        var startInfo = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (redirectInput)
        {
            startInfo.StandardInputEncoding = new UTF8Encoding(false);
        }

        foreach (var argument in command.LeadingArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrEmpty(configFile))
        {
            startInfo.ArgumentList.Add("-config");
            startInfo.ArgumentList.Add(configFile);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    internal static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process had already ended between the check and the kill, which is the outcome wanted.
        }
        catch (Win32Exception)
        {
            // Access was denied because the process is already shutting down; there is nothing more to stop.
        }
    }
}
