using System.Text.RegularExpressions;
using MediaEngine.Domain;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Processors.Contracts;
using MediaEngine.Processors.Models;

namespace MediaEngine.Processors.Processors;

/// <summary>
/// Identifies and extracts metadata from video container formats
/// (MP4/M4V, MKV/WebM, AVI).
///
/// ──────────────────────────────────────────────────────────────────
/// Format detection (spec: Phase 5 – Magic-Byte Detection)
/// ──────────────────────────────────────────────────────────────────
///  • MP4 / M4V / QuickTime: bytes 4-7 = "ftyp" (ASCII)
///    All modern MP4 files begin with an <c>ftyp</c> ISO Base Media box.
///  • MKV / WebM: bytes 0-3 = 0x1A 0x45 0xDF 0xA3 (EBML header)
///  • AVI: bytes 0-3 = "RIFF" + bytes 8-11 = "AVI " (RIFF/AVI)
///
///  At least 12 bytes are read for detection; no more than 16 are needed.
///
/// ──────────────────────────────────────────────────────────────────
/// Disambiguation (Movie vs TV)
/// ──────────────────────────────────────────────────────────────────
///  All video containers are ambiguous between Movie and TV.
///  DetectedType is set to Unknown and MediaTypeCandidates is empty.
///  The IngestionEngine will call the AI MediaTypeAdvisor to classify
///  these files using filename, path, duration, and other signals.
///
/// ──────────────────────────────────────────────────────────────────
/// Metadata extraction (spec: Phase 5 – Content Extraction; FFmpeg stub)
/// ──────────────────────────────────────────────────────────────────
///  Extraction is delegated to <see cref="IVideoMetadataExtractor"/>.
///  The current default is <see cref="StubVideoMetadataExtractor"/>
///  which returns an empty <see cref="VideoMetadata"/>; replace it with
///  <c>FFmpegVideoMetadataExtractor</c> once FFmpeg is integrated.
///
///  Extracted claims:
///   • title         (confidence 0.5 — filename stem fallback)
///   • container     (confidence 1.0 — from magic bytes: "MP4", "MKV", "AVI")
///   • video_width   (confidence 0.8 — from extractor, omitted when null)
///   • video_height  (confidence 0.8 — from extractor, omitted when null)
///   • duration_sec  (confidence 0.8 — total seconds as decimal string)
///   • video_codec   (confidence 0.8 — short codec name, e.g. "h264")
///   • frame_rate    (confidence 0.8 — fps as decimal string)
///
/// Spec: Phase 5 – Media Processor Architecture § VideoProcessor (stub).
/// </summary>
public sealed class VideoProcessor : IMediaProcessor
{
    private readonly IVideoMetadataExtractor _extractor;

