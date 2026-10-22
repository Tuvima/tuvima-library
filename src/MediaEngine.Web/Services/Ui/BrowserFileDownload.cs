using MediaEngine.Web.Services.Integration;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Ui;

/// <summary>Hands a file the Engine made to the browser, which saves it (the same <c>tuvimaDownloads.fromStream</c> the backup and log exports use).</summary>
public static class BrowserFileDownload
{
    public static async Task SaveZipAsync(this IJSRuntime js, DashboardDownloadedFile file)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(file);
        await using var stream = new MemoryStream(file.Content, writable: false);
        using var reference = new DotNetStreamReference(stream);
        await js.InvokeVoidAsync("tuvimaDownloads.fromStream", file.FileName, reference, "application/zip");
    }
}
