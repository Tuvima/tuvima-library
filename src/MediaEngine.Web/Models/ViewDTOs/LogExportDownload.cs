namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>A finished log export on its way to the browser: the file name the Engine chose and the file itself.</summary>
public sealed record LogExportDownload(string FileName, byte[] Content);