    public VideoProcessor(IVideoMetadataExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        _extractor = extractor;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// SupportedType is Movies for CanProcess routing purposes.
    /// ProcessAsync sets DetectedType to Unknown for all video files —
    /// the IngestionEngine will call the AI MediaTypeAdvisor to distinguish
    /// Movies from TV.
    /// </remarks>
    public MediaType SupportedType => MediaType.Movies;

    /// <inheritdoc/>
    /// <remarks>
    /// Priority 90 — below EpubProcessor (100) and AudioProcessor (95),
    /// above ComicProcessor (85).
    /// </remarks>
    public int Priority => 90;

    // -------------------------------------------------------------------------
    // IMediaProcessor
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public bool CanProcess(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        return DetectContainer(filePath) != VideoContainer.Unknown;
    }

    /// <inheritdoc/>
    public async Task<ProcessorResult> ProcessAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var container = DetectContainer(filePath);
        if (container == VideoContainer.Unknown)
        {
            return Corrupt(filePath, "No recognised video magic bytes found.");
        }

        VideoMetadata? meta = null;
        try
        {
            meta = await _extractor.ExtractAsync(filePath, ct).ConfigureAwait(false);
        }
        catch
        {
            // Extraction failure is non-fatal; we still have format + filename claims.
        }

        ct.ThrowIfCancellationRequested();

        var claims = BuildClaims(filePath, container, meta);

        // All video formats are ambiguous between Movie and TV.
        // Return empty candidates and Unknown type — IngestionEngine calls MediaTypeAdvisor.
        return new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Unknown,
            Claims = claims,
            MediaTypeCandidates = [],
        };
    }

    // -------------------------------------------------------------------------
    // Magic-byte detection
    // -------------------------------------------------------------------------

    private enum VideoContainer { Unknown, Mp4, Mkv, Avi }

    private static VideoContainer DetectContainer(string filePath)
    {
        Span<byte> header = stackalloc byte[16];
        if (!ProcessorHeaderReader.TryRead(filePath, header, out var read) || read < 4)
        {
            return VideoContainer.Unknown;
        }

        // MKV / WebM: EBML header  1A 45 DF A3
        if (header[0] == 0x1A && header[1] == 0x45 &&
            header[2] == 0xDF && header[3] == 0xA3)
        {
            return VideoContainer.Mkv;
        }

        // MP4 / M4V / QuickTime: ISO BMFF ftyp box at offset 4
        if (read >= 8 &&
            header[4] == 0x66 && header[5] == 0x74 &&   // 'f' 't'
            header[6] == 0x79 && header[7] == 0x70)      // 'y' 'p'
        {
            return VideoContainer.Mp4;
        }

        // AVI: RIFF....AVI
        if (read >= 12 &&
            header[0] == 0x52 && header[1] == 0x49 &&   // 'R' 'I'
            header[2] == 0x46 && header[3] == 0x46 &&   // 'F' 'F'
            header[8] == 0x41 && header[9] == 0x56 &&   // 'A' 'V'
            header[10] == 0x49 && header[11] == 0x20)    // 'I' ' '
        {
            return VideoContainer.Avi;
        }

        return VideoContainer.Unknown;
    }

    // -------------------------------------------------------------------------
    // Claim construction
    // -------------------------------------------------------------------------

    // ── Season/Episode regex patterns ──────────────────────────────────
    // Matched BEFORE title extraction so the series title (text before the
    // pattern) can be used as a cleaner title claim.

    /// <summary>Trailing year in parentheses — e.g. "Blade Runner 2049 (2017)".</summary>
    private static readonly Regex TrailingYearRegex = new(
        @"\s*\((\d{4})\)\s*$", RegexOptions.Compiled);

    /// <summary>Trailing organizer tokens such as {imdb-tt1160419} or [tmdb-438631].</summary>
    private static readonly Regex TrailingOrganizerTokenRegex = new(
        @"\s*(?:\{[^{}]+\}|\[[^\[\]]+\])\s*$", RegexOptions.Compiled);

    private static readonly Regex TrailingReleaseSuffixRegex = new(
        @"\s+(?:(?:BluRay|Bluray|WEBRip|WEB[ ._-]?DL|HDTV)(?:[ ._-]?\d{3,4}p)?|(?:x|h)[ ._-]?26[45]|HEVC|AVC)(?:\s+v\d+)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>1x01 format.</summary>
    private static readonly Regex NxNNRegex = new(
        @"^(?<series>.+?)\s*[.\-_ ]+(?<season>\d{1,2})[Xx](?<ep1>\d{1,4})",
        RegexOptions.Compiled);

    /// <summary>Season 1 Episode 1 (verbose).</summary>
    private static readonly Regex VerboseRegex = new(
        @"^(?<series>.+?)\s*[.\-_ ]*Season\s*(?<season>\d{1,2})\s*Episode\s*(?<ep1>\d{1,4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Leading SxxExx with optional episode title — e.g. "S01E01 - Episode Title".
    /// When the filename starts with the episode pattern (no series name prefix), the
    /// show name must be inferred from the parent folder hierarchy.</summary>
    private static readonly Regex LeadingSxxExxRegex = new(
        @"^[Ss](?<season>\d{1,2})\s*[Ee](?<ep1>\d{1,4})(?:\s*[Ee](?<ep2>\d{1,4}))?\s*[\-–—._ ]*\s*(?<epTitle>.+)?$",
        RegexOptions.Compiled);

    private static IReadOnlyList<ExtractedClaim> BuildClaims(
        string filePath, VideoContainer container, VideoMetadata? meta)
    {
        var claims = new List<ExtractedClaim>();

        // Title — best-effort from filename stem.
        // SmartLabeler (Step 6b) handles intelligent parsing of year, season, episode, and quality tags.
        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (!string.IsNullOrWhiteSpace(stem))
        {
            // Basic filename cleanup — SmartLabeler (Step 6b) handles intelligent parsing.
            var basicTitle = StripTrailingOrganizerTokens(FileStemTitleCleaner.Clean(stem));

            // Attempt season/episode extraction BEFORE the title-only path.
            // When detected, the series title (text before the episode pattern) is
            // used as a cleaner title and season_number / episode_number are emitted.
            var (seriesTitle, seasonNum, episodeNum, episodeTitle) = ExtractSeasonEpisode(basicTitle);

            // A file holding several episodes (S01E01E02, S01E01-E02, 1x01-02) also reports
            // where the run ends. Only unbroken, in-limit runs count; anything else stays a
            // single-episode read so matching can send it to Review.
            var range = EpisodeRangeParser.Parse(basicTitle);
            int? episodeEnd = range is { IsRange: true } r && r.Season == seasonNum && r.FirstEpisode == episodeNum
                ? r.LastEpisode
                : null;

            if (seriesTitle is not null && seasonNum.HasValue)
            {
                // TV title resolution: prefer the episode title (specific) over the
                // show name (generic). When the filename has no episode title we fall
                // back to the show name so the title field is never empty.
                var resolvedTitle = !string.IsNullOrWhiteSpace(episodeTitle)
                    ? episodeTitle
                    : seriesTitle;
                claims.Add(Claim("title", resolvedTitle, 0.55));
                claims.Add(Claim("show_name", seriesTitle, 0.55));
                claims.Add(Claim("series", seriesTitle, 0.55));
                claims.Add(Claim("season_number", seasonNum.Value.ToString(), 0.55));
                claims.Add(Claim("season", seasonNum.Value.ToString(), 0.55));
                if (episodeNum.HasValue)
                {
                    claims.Add(Claim("episode_number", episodeNum.Value.ToString(), 0.55));
                    claims.Add(Claim("episode", episodeNum.Value.ToString(), 0.55));
                    if (episodeEnd.HasValue)
                    {
                        claims.Add(Claim("episode_end", episodeEnd.Value.ToString(), 0.55));
                    }
                }
                if (!string.IsNullOrWhiteSpace(episodeTitle))
                {
                    claims.Add(Claim("episode_title", episodeTitle, 0.55));
                }

                // Year from containing folder (e.g. "Shogun (2024)/Season 01/...") —
                // disambiguates same-titled shows from different eras during retail match.
                var folderYear = InferYearFromPath(filePath);
                if (!string.IsNullOrWhiteSpace(folderYear))
                {
                    claims.Add(Claim("year", folderYear, 0.55));
                }
            }
            else if (seasonNum.HasValue)
            {
                // Leading SxxExx: filename starts with episode pattern, no series name.
                // Infer show name from the grandparent folder (parent = "Season XX").
                var showName = InferShowNameFromPath(filePath);
                // TV title resolution: episode title preferred, else inferred show name.
                var resolvedTitle = !string.IsNullOrWhiteSpace(episodeTitle)
                    ? episodeTitle
                    : (string.IsNullOrWhiteSpace(showName) ? basicTitle : showName);
                claims.Add(Claim("title", resolvedTitle, 0.50));
                if (!string.IsNullOrWhiteSpace(showName))
                {
                    claims.Add(Claim("show_name", showName, 0.55));
                    claims.Add(Claim("series", showName, 0.55));
                }
                claims.Add(Claim("season_number", seasonNum.Value.ToString(), 0.55));
                claims.Add(Claim("season", seasonNum.Value.ToString(), 0.55));
                if (episodeNum.HasValue)
                {
                    claims.Add(Claim("episode_number", episodeNum.Value.ToString(), 0.55));
                    claims.Add(Claim("episode", episodeNum.Value.ToString(), 0.55));
                    if (episodeEnd.HasValue)
                    {
                        claims.Add(Claim("episode_end", episodeEnd.Value.ToString(), 0.55));
                    }
                }
                if (!string.IsNullOrWhiteSpace(episodeTitle))
                {
                    claims.Add(Claim("episode_title", episodeTitle, 0.55));
                }

                // Year from containing folder — same disambiguation as filename-with-show branch.
                var folderYear = InferYearFromPath(filePath);
                if (!string.IsNullOrWhiteSpace(folderYear))
                {
                    claims.Add(Claim("year", folderYear, 0.55));
                }
            }
            else if (!string.IsNullOrWhiteSpace(basicTitle))
            {
                var hints = MediaEngine.Domain.Services.ReleaseTitleHints.Parse(basicTitle);
                claims.Add(Claim("title", hints.Title ?? basicTitle, 0.50));
                if (hints.Year is not null)
                {
                    claims.Add(Claim("year", hints.Year, 0.50));
                }
            }
        }

        // Container format — authoritative from magic bytes.
        var containerLabel = container switch
        {
            VideoContainer.Mp4 => "MP4",
            VideoContainer.Mkv => "MKV",
            VideoContainer.Avi => "AVI",
            _ => "Unknown",
        };
        claims.Add(Claim("container", containerLabel, 1.0));

        // Technical claims from extractor (emitted only when not null).
        if (meta is not null)
        {
            if (meta.WidthPx.HasValue)
            {
                claims.Add(Claim("video_width", meta.WidthPx.Value.ToString(), 0.8));
            }

            if (meta.HeightPx.HasValue)
            {
                claims.Add(Claim("video_height", meta.HeightPx.Value.ToString(), 0.8));
            }

            if (meta.Duration.HasValue)
            {
                claims.Add(Claim("duration_sec",
                    meta.Duration.Value.TotalSeconds.ToString("F3"), 0.8));
            }

            if (!string.IsNullOrWhiteSpace(meta.VideoCodec))
            {
                claims.Add(Claim("video_codec", meta.VideoCodec, 0.8));
            }

            if (!string.IsNullOrWhiteSpace(meta.AudioLanguage))
            {
                claims.Add(Claim("audio_language", meta.AudioLanguage, 0.8));
            }

            if (!string.IsNullOrWhiteSpace(meta.AudioCodec))
            {
                claims.Add(Claim("audio_codec", meta.AudioCodec, 0.8));
            }

            if (meta.AudioChannels.HasValue)
            {
                claims.Add(Claim("audio_channels", meta.AudioChannels.Value.ToString(), 0.8));
            }

            if (meta.FrameRate.HasValue)
            {
                claims.Add(Claim("frame_rate",
                    meta.FrameRate.Value.ToString("F3"), 0.8));
            }

            // Multi-valued: one claim per distinct language (see MetadataFieldConstants.MultiValuedKeys).
            foreach (var language in LanguageCodeNormalizer.NormalizeDistinct(meta.SubtitleLanguages))
            {
                claims.Add(Claim(MetadataFieldConstants.SubtitleLanguages, language, 0.8));
            }
        }

        return claims;
    }

    // -------------------------------------------------------------------------
    // Season/Episode extraction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Attempts to extract series title, season number, and episode number from
    /// a cleaned filename stem using common TV naming patterns:
    ///   S01E01, S01E01E02, 1x01, Season 1 Episode 1.
    /// Returns (null, null, null) when no pattern matches.
    /// </summary>
    private static (string? SeriesTitle, int? Season, int? Episode, string? EpisodeTitle) ExtractSeasonEpisode(string text)
    {
        // Try S01E01 / S01E01E02 first (most common): "Show Name S01E01 - Episode Title"
        var m = EpisodePatterns.SeasonEpisode().Match(text);
        if (m.Success)
        {
            var series = CleanSeriesTitle(m.Groups["series"].Value);
            var season = int.Parse(m.Groups["season"].Value);
            var episode = int.Parse(m.Groups["ep1"].Value);
            // Extract episode title from text after the SxxExx pattern
            var rangeEnd = EpisodeRangeParser.Parse(text) is { IsRange: true } range ? range.EndIndex : 0;
            var afterPattern = text[Math.Max(m.Length, rangeEnd)..].TrimStart('.', '-', '–', '—', '_', ' ');
            var epTitle = CleanEpisodeTitle(afterPattern);
            return (series, season, episode, epTitle);
        }

        // Try 1x01 format.
        m = NxNNRegex.Match(text);
        if (m.Success)
        {
            var series = CleanSeriesTitle(m.Groups["series"].Value);
            var season = int.Parse(m.Groups["season"].Value);
            var episode = int.Parse(m.Groups["ep1"].Value);
            var afterPattern = text[m.Length..].TrimStart('.', '-', '–', '—', '_', ' ');
            var epTitle = CleanEpisodeTitle(afterPattern);
            return (series, season, episode, epTitle);
        }

        // Try verbose "Season 1 Episode 1".
        m = VerboseRegex.Match(text);
        if (m.Success)
        {
            var series = CleanSeriesTitle(m.Groups["series"].Value);
            var season = int.Parse(m.Groups["season"].Value);
            var episode = int.Parse(m.Groups["ep1"].Value);
            var afterPattern = text[m.Length..].TrimStart('.', '-', '–', '—', '_', ' ');
            var epTitle = CleanEpisodeTitle(afterPattern);
            return (series, season, episode, epTitle);
        }

        // Try leading SxxExx: "S01E01 - Episode Title" (no series name prefix).
        // Series name will be inferred from the folder hierarchy by the caller.
        m = LeadingSxxExxRegex.Match(text);
        if (m.Success)
        {
            var season = int.Parse(m.Groups["season"].Value);
            var episode = int.Parse(m.Groups["ep1"].Value);
            var epTitle = m.Groups["epTitle"].Success
                ? CleanEpisodeTitle(m.Groups["epTitle"].Value)
                : null;
            return (null, season, episode, string.IsNullOrWhiteSpace(epTitle) ? null : epTitle);
        }

        return (null, null, null, null);
    }

    /// <summary>Trims trailing separators and whitespace from extracted series title.</summary>
    private static string CleanSeriesTitle(string raw)
    {
        return raw.TrimEnd('.', '-', '_', ' ');
    }

    private static string? CleanEpisodeTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var cleaned = raw.Trim();
        string previous;
        do
        {
            previous = cleaned;
            cleaned = TrailingReleaseSuffixRegex.Replace(cleaned, string.Empty).TrimEnd(' ', '-', '.', '_');
        }
        while (!string.Equals(previous, cleaned, StringComparison.Ordinal));

        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static string StripTrailingOrganizerTokens(string value)
    {
        var cleaned = value;
        string previous;
        do
        {
            previous = cleaned;
            cleaned = TrailingOrganizerTokenRegex.Replace(cleaned, string.Empty).Trim();
        }
        while (!string.Equals(previous, cleaned, StringComparison.Ordinal));

        return cleaned;
    }

    /// <summary>
    /// Walks up from the file's parent folder (skipping "Season XX") and returns the
    /// first 4-digit year inside parentheses found on a containing folder name.
    /// Used to disambiguate same-titled shows from different eras (e.g. Shōgun 1980 vs 2024).
    /// </summary>
    private static string? InferYearFromPath(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        for (var i = 0; i < 4 && !string.IsNullOrWhiteSpace(dir); i++)
        {
            var dirName = Path.GetFileName(dir);
            if (!string.IsNullOrWhiteSpace(dirName)
                && !Regex.IsMatch(dirName, @"^Season\s+\d{1,2}$", RegexOptions.IgnoreCase))
            {
                var m = Regex.Match(dirName, @"\((\d{4})\)");
                if (m.Success)
                {
                    return m.Groups[1].Value;
                }
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// Infers the show name from the folder hierarchy for files with leading SxxExx filenames.
    /// Walks up from the parent folder, skipping "Season XX" directories, to find the show folder.
    /// Strips trailing " (QXXX)" Wikidata QID suffixes from the folder name.
    /// </summary>
    private static string? InferShowNameFromPath(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        // Walk up: skip "Season XX" folders
        var dirName = Path.GetFileName(dir);
        if (dirName is not null && Regex.IsMatch(dirName, @"^Season\s+\d{1,2}$", RegexOptions.IgnoreCase))
        {
            dir = Path.GetDirectoryName(dir);
            if (string.IsNullOrWhiteSpace(dir))
            {
                return null;
            }

            dirName = Path.GetFileName(dir);
        }

        if (string.IsNullOrWhiteSpace(dirName))
        {
            return null;
        }

        // Strip trailing " (QXXX)" QID suffix — e.g. "Shogun (Q3275620)" → "Shogun"
        var cleaned = Regex.Replace(dirName, @"\s*\(Q\d+\)\s*$", string.Empty).Trim();
        // Strip trailing " (YYYY)" year suffix — e.g. "Shogun (2024)" → "Shogun"
        cleaned = Regex.Replace(cleaned, @"\s*\(\d{4}\)\s*$", string.Empty).Trim();

        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ExtractedClaim Claim(string key, string value, double confidence) =>
        ProcessorClaimFactory.Create(key, value, confidence);

    private static ProcessorResult Corrupt(string filePath, string reason) =>
        ProcessorResultFactory.Corrupt(filePath, MediaType.Movies, reason);
}
