using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

namespace MediaEngine.Ingestion.Videos;

/// <summary>Supplies the Videos naming rules from <c>config/videos.json</c>, re-read when the file changes.</summary>
public sealed class VideoNamingConfigurationProvider
{
    private static readonly JsonSerializerOptions Json = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<string?> _configDirectory;
    private readonly object _gate = new();
    private VideoNamingConfiguration _current = new();
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
    private string? _loadedPath;

    public VideoNamingConfigurationProvider(IConfigurationLoader loader)
        : this(() => loader.ConfigDirectoryPath)
    {
        // Read once at startup so the shared tag list applies before the first lookup.
        _ = Current;
    }

    public VideoNamingConfigurationProvider(Func<string?> configDirectory) => _configDirectory = configDirectory;

    public VideoNamingConfiguration Current
    {
        get
        {
            var directory = _configDirectory();
            var path = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "videos.json");
            var writeTime = path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            lock (_gate)
            {
                if (path == _loadedPath && writeTime == _loadedWriteTimeUtc)
                {
                    return _current;
                }

                _current = Load(path, writeTime);
                // One list of format tags: the lookup name cleaner reads the same remove_tags as Videos folders.
                ReleaseTitleHints.UseTags(_current.TitleCleaning.RemoveTags);
                _loadedPath = path;
                _loadedWriteTimeUtc = writeTime;
                return _current;
            }
        }
    }

    private static VideoNamingConfiguration Load(string? path, DateTime writeTime)
    {
        if (path is null || writeTime == DateTime.MinValue)
        {
            return new VideoNamingConfiguration();
        }

        try
        {
            return JsonSerializer.Deserialize<VideoNamingConfiguration>(File.ReadAllText(path), Json) ?? new VideoNamingConfiguration();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken file never stops Videos from working: the built-in rules apply until it is fixed.
            System.Diagnostics.Trace.TraceWarning($"config/videos.json could not be read ({ex.GetType().Name}); using the built-in rules.");
            return new VideoNamingConfiguration();
        }
    }
}
