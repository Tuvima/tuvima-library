using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Reading;

/// <summary>
/// Supplies the page reader's limits from <c>config/reader.json</c>, re-read when the file changes. A missing or broken file
/// falls back to the built-in values (PDF and comic reader spec D19).
/// </summary>
public sealed class ReaderConfigurationProvider
{
    private static readonly JsonSerializerOptions Json = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<string?> _configDirectory;
    private readonly object _gate = new();
    private ReaderConfiguration _current = new();
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
    private string? _loadedPath;

    public ReaderConfigurationProvider(IConfigurationLoader loader)
        : this(() => loader.ConfigDirectoryPath)
    {
    }

    public ReaderConfigurationProvider(Func<string?> configDirectory) => _configDirectory = configDirectory;

    public ReaderConfiguration Current
    {
        get
        {
            var directory = _configDirectory();
            var path = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "reader.json");
            var writeTime = path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            lock (_gate)
            {
                if (path == _loadedPath && writeTime == _loadedWriteTimeUtc)
                {
                    return _current;
                }

                _current = Load(path, writeTime);
                _loadedPath = path;
                _loadedWriteTimeUtc = writeTime;
                return _current;
            }
        }
    }

    private static ReaderConfiguration Load(string? path, DateTime writeTime)
    {
        if (path is null || writeTime == DateTime.MinValue)
        {
            return new ReaderConfiguration();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<ReaderConfiguration>(File.ReadAllText(path), Json);
            return loaded is not null && IsUsable(loaded.Comic) ? loaded : new ReaderConfiguration();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"config/reader.json could not be read ({ex.GetType().Name}); using the built-in values.");
            return new ReaderConfiguration();
        }
    }

    private static bool IsUsable(ComicReaderConfiguration comic) =>
        comic.Widths is { Length: > 0 } && comic.Widths.All(w => w > 0)
        && comic.ThumbnailWidth > 0 && comic.MaxEntries > 0 && comic.MaxPageMegabytes > 0 && comic.MaxPageMegapixels > 0
        && comic.MaxUnpackedMegabytes > 0 && comic.CacheSizeMb > 0;
}
