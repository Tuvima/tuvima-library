using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Default <see cref="IIdentificationSourceGate"/>. A source works when its provider file is switched on and, if it needs a key, has
/// one saved (<see cref="IdentificationSourceRules"/>, the same rule the Dashboard shows). Answers are remembered for a few seconds so a
/// scan of thousands of files reads the provider files once; saving a key is noticed on the next scan.
/// </summary>
public sealed class IdentificationSourceGate : IIdentificationSourceGate
{
    private static readonly TimeSpan Remember = TimeSpan.FromSeconds(10);

    private readonly ILibraryFolderResolver _folders;
    private readonly IConfigurationLoader _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<IdentificationSourceGate> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, (bool Held, DateTimeOffset At)> _answers = new(StringComparer.OrdinalIgnoreCase);

    public IdentificationSourceGate(
        ILibraryFolderResolver folders,
        IConfigurationLoader configuration,
        ILogger<IdentificationSourceGate> logger,
        TimeProvider? time = null)
    {
        _folders = folders;
        _configuration = configuration;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public bool IsHeld(string fullPath)
    {
        var kindKey = _folders.ResolveForPath(fullPath)?.KindKey;
        if (IdentificationSourceRules.RequiredFor(kindKey).Count == 0)
        {
            return false;
        }

        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_answers.TryGetValue(kindKey!, out var known) && now - known.At < Remember)
            {
                return known.Held;
            }
        }

        var held = IdentificationSourceRules.IsHeld(kindKey, IsWorking);
        lock (_lock)
        {
            var changed = !_answers.TryGetValue(kindKey!, out var before) || before.Held != held;
            _answers[kindKey!] = (held, now);
            if (changed)
            {
                if (held)
                {
                    _logger.LogInformation("Holding {Kind} files until an identification source works ({Sources})", kindKey, string.Join(" or ", IdentificationSourceRules.RequiredFor(kindKey)));
                }
                else
                {
                    _logger.LogInformation("An identification source works for {Kind}; its files are no longer held", kindKey);
                }
            }
        }

        return held;
    }

    private bool IsWorking(string providerName)
    {
        try
        {
            return _configuration.LoadProvider(providerName) is { } provider && IdentificationSourceRules.IsWorking(provider);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Could not read the {Provider} provider file; treating it as not working", providerName);
            return false;
        }
    }
}
