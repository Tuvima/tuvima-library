using System.Text.Json;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Processors.Contracts;
using MediaEngine.Processors.Models;

namespace MediaEngine.Processors.Processors;

/// <summary>
/// Identifies and extracts metadata from audio container formats
/// (MP3, M4A/M4B, FLAC, OGG, WAV, AAC).
///
/// ──────────────────────────────────────────────────────────────────
/// Format detection (magic bytes)
/// ──────────────────────────────────────────────────────────────────
///  • MP3: ID3v2 header (49 44 33) or MPEG sync word (FF Fx)
///  • M4A/M4B: ISO BMFF ftyp box at offset 4 ("ftyp" ASCII)
///  • FLAC: magic bytes 66 4C 61 43 ("fLaC")
///  • OGG: magic bytes 4F 67 67 53 ("OggS")
///  • WAV: RIFF....WAVE header
///
/// ──────────────────────────────────────────────────────────────────
/// Disambiguation
/// ──────────────────────────────────────────────────────────────────
///  For unambiguous formats (FLAC, OGG, WAV → Music; M4B → Audiobooks),
///  a single high-confidence candidate is returned and DetectedType is set.
///
///  For ambiguous formats (MP3, M4A), DetectedType is set to Unknown and
///  MediaTypeCandidates is empty. The IngestionEngine will call the AI
///  MediaTypeAdvisor to classify these files.
/// </summary>
public sealed partial class AudioProcessor : IMediaProcessor
{
    /// <inheritdoc/>
    public MediaType SupportedType => MediaType.Audiobooks;

    /// <inheritdoc/>
    /// <remarks>
    /// Priority 95 — above VideoProcessor (90) to claim audio files
    /// before the video processor could claim M4A (shared ISO BMFF ftyp).
    /// </remarks>
    public int Priority => 95;

    // ── Magic-byte detection ─────────────────────────────────────────────

    private enum AudioContainer { Unknown, Mp3, M4a, M4b, Flac, Ogg, Wav, Aac, Wma }

    /// <inheritdoc/>
    public bool CanProcess(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        return DetectContainer(filePath) != AudioContainer.Unknown;
    }

    /// <inheritdoc/>
    public Task<ProcessorResult> ProcessAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var container = DetectContainer(filePath);
        if (container == AudioContainer.Unknown)
        {
            return Task.FromResult(Corrupt(filePath, "No recognised audio magic bytes found."));
        }

        ct.ThrowIfCancellationRequested();

        // Use TagLibSharp for metadata extraction.
        TagLib.File? tagFile = null;
        try
        {
            tagFile = TagLib.File.Create(filePath);
        }
        catch
        {
            // TagLib failure is non-fatal — proceed with container-only claims.
        }

