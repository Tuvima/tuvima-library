using System.Collections.Concurrent;
using System.Text.Json;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.DevSupport;

/// <summary>Fail-closed monitoring for the lifetime of an explicitly prepared real-media run.</summary>
public sealed class RealMediaProtectionService(RealMediaRun run, IServiceProvider services,
    IHostApplicationLifetime lifetime, ILogger<RealMediaProtectionService> logger) : BackgroundService
{
    private readonly ConcurrentQueue<string> _events = new();
    private readonly SemaphoreSlim _verification = new(1, 1);
    private readonly SemaphoreSlim _configurationChanges = new(1, 1);
    private FileSystemWatcher? _watcher;
    private IReadOnlyList<RealMediaFile> _baseline = [];
    private string _status = "Starting";
    private DateTimeOffset? _verifiedAt;
    private int _failed;
    private int _verificationRequested;
    private bool HasHistoricalViolation => File.Exists(Path.Combine(run.OutputDirectory, "directory-probe-recovery.json"));
    private string ConfigDirectory => Environment.GetEnvironmentVariable("TUVIMA_CONFIG_DIR") ?? "config";

    public object Status => new { active = true, source = run.SourceRoot, run.OutputDirectory, status = _status,
        files = _baseline.Count(e => !e.IsDirectory), verified_at = _verifiedAt, events = _events.ToArray(),
        historical_source_violation = HasHistoricalViolation,
        ingestion = "See Operations and ingestion-report.json for durable work status", playback = "Not certified by source verification" };

    public override async Task StartAsync(CancellationToken ct)
    {
        RealMediaHarness.ValidateConfiguration(ConfigDirectory, run);
        var baselineFile = HasHistoricalViolation ? "monitoring-baseline.json" : "source-baseline.json";
        _baseline = JsonSerializer.Deserialize<List<RealMediaFile>>(await File.ReadAllTextAsync(Path.Combine(run.OutputDirectory, baselineFile), ct))!;
        _watcher = new FileSystemWatcher(run.SourceRoot) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes | NotifyFilters.Security };
        _watcher.Changed += (_, e) => Fail($"Changed: {e.FullPath}");
        _watcher.Created += (_, e) => Fail($"Added: {e.FullPath}");
        _watcher.Deleted += (_, e) => Fail($"Deleted: {e.FullPath}");
        _watcher.Renamed += (_, e) => Fail($"Renamed: {e.OldFullPath} -> {e.FullPath}");
        _watcher.Error += (_, e) => Fail($"Source observer lost coverage: {e.GetException().Message}");
        _watcher.EnableRaisingEvents = true;
        // Establish cheap inventory protection before listening. Full byte verification
        // runs in the background; source mutation is already denied by policy.
        var inventory = await RealMediaHarness.SnapshotAsync(run.SourceRoot, false, ct);
        if (RealMediaHarness.Differences(_baseline, inventory, false).Length > 0)
        {
            throw new IOException("Real-media source inventory differs from the protected baseline.");
        }
        _status = "Protected; background hash verification pending";
        RealMediaHarness.Save(Path.Combine(run.OutputDirectory, "protection-status.json"), Status);
        RealMediaHarness.ValidateConfiguration(ConfigDirectory, run);
        var storage = services.GetRequiredService<ViewStorageService>();
        var spaces = services.GetRequiredService<IViewPersonalSpaceRepository>();
        var space = await storage.EnsurePersonalSpaceAsync(run.ProfileId, ct);
        var existing = await spaces.GetSourcesAsync(space.Id, ct);
        foreach (var folder in RealMediaHarness.ViewFolders)
        {
            var path = Path.Combine(run.SourceRoot, folder);
            var source = existing.FirstOrDefault(s => string.Equals(s.ExternalPath, path, StringComparison.OrdinalIgnoreCase))
                ?? await storage.AddLinkedSourceAsync(space, folder, path, true, ct);
            if (source.StorageMode != ViewSourceStorageMode.Linked || !source.Enabled)
            {
                throw new InvalidOperationException("Real-media View sources must be enabled linked folders.");
            }
            if (!source.IncludeInTimeline)
            {
                await storage.UpdateSourceAsync(space, source with { IncludeInTimeline = true }, ct);
            }
        }
        var paths = await storage.GetEnabledSourcePathsAsync(ct);
        if (paths.Count != 2 || paths.Any(p => !RealMediaHarness.ViewFolders.Any(f =>
                string.Equals(p.Path, Path.Combine(run.SourceRoot, f), StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException("Unexpected View sources in real-media mode.");
        }
        await base.StartAsync(ct);
    }

    public async Task<bool> VerifyAsync(CancellationToken ct = default)
    {
        await _verification.WaitAsync(ct);
        try
        {
            _status = "Verifying source hashes";
            var actual = await RealMediaHarness.SnapshotAsync(run.SourceRoot, true, ct);
            var differences = RealMediaHarness.Differences(_baseline, actual, true);
            RealMediaHarness.Save(Path.Combine(run.OutputDirectory, "source-verification.json"),
                new { checked_at = DateTimeOffset.UtcNow, passed = differences.Length == 0 && _failed == 0 && !HasHistoricalViolation,
                    monitoring_passed = differences.Length == 0 && _failed == 0,
                    historical_source_violation = HasHistoricalViolation, differences, events = _events.ToArray() });
            if (differences.Length > 0)
            {
                Fail($"Baseline differences: {string.Join(", ", differences)}");
            }
            _verifiedAt = DateTimeOffset.UtcNow;
            _status = _failed == 0 ? (HasHistoricalViolation ? "Protected; file hashes verified; earlier folder timestamp violation recorded" : "Protected; hashes verified") : "Source protection failed";
            RealMediaHarness.Save(Path.Combine(run.OutputDirectory, "protection-status.json"), Status);
            return _failed == 0;
        }
        finally { _verification.Release(); }
    }

    public bool RequestVerification()
    {
        if (Interlocked.CompareExchange(ref _verificationRequested, 1, 0) != 0)
        {
            return false;
        }
        _status = "Source verification queued";
        _ = Task.Run(async () =>
        {
            try { await VerifyAsync(lifetime.ApplicationStopping); }
            catch (OperationCanceledException) { _events.Enqueue("Requested verification interrupted by shutdown."); }
            catch (Exception ex) { Fail($"Verification failed: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _verificationRequested, 0); }
        });
        return true;
    }

    public async Task SaveLocaleAsync(MediaEngine.Contracts.Setup.SetupLocaleDto locale, IConfigurationLoader configuration, CancellationToken ct)
    {
        await _configurationChanges.WaitAsync(ct);
        try
        {
            RealMediaHarness.ValidateConfiguration(ConfigDirectory, run);
            var original = configuration.LoadCore();
            var core = JsonSerializer.Deserialize<MediaEngine.Domain.Configuration.CoreConfiguration>(JsonSerializer.Serialize(original))!;
            core.Language.Display = System.Globalization.CultureInfo.GetCultureInfo(locale.DisplayLanguage).Name;
            core.Language.Metadata = System.Globalization.CultureInfo.GetCultureInfo(locale.MetadataLanguage).Name;
            core.Country = new System.Globalization.RegionInfo(locale.Country).TwoLetterISORegionName;
            try
            {
                configuration.SaveCore(core);
                var updated = run with { ConfigurationHash = RealMediaHarness.ConfigHash(ConfigDirectory) };
                RealMediaHarness.ValidateConfiguration(ConfigDirectory, updated);
                RealMediaHarness.Save(Path.Combine(ConfigDirectory, RealMediaHarness.SettingsFile), updated);
                run = updated;
            }
            catch
            {
                configuration.SaveCore(original);
                // SaveCore may normalize formatting; re-seal only the restored, validated configuration.
                var restored = run with { ConfigurationHash = RealMediaHarness.ConfigHash(ConfigDirectory) };
                RealMediaHarness.ValidateConfiguration(ConfigDirectory, restored);
                RealMediaHarness.Save(Path.Combine(ConfigDirectory, RealMediaHarness.SettingsFile), restored);
                run = restored;
                throw;
            }
        }
        finally { _configurationChanges.Release(); }
    }

    private void Fail(string message)
    {
        _events.Enqueue(message);
        Interlocked.Exchange(ref _failed, 1);
        _status = "Source protection failed";
        logger.LogCritical("Real-media protection failed: {Reason}. Stopping Engine.", message);
        try { RealMediaHarness.Save(Path.Combine(run.OutputDirectory, "protection-failure.json"), new { detected_at = DateTimeOffset.UtcNow, message, events = _events.ToArray() }); }
        catch (Exception ex) { logger.LogError(ex, "Could not persist source protection evidence"); }
        lifetime.StopApplication();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!await VerifyAsync(stoppingToken))
            {
                return;
            }
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                  await _configurationChanges.WaitAsync(stoppingToken);
                try { RealMediaHarness.ValidateConfiguration(ConfigDirectory, run); }
                  finally { _configurationChanges.Release(); }
                var actual = await RealMediaHarness.SnapshotAsync(run.SourceRoot, false, stoppingToken);
                var differences = RealMediaHarness.Differences(_baseline, actual, false);
                if (differences.Length > 0)
                {
                    Fail($"Source inventory changed: {string.Join(", ", differences)}");
                }
                RealMediaHarness.Save(Path.Combine(run.OutputDirectory, "protection-status.json"), Status);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex.Message); }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        try { await VerifyAsync(ct); }
        catch (Exception ex) { _events.Enqueue($"Closeout verification incomplete: {ex.Message}"); _status = "Closeout verification incomplete"; }
        RealMediaHarness.Save(Path.Combine(run.OutputDirectory, "protection-status.json"), Status);
        _watcher?.Dispose();
    }
}

public static class RealMediaEndpoints
{
    public static void MapRealMediaEndpoints(this WebApplication app)
    {
        app.MapPost("/dev/real-media/status", (IServiceProvider services) =>
            Results.Ok(services.GetService<RealMediaProtectionService>()?.Status ?? new { active = false }))
            .RequireEffectiveAdministrator();
        app.MapPost("/dev/real-media/verify", (IServiceProvider services) =>
        {
            var protection = services.GetService<RealMediaProtectionService>();
            if (protection is null)
            {
                return Results.Conflict(new { error = "Real-media mode is not active." });
            }
            protection.RequestVerification();
            return Results.Accepted(value: protection.Status);
        }).RequireEffectiveAdministrator();
    }

    // Allow account creation and validation, never source/configuration replacement or restore.
    public static bool IsSafeSetupRequest(string method, string path)
    {
        if (HttpMethods.IsPut(method) && path.TrimEnd('/').Equals("/setup/v1/locale", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var segments = path.Trim('/').Split('/');
        if (segments.Length is 5 or 6 && segments[0] == "setup" && segments[1] == "v1"
            && segments[2] == "providers" && segments[4] == "credentials"
            && ((segments.Length == 5 && HttpMethods.IsPut(method))
                || (segments.Length == 6 && segments[5] == "test" && HttpMethods.IsPost(method))))
        {
            return true;
        }
        if (!HttpMethods.IsPost(method))
        {
            return false;
        }
        return path.TrimEnd('/').ToLowerInvariant() is
            "/setup/v1/begin" or "/setup/v1/preflight" or "/setup/v1/administrator"
            or "/setup/v1/media-locations/validate" or "/setup/v1/steps/providers"
            or "/setup/v1/steps/media-locations" or "/setup/v1/complete";
    }

    // Provider checks and credential rotation do not change the protected media
    // corpus or its library sources. Settings must have the same access as setup.
    public static bool IsSafeProviderSettingsRequest(string method, string path)
    {
        var segments = path.Trim('/').Split('/');
        if (segments.Length < 4 || segments[0] != "settings" || segments[1] != "providers"
            || string.IsNullOrWhiteSpace(segments[2]))
        {
            return false;
        }

        return segments.Length == 4 && segments[3] == "test" && HttpMethods.IsPost(method)
            || segments.Length == 4 && segments[3] == "credentials"
                && (HttpMethods.IsPut(method) || HttpMethods.IsDelete(method))
            || segments.Length == 5 && segments[3] == "credentials" && segments[4] == "test"
                && HttpMethods.IsPost(method);
    }

    public static void UseRealMediaProtection(this WebApplication app) => app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? "";
        var mutation = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method);
        if (mutation && ((path.StartsWith("/dev", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("/dev/real-media/", StringComparison.OrdinalIgnoreCase))
            || (path.StartsWith("/settings", StringComparison.OrdinalIgnoreCase)
                && !IsSafeProviderSettingsRequest(context.Request.Method, path))
            || path.StartsWith("/libraries", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/sources", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("/setup", StringComparison.OrdinalIgnoreCase) && !IsSafeSetupRequest(context.Request.Method, path))))
        {
            context.Response.StatusCode = 409;
            await context.Response.WriteAsJsonAsync(new { error = "Protected real-media mode: source changes and legacy reset/seed operations are disabled. Use the offline real-media runner to reset." });
            return;
        }
        await next(context);
    });
}
