using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Tells the options system to read <c>libraries.json</c> again whenever it is saved, so a folder switched between As-is and
/// Managed is seen by the file-source policy the next time a file is touched, without restarting the Engine.
/// </summary>
public sealed class LibrariesOptionsChangeTokenSource : IOptionsChangeTokenSource<IngestionOptions>, IDisposable
{
    private readonly IConfigurationLoader? _configuration;
    private readonly ILibraryKindSource? _kindSource;
    private readonly object _gate = new();
    private CancellationTokenSource _source = new();

    public LibrariesOptionsChangeTokenSource(IConfigurationLoader configuration, ILibraryKindSource? kindSource = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
        _configuration.ConfigurationChanged += OnConfigurationChanged;

        // The folder list is built against the merged kinds, so a plugin's kinds changing refreshes it too.
        _kindSource = kindSource;
        if (_kindSource is not null)
        {
            _kindSource.Changed += Notify;
        }
    }

    public string? Name => Options.DefaultName;

    public IChangeToken GetChangeToken()
    {
        lock (_gate)
        {
            return new CancellationChangeToken(_source.Token);
        }
    }

    /// <summary>Marks the folder settings as changed now (used right after a save, before the file watcher notices).</summary>
    public void Notify()
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            previous = _source;
            _source = new CancellationTokenSource();
        }

        previous.Cancel();
        previous.Dispose();
    }

    public void Dispose()
    {
        if (_configuration is not null)
        {
            _configuration.ConfigurationChanged -= OnConfigurationChanged;
        }

        if (_kindSource is not null)
        {
            _kindSource.Changed -= Notify;
        }

        lock (_gate)
        {
            _source.Dispose();
        }
    }

    private void OnConfigurationChanged(object? sender, ConfigurationFileChangedEventArgs args)
    {
        if (args.Applied && args.RelativePath.Replace((char)92, '/').EndsWith("libraries.json", StringComparison.OrdinalIgnoreCase))
        {
            Notify();
        }
    }
}
