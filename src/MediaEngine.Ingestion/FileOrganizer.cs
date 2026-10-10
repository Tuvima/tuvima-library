using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion;

/// <summary>
/// Calculates organised destination paths from tokenized templates and
/// performs collision-safe, retry-backed file moves.
///
/// ──────────────────────────────────────────────────────────────────
/// Template tokens (spec: Phase 7 – Extension Points § Organization Templates)
/// ──────────────────────────────────────────────────────────────────
///  Tokens are surrounded by curly braces and resolved from the candidate's
///  claims at the time of organization.  Unresolved tokens are replaced with
///  the literal string "Unknown".
///
///  Built-in tokens:
///   {Title}        — title claim (confidence-winner)
///   {Author}       — author claim
///   {Year}         — year claim (4-digit integer)
///   {MediaType}    — MediaType enum name (Movies, Books, Comic, …)
///   {Extension}    — file extension WITHOUT leading dot (e.g. "mp4", "epub")
///   {Ext}          — file extension WITH leading dot (e.g. ".mp4")
///   {Series}       — series claim (also accepts the legacy "show_name" key)
///   {Publisher}    — publisher claim
///   {Category}     — top-level category folder (Movies, TV, Music, …)
///   {Artist}       — music artist
///   {AlbumArtist}  — music album artist (falls back to track artist)
///   {Album}        — music album
///   {BookTitle}    — audiobook work title from sidecar or album tag
///   {TrackTitle}   — source-authored audiobook/music part title
///   {TrackNumber}  — zero-padded track number
///   {Disc}         — zero-padded disc number for multi-disc albums (empty when single-disc — pair with conditional `({Disc})`)
///   {Season}       — zero-padded TV season
///   {Episode}      — zero-padded TV episode
///   {EpisodeTitle} — TV episode title (falls back to {Title})
///   {IssueNumber}  — comic issue number
///   {ImdbId}       — IMDB id (e.g. "tt1856101"); empty when unknown
///   {TmdbId}       — TheMovieDB id; empty when unknown
///   {TvdbId}       — TheTVDB id; empty when unknown
///   {Qid}          — Wikidata QID (legacy Tuvima identifier)
///
///  Side-by-side-with-Plex plan §A: bridge ID tokens enable Plex / Jellyfin
///  compatible folder names like `{Title} ({Year}) {{imdb-{ImdbId}}}`. Use
///  the conditional group syntax `({Token})` to collapse missing tokens
///  cleanly so unknown IDs don't leave stray brackets in the path.
///
///  Custom tokens may be injected via the <c>IReadOnlyDictionary</c> overload
///  of <see cref="CalculatePath"/>.
///
/// ──────────────────────────────────────────────────────────────────
/// Collision handling
/// ──────────────────────────────────────────────────────────────────
///  When the computed destination already exists, a numeric suffix is appended:
///  <c>Title (2).epub</c>, <c>Title (3).epub</c>, etc.
///  Spec: "If a naming conflict occurs … MUST append a unique suffix."
///
/// ──────────────────────────────────────────────────────────────────
/// Move retry
/// ──────────────────────────────────────────────────────────────────
///  On <see cref="IOException"/>, the move is retried with exponential
///  back-off up to <c>MaxMoveAttempts</c>.
///  Spec: Phase 7 – Lock handling § Retry Exponential Backoff.
/// </summary>
public sealed class FileOrganizer : IFileOrganizer
{
    private const int MaxMoveAttempts = 5;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<FileOrganizer> _logger;
    private readonly IFileHashCacheRepository? _hashCache;

    // Characters that are illegal in file/directory names on Windows and most POSIX systems.
    private static readonly char[] InvalidPathChars =
        Path.GetInvalidFileNameChars()
            .Concat(Path.GetInvalidPathChars())
            .Distinct()
            .ToArray();

