using System.Text.Json;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.DevSupport;

/// <summary>Builds tiny disposable files that reproduce structures observed in real libraries.</summary>
public static class RealWorldEdgeFixtureBuilder
{
    public static async Task<object> CreateAsync(string root, CancellationToken ct = default)
    {
        Directory.CreateDirectory(root);
        var files = new List<object>();

        var audiobookDirectory = Path.Combine(root, "audiobooks", "Andy Weir", "Project Hail Mary");
        Directory.CreateDirectory(audiobookDirectory);
        for (var part = 1; part <= 3; part++)
        {
            var path = Path.Combine(audiobookDirectory, $"{part:D3} - Part {part:D2}.mp3");
            await File.WriteAllBytesAsync(path, Mp3Builder.Create($"Part {part:D2}", "Andy Weir", "Project Hail Mary", 2021,
                narrator: "Ray Porter", asin: "B-EDGE-MULTIPART", trackNumber: part), ct);
            files.Add(Entry(root, path, "ingest", "one audiobook parent with three ordered playable parts"));
        }

        var metadataPath = Path.Combine(audiobookDirectory, "metadata.json");
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(new
        {
            title = "Project Hail Mary",
            authors = new[] { "Andy Weir" },
            narrators = new[] { "Ray Porter" },
            genres = new[] { "Science Fiction & Fantasy" },
            publishedYear = "2021",
            asin = "B-EDGE-MULTIPART",
            language = "English",
            chapters = Enumerable.Range(1, 3).Select(index => new { id = index - 1, start = 0, end = 1, title = $"Part {index:D2}" }),
        }, MediaEngineJson.Indented), ct);
        files.Add(Entry(root, metadataPath, "sidecar", "metadata enrichment only; never a media asset"));

        var bookDirectory = Path.Combine(root, "books", "Noam Chomsky", "Manufacturing Consent");
        Directory.CreateDirectory(bookDirectory);
        var azw3Path = Path.Combine(bookDirectory, "Manufacturing Consent.azw3");
        var azw3 = new byte[68];
        "BOOKMOBI"u8.CopyTo(azw3.AsSpan(60));
        await File.WriteAllBytesAsync(azw3Path, azw3, ct);
        files.Add(Entry(root, azw3Path, "ingest", "Books with external-reader capability"));

        var opfPath = Path.Combine(bookDirectory, "metadata.opf");
        await File.WriteAllTextAsync(opfPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns:dc="http://purl.org/dc/elements/1.1/">
              <metadata><dc:title>Manufacturing Consent</dc:title><dc:creator>Noam Chomsky; Edward S. Herman</dc:creator><dc:language>en</dc:language><dc:date>1988</dc:date></metadata>
            </package>
            """, ct);
        files.Add(Entry(root, opfPath, "sidecar", "metadata enrichment only; never a media asset"));

        var transientPath = Path.Combine(bookDirectory, ".fuse_hidden000e325b0001315a");
        await File.WriteAllBytesAsync(transientPath, EpubBuilder.Create("Transient Copy", "Filesystem", "0000000000", 2026, "Must be ignored."), ct);
        files.Add(Entry(root, transientPath, "ignore", "transient filesystem artifact despite valid EPUB bytes"));

        var isoPath = Path.Combine(root, "movies", "Edge Disc (2004).iso");
        Directory.CreateDirectory(Path.GetDirectoryName(isoPath)!);
        var iso = new byte[0x8006];
        "CD001"u8.CopyTo(iso.AsSpan(0x8001));
        await File.WriteAllBytesAsync(isoPath, iso, ct);
        files.Add(Entry(root, isoPath, "ingest", "movie identity with disc-image-requires-extraction playback status"));

        var tvPath = Path.Combine(root, "tv", "Solo Leveling", "Solo Leveling - S02E08 - Looking Up Was Tiring Me Out WEBRip-1080p v2.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(tvPath)!);
        await File.WriteAllBytesAsync(tvPath, Mp4Builder.Create("Looking Up Was Tiring Me Out", showName: "Solo Leveling", seasonNumber: 2, episodeNumber: 8), ct);
        files.Add(Entry(root, tvPath, "ingest", "episode title excludes WEBRip-1080p and v2 suffixes"));

        var musicPath = Path.Combine(root, "music", "Compilations", "Hot Space", "11 - Under Pressure.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(musicPath)!);
        await File.WriteAllBytesAsync(musicPath, FlacBuilder.Create("Under Pressure", "Queen & David Bowie", "Hot Space", 1982, "Rock", 11, albumArtist: "Queen"), ct);
        files.Add(Entry(root, musicPath, "ingest", "album grouped by album artist Queen while preserving track artist"));

        var viewDirectory = Path.Combine(root, "personal_photos", "2022", "September");
        Directory.CreateDirectory(viewDirectory);
        var photoPath = Path.Combine(viewDirectory, "IMG_20220911_013135.jpg");
        await File.WriteAllBytesAsync(photoPath, [0xFF, 0xD8, 0xFF, 0xD9], ct);
        files.Add(Entry(root, photoPath, "view", "still half of a Live Photo-style pair"));
        var motionPath = Path.Combine(viewDirectory, "IMG_20220911_013135.mov");
        await File.WriteAllBytesAsync(motionPath, Mp4Builder.Create("Live Photo Motion"), ct);
        files.Add(Entry(root, motionPath, "view", "motion half paired by sharing the picture's name"));

        var manifestPath = Path.Combine(root, "MANIFEST.json");
        var manifest = new
        {
            generated_at = DateTimeOffset.UtcNow,
            root,
            safety = "Disposable synthetic fixtures. This directory is not added to configured libraries automatically.",
            files,
        };
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, MediaEngineJson.Indented), ct);
        return new { root, manifest = manifestPath, file_count = files.Count, files };
    }

    private static object Entry(string root, string path, string disposition, string expectation) => new
    {
        path = Path.GetRelativePath(root, path),
        disposition,
        expectation,
    };
}
