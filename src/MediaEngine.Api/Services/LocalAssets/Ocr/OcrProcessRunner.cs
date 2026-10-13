using System.Diagnostics;

namespace MediaEngine.Api.Services.LocalAssets.Ocr;

/// <summary>One child-process call made for text recognition.</summary>
public sealed record OcrProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    bool LowPriority = false,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>What a child process produced. The output is never logged: it can hold document text.</summary>
public sealed record OcrProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>Runs the text recognition helper. Replaced by a hand-written spy in tests.</summary>
public interface IOcrProcessRunner
{
    Task<OcrProcessResult> RunAsync(OcrProcessRequest request, CancellationToken ct);
}

/// <summary>Starts the helper as a child process, stops it when it overruns, and never inherits a shell.</summary>
public sealed class OcrProcessRunner : IOcrProcessRunner
{
    public async Task<OcrProcessResult> RunAsync(OcrProcessRequest request, CancellationToken ct)
    {
        var info = new ProcessStartInfo(request.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                info.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = info };
        process.Start();
        if (request.LowPriority)
        {
            TryLowerPriority(process);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            StopTree(process);
            ct.ThrowIfCancellationRequested();
            return new OcrProcessResult(-1, string.Empty, string.Empty, TimedOut: true);
        }

        return new OcrProcessResult(
            process.ExitCode,
            await output.ConfigureAwait(false),
            await error.ConfigureAwait(false),
            TimedOut: false);
    }

    private static void TryLowerPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // Best effort: the process may already have exited, or the platform refuses priority changes.
            // Recognition still works at normal priority.
        }
    }

    private static void StopTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process finished between the check and the kill; nothing is left to stop.
        }
    }
}
