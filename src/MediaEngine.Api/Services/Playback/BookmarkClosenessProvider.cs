using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Supplies how close two bookmarks must be from <c>config/bookmarks.json</c>, re-read when the file changes, so a value can be tuned
/// without a rebuild or a restart (Global bookmarks spec D7). A missing or broken file falls back to the built-in values.
/// </summary>
public sealed class BookmarkClosenessProvider
{
    private static readonly JsonSerializerOptions Json = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<string?> _configDirectory;
    private readonly object _gate = new();
    private BookmarkClosenessConfiguration _current = new();
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
    private string? _loadedPath;

    public BookmarkClosenessProvider(IConfigurationLoader loader)
        : this(() => loader.ConfigDirectoryPath)
    {
    }

    public BookmarkClosenessProvider(Func<string?> configDirectory) => _configDirectory = configDirectory;

    public BookmarkClosenessConfiguration Current
    {
        get
        {
            var directory = _configDirectory();
            var path = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "bookmarks.json");
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

    /// <summary>The largest gap, in seconds, that still counts as the same moment in a video or audiobook.</summary>
    public double SameSpotSeconds => Current.Time.SameSpot;

    private static BookmarkClosenessConfiguration Load(string? path, DateTime writeTime)
    {
        if (path is null || writeTime == DateTime.MinValue)
        {
            return new BookmarkClosenessConfiguration();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<BookmarkClosenessConfiguration>(File.ReadAllText(path), Json);
            return loaded is not null && IsUsable(loaded) ? loaded : new BookmarkClosenessConfiguration();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken file never stops bookmarks from working: the built-in values apply until it is fixed.
            System.Diagnostics.Trace.TraceWarning($"config/bookmarks.json could not be read ({ex.GetType().Name}); using the built-in values.");
            return new BookmarkClosenessConfiguration();
        }
    }

    /// <summary>
    /// Every block needs finite, non-negative limits. Time and page limits must not shrink from same spot to close to far; the text block
    /// mixes a fraction of a chapter (same spot) with whole chapters (close, far), so only close to far is compared.
    /// </summary>
    private static bool IsUsable(BookmarkClosenessConfiguration configuration) =>
        IsOrdered(configuration.Time) && IsOrdered(configuration.Page) && IsFiniteAndPositive(configuration.Text) && configuration.Text.Far >= configuration.Text.Close;

    private static bool IsFiniteAndPositive(BookmarkGapLimits limits) =>
        double.IsFinite(limits.SameSpot) && double.IsFinite(limits.Close) && double.IsFinite(limits.Far)
        && limits.SameSpot >= 0 && limits.Close >= 0 && limits.Far >= 0;

    private static bool IsOrdered(BookmarkGapLimits limits) =>
        IsFiniteAndPositive(limits) && limits.Close >= limits.SameSpot && limits.Far >= limits.Close;
}
