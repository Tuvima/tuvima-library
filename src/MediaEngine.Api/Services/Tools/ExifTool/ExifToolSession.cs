using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace MediaEngine.Api.Services.Tools.ExifTool;

/// <summary>
/// One running ExifTool process in <c>-stay_open</c> mode. A command is a block of argument lines ended by
/// <c>-execute&lt;N&gt;</c>; ExifTool answers with the result and then <c>{ready&lt;N&gt;}</c> on its standard output. A
/// matching <c>-echo4</c> marker on its standard error shows where that command's complaints end, so output is never
/// mixed between files. Commands run one at a time.
/// </summary>
internal sealed partial class ExifToolSession : IExifToolSession
{
    private const string StoppedMessage = "ExifTool stopped";
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    private readonly Process _process;
    private readonly TimeSpan _commandTimeout;
    private readonly ILogger _logger;
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _errors = Channel.CreateUnbounded<string>();
    private readonly Task _outputPump;
    private readonly Task _errorPump;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _commandId;
    private volatile bool _stopped;
    private int _disposed;

    public ExifToolSession(Process process, TimeSpan commandTimeout, ILogger logger)
    {
        _process = process;
        _commandTimeout = commandTimeout;
        _logger = logger;
        _process.StandardInput.NewLine = "\n";
        _outputPump = PumpAsync(process.StandardOutput, _output.Writer);
        _errorPump = PumpAsync(process.StandardError, _errors.Writer);
    }

    public string Version { get; private set; } = string.Empty;

    public bool IsRunning => !_stopped && Volatile.Read(ref _disposed) == 0 && !HasExited();

    /// <summary>Asks ExifTool for its version, which proves it started and answers; false when it did not.</summary>
    public async Task<bool> StartAsync(CancellationToken ct)
    {
        try
        {
            var (output, _) = await ExecuteAsync(["-ver"], ct).ConfigureAwait(false);
            Version = output.FirstOrDefault(line => line.Length > 0)?.Trim() ?? string.Empty;
            return Version.Length > 0 && char.IsDigit(Version[0]);
        }
        catch (ExifToolStoppedException ex)
        {
            _logger.LogWarning("ExifTool started but did not answer: {Reason}", ex.Message);
            return false;
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    public async Task<ExifToolFileResult> WriteAsync(
        string path, IReadOnlyDictionary<string, string> tags, ExifToolWriteOptions options, CancellationToken ct)
    {
        if (!TryBuildWrite(path, tags, options, out var arguments, out var problem))
        {
            return ExifToolFileResult.Failed(problem!);
        }

        try
        {
            var (output, errors) = await ExecuteAsync(arguments, ct).ConfigureAwait(false);
            var result = ExifToolOutputParser.ParseWrite(output, errors);
            if (!result.IsSuccess)
            {
                _logger.LogDebug("ExifTool did not write a file ({Outcome}): {Reason}", result.Outcome, result.Reason);
            }

            return result;
        }
        catch (ExifToolStoppedException ex)
        {
            return ExifToolFileResult.Failed(ex.Message);
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(string path, IReadOnlyList<string> tags, CancellationToken ct)
    {
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!IsPlainLine(path) || !Path.IsPathRooted(path) || tags.Count == 0 || !tags.All(IsTagName))
        {
            return empty;
        }

        var arguments = new List<string> { "-charset", "filename=utf8", "-j", "-G1" };
        arguments.AddRange(tags.Select(tag => "-" + tag));
        arguments.Add(path);
        try
        {
            var (output, errors) = await ExecuteAsync(arguments, ct).ConfigureAwait(false);
            if (errors.Count > 0)
            {
                _logger.LogDebug("ExifTool said this while reading a file: {Detail}", string.Join(" ", errors));
            }

            return ExifToolOutputParser.ParseRead(output, tags);
        }
        catch (ExifToolStoppedException ex)
        {
            _logger.LogWarning("ExifTool could not read a file back: {Reason}", ex.Message);
            return empty;
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(ex, "ExifTool's answer to a read could not be understood.");
            return empty;
        }
    }

    private static bool TryBuildWrite(
        string path, IReadOnlyDictionary<string, string> tags, ExifToolWriteOptions options,
        out List<string> arguments, out string? problem)
    {
        arguments = [];
        problem = null;
        if (!IsPlainLine(path) || !Path.IsPathRooted(path))
        {
            problem = "The file path must be a full path on one line.";
            return false;
        }

        if (tags.Count == 0)
        {
            problem = "There was nothing to write.";
            return false;
        }

        if (options.OverwriteOriginal == (options.OutputPath is not null))
        {
            problem = "Choose either an output copy or an in-place write.";
            return false;
        }

        if (options.OutputPath is not null && (!IsPlainLine(options.OutputPath) || !Path.IsPathRooted(options.OutputPath)))
        {
            problem = "The output path must be a full path on one line.";
            return false;
        }

        arguments.AddRange(["-charset", "filename=utf8"]);
        if (options.KeepFileTimes)
        {
            arguments.Add("-P");
        }

        if (options.OutputPath is { } output)
        {
            arguments.AddRange(["-o", output]);
        }
        else
        {
            arguments.Add("-overwrite_original");
        }

        foreach (var (tag, value) in tags)
        {
            if (!IsTagName(tag))
            {
                problem = "A tag name was not valid.";
                return false;
            }

            if (!IsPlainLine(value ?? string.Empty))
            {
                problem = $"The value for {tag} held a line break.";
                return false;
            }

            arguments.Add($"-{tag}={value}");
        }

        arguments.Add(path);
        return true;
    }

    /// <summary>A tag such as <c>XMP-tuvima:AddedBy</c>: letters, digits and a few separators, never a leading dash or a space.</summary>
    private static bool IsTagName(string tag) => TagName().IsMatch(tag);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9:_\-\.]*$")]
    private static partial Regex TagName();

    /// <summary>ExifTool's argument file takes one argument per line, so a line break would start another argument.</summary>
    private static bool IsPlainLine(string value) => value.IndexOfAny(['\r', '\n', '\0']) < 0;

    // ── The conversation with ExifTool ───────────────────────────────────────

    private async Task<(List<string> Output, List<string> Errors)> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stopped || Volatile.Read(ref _disposed) == 1)
            {
                throw new ExifToolStoppedException(StoppedMessage);
            }

            var id = ++_commandId;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_commandTimeout);
            try
            {
                var command = new StringBuilder();
                foreach (var argument in arguments)
                {
                    command.Append(argument).Append('\n');
                }

                command.Append("-echo4\n{done").Append(id.ToString(CultureInfo.InvariantCulture)).Append("}\n");
                command.Append("-execute").Append(id.ToString(CultureInfo.InvariantCulture)).Append('\n');
                await _process.StandardInput.WriteAsync(command.ToString().AsMemory(), timeout.Token).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);

