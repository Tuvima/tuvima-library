using System.Diagnostics;
using System.Net;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Services.Tools;

/// <summary>
/// Opens downloads through the named HTTP client, which must not follow redirects itself. GitHub hands release and raw
/// files on to its own storage hosts, so redirects are followed here by hand: only to the short list in
/// <see cref="ToolDownloadSettings.AllowedRedirectHosts"/>, only over https, and for a few hops at most. The only
/// network use of the tool installer.
/// </summary>
public sealed class HttpToolDownloadSource(IHttpClientFactory httpClientFactory) : IToolDownloadSource
{
    public const string HttpClientName = "tool_downloads";

    /// <summary>Most redirects followed before the download is refused.</summary>
    public const int MaxRedirects = 5;

    public async Task<ToolDownload> OpenAsync(Uri address, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var current = address;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!ToolDownloadSettings.IsAllowedRedirectAddress(current.ToString()))
            {
                throw new ToolDownloadRefusedException($"A download address on {current.Host} is not allowed.");
            }

            var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            try
            {
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location
                        ?? throw new ToolDownloadRefusedException("A redirect did not say where to go.");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    response.Dispose();
                    continue;
                }

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

        throw new ToolDownloadRefusedException("The download was redirected too many times.");
    }
}

/// <summary>Starts a program through the shell, so Windows can ask for administrator approval, and waits for it to close.</summary>
public sealed class ShellProcessLauncher : IToolProcessLauncher
{
    public async Task<int> RunAndWaitAsync(string programPath, Action started, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(programPath) { UseShellExecute = true })
            ?? throw new InvalidOperationException("The program did not start.");
        started();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }
}
