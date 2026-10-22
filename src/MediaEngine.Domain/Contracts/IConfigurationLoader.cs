using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Granular access to the multi-file configuration directory.
///
/// <para>
/// Each configuration concern has its own load/save pair; universe and
/// provider configs are accessed by name.
/// </para>
/// </summary>
public interface IConfigurationLoader
{
    /// <summary>
    /// Absolute path to the configuration directory. Exposed so services that
    /// need to watch individual config files (e.g. <c>WritebackConfigState</c>
    /// for <c>writeback-fields.json</c>) can attach a <see cref="System.IO.FileSystemWatcher"/>
    /// without duplicating the resolution logic.
    /// </summary>
    string ConfigDirectoryPath => string.Empty;

    /// <summary>
    /// Raised after a watched config file is reloaded or rejected. Implementations
    /// that do not support hot reload can leave this event unused.
    /// </summary>
    event EventHandler<ConfigurationFileChangedEventArgs>? ConfigurationChanged
    {
        add { }
        remove { }
    }

    /// <summary>Begin bounded file watching for safe reloadable config files.</summary>
    void StartWatching() { }

    // ── Core ─────────────────────────────────────────────────────────────────

    /// <summary>Load core settings (paths, schema version, organisation template).</summary>
    CoreConfiguration LoadCore();

    /// <summary>Persist core settings to <c>config/core.json</c>.</summary>
    void SaveCore(CoreConfiguration config);

    // ── Scoring ──────────────────────────────────────────────────────────────

    /// <summary>Load scoring thresholds and decay parameters.</summary>
    ScoringSettings LoadScoring();

    /// <summary>Persist scoring settings to <c>config/scoring.json</c>.</summary>
    void SaveScoring(ScoringSettings settings);

    // ── Maintenance ──────────────────────────────────────────────────────────

    /// <summary>Load maintenance settings (retention, vacuum, sync).</summary>
    MaintenanceSettings LoadMaintenance();

    /// <summary>Persist maintenance settings to <c>config/maintenance.json</c>.</summary>
    void SaveMaintenance(MaintenanceSettings settings);

    // ── Hydration Pipeline ──────────────────────────────────────────────

    /// <summary>Load hydration pipeline settings (stage concurrency, timeouts, thresholds).</summary>
    HydrationSettings LoadHydration();

    /// <summary>Persist hydration settings to <c>config/hydration.json</c>.</summary>
    void SaveHydration(HydrationSettings settings);

    // ── Pipeline Configuration ──────────────────────────────────────────────

    /// <summary>
    /// Load pipeline configuration from <c>config/pipelines.json</c>.
    /// </summary>
    PipelineConfiguration LoadPipelines();

    /// <summary>Persist pipeline configuration to <c>config/pipelines.json</c>.</summary>
    void SavePipelines(PipelineConfiguration config);

    // ── Disambiguation ─────────────────────────────────────────────────────

    /// <summary>Load media type disambiguation settings from <c>config/disambiguation.json</c>.</summary>
    DisambiguationSettings LoadDisambiguation();

    /// <summary>Persist disambiguation settings to <c>config/disambiguation.json</c>.</summary>
    void SaveDisambiguation(DisambiguationSettings settings);

    // ── Transcoding ──────────────────────────────────────────────────────────

    /// <summary>Load FFmpeg and transcoding settings from <c>config/transcoding.json</c>.</summary>
    TranscodingSettings LoadTranscoding();

    /// <summary>Persist transcoding settings to <c>config/transcoding.json</c>.</summary>
    void SaveTranscoding(TranscodingSettings settings);

    // ── Text recognition ─────────────────────────────────────────────────────

    /// <summary>Load text recognition (OCR) settings from <c>config/ocr.json</c>.</summary>
    OcrSettings LoadOcr() => new();

    /// <summary>Persist text recognition (OCR) settings to <c>config/ocr.json</c>.</summary>
    void SaveOcr(OcrSettings settings) { }

    // ── Tool downloads ───────────────────────────────────────────────────────

    /// <summary>
    /// Load the pinned FFmpeg and Tesseract downloads from <c>config/tool_downloads.json</c>. Entries with an address
    /// outside github.com over https, or a malformed checksum, are dropped. A missing file means the pinned defaults.
    /// </summary>
    ToolDownloadSettings LoadToolDownloads() => new();

    // ── Network & Remote Access ─────────────────────────────────────────────

