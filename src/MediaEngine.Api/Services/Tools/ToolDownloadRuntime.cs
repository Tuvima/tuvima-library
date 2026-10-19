using System.Diagnostics;

namespace MediaEngine.Api.Services.Tools;

/// <summary>Opens downloads through the named HTTP client. The only network use of the tool installer.</summary>
public sealed class HttpToolDownloadSource(IHttpClientFactory httpClientFactory) : IToolDownloadSource
{
    public const string HttpClientName = "tool_downloads";

    public async Task<ToolDownload> OpenAsync(Uri address, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new ToolDownload(stream, response.Content.Headers.ContentLength, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}

/// <summary>Starts a program through the shell, so Windows can ask for administrator approval, and waits for it to close.</summary>
public sealed class ShellProcessLauncher : IToolProcessLauncher
{
    public async Task<int> RunAndWaitAsync(string programPath, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(programPath) { UseShellExecute = true })
            ?? throw new InvalidOperationException("The program did not start.");
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }
}
