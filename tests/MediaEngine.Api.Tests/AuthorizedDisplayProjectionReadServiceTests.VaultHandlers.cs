using System.Reflection;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Realtime;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

// WP17 part C: handler-level proof for the Part B surfaces. The operations routes, the plugin job list, the cast authorizer and the
// ingestion watch-folder and scan handlers each answer for a Vault file or folder exactly as they answer for one that does not exist.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private const string VaultOpsFolder = "/mnt/private-clips";

    private static MediaOperation OperationAbout(Guid? entityId, string? path, string key) => new()
    {
        EntityId = entityId,
        OperationType = MediaOperationType.IngestionFile,
        OperationKind = MediaOperationKind.Ingestion,
        SourcePath = path,
        Status = MediaOperationStatus.Queued,
        Stage = MediaOperationStage.Queued,
        QueueName = "ingestion",
        IdempotencyKey = $"ingestion:file:{key}",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private Task<IReadOnlyDictionary<string, RouteEndpoint>> MapOperationRoutesAsync() =>
        MapRoutesAsync(builder => builder.MapOperationsEndpoints(), builder =>
        {
            builder.Services.AddSingleton<IMediaOperationRepository>(new MediaOperationRepository(_database));
            builder.Services.AddSingleton<IMediaOperationEventRepository>(new MediaOperationEventRepository(_database));
            builder.Services.AddSingleton(new VaultOperationGate(new VaultLibraryRepository(_database), new PrefixVaultPaths(VaultOpsFolder)));
        });

    [Fact]
    public async Task VaultHandlers_TheOperationsList_NeverShowsAnOperationAboutAVaultFile_ByIdOrByPath()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var operations = new MediaOperationRepository(_database);
        var vaultById = await operations.EnsureAsync(OperationAbout(shape.Secret.AssetId, "/watch/by-id.mkv", "by-id"));
        var vaultByPath = await operations.EnsureAsync(OperationAbout(Guid.NewGuid(), $"{VaultOpsFolder}/by-path.mkv", "by-path"));
        var ordinary = await operations.EnsureAsync(OperationAbout(shape.Normal.AssetId, "/watch/ordinary.mkv", "ordinary"));
        var routes = await MapOperationRoutesAsync();

        var list = await InvokeAsync(routes["ListMediaOperations"], shape.Account, NewVaultSession(shape.Account), ("unused", "x"));

        Assert.Equal(StatusCodes.Status200OK, list.Status);
        Assert.Contains(ordinary.Id.ToString("D"), list.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(vaultById.Id.ToString("D"), list.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(vaultByPath.Id.ToString("D"), list.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("by-id", list.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("by-path", list.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VaultHandlers_AVaultOperation_AnswersDetailRetryAndCancelExactlyLikeAMissingOne_AndNothingChanges()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var operations = new MediaOperationRepository(_database);
        var vaultById = await operations.EnsureAsync(OperationAbout(shape.Secret.AssetId, "/watch/by-id.mkv", "by-id-two"));
        var vaultByPath = await operations.EnsureAsync(OperationAbout(Guid.NewGuid(), $"{VaultOpsFolder}/by-path.mkv", "by-path-two"));
        var routes = await MapOperationRoutesAsync();
        var session = NewVaultSession(shape.Account);

        foreach (var routeName in new[] { "GetMediaOperation", "RetryMediaOperation", "CancelMediaOperation" })
        {
            var missingId = Guid.NewGuid();
            var missing = await InvokeAsync(routes[routeName], shape.Account, session, ("id", missingId.ToString("D")));
            Assert.Equal(StatusCodes.Status404NotFound, missing.Status);

            foreach (var hidden in new[] { vaultById, vaultByPath })
            {
                var answer = await InvokeAsync(routes[routeName], shape.Account, session, ("id", hidden.Id.ToString("D")));

                Assert.Equal(missing.Status, answer.Status);
                Assert.Equal(
                    missing.Body.Replace(missingId.ToString("D"), "ID", StringComparison.OrdinalIgnoreCase),
                    answer.Body.Replace(hidden.Id.ToString("D"), "ID", StringComparison.OrdinalIgnoreCase));
            }
        }

        Assert.Equal(MediaOperationStatus.Queued, (await operations.GetByIdAsync(vaultById.Id))!.Status);
        Assert.Equal(MediaOperationStatus.Queued, (await operations.GetByIdAsync(vaultByPath.Id))!.Status);
    }

    [Fact]
    public async Task VaultHandlers_AnOrdinaryOperation_StillGetsDetailAndCancel()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var operations = new MediaOperationRepository(_database);
        var ordinary = await operations.EnsureAsync(OperationAbout(shape.Normal.AssetId, "/watch/ordinary.mkv", "ordinary-two"));
        var routes = await MapOperationRoutesAsync();
        var session = NewVaultSession(shape.Account);

        var detail = await InvokeAsync(routes["GetMediaOperation"], shape.Account, session, ("id", ordinary.Id.ToString("D")));
        var cancel = await InvokeAsync(routes["CancelMediaOperation"], shape.Account, session, ("id", ordinary.Id.ToString("D")));

        Assert.Equal(StatusCodes.Status200OK, detail.Status);
        Assert.Equal(StatusCodes.Status202Accepted, cancel.Status);
        Assert.Equal(MediaOperationStatus.Cancelled, (await operations.GetByIdAsync(ordinary.Id))!.Status);
    }

    [Fact]
    public async Task VaultHandlers_ThePluginJobList_UsesTheSameGate_AndDropsVaultOperations()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var gate = new VaultOperationGate(new VaultLibraryRepository(_database), new PrefixVaultPaths(VaultOpsFolder));
        var vault = OperationAbout(shape.Secret.AssetId, "/watch/a.mkv", "plugin-vault");
        var byPath = OperationAbout(null, $"{VaultOpsFolder}/b.mkv", "plugin-path");
        var ordinary = OperationAbout(shape.Normal.AssetId, "/watch/c.mkv", "plugin-ordinary");

        var visible = await gate.VisibleAsync([vault, byPath, ordinary]);

        Assert.Equal([ordinary.IdempotencyKey], visible.Select(operation => operation.IdempotencyKey));
        Assert.True(await gate.HidesAsync(vault));
        Assert.True(await gate.HidesAsync(byPath));
        Assert.False(await gate.HidesAsync(ordinary));
    }

    [Fact]
    public async Task VaultHandlers_TheCastAuthorizer_NeverAllowsAVaultFile_ToACastGrantOrALockedPerson()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var session = NewVaultSession(shape.Account);
        var locked = VaultContext(shape.Account, session);
        var authorizer = new CatalogueCastAssetAuthorizer(CreateResourceService(locked));
        RequestAuthority Authority(PrincipalKind kind) =>
            new(kind, true, shape.Account, VaultProfile, Guid.NewGuid(), session, Guid.NewGuid(),
                AccountEnabled: true, GrantEnabled: true, ApplicationEnabled: true);

        // Locked, a person (and so anything casting for them) gets no Vault file.
        Assert.False(await authorizer.AllowsPlaybackAsync(Authority(PrincipalKind.Human), shape.Secret.AssetId));
        Assert.False(await authorizer.AllowsPlaybackAsync(Authority(PrincipalKind.DelegatedUserClient), shape.Secret.AssetId));

        // Unlocked, a cast grant is still never a person on their own device session.
        await UnlockVaultAsync(session);
        Assert.False(await authorizer.AllowsPlaybackAsync(Authority(PrincipalKind.DelegatedUserClient), shape.Secret.AssetId));
        Assert.False(await authorizer.AllowsPlaybackAsync(Authority(PrincipalKind.ServiceApplication), shape.Secret.AssetId));
        Assert.False(await authorizer.AllowsPlaybackAsync(Authority(PrincipalKind.Anonymous), shape.Secret.AssetId));
        Assert.True(await authorizer.AllowsPlaybackAsync(Authority(PrincipalKind.Human), shape.Secret.AssetId));
        Assert.True(await authorizer.AllowsPlaybackAsync(Authority(PrincipalKind.Human), shape.Normal.AssetId));
    }

    // ── Ingestion handlers (watch folder and dry-run scan) ──────────────────────────────────────

    [Fact]
    public async Task VaultHandlers_TheWatchFolderList_SkipsAVaultFolder_AndNeverNamesIt()
    {
        var vaultFolder = Directory.CreateTempSubdirectory("tuvima_watch_vault_").FullName;
        var ordinaryFolder = Directory.CreateTempSubdirectory("tuvima_watch_ordinary_").FullName;
        try
        {
            File.WriteAllText(Path.Combine(vaultFolder, "secret-one.mkv"), "x");
            File.WriteAllText(Path.Combine(ordinaryFolder, "Dune.mkv"), "x");
            var route = IngestionRoute("ListWatchFolder");

            var both = await RunIngestionRouteAsync(route, new IngestionOptions { WatchDirectories = [vaultFolder, ordinaryFolder] }, vaultFolder);
            var onlyVault = await RunIngestionRouteAsync(route, new IngestionOptions { WatchDirectories = [vaultFolder] }, vaultFolder);

            Assert.Contains("Dune.mkv", both.Body);
            Assert.DoesNotContain("secret-one", both.Body);
            Assert.DoesNotContain(Path.GetFileName(vaultFolder), both.Body);
            Assert.DoesNotContain("secret-one", onlyVault.Body);
            Assert.DoesNotContain(Path.GetFileName(vaultFolder), onlyVault.Body);
        }
        finally
        {
            Directory.Delete(vaultFolder, true);
            Directory.Delete(ordinaryFolder, true);
        }
    }

    [Fact]
    public async Task VaultHandlers_TheScan_AnswersAVaultFolderLikeAFolderThatDoesNotExist_AndNeverRunsTheEngine()
    {
        var vaultFolder = Directory.CreateTempSubdirectory("tuvima_scan_vault_").FullName;
        var ordinaryFolder = Directory.CreateTempSubdirectory("tuvima_scan_ordinary_").FullName;
        try
        {
            var route = IngestionRoute("TriggerScan");
            var options = new IngestionOptions { WatchDirectories = [vaultFolder, ordinaryFolder] };
            var gone = Path.Combine(Path.GetTempPath(), "tuvima_scan_gone_" + Guid.NewGuid().ToString("N"));

            var missing = await RunIngestionRouteAsync(route, options, vaultFolder, new { root_path = gone });
            var vault = await RunIngestionRouteAsync(route, options, vaultFolder, new { root_path = vaultFolder });

            Assert.Equal(StatusCodes.Status400BadRequest, missing.Status);
            Assert.Equal(missing.Status, vault.Status);
            Assert.Equal(missing.Body.Replace(gone, "PATH"), vault.Body.Replace(vaultFolder, "PATH"));
            Assert.Equal(0, missing.EngineScans + vault.EngineScans);

            // With no folder given, the first folder that is not the Vault's is the one scanned.
            var defaulted = await RunIngestionRouteAsync(route, options, vaultFolder);
            Assert.Equal(StatusCodes.Status200OK, defaulted.Status);
            Assert.Equal([ordinaryFolder], defaulted.ScannedRoots);
        }
        finally
        {
            Directory.Delete(vaultFolder, true);
            Directory.Delete(ordinaryFolder, true);
        }
    }

    private sealed class LockedCaller : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(new RequestAuthority(PrincipalKind.Human, IsAuthenticated: true));
    }

    [Fact]
    public async Task VaultHandlers_TheScan_AnswersAFolderThatHoldsTheVaultLikeAFolderThatDoesNotExist_WhileLocked()
    {
        var parent = Directory.CreateTempSubdirectory("tuvima_scan_parent_").FullName;
        var vaultFolder = Directory.CreateDirectory(Path.Combine(parent, "private")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(vaultFolder, "secret-one.mkv"), "x");
            var route = IngestionRoute("TriggerScan");
            var options = new IngestionOptions { WatchDirectories = [parent] };
            var gone = Path.Combine(Path.GetTempPath(), "tuvima_scan_gone_" + Guid.NewGuid().ToString("N"));

            var missing = await RunIngestionRouteAsync(route, options, vaultFolder, new { root_path = gone });
            var holdsVault = await RunIngestionRouteAsync(route, options, vaultFolder, new { root_path = parent });

            Assert.Equal(StatusCodes.Status400BadRequest, holdsVault.Status);
            Assert.Equal(missing.Status, holdsVault.Status);
            Assert.Equal(missing.Body.Replace(gone, "PATH"), holdsVault.Body.Replace(parent, "PATH"));
            Assert.Equal(0, holdsVault.EngineScans);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    private static RouteEndpoint IngestionRoute(string name)
    {
        var build = typeof(MappedEndpointInventoryTests).GetMethod("BuildEndpointOnlyApplication", BindingFlags.NonPublic | BindingFlags.Static)!;
        var app = (WebApplication)build.Invoke(null, null)!;
        _ingestionApps.Add(app);
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
    }

    private static readonly List<WebApplication> _ingestionApps = [];

    private sealed record IngestionRouteResult(int Status, string Body, int EngineScans, IReadOnlyList<string> ScannedRoots);

    private static async Task<IngestionRouteResult> RunIngestionRouteAsync(
        RouteEndpoint route, IngestionOptions options, string vaultFolder, object? body = null)
    {
        var engine = new RecordingEngine();
        var services = new ServiceCollection()
            .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
            .AddSingleton(Options.Create(options))
            .AddSingleton<IVaultPathClassifier>(new PrefixVaultPaths(vaultFolder))
            .AddSingleton<IRequestAuthorityResolver>(new LockedCaller())
            // A caller with no session is never unlocked, so the Vault repositories are not reached.
            .AddSingleton(new VaultAccessResolver(null!, null!, TimeProvider.System))
            .AddSingleton(typeof(IIngestionEngine), engine.Proxy)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        if (body is not null)
        {
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body);
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody());
        }

        await route.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        return new IngestionRouteResult(
            context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync(), engine.Roots.Count, engine.Roots.ToList());
    }

    private sealed class PrefixVaultPaths(string vaultFolder) : IVaultPathClassifier
    {
        public ValueTask<bool> IsVaultPathAsync(string? path, CancellationToken ct = default) =>
            ValueTask.FromResult(!string.IsNullOrWhiteSpace(path)
                && path.Replace('\\', '/').StartsWith(vaultFolder.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

        public ValueTask<bool> IsOrContainsVaultPathAsync(string? path, CancellationToken ct = default) =>
            ValueTask.FromResult(!string.IsNullOrWhiteSpace(path)
                && (vaultFolder.Replace('\\', '/').StartsWith(path.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
                    || path.Replace('\\', '/').StartsWith(vaultFolder.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Remembers which folders the dry run was asked about; any other call to the engine fails the test.</summary>
    private sealed class RecordingEngine
    {
        public List<string> Roots { get; } = [];

        public IIngestionEngine Proxy { get; }

        public RecordingEngine()
        {
            var proxy = DispatchProxy.Create<IIngestionEngine, RecordingEngineProxy>();
            ((RecordingEngineProxy)(object)proxy).Owner = this;
            Proxy = proxy;
        }
    }

    public class RecordingEngineProxy : DispatchProxy
    {
        internal object? Owner { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IIngestionEngine.DryRunAsync) && Owner is RecordingEngine engine && args?[0] is string root)
            {
                engine.Roots.Add(root);
                return Task.FromResult<IReadOnlyList<PendingOperation>>([]);
            }

            throw new InvalidOperationException($"The Vault folder reached the engine: {targetMethod?.Name}.");
        }
    }
}
