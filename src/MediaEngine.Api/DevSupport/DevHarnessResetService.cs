using System.IO.Enumeration;
using MediaEngine.Api.Services;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;
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

/// <summary>How long a development reset waits for background work before it gives up and says what is blocking it.</summary>
public sealed record DevHarnessResetTimeouts(TimeSpan EnrichmentDrain, TimeSpan WriteLock)
{
    public static DevHarnessResetTimeouts Default { get; } = new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
}

/// <summary>A development reset stopped because background work did not let go in time; nothing was deleted from the data store.</summary>
public sealed class DevHarnessResetBlockedException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

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
    private readonly DevHarnessResetTimeouts _timeouts;
    private readonly SemaphoreSlim _singleReset = new(1, 1);
    private readonly IDevelopmentFixtureLocationRepository _fixtureLocations;
    private readonly RealMediaRun? _realMediaRun;

    // How many files a reset lists by name when it reports the ones it kept, and how far it counts them.
    private const int KeptFileExamples = 5;
    private const int KeptFileCountLimit = 10_000;

    private static readonly HashSet<string> PreservedConfigurationTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "storage_metadata",
        "data_store_upgrades",
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
        ILogger<DevHarnessResetService> logger,
        DevHarnessResetTimeouts? timeouts = null,
        IDevelopmentFixtureLocationRepository? fixtureLocations = null,
        RealMediaRun? realMediaRun = null)
    {
        _db = db;
        _options = options;
        _configLoader = configLoader;
        _ingestionEngine = ingestionEngine;
        _enrichmentPipelineGate = enrichmentPipelineGate;
        _logger = logger;
        _timeouts = timeouts ?? DevHarnessResetTimeouts.Default;
        _fixtureLocations = fixtureLocations ?? new DevelopmentFixtureLocationRepository(db);
        _realMediaRun = realMediaRun;
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
        using var exclusive = BeginExclusiveReset();
        EnsureNotRealMedia();
        var details = new List<string>();
        var completed = false;

        await PauseWatcherAsync(details, ct).ConfigureAwait(false);

        try
        {
            await PauseEnrichmentPipelineAsync(details, ct).ConfigureAwait(false);
            var folders = ResolveResetFolders();
            if (scope == DevHarnessWipeScope.Full)
            {
                EnsureFullWipeIsSafe(details, folders);
            }

            using (await HoldWriteLockAsync(ct).ConfigureAwait(false))
            {
                // The data store says where each fixture was filed, so its files go before the data store is cleared.
                WipeLibraryMadeFiles(details, folders);
                WipeGeneratedCaches(details);
                if (scope == DevHarnessWipeScope.Full)
                {
                    WipeAllSourcePaths(details);
                }

                EnsureConfiguredSourcePathsExist(details);
                if (scope == DevHarnessWipeScope.Full)
                {
                    ResetDatabase(details, ct);
                }
                else
                {
                    ResetLibraryDatabase(details, ct);
                }
            }

            if (scope != DevHarnessWipeScope.Full)
            {
                ReportFilesKept(details, folders);
            }

            WipeRuntimeLogs(details);
            completed = true;
        }
        finally
        {
            ResumeEnrichmentPipeline(details);

            // A failed reset seeds nothing, so the watcher comes back even when the caller meant to resume it
            // later. It resumes even when the caller gave up, or it would stay off until the Engine restarts.
            if (resumeWatcher || !completed)
            {
                await ResumeWatcherAsync(details, CancellationToken.None).ConfigureAwait(false);
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
        using var exclusive = BeginExclusiveReset();
        EnsureNotRealMedia();
        var details = new List<string>();
        var completed = false;

        await PauseWatcherAsync(details, ct).ConfigureAwait(false);

        try
        {
            await PauseEnrichmentPipelineAsync(details, ct).ConfigureAwait(false);
            using (await HoldWriteLockAsync(ct).ConfigureAwait(false))
            {
                // Reingest finds every file again at the path it has now, so the fixture list follows them first.
                FollowFixturesToCurrentPaths(details);
                WipeGeneratedCaches(details);
                ResetLibraryDatabase(details, ct);
            }

            WipeRuntimeLogs(details);
            details.Add("Ingestion engine: FSW resume deferred");
            completed = true;
        }
        finally
        {
            ResumeEnrichmentPipeline(details);

            // No reingest follows a failed reset, so the watcher comes back.
            if (!completed)
            {
                await ResumeWatcherAsync(details, CancellationToken.None).ConfigureAwait(false);
            }
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
        using var exclusive = BeginExclusiveReset();
        EnsureNotRealMedia();
        var details = new List<string>();
        var completed = false;

        await PauseWatcherAsync(details, ct).ConfigureAwait(false);
        try
        {
            await PauseEnrichmentPipelineAsync(details, ct).ConfigureAwait(false);
            var folders = ResolveResetFolders();
            using (await HoldWriteLockAsync(ct).ConfigureAwait(false))
            {
                WipeLibraryMadeFiles(details, folders);
                WipeGeneratedCaches(details);
                EnsureConfiguredSourcePathsExist(details);
                ResetDatabase(details, ct);
            }

            ReportFilesKept(details, folders);

            WipeRuntimeLogs(details);
            completed = true;
        }
        finally
        {
            ResumeEnrichmentPipeline(details);

            // Same rule as WipeAsync: a failed reset always brings the watcher back.
            if (resumeWatcher || !completed)
            {
                await ResumeWatcherAsync(details, CancellationToken.None).ConfigureAwait(false);
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
        try
        {
            await _enrichmentPipelineGate.PauseAndDrainAsync(_timeouts.EnrichmentDrain, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning("[HarnessReset] {Message}", ex.Message);
            throw new DevHarnessResetBlockedException(
                $"The reset was stopped before anything was deleted. {ex.Message} Try again in a moment.",
                ex);
        }

        _logger.LogInformation("[HarnessReset] Enrichment pipeline paused and drained");
        details.Add("Enrichment pipeline: paused and active workers drained");
    }

    private void ResumeEnrichmentPipeline(List<string> details)
    {
        _enrichmentPipelineGate.Resume();
        _logger.LogInformation("[HarnessReset] Enrichment pipeline resumed");
        details.Add("Enrichment pipeline: resumed");
    }

    /// <summary>
    /// Deletes the files development seeding created, wherever ingestion has filed them since, plus the subtitle and
    /// lyric files the Engine wrote for them and the folders they leave empty. Only folders the Library may change are
    /// touched; anything it did not create stays. Runs before the data store is cleared, because the data store is what
    /// knows where each fixture went.
    /// </summary>
    private void WipeLibraryMadeFiles(List<string> details, ResetFolders folders)
    {
        var seeded = DevFixtureManifestStore.Read(_options, _logger)
            .Select(NormalizePathOrNull)
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var locations = _fixtureLocations.FindCurrentLocations(seeded)
            .ToLookup(location => location.DetectedPath, StringComparer.OrdinalIgnoreCase);

        int deleted = 0;
        int filed = 0;
        int generated = 0;
        int refused = 0;
        var retained = new List<string>();

        foreach (var seedPath in seeded)
        {
            var mediaPaths = new List<string> { seedPath };
            var generatedPaths = new List<string>();
            foreach (var location in locations[seedPath])
            {
                mediaPaths.Add(location.CurrentPath);
                generatedPaths.AddRange(location.GeneratedFiles);
            }

            foreach (var mediaPath in mediaPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // A file that stays is listed by the path it has now: once the data store is cleared, nothing else
                // remembers where it was filed.
                switch (TryDeleteLibraryMadeFile(mediaPath, folders, details))
                {
                    case DeleteOutcome.Deleted:
                        deleted++;
                        if (!string.Equals(mediaPath, seedPath, StringComparison.OrdinalIgnoreCase))
                        {
                            filed++;
                        }

                        break;
                    case DeleteOutcome.Refused:
                        refused++;
                        retained.Add(mediaPath);
                        break;
                    case DeleteOutcome.Failed:
                        retained.Add(mediaPath);
                        break;
                }

                // Tag write-back keeps a short-lived copy beside the file; one left by an interrupted write goes too.
                if (TryDeleteLibraryMadeFile(mediaPath + ".tuvima.bak", folders, details) == DeleteOutcome.Deleted)
                {
                    generated++;
                }
            }

            // Track copies kept in the Library's data folder go with the generated caches.
            foreach (var generatedPath in generatedPaths
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(path => !folders.IsProtected(path)))
            {
                if (TryDeleteLibraryMadeFile(generatedPath, folders, details) == DeleteOutcome.Deleted)
                {
                    generated++;
                }
            }
        }

        DevFixtureManifestStore.Replace(_options, retained, _logger);
        details.Add(
            $"Seed fixtures: {deleted} test file(s) deleted ({filed} after being filed), {generated} Library-made subtitle or backup file(s) deleted, {refused} kept because they are outside the folders the Library may change");
    }

    /// <summary>
    /// Rewrites the fixture list to the paths its files have now, for a reset that clears the data store but keeps
    /// the files, so a later reset can still find fixtures that were filed before it.
    /// </summary>
    private void FollowFixturesToCurrentPaths(List<string> details)
    {
        var seeded = DevFixtureManifestStore.Read(_options, _logger)
            .Select(NormalizePathOrNull)
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (seeded.Count == 0)
        {
            return;
        }

        var locations = _fixtureLocations.FindCurrentLocations(seeded)
            .ToLookup(location => location.DetectedPath, StringComparer.OrdinalIgnoreCase);
        var current = seeded
            .SelectMany(seedPath => locations[seedPath].Select(location => location.CurrentPath).Append(seedPath))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        DevFixtureManifestStore.Replace(_options, current, _logger);
        details.Add($"Seed fixtures: list updated to {current.Count} file(s) at their current paths");
    }

    private enum DeleteOutcome
    {
        Absent,
        Deleted,
        Refused,
        Failed,
    }

    private DeleteOutcome TryDeleteLibraryMadeFile(string path, ResetFolders folders, List<string> details)
    {
        var normalized = NormalizePathOrNull(path);
        if (normalized is null || !File.Exists(normalized))
        {
            return DeleteOutcome.Absent;
        }

        var root = folders.ChangeableRootFor(normalized);
        if (root is null)
        {
            _logger.LogWarning(
                "[HarnessReset] Kept {Path}: it is outside the folders a development reset may change",
                normalized);
            return DeleteOutcome.Refused;
        }

        try
        {
            File.SetAttributes(normalized, FileAttributes.Normal);
            File.Delete(normalized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[HarnessReset] Failed to delete {Path}", normalized);
            details.Add($"Seed fixture ({normalized}): FAILED - {ex.Message}");
            return DeleteOutcome.Failed;
        }

        RemoveEmptyFolders(Path.GetDirectoryName(normalized), root);
        return DeleteOutcome.Deleted;
    }

    /// <summary>Removes <paramref name="folder"/> and its parents while they are empty, stopping below <paramref name="root"/>.</summary>
    private void RemoveEmptyFolders(string? folder, string root)
    {
        while (!string.IsNullOrEmpty(folder)
            && !string.Equals(folder, root, StringComparison.OrdinalIgnoreCase)
            && IsSameOrChild(root, folder)
            && Directory.Exists(folder)
            && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            try
            {
                Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "[HarnessReset] Left empty folder {Folder} in place", folder);
                return;
            }

            folder = Path.GetDirectoryName(folder);
        }
    }

    /// <summary>
    /// Lists the files a reset left in the folders it may change because the Library did not create them, so nothing
    /// a person put there disappears silently and nothing is deleted on a guess.
    /// </summary>
    private void ReportFilesKept(List<string> details, ResetFolders folders)
    {
        var count = 0;
        var examples = new List<string>();
        foreach (var root in folders.TopChangeableRoots().Where(Directory.Exists))
        {
            // Protected and as-is folders are not walked at all, so a large photo space costs nothing here.
            var files = new FileSystemEnumerable<string>(
                root,
                (ref FileSystemEntry entry) => entry.ToFullPath(),
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
                ShouldRecursePredicate = (ref FileSystemEntry entry) => folders.ShouldLookInside(entry.ToFullPath()),
            };
            foreach (var file in files)
            {
                if (folders.ChangeableRootFor(file) is null)
                {
                    continue;
                }

                count++;
                if (examples.Count < KeptFileExamples)
                {
                    examples.Add(file);
                }

                if (count >= KeptFileCountLimit)
                {
                    break;
                }
            }

            if (count >= KeptFileCountLimit)
            {
                break;
            }
        }

        if (count == 0)
        {
            details.Add("Kept files: none - every file left in the library folders was cleared or belongs to a protected folder");
            return;
        }

        var total = count >= KeptFileCountLimit ? $"at least {KeptFileCountLimit}" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        details.Add($"Kept files: {total} file(s) the Library did not create were left in place, for example: {string.Join("; ", examples)}");
        _logger.LogInformation("[HarnessReset] Kept {Count} file(s) the Library did not create", total);
    }

    /// <summary>Clears the regenerable folders inside the Library's own data folder; the data store itself stays.</summary>
    private void WipeGeneratedCaches(List<string> details)
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
            Path.Combine(libraryRoot, ".data", "variants"),
        };

        foreach (var dir in generatedDirs)
        {
            WipePathWithReport(details, "Generated cache", dir);
        }
    }

    /// <summary>
    /// The full wipe empties every managed source folder, so it must not run when one of them holds the Library's own
    /// data folder or the photo space: emptying it would delete the data store or people's photos.
    /// </summary>
    private void EnsureFullWipeIsSafe(List<string> details, ResetFolders folders)
    {
        foreach (var sourcePath in EnumerateConfiguredSourcePaths())
        {
            var normalizedSource = NormalizePathOrNull(sourcePath);
            if (normalizedSource is null)
            {
                continue;
            }

            foreach (var protectedRoot in folders.ProtectedRoots)
            {
                if (IsSameOrChild(normalizedSource, protectedRoot))
                {
                    var message =
                        $"The full wipe was stopped before anything was deleted. The source folder '{normalizedSource}' contains '{protectedRoot}', which the Library must keep (its data store, the photo space or the Import folder). Use the standard reset, which removes only the files the Library created.";
                    details.Add(message);
                    throw new DevHarnessResetBlockedException(message);
                }
            }
        }
    }

    private void EnsureNotRealMedia()
    {
        if (_realMediaRun is not null)
        {
            throw new DevHarnessResetBlockedException(
                "The reset was stopped before anything was deleted. Real-media protection is on for this Engine, so development resets are turned off; use tools/Run-RealMediaHarness.ps1 instead.");
        }
    }

    private ResetFolders ResolveResetFolders()
    {
        var libraries = _configLoader.LoadLibraries();
        var sources = libraries.Libraries
            .SelectMany(library => library.Sources)
            .Where(source => !string.IsNullOrWhiteSpace(source.Path))
            .Select(source => (Path: NormalizePathOrNull(source.Path), source.AllowsFileMutation))
            .Where(source => source.Path is not null)
            .Select(source => new ResetSource(source.Path!, source.AllowsFileMutation))
            .ToList();

        var libraryRoot = string.IsNullOrWhiteSpace(_options.Value.LibraryRoot)
            ? null
            : NormalizePathOrNull(_options.Value.LibraryRoot);

        var protectedRoots = new List<string>();
        if (libraryRoot is not null)
        {
            protectedRoots.Add(Path.Combine(libraryRoot, ".data"));
        }

        if (ResolveViewRoot(libraries) is { } viewRoot)
        {
            protectedRoots.Add(viewRoot);
        }

        if (ImportFolderRules.Find(libraries) is { } importFolder && NormalizePathOrNull(importFolder.Path) is { } importPath)
        {
            protectedRoots.Add(importPath);
        }

        return new ResetFolders(libraryRoot, sources, protectedRoots);
    }

    private static string? ResolveViewRoot(LibrariesConfiguration libraries)
    {
        var storage = libraries.StorageLocations.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, libraries.ViewStorage.StorageLocationId, StringComparison.OrdinalIgnoreCase));
        var relative = libraries.ViewStorage.RelativeRoot?.Trim();
        if (storage is null || string.IsNullOrWhiteSpace(storage.Path) || string.IsNullOrWhiteSpace(relative)
            || Path.IsPathRooted(relative))
        {
            return null;
        }

        return NormalizePathOrNull(Path.Combine(storage.Path, relative));
    }

    private sealed record ResetSource(string Path, bool AllowsFileMutation);

    /// <summary>The folders a development reset may change, and the ones inside them it never touches.</summary>
    private sealed record ResetFolders(string? LibraryRoot, IReadOnlyList<ResetSource> Sources, IReadOnlyList<string> ProtectedRoots)
    {
        /// <summary>
        /// The folder a reset may delete <paramref name="path"/> from: its source folder when the Library manages and
        /// may write it, otherwise the library folder for a file filed there; <c>null</c> for an as-is folder, the
        /// Import folder, the photo space, the Library's data folder, or anywhere else.
        /// </summary>
        public string? ChangeableRootFor(string path)
        {
            if (ProtectedRoots.Any(root => IsSameOrChild(root, path)))
            {
                return null;
            }

            var source = Sources
                .Where(candidate => !string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase)
                    && IsSameOrChild(candidate.Path, path))
                .OrderByDescending(candidate => candidate.Path.Length)
                .FirstOrDefault();
            if (source is not null)
            {
                return source.AllowsFileMutation ? source.Path : null;
            }

            return LibraryRoot is not null
                && !string.Equals(LibraryRoot, path, StringComparison.OrdinalIgnoreCase)
                && IsSameOrChild(LibraryRoot, path)
                    ? LibraryRoot
                    : null;
        }

        public bool IsProtected(string path) => ProtectedRoots.Any(root => IsSameOrChild(root, path));

        /// <summary>Whether a folder can hold files a reset may change, so the kept-files walk goes into it.</summary>
        public bool ShouldLookInside(string folder)
        {
            if (IsProtected(folder))
            {
                return false;
            }

            var source = Sources
                .Where(candidate => IsSameOrChild(candidate.Path, folder))
                .OrderByDescending(candidate => candidate.Path.Length)
                .FirstOrDefault();
            return source?.AllowsFileMutation ?? true;
        }

        /// <summary>The changeable folders that are not inside another one, so each file is looked at once.</summary>
        public IEnumerable<string> TopChangeableRoots()
        {
            var roots = Sources.Where(source => source.AllowsFileMutation).Select(source => source.Path).ToList();
            if (LibraryRoot is not null)
            {
                roots.Add(LibraryRoot);
            }

            return roots
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(root => !roots.Any(other =>
                    !string.Equals(other, root, StringComparison.OrdinalIgnoreCase) && IsSameOrChild(other, root)))
                .ToList();
        }
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

    /// <summary>Drops every table and reapplies the baseline. The caller holds the write lock.</summary>
    private void ResetDatabase(List<string> details, CancellationToken ct)
    {
        int dropped;
        using (var conn = _db.CreateConnection())
        {
            dropped = DropAllTables(conn, ct);
        }

        _db.InitializeSchema();
        _db.RunStartupChecks();

        details.Add($"Database: dropped {dropped} table(s) and reinitialized schema");
        _logger.LogInformation("[HarnessReset] Database reset: dropped {Count} table(s)", dropped);
    }

    private static int DropAllTables(SqliteConnection conn, CancellationToken ct)
    {
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

        // Dropping tables takes their indexes and triggers with them but not views, and the baseline step
        // creates the views again without IF NOT EXISTS.
        var views = new List<string>();
        using (var viewListCmd = conn.CreateCommand())
        {
            viewListCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='view';";
            using var viewReader = viewListCmd.ExecuteReader();
            while (viewReader.Read())
            {
                views.Add(viewReader.GetString(0));
            }
        }

        foreach (string view in views)
        {
            using var dropViewCmd = conn.CreateCommand();
            dropViewCmd.CommandText = $"DROP VIEW IF EXISTS [{view}];";
            dropViewCmd.ExecuteNonQuery();
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

        return tables.Count;
    }

    /// <summary>Clears library and ingestion tables, keeping configuration. The caller holds the write lock.</summary>
    private void ResetLibraryDatabase(List<string> details, CancellationToken ct)
    {
        int cleared;
        using (var conn = _db.CreateConnection())
        {
            cleared = ClearLibraryTables(conn, ct);
        }

        _db.RunStartupChecks();
        details.Add($"Database: cleared {cleared} library/ingestion table(s); accounts, profiles, access, provider configuration, and UI settings preserved");
        _logger.LogInformation(
            "[HarnessReset] Library data reset cleared {Count} table(s) while preserving configuration state",
            cleared);
    }

    private static int ClearLibraryTables(SqliteConnection conn, CancellationToken ct)
    {
        using (var fkOff = conn.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }

        // pragma_table_list reports full-text shadow tables as type 'shadow', so they are left out here.
        // Deleting from a full-text table through its virtual table clears its shadow tables consistently;
        // deleting from the shadow tables directly corrupts the index.
        var tables = new List<string>();
        using (var listCmd = conn.CreateCommand())
        {
            listCmd.CommandText = "SELECT name FROM pragma_table_list WHERE schema='main' AND type IN ('table','virtual') AND name NOT LIKE 'sqlite_%';";
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
            if (ShouldPreserveConfigurationTable(table))
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

        return cleared.Count;
    }

    /// <summary>
    /// Takes the data store's write lock for the destructive part of a reset, waiting at most
    /// <see cref="DevHarnessResetTimeouts.WriteLock"/>, so a writer that never lets go fails the reset with its name
    /// instead of hanging the request. Disposing the result releases the lock.
    /// </summary>
    internal async Task<IDisposable> HoldWriteLockAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeouts.WriteLock);
        try
        {
            await _db.AcquireWriteLockAsync(timeout.Token).ConfigureAwait(false);
            return new WriteLockRelease(_db);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            var holder = _db.DescribeWriteLockHolder() ?? "a writer that has since finished";
            var message =
                $"The reset was stopped before anything was deleted. The data store stayed busy for {_timeouts.WriteLock.TotalSeconds:0} seconds (held by {holder}). Try again in a moment.";
            _logger.LogWarning("[HarnessReset] {Message}", message);
            throw new DevHarnessResetBlockedException(message, ex);
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

    private static bool IsSameOrChild(string maybeParent, string maybeChild)
    {
        var parent = maybeParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var child = maybeChild.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Lets one reset run at a time. A second one is refused straight away, so its clean-up can never resume
    /// enrichment or the watcher while the first is still deleting. Disposing the result lets the next reset in.
    /// </summary>
    private IDisposable BeginExclusiveReset()
    {
        if (!_singleReset.Wait(0))
        {
            throw new DevHarnessResetBlockedException(
                "Another reset is already running, so this one was stopped before anything was deleted. Try again when it finishes.");
        }

        return new SingleResetRelease(_singleReset);
    }

    private sealed class SingleResetRelease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private sealed class WriteLockRelease(IDatabaseConnection db) : IDisposable
    {
        private IDatabaseConnection? _db = db;

        public void Dispose() => Interlocked.Exchange(ref _db, null)?.ReleaseWriteLock();
    }
}
