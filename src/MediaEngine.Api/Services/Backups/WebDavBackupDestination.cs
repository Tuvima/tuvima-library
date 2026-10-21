using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using MediaEngine.Contracts.Backups;

namespace MediaEngine.Api.Services.Backups;

/// <summary>
/// A WebDAV folder, such as Nextcloud or a NAS (Developer tools spec WP26). It only ever does the five things a destination can do:
/// <c>PROPFIND</c> to test and list, <c>PUT</c> to send, <c>GET</c> to fetch, <c>DELETE</c> to tidy up, and <c>MKCOL</c> to make the
/// folder when it is missing. The login is sent as Basic authentication to the one address the administrator confirmed.
/// </summary>
internal sealed class WebDavBackupDestination(ConfiguredDestination settings, string password, IBackupHttp http) : IBackupDestination
{
    private static readonly HttpMethod PropFind = new("PROPFIND");
    private static readonly HttpMethod MkCol = new("MKCOL");
    private static readonly XNamespace Dav = "DAV:";

    public string Id => settings.Id;

    public string Name => settings.Name;

    public bool LeavesMachine => true;

    public bool IsConfigured => BackupDestinationRules.ParseWebAddress(settings.Location, out _) is not null
        && !string.IsNullOrEmpty(settings.Username) && !string.IsNullOrEmpty(password);

    public async Task<DestinationTestResult> TestAsync(CancellationToken ct)
    {
        try
        {
            using (var first = await SendAsync(PropFind, UrlFor(null), ct, depth: "0").ConfigureAwait(false))
            {
                if ((int)first.StatusCode == 404)
                {
                    // The folder is made on first use, so a test of a new destination makes it too.
                    using var made = await SendAsync(MkCol, UrlFor(null), ct).ConfigureAwait(false);
                    if (!made.IsSuccessStatusCode)
                    {
                        return new DestinationTestResult(false, BackupHttpMessages.For(made).Message);
                    }
                }
                else if (!first.IsSuccessStatusCode && (int)first.StatusCode != 207)
                {
                    return new DestinationTestResult(false, BackupHttpMessages.For(first).Message);
                }
            }

            using var again = await SendAsync(PropFind, UrlFor(null), ct, depth: "0").ConfigureAwait(false);
            if (!again.IsSuccessStatusCode && (int)again.StatusCode != 207)
            {
                return new DestinationTestResult(false, BackupHttpMessages.For(again).Message);
            }

            return new DestinationTestResult(true, "The WebDAV folder answered and the login was accepted.");
        }
        catch (BackupDestinationException ex)
        {
            return new DestinationTestResult(false, ex.Message);
        }
    }

    public async Task SendAsync(string name, Stream content, long length, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (content.CanSeek)
            {
                content.Position = 0;
            }

            using var response = await SendAsync(HttpMethod.Put, UrlFor(Safe(name)), ct, content: () => Octets(content, length)).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            // A missing folder answers 404 or 409 on the first copy: make it once and try again.
            if (attempt == 0 && (int)response.StatusCode is 404 or 409 && content.CanSeek)
            {
                using var made = await SendAsync(MkCol, UrlFor(null), ct).ConfigureAwait(false);
                if (made.IsSuccessStatusCode || (int)made.StatusCode == 405)
                {
                    continue;
                }
            }

            throw BackupHttpMessages.For(response);
        }
    }

    public async Task<IReadOnlyList<DestinationBackup>> ListAsync(CancellationToken ct)
    {
        using var response = await SendAsync(PropFind, UrlFor(null), ct, depth: "1").ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 207)
        {
            throw BackupHttpMessages.For(response);
        }

        try
        {
            var document = await BackupDestinationRules.ReadXmlAsync(response, ct).ConfigureAwait(false);
            var items = new List<DestinationBackup>();
            foreach (var entry in document.Descendants(Dav + "response"))
            {
                var href = entry.Element(Dav + "href")?.Value;
                var properties = entry.Descendants(Dav + "prop").FirstOrDefault();
                if (href is null || properties is null || properties.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null)
                {
                    continue;
                }

                var name = Uri.UnescapeDataString(href.TrimEnd('/').Split('/')[^1]);
                _ = long.TryParse(properties.Element(Dav + "getcontentlength")?.Value, CultureInfo.InvariantCulture, out var size);
                var modified = DateTimeOffset.TryParse(properties.Element(Dav + "getlastmodified")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                    ? when
                    : DateTimeOffset.UnixEpoch;
                items.Add(new DestinationBackup(name, size, modified));
            }

            return items;
        }
        catch (System.Xml.XmlException ex)
        {
            throw new BackupDestinationException("The destination answered with something that is not a WebDAV folder listing.", ex);
        }
    }

    public async Task<Stream> FetchAsync(string name, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, UrlFor(Safe(name)), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw BackupHttpMessages.For(response);
        }

        return await BackupDownload.ToTemporaryFileAsync(response, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, UrlFor(Safe(name)), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 404)
        {
            throw BackupHttpMessages.For(response);
        }
    }

    public async Task<long?> FreeSpaceAsync(CancellationToken ct)
    {
        try
        {
            using var request = Build(PropFind, UrlFor(null), "0");
            request.Content = new StringContent(
                "<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:quota-available-bytes/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && (int)response.StatusCode != 207)
            {
                return null;
            }

            var value = (await BackupDestinationRules.ReadXmlAsync(response, ct).ConfigureAwait(false))
                .Descendants(Dav + "quota-available-bytes").FirstOrDefault()?.Value;
            return long.TryParse(value, CultureInfo.InvariantCulture, out var bytes) && bytes >= 0 ? bytes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or BackupDestinationException or TaskCanceledException)
        {
            // Not every server reports free space; the answer is simply unknown.
            return null;
        }
    }

    private static HttpContent Octets(Stream stream, long length)
    {
        var content = new StreamContent(new LeaveOpenStream(stream));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = length;
        return content;
    }

    private Uri UrlFor(string? file)
    {
        var uri = BackupDestinationRules.ParseWebAddress(settings.Location, out _)
            ?? throw new BackupDestinationException("The WebDAV address is not valid.");
        var path = uri.AbsolutePath.TrimEnd('/') + "/" + (file is null ? string.Empty : Uri.EscapeDataString(file));
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + path);
    }

    private static string Safe(string name) =>
        string.IsNullOrWhiteSpace(name) || !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal)
            ? throw new BackupDestinationException("That backup name is not allowed.")
            : name;

    private HttpRequestMessage Build(HttpMethod method, Uri url, string? depth)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{password}")));
        if (depth is not null)
        {
            request.Headers.TryAddWithoutValidation("Depth", depth);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, CancellationToken ct, string? depth = null, Func<HttpContent>? content = null)
    {
        try
        {
            using var request = Build(method, url, depth);
            if (content is not null)
            {
                request.Content = content();
            }

            return await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw BackupHttpMessages.Unreachable(ex);
        }
    }
}

/// <summary>Saves a downloaded backup to a temporary file that removes itself when it is closed, so a restore or a check never holds a whole backup in memory.</summary>
internal static class BackupDownload
{
    public static async Task<Stream> ToTemporaryFileAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-fetch-{Guid.NewGuid():N}.tmp");
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
