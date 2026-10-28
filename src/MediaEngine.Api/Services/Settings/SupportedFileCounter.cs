using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Organise;

namespace MediaEngine.Api.Services.Settings;

/// <summary>The outcome of counting a folder's supported files: the number, whether it stopped at the cap, or that it could not be made.</summary>
/// <param name="Count">Files found (up to the cap); null when <paramref name="Unavailable"/>.</param>
/// <param name="Capped">True when the walk stopped at the cap, so the real number is larger ("10,000+").</param>
/// <param name="Unavailable">True when the count ran out of time or was cancelled.</param>
/// <param name="KindLabel">One lower-case word for what was counted ("video", "audio", "book", "comic").</param>
public sealed record SupportedFileCount(int? Count, bool Capped, bool Unavailable, string? KindLabel);

/// <summary>
/// Counts the files in a folder that a library kind can take, for the folder picker's "126 video files found" line. It reuses the
/// extension rules ingestion already uses (the media extension catalogue for the kind's format) and the same read-only folder walk, never
/// opens a file, stops at <see cref="Cap"/>, honours cancellation and gives up after the time limit so a slow network drive can
/// never freeze the dialog.
/// </summary>
public static class SupportedFileCounter
{
    /// <summary>The most files counted; more than this reads "10,000+".</summary>
    public const int Cap = 10_000;

    /// <summary>The longest the count may take before it answers "unavailable".</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(6);

    /// <summary>The words for what a kind's format counts, or null for a kind with no files to count (View kinds).</summary>
    public static string? KindLabelFor(string? format) => format switch
    {
        nameof(MediaType.Movies) or nameof(MediaType.TV) or nameof(MediaType.Videos) => "video",
        nameof(MediaType.Music) or nameof(MediaType.Audiobooks) => "audio",
        nameof(MediaType.Books) => "book",
        nameof(MediaType.Comics) => "comic",
        _ => null,
    };

    /// <summary>The extensions a kind's format takes, from the media type catalogue. Empty for a kind with no format.</summary>
    public static IReadOnlySet<string> ExtensionsFor(IMediaTypeExtensionCatalog catalog, string? format)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Enum.TryParse<MediaType>(format, ignoreCase: false, out var mediaType) && mediaType != MediaType.Unknown
            ? catalog.GetExtensionsFor(mediaType)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Counts <paramref name="extensions"/> files under <paramref name="path"/>.</summary>
    public static SupportedFileCount Count(
        string path,
        IReadOnlySet<string> extensions,
        string? kindLabel,
        CancellationToken cancellationToken = default,
        TimeSpan? timeLimit = null,
        int cap = Cap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(extensions);

        var limit = timeLimit ?? TimeLimit;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        var token = timeout.Token;

        // The walk runs on its own task so one folder listing that hangs (an offline network share) cannot hold the caller past the limit.
        var walk = Task.Run(
            () => FolderMediaEnumerator.Enumerate(path, includeSubdirectories: true, extensions, stopAfter: cap + 1, token, skipLinks: true),
            CancellationToken.None);
        try
        {
            if (!walk.Wait(limit, cancellationToken))
            {
                timeout.Cancel();
                Observe(walk);
                return new SupportedFileCount(null, false, true, kindLabel);
            }

            var found = walk.GetAwaiter().GetResult().Count;
            return found > cap
                ? new SupportedFileCount(cap, true, false, kindLabel)
                : new SupportedFileCount(found, false, false, kindLabel);
        }
        catch (OperationCanceledException)
        {
            timeout.Cancel();
            Observe(walk);
            return new SupportedFileCount(null, false, true, kindLabel);
        }
    }

    // A walk left behind finishes on its own (it was told to stop); its outcome is never read, so keep it from surfacing as unobserved.
    private static void Observe(Task walk) => walk.ContinueWith(
        static finished => _ = finished.Exception,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}
