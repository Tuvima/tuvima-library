using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Realtime;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.HealthChecks;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

// WP17 part A: the data readers under the shared screens treat anything in a Vault library as not existing for anyone without an
// unlocked NSFW session (Videos spec D17). Each test names one reader; the inventory is engineering/validation/vault-surface-inventory.md.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    [Fact]
    public async Task VaultAudit_WorkDetail_AnswersAVaultWorkLikeAnUnknownId()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        var service = new WorkDetailReadService(_database);

        Assert.Null(await service.GetAsync(shape.Secret.WorkId));
        Assert.Null(await service.GetAsync(Guid.NewGuid()));
        Assert.NotNull(await service.GetAsync(shape.Normal.WorkId));
    }

    [Fact]
    public async Task VaultAudit_EditorSuggestions_NeverOfferAVaultGenreOrTag()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@vault, 'genre', 0, 'Secretgenre');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@normal, 'genre', 0, 'Ordinarygenre');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@vault, 'custom_tags', 0, 'Secrettag');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@normal, 'custom_tags', 0, 'Ordinarytag');
                """,
                new { vault = shape.Secret.AssetId, normal = shape.Normal.AssetId });
        }

        var service = new EditorSuggestionReadService(_database);
        var genres = service.GetValues("genre", null, 100, CancellationToken.None);
        var tags = service.GetValues("tag", null, 100, CancellationToken.None);

        Assert.Contains("Ordinarygenre", genres);
        Assert.DoesNotContain("Secretgenre", genres);
        Assert.Contains("Ordinarytag", tags);
        Assert.DoesNotContain("Secrettag", tags);
    }

    [Fact]
    public async Task VaultAudit_WatchFolderHealth_NeverNamesAVaultFolder()
    {
        var vaultFolder = Path.Combine(Path.GetTempPath(), $"tuvima_vault_folder_{Guid.NewGuid():N}");
        var normalFolder = Path.Combine(Path.GetTempPath(), $"tuvima_normal_folder_{Guid.NewGuid():N}");
        Directory.CreateDirectory(vaultFolder);
        Directory.CreateDirectory(normalFolder);
        try
        {
            var options = Options.Create(new IngestionOptions { WatchDirectories = [vaultFolder, normalFolder] });
            var check = new WatchFolderHealthCheck(options, new FolderOnlyClassifier(vaultFolder));

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Healthy, result.Status);
            Assert.Contains(normalFolder, result.Description);
            Assert.DoesNotContain(vaultFolder, result.Description);
            Assert.DoesNotContain(vaultFolder, string.Join(",", result.Data.Values.Select(value => value.ToString())));
        }
        finally
        {
            Directory.Delete(vaultFolder, true);
            Directory.Delete(normalFolder, true);
        }
    }

    [Fact]
    public async Task VaultAudit_WatchFolderHealth_ReportsNotConfigured_WhenOnlyTheVaultHasAFolder()
    {
        var vaultFolder = Path.Combine(Path.GetTempPath(), $"tuvima_vault_only_{Guid.NewGuid():N}");
        var options = Options.Create(new IngestionOptions { WatchDirectories = [vaultFolder] });

        var result = await new WatchFolderHealthCheck(options, new FolderOnlyClassifier(vaultFolder)).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.DoesNotContain(vaultFolder, result.Description);
    }

    [Fact]
    public async Task VaultAudit_NonVaultDirectories_DropsEveryVaultFolder()
    {
        var kept = await IngestionEndpoints.NonVaultDirectoriesAsync(
            ["/watch/vault", "/watch/movies"], new FolderOnlyClassifier("/watch/vault"), CancellationToken.None);

        Assert.Equal(["/watch/movies"], kept);
    }

    [Fact]
    public async Task VaultAudit_ALockedCallerWithAMalformedQuery_GetsTheSameNotFoundAsAnyOtherVaultRequest()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var routes = await MapRoutesAsync(builder => builder.MapVaultBrowseEndpoints());
        var session = NewVaultSession(shape.Account);
        var nextCalled = false;
        var middleware = new VaultGateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await using var scope = _routeApps[^1].Services.CreateAsyncScope();
        var context = VaultContext(shape.Account, session);
        context.RequestServices = scope.ServiceProvider;
        context.SetEndpoint(routes["ListVaultScenes"]);
        context.Request.QueryString = new QueryString("?performer=not-an-id&take=many&desc=maybe");
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("private, no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task VaultAudit_AnUnlockedCaller_PassesTheGateMiddlewareOnce()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var routes = await MapRoutesAsync(builder => builder.MapVaultBrowseEndpoints());
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var nextCalled = false;
        var middleware = new VaultGateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await using var scope = _routeApps[^1].Services.CreateAsyncScope();
        var context = VaultContext(shape.Account, session);
        context.RequestServices = scope.ServiceProvider;
        context.SetEndpoint(routes["ListVaultScenes"]);
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task VaultAudit_TheGateMiddleware_LeavesOrdinaryRoutesAlone()
    {
        var nextCalled = false;
        var middleware = new VaultGateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(new DefaultHttpContext());

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task VaultAudit_ARemovedVaultLibrary_LosesItsSettingsEntryAndSoItsWatcher()
    {
        var libraryId = Guid.NewGuid().ToString("D");
        var kept = new LibraryFolderConfig { Id = Guid.NewGuid().ToString("D"), Name = "Movies" };
        var vault = new LibraryFolderConfig { Id = libraryId, Name = VaultFolderAttachmentService.LibraryName };
        var loader = new SavingLibrariesLoader(new LibrariesConfiguration { Libraries = [kept, vault] });

        await VaultLibrarySettings.RemoveEntryAsync(loader, libraryId, CancellationToken.None);

        Assert.Equal(1, loader.Saves);
        Assert.Equal([kept.Id], loader.Libraries.Libraries.Select(library => library.Id));
    }

    [Fact]
    public async Task VaultAudit_RemovingAnEntryThatIsNotThere_SavesNothing()
    {
        var loader = new SavingLibrariesLoader(new LibrariesConfiguration { Libraries = [new LibraryFolderConfig { Id = "x", Name = "Movies" }] });

        await VaultLibrarySettings.RemoveEntryAsync(loader, Guid.NewGuid().ToString("D"), CancellationToken.None);

        Assert.Equal(0, loader.Saves);
    }

    [Fact]
    public void VaultAudit_AFolderMessage_NeverNamesTheVaultEntry()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"tuvima_vault_msg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var vault = new LibraryFolderConfig
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = VaultFolderAttachmentService.LibraryName,
                Sources = [new LibrarySourceConfig { Id = Guid.NewGuid().ToString("D"), Path = folder }],
            };
            var loader = new SavingLibrariesLoader(new LibrariesConfiguration { Libraries = [vault] });
            var service = new MediaEngine.Api.Services.Settings.ServerFolderBrowserService(
                loader, Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaEngine.Api.Services.Settings.ServerFolderBrowserService>.Instance);

            var result = service.Validate(new MediaEngine.Contracts.Settings.ValidateServerFolderRequest { ManualPath = folder });

            var issue = Assert.Single(result.Issues, candidate => candidate.Code == "already_configured");
            Assert.DoesNotContain("Vault", issue.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Vault", issue.LibraryName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private sealed class SavingLibrariesLoader(LibrariesConfiguration libraries) : IConfigurationLoader
    {
        public LibrariesConfiguration Libraries { get; private set; } = libraries;
        public int Saves { get; private set; }

        public LibrariesConfiguration LoadLibraries() => Libraries;
        public void SaveLibraries(LibrariesConfiguration config) { Libraries = config; Saves++; }

        public string ConfigDirectoryPath => string.Empty;
        public CoreConfiguration LoadCore() => new();
        public void SaveCore(CoreConfiguration config) { }
        public ScoringSettings LoadScoring() => new();
        public void SaveScoring(ScoringSettings settings) { }
        public MaintenanceSettings LoadMaintenance() => new();
        public void SaveMaintenance(MaintenanceSettings settings) { }
        public HydrationSettings LoadHydration() => new();
        public void SaveHydration(HydrationSettings settings) { }
        public PipelineConfiguration LoadPipelines() => new();
        public void SavePipelines(PipelineConfiguration config) { }
        public DisambiguationSettings LoadDisambiguation() => new();
        public void SaveDisambiguation(DisambiguationSettings settings) { }
        public MediaTypeConfiguration LoadMediaTypes() => new();
        public void SaveMediaTypes(MediaTypeConfiguration config) { }
        public TranscodingSettings LoadTranscoding() => new();
        public void SaveTranscoding(TranscodingSettings settings) { }
        public FieldPriorityConfiguration LoadFieldPriorities() => new();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
        public ProviderConfiguration? LoadProvider(string name) => null;
        public void SaveProvider(ProviderConfiguration config) { }
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
        public T? LoadAi<T>() where T : class => default;
        public void SaveAi<T>(T settings) where T : class { }
        public PaletteConfiguration LoadPalette() => new();
        public void SavePalette(PaletteConfiguration palette) { }
    }

    private sealed class FolderOnlyClassifier(string vaultFolder) : IVaultPathClassifier
    {
        public ValueTask<bool> IsVaultPathAsync(string? path, CancellationToken ct = default) =>
            ValueTask.FromResult(string.Equals(path, vaultFolder, StringComparison.OrdinalIgnoreCase));
    }
}