                var output = await ReadUntilAsync(_output.Reader, "{ready" + id.ToString(CultureInfo.InvariantCulture) + "}", timeout.Token).ConfigureAwait(false);
                var errors = await ReadUntilAsync(_errors.Reader, "{done" + id.ToString(CultureInfo.InvariantCulture) + "}", timeout.Token).ConfigureAwait(false);
                return (output, errors);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller gave up part-way, so what ExifTool is doing is unknown: it is stopped, never reused.
                Stop("ExifTool was stopped because the work was cancelled.");
                throw;
            }
            catch (OperationCanceledException)
            {
                var reason = $"ExifTool did not answer within {_commandTimeout.TotalSeconds:0} seconds.";
                Stop(reason);
                throw new ExifToolStoppedException(reason);
            }
            catch (Exception ex) when (ex is ChannelClosedException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "ExifTool exited while a command was running.");
                Stop(StoppedMessage);
                throw new ExifToolStoppedException(StoppedMessage);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<List<string>> ReadUntilAsync(ChannelReader<string> reader, string marker, CancellationToken ct)
    {
        var lines = new List<string>();
        while (true)
        {
            var line = await reader.ReadAsync(ct).ConfigureAwait(false);
            if (string.Equals(line.TrimEnd(), marker, StringComparison.Ordinal))
            {
                return lines;
            }

            lines.Add(line);
        }
    }

    private static async Task PumpAsync(StreamReader reader, ChannelWriter<string> writer)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                writer.TryWrite(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process ended or was killed while its output was being read; closing the channel below tells the waiting command.
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private bool HasExited()
    {
        try
        {
            return _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // The process object was already released, which means it is not running.
            return true;
        }
    }

    private void Stop(string reason)
    {
        if (_stopped)
        {
            return;
        }

        // The first file to meet the stop is told why (the caller throws with the reason); every later one hears the short message.
        _stopped = true;
        _logger.LogWarning("ExifTool was stopped: {Reason}", reason);
        ExifToolRunner.Kill(_process);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_stopped)
            {
                await CloseGentlyAsync().ConfigureAwait(false);
            }

            _stopped = true;
            await Task.WhenAll(_outputPump, _errorPump).WaitAsync(CloseGrace).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "ExifTool's output did not finish closing cleanly.");
        }
        finally
        {
            _process.Dispose();
            _gate.Release();
        }
    }

    /// <summary>Tells ExifTool to finish (<c>-stay_open False</c>) and waits a few seconds before ending it.</summary>
    private async Task CloseGentlyAsync()
    {
        try
        {
            await _process.StandardInput.WriteAsync("-stay_open\nFalse\n".AsMemory()).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            _process.StandardInput.Close();
            using var grace = new CancellationTokenSource(CloseGrace);
            await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "ExifTool did not close when asked; ending it.");
            ExifToolRunner.Kill(_process);
        }
    }

    private sealed class ExifToolStoppedException(string message) : Exception(message);
}