    public FileOrganizer(ILogger<FileOrganizer> logger, IFileHashCacheRepository? hashCache = null)
    {
        _logger = logger;
        _hashCache = hashCache;
    }

    // Matches an optional leading space followed by ({Token}) — used for conditional groups.
    private static readonly System.Text.RegularExpressions.Regex ConditionalGroupRegex = new(
        @"\s?\(\{([^}]+)\}\)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Matches an optional leading space followed by `{{prefix-{Token}}}` — the
    // Plex / Jellyfin bridge-ID literal-brace convention used by templates like
    //     `{Title} ({Year}) {{imdb-{ImdbId}}}`
    // Group 1 captures the prefix (e.g. "imdb-"); group 2 captures the inner
    // token name (e.g. "ImdbId"). When the token resolves to a non-empty value
    // the whole match expands to ` {imdb-tt1856101}` using SENTINEL braces that
    // get unescaped in the cleanup pass — this prevents the unresolved-token
    // regex from mistaking the result for a missing template token. When the
    // token is empty/missing the entire group collapses (incl. leading space).
    // Side-by-side-with-Plex plan §A.
    private static readonly System.Text.RegularExpressions.Regex BridgeIdGroupRegex = new(
        @"\s?\{\{([^{}]+)\{([^}]+)\}\}\}",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Sentinel characters used to stand in for literal `{` and `}` while the
    // template is being resolved. They are unescaped in the cleanup pass.
    // Using control characters that cannot legally appear in any path segment.
    private const char OpenBraceSentinel = '\u0001';
    private const char CloseBraceSentinel = '\u0002';

    // Matches any remaining {Token} references not inside parentheses.
    private static readonly System.Text.RegularExpressions.Regex UnresolvedTokenRegex = new(
        @"\{[^}]+\}",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Matches an optional token of the form `{Token?}` — group 1 captures the
    // bare token name. Optional tokens that resolve to empty produce an empty
    // string (rather than "Unknown") and any path segment they leave empty is
    // dropped in the cleanup pass.
    private static readonly System.Text.RegularExpressions.Regex OptionalTokenRegex = new(
        @"\{([A-Za-z0-9_]+)\?\}",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Matches two or more consecutive whitespace characters.
    private static readonly System.Text.RegularExpressions.Regex MultiSpaceRegex = new(
        @"\s{2,}",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // -------------------------------------------------------------------------
    // IFileOrganizer
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public string CalculatePath(IngestionCandidate candidate, string template)
        => CalculatePath(candidate, template, extraTokens: null);

    /// <summary>
    /// Overload that accepts additional caller-supplied token values.
    /// </summary>
    public string CalculatePath(
        IngestionCandidate candidate,
        string template,
        IReadOnlyDictionary<string, string>? extraTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        ArgumentNullException.ThrowIfNull(candidate);

        var tokens = BuildTokens(candidate, extraTokens);

        string resolved = ResolveTemplate(template, tokens);

        return resolved;
    }

    /// <inheritdoc/>
    public string? ValidateTemplate(string template, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(template))
        {
            error = "Template cannot be empty.";
            return null;
        }

        // Build sample tokens with representative values.
        var sampleTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Title"] = "Sample Book",
            ["Author"] = "Jane Author",
            ["Year"] = "2024",
            ["MediaType"] = "Epub",
            ["Extension"] = "epub",
            ["Ext"] = ".epub",
            ["Series"] = "Great Series",
            ["Publisher"] = "Publisher Co",
            ["Category"] = "Books",
            ["CollectionName"] = "Sample Book",
            ["Format"] = "Epub",
            ["Edition"] = "Hardcover",
            ["Qid"] = "Q190159",
            ["Artist"] = "Sample Artist",
            ["AlbumArtist"] = "Sample Album Artist",
            ["Album"] = "Sample Album",
            ["BookTitle"] = "Sample Audiobook",
            ["TrackTitle"] = "Chapter One",
            ["TrackNumber"] = "01",
            ["Season"] = "01",
            ["Episode"] = "01",
            ["EpisodeTitle"] = "Pilot",
            ["Disc"] = string.Empty,
            ["IssueNumber"] = "001",
            ["ImdbId"] = "tt1234567",
            ["TmdbId"] = "12345",
            ["TvdbId"] = "67890",
            ["Hash6"] = "a1b2c3",
        };

        string resolved = ResolveTemplate(template, sampleTokens);

        // Validate: no empty parentheses.
        if (resolved.Contains("()", StringComparison.Ordinal))
        {
            error = "Template produces empty parentheses '()'. Check your token names.";
            return null;
        }

        // Validate: no double spaces.
        if (resolved.Contains("  ", StringComparison.Ordinal))
        {
            error = "Template produces double spaces. Check token placement.";
            return null;
        }

        // Validate: no consecutive path separators.
        if (resolved.Contains("//", StringComparison.Ordinal)
            || resolved.Contains("\\\\", StringComparison.Ordinal))
        {
            error = "Template produces consecutive path separators.";
            return null;
        }

        return resolved;
    }

    /// <inheritdoc/>
    public async Task<bool> ExecuteMoveAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (!File.Exists(sourcePath))
        {
            _logger.LogWarning("Move skipped — source file not found: {Source}", sourcePath);
            return false;
        }

        // Ensure the destination directory exists.
        string? destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        // If the source is already at the requested destination, there is
        // nothing left to do.
        if (Path.GetFullPath(sourcePath).Equals(
                Path.GetFullPath(destinationPath),
                StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Move skipped — source already matches destination: {Destination}",
                destinationPath);
            return true;
        }

        // Resolve collision: if destination exists with different content, find a free name.
        string finalDest = ResolveCollision(destinationPath);

        var delay = InitialRetryDelay;
        for (int attempt = 1; attempt <= MaxMoveAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                File.Move(sourcePath, finalDest);
                if (_hashCache is not null)
                {
                    await _hashCache.MoveAsync(
                        Path.GetFullPath(sourcePath),
                        Path.GetFullPath(finalDest),
                        ct).ConfigureAwait(false);
                }
                _logger.LogInformation("Moved {Source} → {Destination}", sourcePath, finalDest);
                return true;
            }
            catch (IOException ex) when (attempt < MaxMoveAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Move attempt {Attempt}/{Max} failed for {Source}; retrying in {Delay}ms.",
                    attempt, MaxMoveAttempts, sourcePath, (int)delay.TotalMilliseconds);

                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay *= 2; // exponential back-off
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to move {Source} → {Destination}.", sourcePath, finalDest);
                return false;
            }
        }

        return false;
    }

