using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What the View pages need to know about their kind's folders (View folders spec D4b, D4c): which folders a kind has (for the Folders filter) and whether it has a
/// Managed folder yet (uploads are filed into one, so Add files stays off without it). Reads the Shared Library's folders once per call; when they cannot be read
/// (not an administrator, or the Engine is unreachable) nothing is claimed: no filter is drawn and uploads are not blocked.
/// </summary>
public sealed class ViewFolderGate(IEngineApiClient api)
{
    /// <summary>The folders a person added to the kind, or an empty list when they cannot be read.</summary>
    public async Task<IReadOnlyList<ViewSourceAdminDto>> FoldersAsync(string kind, CancellationToken ct = default)
    {
        var sources = await api.GetViewSharedSourcesAsync(ct);
        return sources is null ? [] : ViewFolderRules.OfKind(sources, kind);
    }

    /// <summary>True only when the folders were read and none of the kinds has a Managed folder.</summary>
    public async Task<bool> UploadsOffAsync(IReadOnlyList<string> kinds, CancellationToken ct = default)
    {
        var sources = await api.GetViewSharedSourcesAsync(ct);
        return sources is not null && !kinds.Any(kind => ViewFolderRules.HasManagedFolder(sources, kind));
    }
}
