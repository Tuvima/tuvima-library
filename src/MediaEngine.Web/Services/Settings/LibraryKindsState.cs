using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Settings;

/// <summary>
/// The Engine's list of library kinds as the signed-in Dashboard holds it, shared by the Settings frame (title and breadcrumb)
/// and the Libraries page, so the list is read once per visit and every screen draws from the same one.
/// </summary>
public sealed class LibraryKindsState(UIOrchestratorService orchestrator)
{
    /// <summary>The kinds read so far; empty until the first successful read.</summary>
    public LibraryKindCatalog Catalog { get; private set; } = LibraryKindCatalog.Empty;

    /// <summary>True once the list has been read.</summary>
    public bool Loaded { get; private set; }

    /// <summary>Raised after the list is read again.</summary>
    public event Action? Changed;

    /// <summary>Reads the list when it has not been read yet. False when the Engine could not be read.</summary>
    public Task<bool> EnsureLoadedAsync(CancellationToken ct = default) => Loaded ? Task.FromResult(true) : RefreshAsync(ct);

    /// <summary>Reads the list again (a plugin may have been switched on or off since). False when the Engine could not be read; the last list stays.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var kinds = await orchestrator.GetLibraryKindsAsync(ct);
        if (kinds is null)
        {
            return false;
        }

        Catalog = new LibraryKindCatalog(kinds);
        Loaded = true;
        Changed?.Invoke();
        return true;
    }
}