    // -------------------------------------------------------------------------
    // Template resolution engine
    // -------------------------------------------------------------------------

    /// <summary>
    /// Three-pass conditional template resolution:
    /// 1. Resolve conditional groups: <c> ({Token})</c> — collapse entirely when token is empty.
    /// 2. Resolve remaining bare <c>{Token}</c> references via standard replacement.
    /// 3. Cleanup: collapse multiple spaces, trim each path segment.
    /// </summary>
    private static string ResolveTemplate(string template, Dictionary<string, string> tokens)
    {
        // Pass -1 — Optional tokens: `{Token?}`. Resolves the token directly to
        // its value (empty when missing) so empty path segments can be dropped
        // in the cleanup pass. We sidestep the unresolved-token regex by
        // resolving these BEFORE pass 2 fires.
        string template1 = OptionalTokenRegex.Replace(template, match =>
        {
            string tokenName = match.Groups[1].Value;
            if (tokens.TryGetValue(tokenName, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                string sanitized = Sanitize(value);
                return string.IsNullOrWhiteSpace(sanitized) ? string.Empty : sanitized;
            }
            return string.Empty;
        });

        // Pass 0 — Bridge-ID literal-brace groups: ` {{imdb-{ImdbId}}}`.
        // Resolved to ` {imdb-VALUE}` (using sentinel braces) when the inner
        // token is non-empty, or collapsed entirely when empty.
        string resolved = BridgeIdGroupRegex.Replace(template1, match =>
        {
            string prefix = match.Groups[1].Value;
            string tokenName = match.Groups[2].Value;
            bool hasLeadingSpace = match.Value.Length > 0 && char.IsWhiteSpace(match.Value[0]);

            if (tokens.TryGetValue(tokenName, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                string sanitized = Sanitize(value);
                if (!string.IsNullOrWhiteSpace(sanitized) && sanitized != "Unknown")
                {
                    string body = $"{OpenBraceSentinel}{prefix}{sanitized}{CloseBraceSentinel}";
                    return hasLeadingSpace ? " " + body : body;
                }
            }

            return string.Empty;
        });

        // Pass 1 — Conditional groups: ` ({Token})` or `({Token})`
        // If the token value is empty/whitespace, remove the entire group (incl. leading space).
        // If the token has content, replace with ` (Value)` preserving the leading space.
        resolved = ConditionalGroupRegex.Replace(resolved, match =>
        {
            string tokenName = match.Groups[1].Value;
            bool hasLeadingSpace = match.Value.Length > 0 && char.IsWhiteSpace(match.Value[0]);

            if (tokens.TryGetValue(tokenName, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                string sanitized = Sanitize(value);
                if (!string.IsNullOrWhiteSpace(sanitized) && sanitized != "Unknown")
                {
                    return hasLeadingSpace ? $" ({sanitized})" : $"({sanitized})";
                }
            }

            // Token is empty/missing — collapse the entire group.
            return string.Empty;
        });

        // Pass 2 — Standard token replacement for remaining {Token} references.
        // For bare (non-conditional) tokens, empty values become "Unknown".
        foreach (var (key, value) in tokens)
        {
            string sanitized = Sanitize(value);
            string replacement = string.IsNullOrEmpty(sanitized) ? "Unknown" : sanitized;
            resolved = resolved.Replace($"{{{key}}}", replacement, StringComparison.OrdinalIgnoreCase);
        }

        // Replace any still-unresolved tokens with "Unknown".
        resolved = UnresolvedTokenRegex.Replace(resolved, "Unknown");

        // Pass 3 — Cleanup.
        resolved = MultiSpaceRegex.Replace(resolved, " ");

        // Unescape bridge-ID sentinel braces back to literal `{` / `}`.
        resolved = resolved
            .Replace(OpenBraceSentinel, '{')
            .Replace(CloseBraceSentinel, '}');

        // Trim each path segment individually, then drop any segment that
        // collapsed to empty (left behind by an optional `{Token?}` that had
        // no value). The final filename segment is preserved even if empty so
        // a malformed template fails loudly rather than silently producing a
        // directory path.
        var segments = resolved.Split('/');
        var kept = new List<string>(segments.Length);
        for (int i = 0; i < segments.Length; i++)
        {
            string trimmed = segments[i].Trim();
            bool isLast = i == segments.Length - 1;
            if (trimmed.Length == 0 && !isLast)
            {
                continue;
            }

            kept.Add(trimmed);
        }
        resolved = string.Join('/', kept);

        return resolved;
    }

    // -------------------------------------------------------------------------
    // Token resolution
    // -------------------------------------------------------------------------

    private static Dictionary<string, string> BuildTokens(
        IngestionCandidate candidate,
        IReadOnlyDictionary<string, string>? extra)
    {
        // candidate.Metadata is a flat KV bag populated by the processor and scorer.
        // We try known claim keys; fall back to "Unknown" for absent keys.
        var meta = candidate.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var ext = Path.GetExtension(candidate.Path); // includes the dot: ".epub"

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Title"] = meta.GetValueOrDefault(MetadataFieldConstants.Title, "Unknown"),
            ["Author"] = meta.GetValueOrDefault(MetadataFieldConstants.Author, "Unknown"),
            ["Year"] = meta.GetValueOrDefault(MetadataFieldConstants.Year, string.Empty),
            ["MediaType"] = candidate.DetectedMediaType?.ToString() ?? "Unknown",
            ["Extension"] = ext.TrimStart('.'),
            ["Ext"] = ext,     // includes the dot — e.g. ".epub"
            ["Series"] = meta.GetValueOrDefault(MetadataFieldConstants.Series, "") is { Length: > 0 } sv
                              ? sv
                              : meta.GetValueOrDefault("show_name", string.Empty),
            ["Publisher"] = meta.GetValueOrDefault(MetadataFieldConstants.PublisherField, "Unknown"),
            // ── Collection-First template tokens ────────────────────────────────────────
            ["Category"] = ResolveCategoryFromMediaType(candidate.DetectedMediaType),
            ["CollectionName"] = meta.GetValueOrDefault(MetadataFieldConstants.Title, "Unknown"),
            ["Format"] = candidate.DetectedMediaType?.ToString() ?? "Unknown",
            ["Edition"] = meta.GetValueOrDefault("edition", string.Empty),
            ["Qid"] = meta.GetValueOrDefault("wikidata_qid") is { Length: > 0 } q ? q : "Q0",
            // ── Per-media-type tokens ────────────────────────────────────────────
            ["Artist"] = meta.GetValueOrDefault(MetadataFieldConstants.Artist, meta.GetValueOrDefault(MetadataFieldConstants.Author, "Unknown")),
            ["AlbumArtist"] = meta.GetValueOrDefault("album_artist", meta.GetValueOrDefault(MetadataFieldConstants.Artist, meta.GetValueOrDefault(MetadataFieldConstants.Author, "Unknown"))),
            ["Album"] = meta.GetValueOrDefault(MetadataFieldConstants.Album, "Unknown"),
            ["BookTitle"] = meta.GetValueOrDefault("book_title", meta.GetValueOrDefault(MetadataFieldConstants.Album, meta.GetValueOrDefault(MetadataFieldConstants.Title, "Unknown"))),
            ["TrackTitle"] = meta.GetValueOrDefault(MetadataFieldConstants.Title, "Unknown"),
            ["TrackNumber"] = PadNumeric(meta.GetValueOrDefault(MetadataFieldConstants.TrackNumber, string.Empty) is { Length: > 0 } track
                                  ? track
                                  : meta.GetValueOrDefault("audiobook_part_number", string.Empty)),
            ["Season"] = PadNumeric(meta.GetValueOrDefault("season", "") is { Length: > 0 } sn ? sn : meta.GetValueOrDefault("season_number", string.Empty)),
            ["Episode"] = ResolveEpisodeToken(meta),
            // ── TV episode title (Plex/Jellyfin filename convention) ─────────────
            ["EpisodeTitle"] = meta.GetValueOrDefault("episode_title", "") is { Length: > 0 } et
                                  ? et
                                  : meta.GetValueOrDefault(MetadataFieldConstants.Title, string.Empty),
            // ── Multi-disc music albums ──────────────────────────────────────────
            // Optional segment — collapses cleanly when used with the conditional
            // group syntax `({Disc})` so single-disc albums don't get a stray
            // "Disc 01/" subfolder.
            ["Disc"] = meta.GetValueOrDefault("disc", "") is { Length: > 0 } d
                                  ? PadNumeric(d)
                                  : meta.GetValueOrDefault("disc_number", string.Empty) is { Length: > 0 } dn
                                      ? PadNumeric(dn)
                                      : string.Empty,
            // ── Comics ───────────────────────────────────────────────────────────
            ["IssueNumber"] = PadNumeric(meta.GetValueOrDefault("issue_number", string.Empty)),
            // ── External bridge IDs (Plex/Jellyfin folder name convention) ───────
            // Used by templates like `{Title} ({Year}) {{imdb-{ImdbId}}}` so files
            // organized by Tuvima drop straight into Plex / Jellyfin without further
            // intervention. Bridge IDs are populated into the metadata bag by the
            // ConfigDrivenAdapter / hydration pipeline using the BridgeIdKeys names.
            ["ImdbId"] = meta.GetValueOrDefault(BridgeIdKeys.ImdbId, string.Empty),
            ["TmdbId"] = meta.GetValueOrDefault(BridgeIdKeys.TmdbId, string.Empty),
            ["TvdbId"] = meta.GetValueOrDefault(BridgeIdKeys.TvdbId, string.Empty),
            // ── Content hash token for collision avoidance ──────────────────────
            ["Hash6"] = meta.TryGetValue("content_hash", out var hash) && hash.Length >= 6
                                  ? hash[..6]
                                  : string.Empty,
        };

        // Merge caller-supplied extras (allow overriding built-ins).
        if (extra is not null)
        {
            foreach (var (k, v) in extra)
            {
                tokens[k] = v;
            }
        }

        return tokens;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Removes characters that are illegal in a path segment.
    /// Collapses multiple spaces and trims the result.
    /// Returns empty string for empty/whitespace input (instead of "Unknown")
    /// so that conditional template groups can detect empty values.
    /// </summary>
    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (Array.IndexOf(InvalidPathChars, c) >= 0)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }

        // Collapse sequences of spaces and trim.
        string result = MultiSpaceRegex.Replace(sb.ToString(), " ").Trim();
        return result;
    }

    /// <summary>
    /// Maps a <see cref="MediaType"/> to a broad human-readable category
    /// used as the top-level directory in the Collection-first organisation template.
    /// </summary>
    private static string ResolveCategoryFromMediaType(MediaType? mt) => mt switch
    {
        MediaType.Books => "Books",
        MediaType.Comics => "Comics",
        MediaType.Movies => "Movies",
        MediaType.TV => "TV",
        MediaType.Audiobooks => "Audiobooks",
        MediaType.Music => "Music",
        _ => "Other",  // Unknown, null — caught by upstream guard
    };

    /// <summary>
    /// Builds the <c>{Episode}</c> token. A file that covers several episodes
    /// (<c>episode_end</c> set, unbroken and within <see cref="EpisodeRangeParser.MaxEpisodesPerFile"/>)
    /// reads as <c>01-e02</c>, so a template such as <c>s{Season}e{Episode}</c> names it
    /// <c>s01e01-e02</c>. Single-episode files are unchanged.
    /// </summary>
    private static string ResolveEpisodeToken(IReadOnlyDictionary<string, string> meta)
    {
        string episode = PadNumeric(meta.GetValueOrDefault("episode", "") is { Length: > 0 } ep
            ? ep
            : meta.GetValueOrDefault("episode_number", string.Empty));

        if (!int.TryParse(episode, out int first)
            || !int.TryParse(meta.GetValueOrDefault(MetadataFieldConstants.EpisodeEnd, string.Empty), out int last)
            || last <= first
            || last - first + 1 > EpisodeRangeParser.MaxEpisodesPerFile)
        {
            return episode;
        }

        return $"{episode}-e{last:D2}";
    }

    /// <summary>
    /// Pads a numeric string to at least 2 digits (e.g. "3" → "03", "12" → "12").
    /// Returns the original value unchanged if it is empty or non-numeric.
    /// </summary>
    private static string PadNumeric(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return int.TryParse(value.Trim(), out int n) ? n.ToString("D2") : value.Trim();
    }

    /// <summary>
    /// If <paramref name="path"/> does not exist, returns it unchanged.
    /// Otherwise appends " (2)", " (3)", … until a free path is found.
    /// </summary>
    private static string ResolveCollision(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string dir = Path.GetDirectoryName(path) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        for (int i = 2; i < 10_000; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Extremely unlikely: fall back to a GUID suffix.
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }
}
