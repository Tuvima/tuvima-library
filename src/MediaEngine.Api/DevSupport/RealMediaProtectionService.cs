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
        if (!await VerifyAsync(ct)) throw new IOException("Real-media source baseline verification failed.");
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
                throw new InvalidOperationException("Real-media View sources must be enabled linked folders.");
            if (!source.IncludeInTimeline) await storage.UpdateSourceAsync(space, source with { IncludeInTimeline = true }, ct);
        }
        var paths = await storage.GetEnabledSourcePathsAsync(ct);
        if (paths.Count != 2 || paths.Any(p => !RealMediaHarness.ViewFolders.Any(f =>
                string.Equals(p.Path, Path.Combine(run.SourceRoot, f), StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Unexpected View sources in real-media mode.");
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
            if (differences.Length > 0) Fail($"Baseline differences: {string.Join(", ", differences)}");
            _verifiedAt = DateTimeOffset.UtcNow;
            _status = _failed == 0 ? (HasHistoricalViolation ? "Protected; file hashes verified; earlier folder timestamp violation recorded" : "Protected; hashes verified") : "Source protection failed";
            return _failed == 0;
        }
        finally { _verification.Release(); }
    }

    public bool RequestVerification()
    {
        if (Interlocked.CompareExchange(ref _verificationRequested, 1, 0) != 0) return false;
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
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                RealMediaHarness.ValidateConfiguration(ConfigDirectory, run);
                var actual = await RealMediaHarness.SnapshotAsync(run.SourceRoot, false, stoppingToken);
                var differences = RealMediaHarness.Differences(_baseline, actual, false);
                if (differences.Length > 0) Fail($"Source inventory changed: {string.Join(", ", differences)}");
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
            if (protection is null) return Results.Conflict(new { error = "Real-media mode is not active." });
            protection.RequestVerification();
            return Results.Accepted(value: protection.Status);
        }).RequireEffectiveAdministrator();
    }

    public static void UseRealMediaProtection(this WebApplication app) => app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? "";
        var mutation = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method);
        if (mutation && ((path.StartsWith("/dev", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("/dev/real-media/", StringComparison.OrdinalIgnoreCase))
            || path.StartsWith("/settings", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/libraries", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/sources", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/setup", StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = 409;
            await context.Response.WriteAsJsonAsync(new { error = "Protected real-media mode: source changes and legacy reset/seed operations are disabled. Use the offline real-media runner to reset." });
            return;
        }
        await next(context);
    });
}
