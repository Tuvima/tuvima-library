using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using MediaEngine.Contracts.Backups;

namespace MediaEngine.Api.Services.Backups;

/// <summary>
/// S3-compatible storage: Amazon S3, MinIO, Backblaze B2, Wasabi and similar (Developer tools spec WP26). Requests are signed with
/// AWS Signature Version 4 in the path style (<c>https://endpoint/bucket/key</c>), which every S3-compatible service accepts. The
/// signing is written out here, so no outside library is needed. Only the files the Library itself wrote are touched, under the
/// optional folder the administrator chose.
/// </summary>
internal sealed class S3BackupDestination(ConfiguredDestination settings, string secretKey, IBackupHttp http) : IBackupDestination
{
    private const int MaxListPages = 1000;
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private static readonly XNamespace Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    public string Id => settings.Id;

    public string Name => settings.Name;

    public bool LeavesMachine => true;

    public bool IsConfigured => BackupDestinationRules.ParseWebAddress(settings.Location, out _) is not null
        && !string.IsNullOrWhiteSpace(settings.Bucket) && !string.IsNullOrEmpty(settings.Username) && !string.IsNullOrEmpty(secretKey);

    public async Task<DestinationTestResult> TestAsync(CancellationToken ct)
    {
        try
        {
            var probe = $".write-test-{Guid.NewGuid():N}";
            await using var content = new MemoryStream("ok"u8.ToArray());
            await SendAsync(probe, content, content.Length, ct).ConfigureAwait(false);
            await DeleteAsync(probe, ct).ConfigureAwait(false);
            return new DestinationTestResult(true, "The bucket answered and the keys were accepted.");
        }
        catch (BackupDestinationException ex)
        {
            return new DestinationTestResult(false, ex.Message);
        }
    }

    public async Task SendAsync(string name, Stream content, long length, CancellationToken ct)
    {
        var hash = await HashAsync(content, ct).ConfigureAwait(false);
        using var response = await SignedAsync(HttpMethod.Put, KeyFor(name), [], hash, ct, () =>
        {
            var body = new StreamContent(new LeaveOpenStream(content));
            body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            body.Headers.ContentLength = length;
            return body;
        }).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw BackupHttpMessages.For(response);
        }
    }

    public async Task<IReadOnlyList<DestinationBackup>> ListAsync(CancellationToken ct)
    {
        var items = new List<DestinationBackup>();
        string? token = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            // A server that keeps saying "more" with a token it already gave, or never stops, must not loop us forever.
            if (token is not null && (!seen.Add(token) || seen.Count > MaxListPages))
            {
                throw new BackupDestinationException("The destination's listing never finished.");
            }

            var query = new List<KeyValuePair<string, string>> { new("list-type", "2"), new("prefix", Prefix) };
            if (token is not null)
            {
                query.Add(new("continuation-token", token));
            }

            using var response = await SignedAsync(HttpMethod.Get, null, query, EmptyHash, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw BackupHttpMessages.For(response);
            }

            try
            {
                var document = await BackupDestinationRules.ReadXmlAsync(response, ct).ConfigureAwait(false);
                var root = document.Root ?? throw new BackupDestinationException("The destination answered with something that is not a bucket listing.");
                foreach (var entry in root.Elements().Where(element => element.Name.LocalName == "Contents"))
                {
                    var key = Value(entry, "Key");
                    if (key is null || !key.StartsWith(Prefix, StringComparison.Ordinal) || key[Prefix.Length..].Contains('/', StringComparison.Ordinal))
                    {
                        continue;
                    }

                    _ = long.TryParse(Value(entry, "Size"), CultureInfo.InvariantCulture, out var size);
                    var modified = DateTimeOffset.TryParse(Value(entry, "LastModified"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                        ? when
                        : DateTimeOffset.UnixEpoch;
                    items.Add(new DestinationBackup(key[Prefix.Length..], size, modified));
                }

                token = string.Equals(Value(root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase) ? Value(root, "NextContinuationToken") : null;
            }
            catch (System.Xml.XmlException ex)
            {
                throw new BackupDestinationException("The destination answered with something that is not a bucket listing.", ex);
            }
        }
        while (token is not null);

        return items;
    }

    public async Task<Stream> FetchAsync(string name, CancellationToken ct)
    {
        using var response = await SignedAsync(HttpMethod.Get, KeyFor(name), [], EmptyHash, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw BackupHttpMessages.For(response);
        }

        return await BackupDownload.ToTemporaryFileAsync(response, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        using var response = await SignedAsync(HttpMethod.Delete, KeyFor(name), [], EmptyHash, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 404)
        {
            throw BackupHttpMessages.For(response);
        }
    }

    public Task<long?> FreeSpaceAsync(CancellationToken ct) => Task.FromResult<long?>(null);

    private static readonly string EmptyHash = Convert.ToHexStringLower(SHA256.HashData([]));

    /// <summary>The folder inside the bucket, ending in a slash, or empty.</summary>
    private string Prefix => string.IsNullOrWhiteSpace(settings.Prefix) ? string.Empty : settings.Prefix.Trim().Trim('/') + "/";

    private string KeyFor(string name) =>
        string.IsNullOrWhiteSpace(name) || !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal)
            ? throw new BackupDestinationException("That backup name is not allowed.")
            : Prefix + name;

    private static string? Value(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value;

    private static async Task<string> HashAsync(Stream content, CancellationToken ct)
    {
        if (!content.CanSeek)
        {
            throw new BackupDestinationException("The backup could not be prepared for sending.");
        }

        content.Position = 0;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, ct).ConfigureAwait(false));
        content.Position = 0;
        return hash;
    }

    private async Task<HttpResponseMessage> SignedAsync(
        HttpMethod method,
        string? key,
        IReadOnlyList<KeyValuePair<string, string>> query,
        string payloadHash,
        CancellationToken ct,
        Func<HttpContent>? content = null)
    {
        var endpoint = BackupDestinationRules.ParseWebAddress(settings.Location, out _)
            ?? throw new BackupDestinationException("The S3 address is not valid.");
        var bucket = (settings.Bucket ?? string.Empty).Trim();
        var path = "/" + Uri.EscapeDataString(bucket) + (key is null ? string.Empty : "/" + string.Join('/', key.Split('/').Select(Uri.EscapeDataString)));
        var canonicalQuery = string.Join('&', query
            .Select(pair => (Name: Uri.EscapeDataString(pair.Key), Value: Uri.EscapeDataString(pair.Value)))
            .OrderBy(pair => pair.Name, StringComparer.Ordinal)
            .Select(pair => $"{pair.Name}={pair.Value}"));
        var now = DateTimeOffset.UtcNow;
        var stamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var host = endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}";
        var region = string.IsNullOrWhiteSpace(settings.Region) ? "us-east-1" : settings.Region.Trim();

        var canonicalRequest = string.Join('\n',
            method.Method, path, canonicalQuery,
            $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{stamp}\n",
            "host;x-amz-content-sha256;x-amz-date",
            payloadHash);
        var scope = $"{day}/{region}/s3/aws4_request";
        var toSign = $"{Algorithm}\n{stamp}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";
        var signingKey = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), day), region), "s3"), "aws4_request");
        var signature = Convert.ToHexStringLower(Hmac(signingKey, toSign));

        var url = new Uri($"{endpoint.GetLeftPart(UriPartial.Authority)}{path}{(canonicalQuery.Length > 0 ? "?" + canonicalQuery : string.Empty)}");
        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("x-amz-date", stamp);
            request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                $"{Algorithm} Credential={settings.Username}/{scope}, SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature={signature}");
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

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}
