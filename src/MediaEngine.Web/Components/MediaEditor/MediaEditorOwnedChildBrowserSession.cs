using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Components.MediaEditor;

/// <summary>Keeps the local review list in place while the editor targets one child.</summary>
public sealed class MediaEditorOwnedChildBrowserSession
{
    public Guid ParentEntityId { get; set; }
    public MediaEditorOwnedChildSearchDto? Result { get; set; }
    public Guid? SelectedAssetId { get; set; }
    public string Query { get; set; } = string.Empty;
    public int? Season { get; set; }
    public int? Disc { get; set; }
    public int? Volume { get; set; }
    public string? MatchStatus { get; set; }
    public string? FileStatus { get; set; }
    public double ScrollTop { get; set; }
    public bool RefreshOnReturn { get; set; }
}
