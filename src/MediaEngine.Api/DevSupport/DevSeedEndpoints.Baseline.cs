using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.DevSupport;

/// <summary>
/// The fixed catalogue fixtures of the repeatable ingestion baseline. They are picked from the same seed
/// arrays the development endpoints use, so a baseline run exercises real titles every provider can match,
/// plus one deliberately unmatchable item per type for the Needs Review path.
/// </summary>
public static partial class DevSeedEndpoints
{
    private static readonly HashSet<string> BaselineBookTitles = new(StringComparer.Ordinal)
    {
        "Dune",
        "Project Hail Mary",
        "The Hobbit",
        "Leviathan Wakes",
        "Caliban's War",
        "Le Petit Prince",
        "1Q84",
        "Neuromancer",
        "The Fellowship of the Ring",
        "Good Omens",
        "Frankenstein",
        // Fictional title with a synthetic ISBN: must end in Needs Review.
        "The Extraordinary & Fantastical Adventures of Dr. Enid Hartwell-Smythe III: A Most Peculiar Chronicle",
    };

    private static readonly HashSet<string> BaselineAudiobookKeys = new(StringComparer.Ordinal)
    {
        "Dune|Simon Vance",
        "Dune|Scott Brick",
        "Project Hail Mary|Ray Porter",
        "The Hobbit|Andy Serkis",
        "Neuromancer|Robertson Dean",
        "The Clockmaker's Kaleidoscope of Vanishing Summers|Imogen Vale",
    };

    // Key: media type | title | season | episode.
    private static readonly HashSet<string> BaselineVideoKeys = new(StringComparer.Ordinal)
    {
        "Movie|Blade Runner 2049||",
        "Movie|Dune: Part One||",
        "Movie|Dune: Part Two||",
        "Movie|The Matrix||",
        "Movie|Spirited Away||",
        "Movie|Arrival||",
        "Movie|Chronicles of the Titanium Orchard||",
        "TV|Breaking Bad|1|1",
        "TV|Breaking Bad|1|2",
        "TV|Seven Thirty-Seven|2|1",
        "TV|The Expanse|1|1",
        "TV|Safe|2|1",
        "TV|Shogun|1|1",
        "TV|The You You Are|1|4",
        "TV|Paper Storm|1|1",
    };

    private static readonly HashSet<string> BaselineMusicTitles = new(StringComparer.Ordinal)
    {
        "Bohemian Rhapsody",
        "Five Years",
        "Come Together",
        "Something",
        "BLOOD.",
        "Nebula Teacup Waltz",
    };

    private static readonly HashSet<string> BaselineComicTitles = new(StringComparer.Ordinal)
    {
        "Batman: Year One Part 1",
        "Batman: Year One Part 2",
        "Saga Chapter One",
        "Saga Chapter Two",
        "The Sandman: Sleep of the Just",
        "Akira Vol 1",
        "Captain Semaphore #404",
    };