        try
        {
            var claims = BuildClaims(filePath, container, tagFile).ToList();
            AddAudiobookCompanionClaims(filePath, container, tagFile, claims);
            var candidates = BuildMediaTypeCandidates(container, tagFile);
            var topType = candidates.Count > 0 ? candidates[0].Type : MediaType.Unknown;

            // Extract cover art if available.
            byte[]? coverImage = null;
            string? coverMimeType = null;
            if (tagFile?.Tag.Pictures.Length > 0)
            {
                var pic = tagFile.Tag.Pictures[0];
                coverImage = pic.Data.Data;
                coverMimeType = pic.MimeType ?? "image/jpeg";
            }

            return Task.FromResult(new ProcessorResult
            {
                FilePath = filePath,
                DetectedType = topType,
                Claims = claims,
                MediaTypeCandidates = candidates,
                CoverImage = coverImage,
                CoverImageMimeType = coverMimeType,
            });
        }
        finally
        {
            tagFile?.Dispose();
        }
    }

    // ── Container detection ──────────────────────────────────────────────

    private static AudioContainer DetectContainer(string filePath)
    {
        Span<byte> header = stackalloc byte[16];
        if (!ProcessorHeaderReader.TryRead(filePath, header, out var read) || read < 4)
        {
            return AudioContainer.Unknown;
        }

        // FLAC: 66 4C 61 43 ("fLaC")
        if (header[0] == 0x66 && header[1] == 0x4C &&
            header[2] == 0x61 && header[3] == 0x43)
        {
            return AudioContainer.Flac;
        }

        // OGG: 4F 67 67 53 ("OggS")
        if (header[0] == 0x4F && header[1] == 0x67 &&
            header[2] == 0x67 && header[3] == 0x53)
        {
            return AudioContainer.Ogg;
        }

        // Windows Media / ASF container GUID.
        ReadOnlySpan<byte> asfHeader = [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
        if (read >= asfHeader.Length && header[..asfHeader.Length].SequenceEqual(asfHeader))
        {
            return AudioContainer.Wma;
        }

        // MP3: ID3v2 header (49 44 33 = "ID3")
        if (header[0] == 0x49 && header[1] == 0x44 && header[2] == 0x33)
        {
            return AudioContainer.Mp3;
        }

        // AAC ADTS: sync word FF F1/F9.
        if (string.Equals(Path.GetExtension(filePath), ".aac", StringComparison.OrdinalIgnoreCase)
            && header[0] == 0xFF
            && (header[1] & 0xF6) == 0xF0)
        {
            return AudioContainer.Aac;
        }

        // MP3: MPEG sync word (FF Fx where x >= B0)
        if (header[0] == 0xFF && (header[1] & 0xE0) == 0xE0)
        {
            return AudioContainer.Mp3;
        }

        // ISO BMFF ftyp box at offset 4 (shared with video — distinguish by extension)
        if (read >= 8 &&
            header[4] == 0x66 && header[5] == 0x74 &&
            header[6] == 0x79 && header[7] == 0x70)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".m4b" => AudioContainer.M4b,
                ".m4a" => AudioContainer.M4a,
                // Other ftyp files (.mp4, .m4v) are video — not our concern.
                _ => AudioContainer.Unknown,
            };
        }

        // WAV: RIFF....WAVE
        if (read >= 12 &&
            header[0] == 0x52 && header[1] == 0x49 &&
            header[2] == 0x46 && header[3] == 0x46 &&
            header[8] == 0x57 && header[9] == 0x41 &&
            header[10] == 0x56 && header[11] == 0x45)
        {
            return AudioContainer.Wav;
        }

        return AudioContainer.Unknown;
    }

    // ── Claim construction ───────────────────────────────────────────────

    private static IReadOnlyList<ExtractedClaim> BuildClaims(
        string filePath, AudioContainer container, TagLib.File? tagFile)
    {
        var claims = new List<ExtractedClaim>();

        // Title — from tags first, then filename fallback.
        var title = tagFile?.Tag.Title;
        if (!string.IsNullOrWhiteSpace(title))
        {
            claims.Add(Claim("title", title, 0.8));
        }
        else
        {
            var stem = Path.GetFileNameWithoutExtension(filePath);
            if (!string.IsNullOrWhiteSpace(stem))
            {
                // Basic filename cleanup — SmartLabeler (Step 6b) handles intelligent parsing.
                var basicTitle = FileStemTitleCleaner.Clean(stem);
                if (!string.IsNullOrWhiteSpace(basicTitle))
                {
                    claims.Add(Claim("title", basicTitle, 0.50));
                }
            }
        }

        // Author / Narrator — Audible M4B files often store the narrator in AlbumArtist
        // (TPE2) and the author in the Comment field or Performer (TPE1). The priority:
        //   1. Comment field "By: Author Name" (highest confidence — explicit)
        //   2. TXXX:AUTHOR custom frame (explicit author tag)
        //   3. FirstPerformer (TPE1) — when narrator is detected separately, TPE1 is
        //      almost always the author (Audible/audiobook convention)
        //   4. FirstAlbumArtist (TPE2) — only as last resort when no narrator detected,
        //      since TPE2 is the narrator for most audiobook files
        var narrator = ExtractNarrator(tagFile);
        var commentAuthor = ExtractAuthorFromComment(tagFile);
        var txxxAuthor = ExtractTxxxValue(tagFile, "AUTHOR");

        string? author;
        if (!string.IsNullOrWhiteSpace(commentAuthor))
        {
            // Comment field has explicit author — highest priority.
            author = commentAuthor;
        }
        else if (!string.IsNullOrWhiteSpace(txxxAuthor))
        {
            // Custom TXXX:AUTHOR frame — explicit author tag.
            author = txxxAuthor;
        }
        else if (!string.IsNullOrWhiteSpace(narrator))
        {
            // Narrator detected — TPE1 (FirstPerformer) is the author, NOT TPE2
            // (FirstAlbumArtist) which is the narrator in audiobook convention.
            // Fall back to AlbumArtist only if Performer is empty.
            author = tagFile?.Tag.FirstPerformer ?? tagFile?.Tag.FirstAlbumArtist;

            // If the resolved author matches the narrator, the file probably only
            // has one person tagged everywhere — clear it so we don't duplicate.
            if (string.Equals(author, narrator, StringComparison.OrdinalIgnoreCase))
            {
                author = null;
            }
        }
        else
        {
            // No narrator detected — standard fallback: AlbumArtist then Performer.
            author = tagFile?.Tag.FirstAlbumArtist ?? tagFile?.Tag.FirstPerformer;
        }

        if (!string.IsNullOrWhiteSpace(author))
        {
            claims.Add(Claim("author", author, 0.7));
        }

        if (!string.IsNullOrWhiteSpace(narrator))
        {
            claims.Add(Claim("narrator", narrator, 0.7));
        }

        // Artist — for music files: use FirstPerformer (TPE1), fall back to FirstAlbumArtist (TPE2)
        var artist = tagFile?.Tag.FirstPerformer ?? tagFile?.Tag.FirstAlbumArtist;
        if (!string.IsNullOrWhiteSpace(artist))
        {
            claims.Add(Claim("artist", artist, 0.7));
        }

        if (!string.IsNullOrWhiteSpace(tagFile?.Tag.FirstAlbumArtist))
        {
            claims.Add(Claim("album_artist", tagFile.Tag.FirstAlbumArtist, 0.8));
        }

        // Album
        if (!string.IsNullOrWhiteSpace(tagFile?.Tag.Album))
        {
            claims.Add(Claim("album", tagFile!.Tag.Album, 0.7));
        }

        var series = ExtractTxxxValue(tagFile, "SERIES");
        if (!string.IsNullOrWhiteSpace(series))
        {
            claims.Add(Claim("series", series, 0.7));
        }

        var seriesIndex = ExtractTxxxValue(tagFile, "SERIES_INDEX");
        if (!string.IsNullOrWhiteSpace(seriesIndex))
        {
            claims.Add(Claim("series_position", seriesIndex, 0.7));
        }

        // Year
        if (tagFile?.Tag.Year is > 0)
        {
            claims.Add(Claim("year", tagFile.Tag.Year.ToString(), 0.7));
        }

        // Genre
        if (tagFile?.Tag.FirstGenre is not null)
        {
            claims.Add(Claim("genre", tagFile.Tag.FirstGenre, 0.7));
        }

        // Track number
        if (tagFile?.Tag.Track is > 0)
        {
            claims.Add(Claim("track_number", tagFile.Tag.Track.ToString(), 0.7));
        }

        // Duration
        if (tagFile?.Properties.Duration is { TotalSeconds: > 0 } dur)
        {
            claims.Add(Claim("duration_sec", dur.TotalSeconds.ToString("F3"), 0.8));
        }

        // Container format
        var containerLabel = container switch
        {
            AudioContainer.Mp3 => "MP3",
            AudioContainer.M4a => "M4A",
            AudioContainer.M4b => "M4B",
            AudioContainer.Flac => "FLAC",
            AudioContainer.Ogg => "OGG",
            AudioContainer.Wav => "WAV",
            AudioContainer.Wma => "WMA",
            _ => "Unknown",
        };
        claims.Add(Claim("container", containerLabel, 1.0));

        // Bitrate
        if (tagFile?.Properties.AudioBitrate is > 0)
        {
            claims.Add(Claim("audio_bitrate", tagFile.Properties.AudioBitrate.ToString(), 0.8));
        }

        // ASIN — commonly embedded in Audible audiobooks.
        var asin = ExtractAsin(tagFile);
        if (!string.IsNullOrWhiteSpace(asin))
        {
            claims.Add(Claim("asin", asin, 0.9));
        }

        return claims;
    }

    private static void AddAudiobookCompanionClaims(
        string filePath,
        AudioContainer container,
        TagLib.File? tagFile,
        List<ExtractedClaim> claims)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var sidecarPath = Path.Combine(directory, "metadata.json");
        var genre = tagFile?.Tag.FirstGenre;
        var isAudiobook = container == AudioContainer.M4b
            || !string.IsNullOrWhiteSpace(ExtractNarrator(tagFile))
            || !string.IsNullOrWhiteSpace(ExtractAsin(tagFile))
            || genre?.Contains("audiobook", StringComparison.OrdinalIgnoreCase) == true
            || genre?.Contains("speech", StringComparison.OrdinalIgnoreCase) == true
            || HasAudiobookSidecarShape(sidecarPath);
        if (!isAudiobook)
        {
            return;
        }

        var audioFiles = Directory.EnumerateFiles(directory)
            .Where(path => IsAudioExtension(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (audioFiles.Length > 1)
        {
            var partIndex = Array.FindIndex(audioFiles,
                path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));
            claims.Add(Claim("audiobook_part_number", (Math.Max(0, partIndex) + 1).ToString(), 0.95));
            claims.Add(Claim("audiobook_part_count", audioFiles.Length.ToString(), 0.95));
        }

        if (!File.Exists(sidecarPath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            var root = document.RootElement;
            AddJsonString(root, "title", "book_title", 0.98, claims);
            AddJsonString(root, "title", "album", 0.98, claims);
            AddJsonString(root, "publishedYear", "year", 0.92, claims);
            AddJsonString(root, "publisher", "publisher", 0.92, claims);
            AddJsonString(root, "description", "description", 0.92, claims);
            AddJsonString(root, "asin", "asin", 0.98, claims);
            AddJsonString(root, "language", "language", 0.92, claims);
            AddFirstArrayValue(root, "authors", "author", 0.98, claims);
            AddFirstArrayValue(root, "narrators", "narrator", 0.98, claims);
            AddFirstArrayValue(root, "series", "series", 0.95, claims);
            AddFirstArrayValue(root, "genres", "genre", 0.92, claims);

            if (root.TryGetProperty("chapters", out var chapters) && chapters.ValueKind == JsonValueKind.Array)
            {
                claims.Add(Claim("sidecar_chapter_count", chapters.GetArrayLength().ToString(), 0.98));
            }
        }
        catch (JsonException)
        {
            // A malformed optional sidecar must not make a playable audio file corrupt.
        }
        catch (IOException)
        {
            // The watcher may observe the audio while its companion file is still settling.
        }
    }

    private static bool HasAudiobookSidecarShape(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return root.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array
                || root.TryGetProperty("narrators", out var narrators) && narrators.ValueKind == JsonValueKind.Array
                || root.TryGetProperty("chapters", out var chapters) && chapters.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsAudioExtension(string extension)
        => extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".m4b", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".aac", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".opus", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".wma", StringComparison.OrdinalIgnoreCase);

    private static void AddJsonString(
        JsonElement root,
        string property,
        string claimKey,
        double confidence,
        List<ExtractedClaim> claims)
    {
        if (root.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            claims.Add(Claim(claimKey, value.GetString()!, confidence));
        }
    }

    private static void AddFirstArrayValue(
        JsonElement root,
        string property,
        string claimKey,
        double confidence,
        List<ExtractedClaim> claims)
    {
        if (!root.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                claims.Add(Claim(claimKey, value.GetString()!, confidence));
                return;
            }
        }
    }

    // ── Media type disambiguation ────────────────────────────────────────

    /// <summary>
    /// Returns media type candidates based on container format and embedded tag signals.
    /// Unambiguous formats (M4B, FLAC, OGG, WAV) return a single high-confidence candidate.
    /// Ambiguous formats (MP3, M4A) use tag-based heuristics — genre, narrator, ASIN,
    /// duration — to produce candidates. When no heuristic signals are found, the list
    /// is empty and the IngestionEngine will call the AI MediaTypeAdvisor.
    /// </summary>
    private static List<MediaTypeCandidate> BuildMediaTypeCandidates(AudioContainer container, TagLib.File? tagFile)
    {
        return container switch
        {
            AudioContainer.M4b => [new() { Type = MediaType.Audiobooks, Confidence = 0.98, Reason = "M4B container (chapter markers)" }],
            AudioContainer.Flac => [new() { Type = MediaType.Music, Confidence = 0.95, Reason = "FLAC format (lossless music)" }],
            AudioContainer.Ogg => [new() { Type = MediaType.Music, Confidence = 0.95, Reason = "OGG format (music)" }],
            AudioContainer.Wav => [new() { Type = MediaType.Music, Confidence = 0.95, Reason = "WAV format (uncompressed audio)" }],
            AudioContainer.Aac => [new() { Type = MediaType.Music, Confidence = 0.90, Reason = "AAC audio stream" }],
            AudioContainer.Wma => [new() { Type = MediaType.Music, Confidence = 0.90, Reason = "Windows Media Audio container" }],

            // MP3 and M4A are ambiguous (could be audiobook or music).
            // Use tag-based heuristics to produce candidates when signals are present.
            // When no signals are found, return empty — IngestionEngine will call MediaTypeAdvisor.
            _ => BuildAmbiguousAudioCandidates(tagFile),
        };
    }

    /// <summary>
    /// Produces media type candidates for ambiguous audio formats (MP3, M4A) by
    /// examining embedded tag signals. Each signal contributes a confidence boost:
    ///   - Genre contains "audiobook" or "speech"   → +0.30
    ///   - Narrator tag present (TXXX:NARRATOR)     → +0.25
    ///   - ASIN present (Audible identifier)        → +0.25
    ///   - Duration > 20 minutes                    → +0.10
    /// When the combined audiobook confidence exceeds 0.50, an Audiobooks candidate
    /// is returned. Otherwise, a Music candidate is returned if duration is short,
    /// or an empty list is returned for AI classification.
    /// </summary>
    private static List<MediaTypeCandidate> BuildAmbiguousAudioCandidates(TagLib.File? tagFile)
    {
        if (tagFile is null)
        {
            return [];
        }

        double audiobookScore = 0.0;
        var reasons = new List<string>();

        // Signal 1: Genre tag contains "audiobook" or "speech".
        var genre = tagFile.Tag.FirstGenre;
        if (!string.IsNullOrWhiteSpace(genre))
        {
            if (genre.Contains("audiobook", StringComparison.OrdinalIgnoreCase)
                || genre.Contains("speech", StringComparison.OrdinalIgnoreCase))
            {
                audiobookScore += 0.30;
                reasons.Add($"genre tag '{genre}'");
            }
        }

        // Signal 2: Narrator tag (TXXX:NARRATOR or Composers).
        var narrator = ExtractNarrator(tagFile);
        if (!string.IsNullOrWhiteSpace(narrator))
        {
            audiobookScore += 0.25;
            reasons.Add("narrator tag present");
        }

        // Signal 3: ASIN (Amazon/Audible identifier).
        var asin = ExtractAsin(tagFile);
        if (!string.IsNullOrWhiteSpace(asin))
        {
            audiobookScore += 0.25;
            reasons.Add("ASIN present");
        }

        // Signal 4: Long duration (> 20 minutes suggests audiobook, not a music track).
        var durationMinutes = tagFile.Properties.Duration.TotalMinutes;
        if (durationMinutes > 20)
        {
            audiobookScore += 0.10;
            reasons.Add($"duration {durationMinutes:F0}min");
        }

        if (audiobookScore >= 0.50)
        {
            // Strong audiobook signals — return an Audiobooks candidate.
            // Cap at 0.90 since tag-based heuristics aren't definitive.
            var confidence = Math.Min(audiobookScore, 0.90);
            return [new()
            {
                Type       = MediaType.Audiobooks,
                Confidence = confidence,
                Reason     = $"Tag signals: {string.Join(", ", reasons)}",
            }];
        }

        // No strong audiobook signals — return empty for AI classification.
        return [];
    }

    // ── Narrator extraction ────────────────────────────────────────────

    /// <summary>
    /// Attempts to extract a narrator name from audio tags, checking multiple
    /// tag fields in priority order:
    /// 1. TXXX:NARRATOR custom ID3v2 frame (written by Mp3Builder and some tools)
    /// 2. Composers (common for M4B — "narrator" tagged as composer)
    /// 3. Comment field with "Narrated by ..." pattern
    /// 4. Performers (when AlbumArtist holds the author, Performers can hold the narrator)
    /// </summary>
    private static string? ExtractNarrator(TagLib.File? tagFile)
    {
        if (tagFile is null)
        {
            return null;
        }

        // 1. ID3v2 TXXX:NARRATOR user text frame (MP3 files written by Mp3Builder).
        if (tagFile.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3Tag)
        {
            foreach (var frame in id3Tag.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
            {
                if (string.Equals(frame.Description, "NARRATOR", StringComparison.OrdinalIgnoreCase) &&
                    frame.Text.Length > 0)
                {
                    var value = frame.Text[0]?.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }

        // 2. Composers tag — common convention in M4B audiobooks.
        var composers = tagFile.Tag.Composers;
        if (composers is { Length: > 0 })
        {
            var firstComposer = composers[0]?.Trim();
            if (!string.IsNullOrWhiteSpace(firstComposer))
            {
                return firstComposer;
            }
        }

        // 2. Comment field — look for "Narrated by <Name>" pattern.
        var comment = tagFile.Tag.Comment;
        if (!string.IsNullOrWhiteSpace(comment))
        {
            var match = NarratedByRegex().Match(comment);
            if (match.Success)
            {
                var name = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }

        // 3. Performers — if AlbumArtist is set (author), Performers may be the narrator.
        //    Only use this when AlbumArtist is present (so we know Performers isn't the author).
        if (!string.IsNullOrWhiteSpace(tagFile.Tag.FirstAlbumArtist))
        {
            var performer = tagFile.Tag.FirstPerformer?.Trim();
            if (!string.IsNullOrWhiteSpace(performer) &&
                !string.Equals(performer, tagFile.Tag.FirstAlbumArtist?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return performer;
            }
        }

        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"[Nn]arrat(?:ed|or)\s+(?:by\s+)?(.+)",
        System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex NarratedByRegex();

    // ── Author extraction from Comment field ──────────────────────────

    /// <summary>
    /// Attempts to extract an author name from the Comment tag field.
    /// Audible audiobooks commonly embed "By: Author Name" or
    /// "Written by: Author Name" in the comment, while using AlbumArtist
    /// for the narrator. Also handles the combined format:
    /// "By: Author Name, Narrated by: Narrator Name".
    /// </summary>
    private static string? ExtractAuthorFromComment(TagLib.File? tagFile)
    {
        if (tagFile is null)
        {
            return null;
        }

        var comment = tagFile.Tag.Comment;
        if (string.IsNullOrWhiteSpace(comment))
        {
            return null;
        }

        var match = AuthorByRegex().Match(comment);
        if (match.Success)
        {
            var name = match.Groups[1].Value.Trim();
            // Strip trailing commas or "Narrated by" continuation.
            var commaIdx = name.IndexOf(',');
            if (commaIdx > 0)
            {
                name = name[..commaIdx].Trim();
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?:^|[,;]\s*)(?:[Bb]y|[Ww]ritten\s+[Bb]y|[Aa]uthor)[:\s]+(.+)",
        System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex AuthorByRegex();

    // ── TXXX value extraction ────────────────────────────────────────

    /// <summary>
    /// Reads a custom TXXX (user text) frame value from an ID3v2 tag.
    /// Returns null when the frame is not found or the file has no ID3v2 tag.
    /// </summary>
    private static string? ExtractTxxxValue(TagLib.File? tagFile, string description)
    {
        if (tagFile is null)
        {
            return null;
        }

        var id3 = tagFile.GetTag(TagLib.TagTypes.Id3v2) as TagLib.Id3v2.Tag;
        if (id3 is null)
        {
            return null;
        }

        foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
        {
            if (string.Equals(frame.Description, description, StringComparison.OrdinalIgnoreCase)
                && frame.Text?.Length > 0
                && !string.IsNullOrWhiteSpace(frame.Text[0]))
            {
                return frame.Text[0].Trim();
            }
        }

        return null;
    }

    // ── ASIN extraction ──────────────────────────────────────────────

    /// <summary>
    /// Attempts to extract an ASIN (Amazon Standard Identification Number) from
    /// audio tags. Audible audiobooks commonly embed ASINs in custom tag fields.
    /// Sources checked in priority order:
    /// 1. M4B/M4A iTunes custom atoms (com.audible.asin, com.apple.iTunes:ASIN)
    /// 2. MP3 ID3v2 TXXX user text frames (ASIN, AUDIBLE_ASIN, AMAZON_ASIN)
    /// 3. Vorbis/FLAC custom fields (ASIN)
    /// 4. Comment field regex fallback (B0[A-Z0-9]{8} pattern)
    /// </summary>
    private static string? ExtractAsin(TagLib.File? tagFile)
    {
        if (tagFile is null)
        {
            return null;
        }

        // 1. iTunes custom atoms (M4B/M4A from Audible).
        if (tagFile.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag appleTag)
        {
            var asin = ReadAppleCustomAtom(appleTag, "com.audible.asin")
                    ?? ReadAppleCustomAtom(appleTag, "com.apple.iTunes", "ASIN")
                    ?? ReadAppleCustomAtom(appleTag, "com.apple.iTunes", "AUDIBLE_ASIN");
            if (IsValidAsin(asin))
            {
                return asin;
            }
        }

        // 2. ID3v2 TXXX user text frames (MP3).
        if (tagFile.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3Tag)
        {
            foreach (var frame in id3Tag.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
            {
                if (frame.Description is not null &&
                    AsinFrameDescriptions.Contains(frame.Description) &&
                    frame.Text.Length > 0)
                {
                    var value = frame.Text[0]?.Trim();
                    if (IsValidAsin(value))
                    {
                        return value;
                    }
                }
            }
        }

        // 3. Vorbis/FLAC custom fields.
        if (tagFile.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiphTag)
        {
            var fields = xiphTag.GetField("ASIN");
            if (fields.Length > 0)
            {
                var value = fields[0]?.Trim();
                if (IsValidAsin(value))
                {
                    return value;
                }
            }
        }

        // 4. Comment field regex fallback — look for ASIN pattern.
        var comment = tagFile.Tag.Comment;
        if (!string.IsNullOrWhiteSpace(comment))
        {
            var match = AsinRegex().Match(comment);
            if (match.Success)
            {
                return match.Value;
            }
        }

        return null;
    }

    /// <summary>Reads a freeform iTunes custom atom by mean+name key.</summary>
    private static string? ReadAppleCustomAtom(TagLib.Mpeg4.AppleTag appleTag, string mean, string? name = null)
    {
        // When name is null, mean contains the full key (e.g. "com.audible.asin")
        // and we try both as mean-only and mean+name split.
        if (name is null)
        {
            // Try the full string as the "mean" with "asin" as "name" for common patterns.
            var lastDot = mean.LastIndexOf('.');
            if (lastDot < 0)
            {
                return null;
            }

            var derivedMean = mean[..lastDot];
            var derivedName = mean[(lastDot + 1)..];
            return appleTag.GetDashBox(derivedMean, derivedName);
        }
        else
        {
            return appleTag.GetDashBox(mean, name);
        }
    }

    /// <summary>Validates that a string looks like an Amazon ASIN (B0 + 8 alphanumeric chars).</summary>
    private static bool IsValidAsin(string? value)
        => !string.IsNullOrWhiteSpace(value) && AsinRegex().IsMatch(value);

    private static readonly HashSet<string> AsinFrameDescriptions =
        new(StringComparer.OrdinalIgnoreCase) { "ASIN", "AUDIBLE_ASIN", "AMAZON_ASIN" };

    [System.Text.RegularExpressions.GeneratedRegex(
        @"B0[A-Z0-9]{8}",
        System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex AsinRegex();

    // ── Helpers ──────────────────────────────────────────────────────────

    private static ExtractedClaim Claim(string key, string value, double confidence) =>
        ProcessorClaimFactory.Create(key, value, confidence);

    private static ProcessorResult Corrupt(string filePath, string reason) =>
        ProcessorResultFactory.Corrupt(filePath, MediaType.Unknown, reason);
}
