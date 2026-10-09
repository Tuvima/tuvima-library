using MediaEngine.Api.Services;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.DevSupport;

public enum DevHarnessWipeScope
{
    GeneratedState,
    Full,
}

public sealed record DevHarnessResetResult(
    DevHarnessWipeScope Scope,
    IReadOnlyList<string> Details);

/// <summary>
/// Central reset service for development ingestion harness endpoints.
/// The default generated-state scope preserves unrelated watch-folder source files.
/// </summary>
public sealed class DevHarnessResetService
{
    public const string GeneratedStateScopeName = "generated-state";
    public const string FullScopeName = "full";

    private readonly IDatabaseConnection _db;
    private readonly IOptions<IngestionOptions> _options;
    private readonly IConfigurationLoader _configLoader;
    private readonly IIngestionEngine _ingestionEngine;
    private readonly EnrichmentPipelineExecutionGate _enrichmentPipelineGate;
    private readonly ILogger<DevHarnessResetService> _logger;

    private static readonly HashSet<string> PreservedConfigurationTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "storage_metadata",
        "schema_migrations",
        "profiles",
        "households",
        "accounts",
        "account_profile_grants",
        "account_invitations",
        "account_feature_grants",
        "account_library_grants",
        "grant_admin_protections",
        "account_external_logins",
        "account_credentials",
        "profile_credentials",
        "auth_sessions",
        "grant_admin_unlocks",
        "applications",
        "application_credentials",
        "application_permission_grants",
        "application_client_bindings",
        "account_passkeys",
        "password_reset_challenges",
        "client_devices",
        "device_pairing_requests",
        "client_tokens",
        "password_recovery_codes",
        "service_credentials",
        "identity_audit_events",
        "authorization_audit_events",
        "profile_view_policies",
        "profile_view_preferences",
        "profile_sequence_preferences",
        "metadata_providers",
        "provider_config",
        "ui_settings_cache",
        "user_playback_settings",
        "application_events",
        "application_webhooks",
        "application_webhook_deliveries",
    };

    public DevHarnessResetService(
        IDatabaseConnection db,
        IOptions<IngestionOptions> options,
        IConfigurationLoader configLoader,
        IIngestionEngine ingestionEngine,
        EnrichmentPipelineExecutionGate enrichmentPipelineGate,
        ILogger<DevHarnessResetService> logger)
    {
        _db = db;
        _options = options;
        _configLoader = configLoader;
        _ingestionEngine = ingestionEngine;
        _enrichmentPipelineGate = enrichmentPipelineGate;
        _logger = logger;
    }

    public static DevHarnessWipeScope ParseScope(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DevHarnessWipeScope.GeneratedState;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            GeneratedStateScopeName or "generated" or "state" => DevHarnessWipeScope.GeneratedState,
            FullScopeName or "all" or "dangerous-full" => DevHarnessWipeScope.Full,
            _ => throw new ArgumentException(
                $"Unknown wipe scope '{value}'. Use '{GeneratedStateScopeName}' or '{FullScopeName}'.",
                nameof(value)),
        };
    }

    public async Task<DevHarnessResetResult> WipeAsync(
        DevHarnessWipeScope scope,
        bool resumeWatcher,
        CancellationToken ct = default)
    {
        var details = new List<string>();

        await PauseWatcherAsync(details, ct).ConfigureAwait(false);

        try
        {
            await PauseEnrichmentPipelineAsync(details, ct).ConfigureAwait(false);
            EnsureDestructivePathSafety(details);
            WipeGeneratedLibraryState(details);

            if (scope == DevHarnessWipeScope.Full)
            {
                WipeAllSourcePaths(details);
            }
            else
            {
                WipeTrackedSeedFiles(details);
            }

            EnsureConfiguredSourcePathsExist(details);
            if (scope == DevHarnessWipeScope.Full)
            {
                await ResetDatabaseAsync(details, ct).ConfigureAwait(false);
            }
            else
            {
                await ResetLibraryDatabaseAsync(details, ct).ConfigureAwait(false);
            }
            WipeRuntimeLogs(details);
        }
        finally
        {
            ResumeEnrichmentPipeline(details);

            if (resumeWatcher)
            {
                await ResumeWatcherAsync(details, ct).ConfigureAwait(false);
            }
            else
            {
                details.Add("Ingestion engine: FSW resume deferred");
            }
        }

        return new DevHarnessResetResult(scope, details);
    }

    public async Task<DevHarnessResetResult> PrepareForReingestAsync(CancellationToken ct = default)
    {
        var details = new List<string>();

        await PauseWatcherAsync(details, ct).ConfigureAwait(false);

        try
        {
            await PauseEnrichmentPipelineAsync(details, ct).ConfigureAwait(false);
            WipeGeneratedCachesOnly(details);
            await ResetLibraryDatabaseAsync(details, ct).ConfigureAwait(false);
            WipeRuntimeLogs(details);
            details.Add("Ingestion engine: FSW resume deferred");
        }
        finally
        {
            ResumeEnrichmentPipeline(details);
        }

        return new DevHarnessResetResult(DevHarnessWipeScope.GeneratedState, details);
    }

    public Task<DevHarnessResetResult> ResetLibraryDataAsync(
        bool resumeWatcher,
        CancellationToken ct = default) =>
        WipeAsync(DevHarnessWipeScope.GeneratedState, resumeWatcher, ct);

    public async Task<DevHarnessResetResult> FactoryResetAsync(
        bool resumeWatcher,
        CancellationToken ct = default)
    {
        var details = new List<string>();

        await PauseWatcherAsync(details, ct).ConfigureAwait(false);
        try
        {
            await PauseEnrichmentPipelineAsync(details, ct).ConfigureAwait(false);
            EnsureDestructivePathSafety(details);
            WipeGeneratedLibraryState(details);
            WipeTrackedSeedFiles(details);
            EnsureConfiguredSourcePathsExist(details);
            await ResetDatabaseAsync(details, ct).ConfigureAwait(false);
            WipeRuntimeLogs(details);
        }
        finally
        {
            ResumeEnrichmentPipeline(details);
            if (resumeWatcher)
            {
                await ResumeWatcherAsync(details, ct).ConfigureAwait(false);
            }
            else
            {
                details.Add("Ingestion engine: FSW resume deferred");
            }
        }

        details.Add("Configured source media was preserved");
        return new DevHarnessResetResult(DevHarnessWipeScope.Full, details);
    }

    public async Task PauseWatcherAsync(List<string>? details = null, CancellationToken ct = default)
    {
        try
        {
            await _ingestionEngine.PauseWatcherAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("[HarnessReset] Ingestion engine FSW paused");
            details?.Add("Ingestion engine: FSW paused");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HarnessReset] Failed to pause ingestion engine");
            details?.Add($"Ingestion engine pause: FAILED - {ex.Message}");
        }
    }

    public async Task ResumeWatcherAsync(List<string>? details = null, CancellationToken ct = default)
    {
        try
        {
            EnsureConfiguredSourcePathsExist(details ?? []);
            await _ingestionEngine.ResumeWatcherAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("[HarnessReset] Ingestion engine FSW resumed");
            details?.Add("Ingestion engine: FSW resumed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HarnessReset] Failed to resume ingestion engine");
            details?.Add($"Ingestion engine resume: FAILED - {ex.Message}");
        }
    }

    private async Task PauseEnrichmentPipelineAsync(List<string> details, CancellationToken ct)
    {
        await _enrichmentPipelineGate.PauseAndDrainAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("[HarnessReset] Enrichment pipeline paused and drained");
        details.Add("Enrichment pipeline: paused and active workers drained");
    }

    private void ResumeEnrichmentPipeline(List<string> details)
    {
        _enrichmentPipelineGate.Resume();
        _logger.LogInformation("[HarnessReset] Enrichment pipeline resumed");
        details.Add("Enrichment pipeline: resumed");
    }

    private void WipeGeneratedLibraryState(List<string> details)
    {
        string? libraryRoot = _options.Value.LibraryRoot;
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            details.Add("Library root: not configured - skipped");
            return;
        }

        var assetPathService = new AssetPathService(libraryRoot);
        WipePathWithReport(details, "Central artwork and renditions", assetPathService.AssetsRoot);

        if (Directory.Exists(libraryRoot))
        {
            try
            {
                int count = WipeDirectoryContentsExcept(libraryRoot, ".data");
                details.Add($"Library output ({libraryRoot}): {count} items deleted");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HarnessReset] Failed to wipe library output");
                details.Add($"Library output ({libraryRoot}): FAILED - {ex.Message}");
            }
        }
        else
        {
            details.Add($"Library output ({libraryRoot}): directory does not exist - skipped");
        }
    }

    private void WipeGeneratedCachesOnly(List<string> details)
    {
        string? libraryRoot = _options.Value.LibraryRoot;
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            details.Add("Generated caches: library root not configured - skipped");
            return;
        }

        var assetPathService = new AssetPathService(libraryRoot);
        WipePathWithReport(details, "Central artwork and renditions", assetPathService.AssetsRoot);

        var generatedDirs = new[]
        {
            Path.Combine(libraryRoot, ".data", "sidecars-cache"),
            Path.Combine(libraryRoot, ".data", "staging"),
            Path.Combine(libraryRoot, ".data", "generated"),
        };

        foreach (var dir in generatedDirs)
        {
            WipePathWithReport(details, "Generated cache", dir);
        }
    }

    private void EnsureDestructivePathSafety(List<string> details)
    {
        string? libraryRoot = _options.Value.LibraryRoot;
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return;
        }

        var normalizedLibraryRoot = NormalizePathOrNull(libraryRoot);
        if (normalizedLibraryRoot is null)
        {
            return;
        }

        foreach (var sourcePath in EnumerateConfiguredSourcePaths())
        {
            var normalizedSource = NormalizePathOrNull(sourcePath);
            if (normalizedSource is null)
            {
                continue;
            }

            if (PathsOverlap(normalizedLibraryRoot, normalizedSource))
            {
                var message =
                    $"Refusing destructive dev wipe because library output '{normalizedLibraryRoot}' overlaps source folder '{normalizedSource}'.";
                details.Add(message);
                throw new InvalidOperationException(message);
            }
        }
    }

    private void WipeTrackedSeedFiles(List<string> details)
    {
        var sourceRoots = EnumerateConfiguredSourcePaths()
            .Select(NormalizePathOrNull)
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var seedFiles = DevFixtureManifestStore.Read(_options, _logger);
        int deleted = 0;
        int missing = 0;
        int rejected = 0;
        var retainedPaths = new List<string>();

        foreach (string path in seedFiles)
        {
            var normalizedPath = NormalizePathOrNull(path);
            if (normalizedPath is null
                || !sourceRoots.Any(root =>
                    !string.Equals(root, normalizedPath, StringComparison.OrdinalIgnoreCase)
                    && IsSameOrChild(root, normalizedPath)))
            {
                rejected++;
                _logger.LogWarning(
                    "[HarnessReset] Refused to delete untrusted fixture manifest path {Path}",
                    path);
                retainedPaths.Add(path);
                continue;
            }

            if (!File.Exists(normalizedPath))
            {
                missing++;
                continue;
            }

            try
            {
                File.SetAttributes(normalizedPath, FileAttributes.Normal);
                File.Delete(normalizedPath);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[HarnessReset] Failed to delete seed fixture {Path}", normalizedPath);
                details.Add($"Seed fixture ({normalizedPath}): FAILED - {ex.Message}");
                retainedPaths.Add(normalizedPath);
            }
        }

        DevFixtureManifestStore.Replace(_options, retainedPaths, _logger);
        details.Add(
            $"Seed fixtures: {deleted} tracked fixture file(s) deleted, {missing} absent, {rejected} untrusted path(s) rejected");
    }

    private void WipeAllSourcePaths(List<string> details)
    {
        foreach (string srcPath in EnumerateConfiguredSourcePaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(srcPath))
            {
                continue;
            }

            try
            {
                int count = WipeDirectoryContents(srcPath);
                details.Add($"Library source ({srcPath}): {count} items deleted");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HarnessReset] Failed to wipe source path {Path}", srcPath);
                details.Add($"Library source ({srcPath}): FAILED - {ex.Message}");
            }
        }

    }

    private void EnsureConfiguredSourcePathsExist(List<string> details)
    {
        foreach (string srcPath in EnumerateConfiguredSourcePaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(srcPath);
                details.Add($"Library source ({srcPath}): directory ready");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HarnessReset] Failed to create source path {Path}", srcPath);
                details.Add($"Library source ({srcPath}): create FAILED - {ex.Message}");
            }
        }
    }

    private async Task ResetDatabaseAsync(List<string> details, CancellationToken ct)
    {
        await _db.AcquireWriteLockAsync().ConfigureAwait(false);
        try
        {
            var conn = _db.Open();

            using (var fkOff = conn.CreateCommand())
            {
                fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
                fkOff.ExecuteNonQuery();
            }

            var tables = new List<string>();
            using (var listCmd = conn.CreateCommand())
            {
                listCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
                using var reader = listCmd.ExecuteReader();
                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            foreach (string table in tables)
            {
                ct.ThrowIfCancellationRequested();
                using var dropCmd = conn.CreateCommand();
                dropCmd.CommandText = $"DROP TABLE IF EXISTS [{table}];";
                dropCmd.ExecuteNonQuery();
            }

            using (var ftsCmd = conn.CreateCommand())
            {
                ftsCmd.CommandText = "DROP TABLE IF EXISTS search_index;";
                ftsCmd.ExecuteNonQuery();
            }

            using (var fkOn = conn.CreateCommand())
            {
                fkOn.CommandText = "PRAGMA foreign_keys = ON;";
                fkOn.ExecuteNonQuery();
            }

            using (var vacuumCmd = conn.CreateCommand())
            {
                vacuumCmd.CommandText = "VACUUM;";
                vacuumCmd.ExecuteNonQuery();
            }

            _db.InitializeSchema();
            _db.RunStartupChecks();

            details.Add($"Database: dropped {tables.Count} table(s) and reinitialized schema");
            _logger.LogInformation("[HarnessReset] Database reset: dropped {Count} table(s)", tables.Count);
        }
        finally
        {
            _db.ReleaseWriteLock();
        }
    }

    private async Task ResetLibraryDatabaseAsync(List<string> details, CancellationToken ct)
    {
        await _db.AcquireWriteLockAsync().ConfigureAwait(false);
        try
        {
            var conn = _db.Open();

            using (var fkOff = conn.CreateCommand())
            {
                fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
                fkOff.ExecuteNonQuery();
            }

            var tables = new List<string>();
            using (var listCmd = conn.CreateCommand())
            {
                listCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
                using var reader = listCmd.ExecuteReader();
                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            var cleared = new List<string>();
            foreach (string table in tables)
            {
                ct.ThrowIfCancellationRequested();
                if (ShouldPreserveConfigurationTable(table) || IsSearchIndexShadowTable(table))
                {
                    continue;
                }

                using var deleteCmd = conn.CreateCommand();
                deleteCmd.CommandText = $"DELETE FROM [{table}];";
                deleteCmd.ExecuteNonQuery();
                cleared.Add(table);
            }

            using (var sequenceCmd = conn.CreateCommand())
            {
                sequenceCmd.CommandText = "DELETE FROM sqlite_sequence WHERE name NOT IN (" +
                    string.Join(",", PreservedConfigurationTables.Select((_, index) => $"$table{index}")) + ");";
                var index = 0;
                foreach (var table in PreservedConfigurationTables)
                {
                    sequenceCmd.Parameters.AddWithValue($"$table{index++}", table);
                }
                sequenceCmd.ExecuteNonQuery();
            }

            using (var fkOn = conn.CreateCommand())
            {
                fkOn.CommandText = "PRAGMA foreign_keys = ON;";
                fkOn.ExecuteNonQuery();
            }

            using (var vacuumCmd = conn.CreateCommand())
            {
                vacuumCmd.CommandText = "VACUUM;";
                vacuumCmd.ExecuteNonQuery();
            }

            _db.RunStartupChecks();
            details.Add($"Database: cleared {cleared.Count} library/ingestion table(s); accounts, profiles, access, provider configuration, and UI settings preserved");
            _logger.LogInformation(
                "[HarnessReset] Library data reset cleared {Count} table(s) while preserving configuration state",
                cleared.Count);
        }
        finally
        {
            _db.ReleaseWriteLock();
        }
    }

    public static bool PreserveForRealMediaReset(string table) =>
        (PreservedConfigurationTables.Contains(table) && table != "profile_sequence_preferences")
        || table.StartsWith("onboarding_", StringComparison.OrdinalIgnoreCase)
        || table is "view_personal_spaces" or "view_storage_labels" or "view_shared_library";

    private static bool ShouldPreserveConfigurationTable(string table) =>
        PreservedConfigurationTables.Contains(table)
        || table.StartsWith("view_", StringComparison.OrdinalIgnoreCase)
        || table.StartsWith("local_", StringComparison.OrdinalIgnoreCase)
        || table.StartsWith("onboarding_", StringComparison.OrdinalIgnoreCase);

    private static bool IsSearchIndexShadowTable(string table) =>
        table.StartsWith("search_index_", StringComparison.OrdinalIgnoreCase);

    private void WipeRuntimeLogs(List<string> details)
    {
        string logsPath = Path.Combine(Directory.GetCurrentDirectory(), "logs");
        if (!Directory.Exists(logsPath))
        {
            return;
        }

        try
        {
            int count = WipeDirectoryContents(logsPath);
            details.Add($"Logs ({logsPath}): {count} items deleted");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HarnessReset] Failed to wipe logs");
            details.Add($"Logs ({logsPath}): FAILED - {ex.Message}");
        }
    }

    private void WipePathWithReport(List<string> details, string label, string path)
    {
        if (!Directory.Exists(path))
        {
            details.Add($"{label} ({path}): not found - skipped");
            return;
        }

        try
        {
            int count = WipeDirectoryContents(path);
            details.Add($"{label} ({path}): {count} items deleted");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HarnessReset] Failed to wipe {Label}", label);
            details.Add($"{label} ({path}): FAILED - {ex.Message}");
        }
    }

    private static int WipeDirectoryContents(string dirPath)
    {
        if (!Directory.Exists(dirPath))
        {
            return 0;
        }

        int count = 0;
        var dir = new DirectoryInfo(dirPath);
        foreach (FileInfo file in dir.GetFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                file.Attributes = FileAttributes.Normal;
                file.Delete();
                count++;
            }
            catch
            {
                // Best-effort cleanup for dev harness state.
            }
        }

        foreach (DirectoryInfo sub in dir.GetDirectories())
        {
            try
            {
                sub.Delete(recursive: true);
                count++;
            }
            catch
            {
                // Best-effort cleanup for dev harness state.
            }
        }

        return count;
    }

    private static int WipeDirectoryContentsExcept(string dirPath, params string[] excludedChildNames)
    {
        if (!Directory.Exists(dirPath))
        {
            return 0;
        }

        var excluded = new HashSet<string>(excludedChildNames, StringComparer.OrdinalIgnoreCase);
        int count = 0;
        var dir = new DirectoryInfo(dirPath);

        foreach (FileInfo file in dir.GetFiles())
        {
            try
            {
                file.Attributes = FileAttributes.Normal;
                file.Delete();
                count++;
            }
            catch
            {
                // Best-effort cleanup for dev harness state.
            }
        }

        foreach (DirectoryInfo sub in dir.GetDirectories())
        {
            if (excluded.Contains(sub.Name))
            {
                continue;
            }

            try
            {
                sub.Delete(recursive: true);
                count++;
            }
            catch
            {
                // Best-effort cleanup for dev harness state.
            }
        }

        return count;
    }

    private IEnumerable<string> EnumerateConfiguredSourcePaths()
    {
        var libConfig = _configLoader.LoadLibraries();
        foreach (var lib in libConfig.Libraries)
        {
            foreach (var source in lib.Sources.Where(source =>
                         source.IsManaged && !string.IsNullOrWhiteSpace(source.Path)))
            {
                yield return source.Path;
            }
        }
    }

    private static string? NormalizePathOrNull(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private static bool PathsOverlap(string first, string second)
    {
        return IsSameOrChild(first, second) || IsSameOrChild(second, first);
    }

    private static bool IsSameOrChild(string maybeParent, string maybeChild)
    {
        var parent = maybeParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var child = maybeChild.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }
}
