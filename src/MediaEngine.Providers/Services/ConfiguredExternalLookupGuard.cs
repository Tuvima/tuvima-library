using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Answers <see cref="IExternalLookupGuard"/> from the libraries and library kinds the Engine is configured with, read fresh on each
/// question so a changed setting takes effect at once. The same rule the ingestion pipeline uses
/// (<see cref="LibraryKindLookupPolicy"/>) decides, so a worker can never disagree with ingestion about a library.
/// </summary>
public sealed class ConfiguredExternalLookupGuard : IExternalLookupGuard
{
    private readonly IConfigurationLoader _configuration;
    private readonly IMediaAssetRepository _assets;
    private readonly ILogger<ConfiguredExternalLookupGuard> _logger;
    private readonly ILibraryKindSource? _kindSource;

    public ConfiguredExternalLookupGuard(
        IConfigurationLoader configuration,
        IMediaAssetRepository assets,
        ILogger<ConfiguredExternalLookupGuard> logger,
        ILibraryKindSource? kindSource = null)
    {
        _kindSource = kindSource;
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(logger);
        _configuration = configuration;
        _assets = assets;
        _logger = logger;
    }

    public bool BlocksLibrary(string? libraryId)
    {
        if (string.IsNullOrWhiteSpace(libraryId))
        {
            return false;
        }

        try
        {
            var library = _configuration.LoadLibraries().Libraries.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, libraryId, StringComparison.OrdinalIgnoreCase));
            return library is not null
                && LibraryKindLookupPolicy.BypassesExternalIdentity(library, Kinds());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the settings of library {LibraryId}; no outside provider is asked about its files.", libraryId);
            return true;
        }
    }

    public IReadOnlyCollection<string> GetBlockedLibraryIds()
    {
        var kinds = Kinds();
        return _configuration.LoadLibraries().Libraries
            .Where(library => !string.IsNullOrWhiteSpace(library.Id)
                && LibraryKindLookupPolicy.BypassesExternalIdentity(library, kinds))
            .Select(library => library.Id)
            .ToArray();
    }

    // The merged list (built-in kinds plus switched-on plugins' kinds), so a plugin's plain kind is plain here too.
    private IReadOnlyList<LibraryKindDefinition> Kinds() => _kindSource?.GetKinds() ?? _configuration.LoadLibraryKinds().Kinds;

    /// <summary>
    /// Only a media asset id or a work id is resolved (the work through its first file). Every caller passes one of those: identity jobs
    /// are for assets or works, and the sweeps pick assets. An id that is neither (a person, a character) is not blocked, which is what
    /// those callers have always done; it is logged at debug level so a new kind of caller is noticed.
    /// </summary>
    public async Task<bool> BlocksEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        if (entityId == Guid.Empty)
        {
            return false;
        }

        var asset = await _assets.FindByIdAsync(entityId, ct).ConfigureAwait(false)
            ?? await _assets.FindFirstByWorkIdAsync(entityId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            _logger.LogDebug("{EntityId} is neither a media asset nor a work with a file, so it is not held back by a library's lookup rule.", entityId);
            return false;
        }

        return BlocksLibrary(asset.LibraryId);
    }
}
