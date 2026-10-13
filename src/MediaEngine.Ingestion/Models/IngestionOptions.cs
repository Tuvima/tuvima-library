using System.IO;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Ingestion.Models;

/// <summary>
/// Represents a single entry from <c>config/libraries.json</c>.
/// Populated during startup PostConfigure and made available to the
/// ingestion pipeline so media type disambiguation can use the
/// library folder's configured media types as a strong prior.
/// </summary>
public sealed class LibraryFolderEntry
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>Write tags into this library's media files (managed folders only; see the mutation policy).</summary>
    public bool WriteTags { get; init; } = true;
    public string OrganizationMode { get; init; } = LibraryOrganizationModes.TuvimaStandard;
    public string? CustomOrganizationTemplate { get; init; }

    public string Kind { get; init; } = LibraryKinds.Catalogued;

    public string MetadataPolicy { get; init; } = LibraryMetadataPolicies.Enriched;

    public string Area { get; init; } = LibraryAreas.Read;

    public string Presentation { get; init; } = LibraryPresentations.Catalogue;

    public string? PrimaryDestinationSourceId { get; init; }

    /// <summary>
    /// All source paths belonging to this logical library. A single library can
    /// span multiple drives (e.g. <c>D:\Movies</c> and <c>E:\Movies</c> as one
    /// Movies library), the same way Plex and Jellyfin already allow. Files always
    /// reorganise in place within whichever source path they already live in —
    /// Tuvima never moves files across source paths during organise.
    /// Spec: side-by-side-with-Plex plan §F.
    /// </summary>
    public IReadOnlyList<LibrarySourceEntry> Sources { get; init; } = [];

    /// <summary>
    /// The effective list of source paths.
    /// </summary>
    public IReadOnlyList<string> EffectiveSourcePaths =>
        Sources
            .Select(source => source.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The destination folder. An as-is folder is never returned, even if the id points at one.</summary>
    public LibrarySourceEntry? PrimaryDestination =>
        string.IsNullOrWhiteSpace(PrimaryDestinationSourceId)
            ? null
            : Sources.FirstOrDefault(source =>
                source.IsManaged
                && string.Equals(source.Id, PrimaryDestinationSourceId, StringComparison.OrdinalIgnoreCase));

    public string LibraryRoot => PrimaryDestination?.Path ?? string.Empty;

    /// <summary>
    /// Media types configured for this library folder (e.g. Epub, Audiobook).
    /// Parsed from the JSON <c>media_types</c> string array at startup.
    /// </summary>
    public IReadOnlyList<MediaType> MediaTypes { get; init; } = [];

    /// <summary>Configured intake surfaces this library accepts.</summary>
    public IReadOnlyList<string> AcceptedIntakeModes { get; init; } = [];

    /// <summary>
    /// Hard read-only gate. When <see langword="true"/>, the ingestion pipeline
    /// will never move, rename, or tag files that belong to this library — they
    /// are indexed in place. The escape hatch for users who want Tuvima to
    /// mirror an external library (e.g. a Plex tree) without ever touching it.
    /// Spec: side-by-side-with-Plex plan §I.
    /// </summary>
    public bool ReadOnly => Sources.Count == 0 || Sources.All(source => !source.AllowsFileMutation);

    public bool BypassesExternalIdentity =>
        LibraryMetadataPolicies.BypassesExternalIdentity(MetadataPolicy);
}

/// <summary>Runtime projection of one stable configured library source.</summary>
public sealed class LibrarySourceEntry
{
    public string Id { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string ManagementMode { get; init; } = LibrarySourceManagementModes.ExistingLibrary;

    public string AccessMode { get; init; } = LibrarySourceAccessModes.ReadOnly;

    public bool IncludeSubdirectories { get; init; } = true;

    public string ConversionState { get; init; } = LibrarySourceConversionStates.None;

    public bool IsManaged => ManagementMode == LibrarySourceManagementModes.ManagedByTuvima;

    public bool IsWritable => AccessMode == LibrarySourceAccessModes.Writable;

    public bool AllowsFileMutation => IsManaged && IsWritable;
}

/// <summary>Longest-prefix match of a file path to its logical library and stable source.</summary>
public sealed record ResolvedLibrarySource(
    LibraryFolderEntry Library,
    LibrarySourceEntry Source);

/// <summary>
/// Runtime options that control the ingestion pipeline behaviour.
/// Bind from <c>appsettings.json</c> (section <c>"Ingestion"</c>) via
/// <c>services.Configure&lt;IngestionOptions&gt;(config.GetSection("Ingestion"))</c>.
///
/// Spec: Phase 7 – Configuration § Ingestion Settings.
/// </summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>
    /// Directories monitored for new files, sourced from <c>config/libraries.json</c>.
    /// </summary>
    public IReadOnlyList<string> WatchDirectories { get; set; } = [];

