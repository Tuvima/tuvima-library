using Microsoft.Extensions.Logging;

namespace MediaEngine.AI.Infrastructure;

/// <summary>Whether the native AI runtimes can load on this machine. Cached; re-probed on tool install or refresh.</summary>
public interface IAiRuntimeStatus
{
    /// <summary>True when the native text runtime (LLamaSharp) loads.</summary>
    bool TextRuntimeLoads { get; }

    /// <summary>True when the native speech runtime (Whisper) loads.</summary>
    bool AudioRuntimeLoads { get; }

    /// <summary>Probes the text runtime again right now, stores the answer and returns it.</summary>
    bool RefreshTextRuntime();
}

/// <summary>
/// The real probe behind <see cref="IAiRuntimeStatus"/>: asks <see cref="SharedAiRuntime"/> to load the native runtime.
/// A working runtime stays working for the life of the process, so a yes is kept. A no is kept for a short time so a
/// missing install is not re-checked (and its files re-hashed) on every request, then looked at again.
/// </summary>
public sealed class AiRuntimeStatus : IAiRuntimeStatus
{
    private static readonly TimeSpan NegativeLifetime = TimeSpan.FromSeconds(30);

    private readonly SharedAiRuntime _runtime;
    private readonly ILogger<AiRuntimeStatus> _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private ProbeResult? _text;
    private ProbeResult? _audio;
    private readonly HashSet<string> _notInstalledLogged = [];

    public AiRuntimeStatus(SharedAiRuntime runtime, ILogger<AiRuntimeStatus> logger, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(logger);
        _runtime = runtime;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public bool TextRuntimeLoads => Read(ref _text, () => _runtime.EnsureLlama(), "text");

    public bool AudioRuntimeLoads => Read(ref _audio, () => _runtime.EnsureWhisper(), "speech");

    public bool RefreshTextRuntime()
    {
        lock (_gate)
        {
            _text = Probe(() => _runtime.EnsureLlama(), "text");
            return _text.Loads;
        }
    }

    private bool Read(ref ProbeResult? cached, Func<string> load, string name)
    {
        lock (_gate)
        {
            if (cached is { } known && (known.Loads || _time.GetUtcNow() - known.At < NegativeLifetime))
            {
                return known.Loads;
            }

            cached = Probe(load, name);
            return cached.Loads;
        }
    }

    private ProbeResult Probe(Func<string> load, string name)
    {
        try
        {
            load();
            return new ProbeResult(true, _time.GetUtcNow());
        }
        catch (AiRuntimeNotInstalledException)
        {
            // The normal case on a machine that never ran the AI runtime install: say so once, calmly, with no exception attached.
            if (_notInstalledLogged.Add(name))
            {
                _logger.LogInformation("Local AI is off: the {Runtime} AI runtime is not installed. Settings, Tools and requirements shows how to add it.", name);
            }

            return new ProbeResult(false, _time.GetUtcNow());
        }
        catch (Exception ex)
        {
            // An install that is present but cannot load fails in many ways (files, manifest, native loader); all mean "not loaded",
            // and the reason is in the log.
            _logger.LogWarning(ex, "The {Runtime} AI runtime could not be loaded.", name);
            return new ProbeResult(false, _time.GetUtcNow());
        }
    }

    private sealed record ProbeResult(bool Loads, DateTimeOffset At);
}