    /// <summary>Load desired network settings from <c>config/network.json</c>.</summary>
    NetworkSettings LoadNetwork() => new();

    /// <summary>Persist desired network settings to <c>config/network.json</c>.</summary>
    void SaveNetwork(NetworkSettings settings) =>
        throw new NotSupportedException("This configuration loader does not support saving network settings.");

    // ── Media Types ──────────────────────────────────────────────────────────

    /// <summary>Load media type definitions from <c>config/media_types.json</c>.</summary>
    MediaTypeConfiguration LoadMediaTypes();

    /// <summary>Persist media type definitions to <c>config/media_types.json</c>.</summary>
    void SaveMediaTypes(MediaTypeConfiguration config);

    // ── Library Kinds ────────────────────────────────────────────────────────

    /// <summary>
    /// Load the list of library kinds ("recipes") from <c>config/library_kinds.json</c>. A missing file means the kinds that ship
    /// with the Library. Kinds a plugin adds are not part of this list; the Engine merges them in when it serves the list.
    /// </summary>
    LibraryKindsConfiguration LoadLibraryKinds() => new();

    // ── Library Folders ───────────────────────────────────────────────────

    /// <summary>Load library folder definitions from <c>config/libraries.json</c>.</summary>
    LibrariesConfiguration LoadLibraries();

    /// <summary>Persist library folder definitions to <c>config/libraries.json</c>.</summary>
    void SaveLibraries(LibrariesConfiguration config) =>
        throw new NotSupportedException("This configuration loader does not support saving library folder definitions.");

    // ── Field Priorities ───────────────────────────────────────────────────

    /// <summary>Load per-field provider priority overrides from <c>config/field_priorities.json</c>.</summary>
    FieldPriorityConfiguration LoadFieldPriorities();

    /// <summary>Persist field priority overrides to <c>config/field_priorities.json</c>.</summary>
    void SaveFieldPriorities(FieldPriorityConfiguration config);

    // ── Providers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Load a single provider's configuration by name.
    /// Returns <c>null</c> if the file does not exist or is corrupt.
    /// </summary>
    ProviderConfiguration? LoadProvider(string name);

    /// <summary>
    /// Persist a provider configuration to <c>config/providers/{name}.json</c>.
    /// The filename is derived from <see cref="ProviderConfiguration.Name"/>.
    /// </summary>
    void SaveProvider(ProviderConfiguration config);

    /// <summary>Remove the provider file <c>config/providers/{name}.json</c> (and its backup). Does nothing when it does not exist.</summary>
    void DeleteProvider(string name);

    /// <summary>
    /// Load all provider configuration files from <c>config/providers/</c>.
    /// Files that fail to deserialize are silently skipped.
    /// </summary>
    IReadOnlyList<ProviderConfiguration> LoadAllProviders();

    // ── AI Settings ──────────────────────────────────────────────────────────

    /// <summary>Load AI settings (models, features, scheduling) from <c>config/ai.json</c>.</summary>
    T? LoadAi<T>() where T : class;

    /// <summary>Persist AI settings to <c>config/ai.json</c>.</summary>
    void SaveAi<T>(T settings) where T : class;

    // ── UI Palette ───────────────────────────────────────────────────────────

    /// <summary>Load the centralised colour palette from <c>config/ui/palette.json</c>.</summary>
    PaletteConfiguration LoadPalette();

    /// <summary>Persist the colour palette to <c>config/ui/palette.json</c>.</summary>
    void SavePalette(PaletteConfiguration palette);

    // ── Generic (universe, etc.) ─────────────────────────────────────────────

    /// <summary>
    /// Load a typed configuration from a named subdirectory.
    /// Used for universe knowledge models and any future extensible config sections.
    /// Returns <c>null</c> if the file does not exist or is corrupt.
    /// </summary>
    /// <typeparam name="T">The configuration model type to deserialize.</typeparam>
    /// <param name="subdirectory">Subdirectory within the config root (e.g. <c>"universe"</c>).</param>
    /// <param name="name">Filename stem without extension (e.g. <c>"wikidata"</c>).</param>
    T? LoadConfig<T>(string subdirectory, string name) where T : class;

    /// <summary>
    /// Persist a typed configuration to a named subdirectory.
    /// Creates the subdirectory if it does not exist.
    /// </summary>
    void SaveConfig<T>(string subdirectory, string name, T config) where T : class;
}