    /// <summary>
    /// Normalized watch directory list. Legacy single-folder configuration is not
    /// used as a fallback.
    /// </summary>
    public IReadOnlyList<string> EffectiveWatchDirectories =>
        WatchDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Root of the organised library into which accepted files are moved.
    /// Required when <see cref="AutoOrganize"/> is <see langword="true"/>.
    /// </summary>
    public string LibraryRoot { get; set; } = string.Empty;

    /// <summary>
    /// Default tokenized path template applied by <see cref="Contracts.IFileOrganizer"/>
    /// when no media-type-specific template matches in <see cref="OrganizationTemplates"/>.
    /// Supports conditional groups: <c>({Token})</c> — when the token value is empty,
    /// the entire group (parentheses + leading space) is collapsed.
    /// </summary>
    public string OrganizationTemplate { get; set; } =
        "{Category}/{Title} ({Year})/{Title}{Ext}";

    /// <summary>
    /// Per-media-type organisation templates.  Keys are media type names
    /// (e.g. "Books", "Audiobooks", "Movies", "TV", "Comic", "Music")
    /// or "default".  Values are tokenised path templates.
    /// Fallback chain: media-type-specific → "default" → <see cref="OrganizationTemplate"/>
    /// → hardcoded <c>{Category}/{Title}/{Title}{Ext}</c>.
    /// </summary>
    // ──────────────────────────────────────────────────────────────────
    // Default templates: Plex / Jellyfin / Audiobookshelf compatible.
    // Side-by-side-with-Plex plan §A. Bridge-ID groups (`{{imdb-{ImdbId}}}`)
    // collapse cleanly when the ID is missing because the inner token resolves
    // to empty and the surrounding literal `{...}` becomes `{}` which the
    // outer cleanup pass strips. The legacy `{Qid}` token stays available for
    // power users but is no longer in the defaults.
    // ──────────────────────────────────────────────────────────────────
    public Dictionary<string, string> OrganizationTemplates { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = "{Category}/{Title} ({Year})/{Title}{Ext}",
        // Movies: Plex convention — `Title (Year)/Title (Year)`
        ["Movies"] = "Movies/{Title} ({Year})/{Title} ({Year}){Ext}",
        // TV: Plex convention — `Show (Year)/Season XX/Show - sXXeYY - Title`
        ["TV"] = "TV/{Series} ({Year})/Season {Season}/{Series} - s{Season}e{Episode} - {EpisodeTitle}{Ext}",
        // Music: Picard / Plex convention — `Artist/Album (Year)/[Disc]## - Title`
        // {Disc?} optional segment expands to e.g. "Disc 02/" for multi-disc
        // releases and collapses entirely for single-disc albums.
        ["Music"] = "Music/{Artist}/{Album} ({Year})/{TrackNumber} - {Title}{Ext}",
        // Audiobooks: Author/Title (Year)/Title — matches Music pattern
        ["Audiobooks"] = "Audiobooks/{Author}/{Title} ({Year})/{Title}{Ext}",
        // Books: Author/Title (Year)/Title (Year) — matches Music pattern
        ["Books"] = "Books/{Author}/{Title} ({Year})/{Title} ({Year}){Ext}",

        // Comics: Komga / Mylar / Kavita convention — `Series/Series - NNN (Year)`
        ["Comic"] = "Comics/{Series}/{Series} - {IssueNumber} ({Year}){Ext}",
    };

    /// <summary>
    /// Path to the staging directory: {LibraryRoot}/.data/staging/.
    /// All ingested files land here first, awaiting hydration and promotion
    /// to the organised library. Files that cannot be identified remain here
    /// for manual review.
    /// Derived from LibraryRoot — not independently configurable.
    /// </summary>
    public string StagingPath => string.IsNullOrWhiteSpace(LibraryRoot)
        ? string.Empty
        : Path.Combine(LibraryRoot, ".data", "staging");

    /// <summary>
    /// When <see langword="true"/> the engine automatically moves accepted files
    /// to <see cref="LibraryRoot"/> using <see cref="OrganizationTemplate"/>.
    /// Default: <see langword="false"/> (safe mode — monitor only).
    /// </summary>
    public bool AutoOrganize { get; set; }

    /// <summary>
    /// When <see langword="true"/> the engine calls <see cref="Contracts.IMetadataTagger"/>
    /// to embed resolved metadata back into supported file formats.
    /// Default: <see langword="false"/>.
    /// </summary>
    public bool WriteBack { get; set; }

    /// <summary>
    /// What to do with an exact copy of a file already held, for every library alike. Comes from
    /// <c>file_handling.duplicate_policy</c> in <c>libraries.json</c>.
    /// </summary>
    public string DuplicatePolicy { get; set; } = LibraryDuplicatePolicies.KeepOne;

    /// <summary>
    /// Whether to also watch sub-directories of configured source directories.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool IncludeSubdirectories { get; set; } = true;