    /// <summary>
    /// Writes the baseline catalogue fixtures (EPUB, audiobook MP3, movie and TV MP4, FLAC, CBZ) under
    /// <paramref name="sampleRoot"/> in the folders books, audiobooks, movies, tv, music and comics, with a fixed
    /// file time. Returns the written paths relative to the sample root, in a stable order.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> WriteBaselineCatalogueAsync(
        string sampleRoot, DateTime fileTimeUtc, CancellationToken ct = default)
    {
        var logger = NullLogger.Instance;
        var written = new List<string>();

        var booksDir = Directory.CreateDirectory(Path.Combine(sampleRoot, "books")).FullName;
        foreach (var book in SeedBooks.Where(b => BaselineBookTitles.Contains(b.Title)))
        {
            var path = Path.Combine(booksDir, $"{SanitizeFileName(book.Title)}.epub");
            var bytes = EpubBuilder.Create(
                book.Title, book.Author, book.Isbn, book.Year, book.Description,
                book.Publisher, book.Language, book.AdditionalAuthors, book.Series, book.SeriesPosition);
            await WriteFixtureAsync(path, bytes, fileTimeUtc, written, ct).ConfigureAwait(false);
        }

        var audiobooksDir = Directory.CreateDirectory(Path.Combine(sampleRoot, "audiobooks")).FullName;
        foreach (var audiobook in SeedAudiobooks.Where(a => BaselineAudiobookKeys.Contains($"{a.Title}|{a.Narrator}")))
        {
            var fixture = await CreateAudiobookFixtureAsync(audiobooksDir, audiobook, logger).ConfigureAwait(false);
            foreach (var path in fixture.CreatedPaths)
            {
                StampFile(path, fileTimeUtc);
                written.Add(path);
            }
        }

        foreach (var video in SeedVideos.Where(v => BaselineVideoKeys.Contains(
                     $"{v.MediaType}|{v.Title}|{v.SeasonNumber}|{v.EpisodeNumber}")))
        {
            var videoDir = Directory.CreateDirectory(Path.Combine(sampleRoot, video.MediaType == "TV" ? "tv" : "movies")).FullName;
            string path;
            if (!string.IsNullOrWhiteSpace(video.FileNameOverride))
            {
                path = Path.Combine(videoDir, video.FileNameOverride.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            }
            else if (video.MediaType == "TV" && video.SeasonNumber is not null && video.EpisodeNumber is not null)
            {
                path = Path.Combine(videoDir, $"{SanitizeFileName(video.Series ?? video.Title)} S{video.SeasonNumber:D2}E{video.EpisodeNumber:D2}.mp4");
            }
            else
            {
                path = Path.Combine(videoDir, $"{SanitizeFileName(video.Title)} ({video.Year}).mp4");
            }

            var bytes = Mp4Builder.Create(
                video.Title, video.Director, video.Year,
                showName: video.MediaType == "TV" ? video.Series : null,
                seasonNumber: video.SeasonNumber,
                episodeNumber: video.EpisodeNumber);
            await WriteFixtureAsync(path, bytes, fileTimeUtc, written, ct).ConfigureAwait(false);
        }

        var musicDir = Directory.CreateDirectory(Path.Combine(sampleRoot, "music")).FullName;
        foreach (var track in SeedMusicTracks.Where(t => BaselineMusicTitles.Contains(t.Title)))
        {
            var path = Path.Combine(musicDir, $"{SanitizeFileName(track.Artist)} - {SanitizeFileName(track.Title)}.flac");
            var bytes = FlacBuilder.Create(
                track.Title, track.Artist, track.Album, track.Year, track.Genre, track.TrackNumber, track.AlbumArtist);
            await WriteFixtureAsync(path, bytes, fileTimeUtc, written, ct).ConfigureAwait(false);
        }

        var comicsDir = Directory.CreateDirectory(Path.Combine(sampleRoot, "comics")).FullName;
        foreach (var comic in SeedComics.Where(c => BaselineComicTitles.Contains(c.Title)))
        {
            var path = Path.Combine(comicsDir, $"{SanitizeFileName(comic.Title)}.cbz");
            var bytes = CbzBuilder.Create(
                comic.Title, comic.Writer, comic.Series, comic.Number,
                comic.Year, comic.Genre, comic.Summary, comic.Publisher, comic.Penciller);
            await WriteFixtureAsync(path, bytes, fileTimeUtc, written, ct).ConfigureAwait(false);
        }

        return written
            .Select(path => Path.GetRelativePath(sampleRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static async Task WriteFixtureAsync(
        string path, byte[] bytes, DateTime fileTimeUtc, List<string> written, CancellationToken ct)
    {
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        StampFile(path, fileTimeUtc);
        written.Add(path);
    }

    internal static void StampFile(string path, DateTime fileTimeUtc)
    {
        File.SetCreationTimeUtc(path, fileTimeUtc);
        File.SetLastWriteTimeUtc(path, fileTimeUtc);
    }
}
