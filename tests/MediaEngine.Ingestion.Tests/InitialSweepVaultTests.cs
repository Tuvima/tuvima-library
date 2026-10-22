using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// The first sweep of the library folders never touches a Vault library: its folders are not announced, hashed or counted
/// (Videos spec D17, D33). Hand-written spies and real temporary folders.
/// </summary>
public sealed class InitialSweepVaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_sweep_vault_{Guid.NewGuid():N}");
    private readonly string _vaultFolder;
    private readonly string _ordinaryFolder;

    public InitialSweepVaultTests()
    {
        _vaultFolder = Path.Combine(_root, "private");
        _ordinaryFolder = Path.Combine(_root, "movies");
        Directory.CreateDirectory(_vaultFolder);
        Directory.CreateDirectory(_ordinaryFolder);
        File.WriteAllText(Path.Combine(_vaultFolder, "one.mkv"), "vault");
        File.WriteAllText(Path.Combine(_ordinaryFolder, "Dune.mkv"), "ordinary");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task AVaultLibraryFolder_IsNeverAnnouncedHashedOrCounted_ButAnOrdinaryOneIs()
    {
        var hasher = new SpyHasher();
        var publisher = new SpyPublisher();
        var logger = new SpyLogger();
        var sweep = Build(hasher, publisher, logger, vaultLibraryId: "vault-lib");

        var result = await sweep.RunAsync();

        Assert.Equal(1, result.FilesDiscovered);
        Assert.Equal([Path.Combine(_ordinaryFolder, "Dune.mkv")], hasher.Paths);
        var started = Assert.Single(publisher.Payloads.OfType<InitialSweepStartedEvent>());
        Assert.Equal([_ordinaryFolder], started.Roots);
        Assert.DoesNotContain(logger.Lines, line => line.Contains("private", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Lines, line => line.Contains("one.mkv", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WhenOnlyTheVaultHasAFolder_NothingIsSweptAndNothingIsAnnounced()
    {
        var hasher = new SpyHasher();
        var publisher = new SpyPublisher();
        var sweep = Build(hasher, publisher, new SpyLogger(), vaultLibraryId: "vault-lib", includeOrdinary: false);

        var result = await sweep.RunAsync();

        Assert.Equal(0, result.FilesDiscovered);
        Assert.Empty(hasher.Paths);
        Assert.Empty(publisher.Payloads);
    }

    private InitialSweepService Build(SpyHasher hasher, SpyPublisher publisher, SpyLogger logger, string vaultLibraryId, bool includeOrdinary = true)
    {
        var folders = new List<LibraryFolderEntry>
        {
            new() { Id = vaultLibraryId, Name = "Vault", Sources = [new LibrarySourceEntry { Id = "v", Path = _vaultFolder }] },
        };
        if (includeOrdinary)
        {
            folders.Add(new LibraryFolderEntry { Id = "movies-lib", Name = "Movies", Sources = [new LibrarySourceEntry { Id = "m", Path = _ordinaryFolder }] });
        }

        return new InitialSweepService(
            hasher, new SpyCache(), publisher,
            Options.Create(new IngestionOptions { LibraryFolders = folders }),
            new MediaTypeExtensionCatalog(), logger, new SpyHook(vaultLibraryId));
    }

    private sealed class SpyHook(string vaultLibraryId) : IVaultIngestionHook
    {
        public Task<bool> IsVaultLibraryAsync(string? libraryId, CancellationToken ct = default) =>
            Task.FromResult(string.Equals(libraryId, vaultLibraryId, StringComparison.OrdinalIgnoreCase));

        public Task<bool> IsVaultAssetAsync(Guid assetId, CancellationToken ct = default) => Task.FromResult(false);

        public Task OnFileRegisteredAsync(VaultRegisteredFile file, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class SpyHasher : IAssetHasher
    {
        public List<string> Paths { get; } = [];

        public Task<HashResult> ComputeAsync(string filePath, CancellationToken ct = default)
        {
            Paths.Add(filePath);
            return Task.FromResult(new HashResult { FilePath = filePath, Hex = new string((char)97, 64), FileSize = 1, Elapsed = TimeSpan.Zero });
        }
    }

    private sealed class SpyCache : IFileHashCacheRepository
    {
        public Task<string?> TryGetAsync(string absolutePath, long sizeBytes, DateTimeOffset mtimeUtc, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task UpsertAsync(string absolutePath, long sizeBytes, DateTimeOffset mtimeUtc, string sha256, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(string absolutePath, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class SpyPublisher : IEventPublisher
    {
        public List<object> Payloads { get; } = [];

        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull
        {
            Payloads.Add(payload);
            return Task.CompletedTask;
        }
    }

    private sealed class SpyLogger : ILogger<InitialSweepService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
