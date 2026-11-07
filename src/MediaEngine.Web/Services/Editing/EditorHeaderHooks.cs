namespace MediaEngine.Web.Services.Editing;

/// <summary>What "All supporting files (N)" asks for: the item being edited and the scope the Engine named for the level it sits in.</summary>
public sealed record EditorSupportingFilesRequest(Guid EntityId, string? ScopeKey);

/// <summary>
/// Where the header's file rows lead (universal editor v5, 1a item 5). The header draws the rows and the folder button; what opens when one is
/// chosen belongs to the supporting-file viewer and the folder browser, which register here when they are built. A hook nobody has registered
/// does nothing, so the row never claims a window that does not exist.
/// </summary>
public sealed class EditorHeaderHooks
{
    /// <summary>A supporting file of the level was chosen: its id.</summary>
    public Func<Guid, Task>? OpenSupportingFile { get; set; }

    /// <summary>"All supporting files (N)" was chosen.</summary>
    public Func<EditorSupportingFilesRequest, Task>? OpenSupportingFiles { get; set; }

    /// <summary>"Browse files and folders" was chosen: the id of the item being edited.</summary>
    public Func<Guid, Task>? OpenFolders { get; set; }
}