    /// <summary>
    /// Maximum accepted size for a single Dashboard upload. The default is
    /// finite but large enough for local video files. Override with
    /// <c>Ingestion:MaxUploadSizeBytes</c> / <c>Ingestion__MaxUploadSizeBytes</c>.
    /// </summary>
    public long MaxUploadSizeBytes { get; set; } = 25L * 1024 * 1024 * 1024;

    /// <summary>
    /// Required free-space buffer left on the destination drive after upload.
    /// Override with <c>Ingestion:UploadFreeSpaceBufferBytes</c>.
    /// </summary>
    public long UploadFreeSpaceBufferBytes { get; set; } = 512L * 1024 * 1024;

    // ── Polling Fallback ────────────────────────────────────────────

    /// <summary>
    /// Interval in seconds between polling sweeps of the Watch Folder.
    /// Acts as a safety net when <see cref="System.IO.FileSystemWatcher"/>
    /// misses OS events. Set to 0 to disable polling. Default: 300.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Seconds to wait after the last FileSystemWatcher/poll event before
    /// flushing the collected file batch into the debounce queue. Default: 30.
    /// </summary>
    public int FswQuietPeriodSeconds { get; set; } = 30;

    public TimeSpan FswQuietPeriod =>
        TimeSpan.FromSeconds(Math.Max(1, FswQuietPeriodSeconds));

    /// <summary>
    /// Maximum delayed retries after the debounce lock probe exhausts its own
    /// attempts. After this cap, the operation remains interrupted for a later
    /// scan/manual retry instead of being marked as completed with no result.
    /// </summary>
    public int LockProbeRetryMaxAttempts { get; set; } = 3;

    /// <summary>
    /// Base delay in seconds for delayed lock-probe recovery attempts.
    /// Exponential backoff is applied and capped at five minutes.
    /// </summary>
    public int LockProbeRetryBaseDelaySeconds { get; set; } = 30;

    // ── Language configuration ────────────────────────────────────────

    /// <summary>
    /// The configured library language (ISO 639-1 code, e.g. "en", "fr", "de").
    /// Populated from <c>CoreConfiguration.Language</c> by the PostConfigure hook.
    /// Files whose embedded language tag does not match this value are routed
    /// to the review queue with trigger <c>ReviewTrigger.LanguageMismatch</c>.
    /// Default: "en".
    /// </summary>
    public string ConfiguredLanguage { get; set; } = "en";

    // ── Media Type Disambiguation ────────────────────────────────────

    /// <summary>
    /// Minimum confidence for auto-assigning a media type without review.
    /// Populated from <c>config/disambiguation.json</c> at startup.
    /// Default: 0.70.
    /// </summary>
    public double MediaTypeAutoAssignThreshold { get; set; } = 0.70;

    /// <summary>
    /// Minimum confidence for creating a review queue entry (provisional assignment).
    /// Below this threshold, the file is assigned <c>MediaType.Unknown</c>.
    /// Populated from <c>config/disambiguation.json</c> at startup.
    /// Default: 0.40.
    /// </summary>
    public double MediaTypeReviewThreshold { get; set; } = 0.40;

    // ── Library Folder Priors ─────────────────────────────────────────

    /// <summary>
    /// Library folder entries loaded from <c>config/libraries.json</c>.
    /// Each entry maps a source path to its configured media types so that
    /// the ingestion pipeline can apply a strong media type prior when a
    /// file arrives from a folder whose content category is known.
    /// Populated by the PostConfigure hook in Program.cs at startup.
    /// </summary>
    public IReadOnlyList<LibraryFolderEntry> LibraryFolders { get; set; } = [];

    // ── Template Resolution ────────────────────────────────────────────

    private const string HardcodedFallback = "{Category}/{Title}/{Title}{Ext}";

    /// <summary>
    /// Resolves the organisation template for a given media type.
    /// Fallback chain: media-type-specific → "default" key → <see cref="OrganizationTemplate"/>
    /// → hardcoded <c>{Category}/{Title}/{Title}{Ext}</c>.
    /// </summary>
    public string ResolveTemplate(string? mediaTypeName)
    {
        // 1. Try media-type-specific template.
        if (!string.IsNullOrWhiteSpace(mediaTypeName)
            && OrganizationTemplates.TryGetValue(mediaTypeName, out var specific)
            && !string.IsNullOrWhiteSpace(specific))
        {
            return specific;
        }

        // 2. Try "default" key in templates dictionary.
        if (OrganizationTemplates.TryGetValue("default", out var def)
            && !string.IsNullOrWhiteSpace(def))
        {
            return def;
        }

        // 3. Fall back to OrganizationTemplate property.
        if (!string.IsNullOrWhiteSpace(OrganizationTemplate))
        {
            return OrganizationTemplate;
        }

        // 4. Hardcoded fallback.
        return HardcodedFallback;
    }

    private static string NormalizeComparablePath(string path) =>
        Path.GetFullPath(path)
            .Replace('\\', '/')
            .TrimEnd('/');

    private static bool IsUnderRoot(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
}
