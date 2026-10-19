using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MediaEngine.Api.DevSupport;

public enum ProviderAnswerMode
{
    Record,
    Replay,
}

/// <summary>
/// Record-and-replay of every outside answer for the ingestion baseline (development builds only). A recording run
/// saves each outside response under the run folder; a replaying run answers the same requests from that folder and
/// never reaches the network, so two replays of one build differ only where our own code does. Layout:
/// <c>entries/&lt;key-hash&gt;.json</c> (method, URL with keys removed, status, a few headers, body hash) and
/// <c>bodies/&lt;sha256&gt;.bin</c>. No saved answer may hold a key: key-like query values and token fields are
/// replaced, every key value found in the run's copied config is scrubbed, and <see cref="FindLeaks"/> checks the folder
/// before the copied keys are deleted.
/// </summary>
public sealed partial class ProviderAnswerStore
{
    public const string ModeVariable = "TUVIMA_PROVIDER_ANSWERS_MODE";
    public const string FolderVariable = "TUVIMA_PROVIDER_ANSWERS_DIR";
    public const string MissLogVariable = "TUVIMA_PROVIDER_ANSWERS_MISSES";
    public const string Redacted = "REDACTED";
    public const string EntriesFolder = "entries";
    public const string BodiesFolder = "bodies";
    public const string MissReason = "Not in the recorded provider answers";

    public const long MaxRecordedBytes = 64L * 1024 * 1024;
    private const int MinimumSecretLength = 6;

    private static readonly string[] KeptHeaders =
    [
        "Content-Type", "Content-Disposition", "Content-Language", "Location", "Retry-After", "Last-Modified", "ETag", "Link",
    ];

    private static readonly JsonSerializerOptions EntryJson = new() { WriteIndented = true };

    private readonly ConcurrentDictionary<string, object> _keyLocks = new(StringComparer.Ordinal);
    private readonly object _missGate = new();
    private readonly IReadOnlyList<string> _secrets;
    private readonly string? _missLog;

    public ProviderAnswerStore(ProviderAnswerMode mode, string folder, IEnumerable<string> secrets, string? missLog = null)
    {
        Mode = mode;
        Folder = Path.GetFullPath(folder);
        _missLog = missLog;

        // Longest first so a key that contains another is scrubbed whole; URL-escaped forms too, as they appear in URLs.
        _secrets = secrets
            .Where(secret => secret.Length >= MinimumSecretLength)
            .SelectMany(secret => new[] { secret, Uri.EscapeDataString(secret) })
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(secret => secret.Length)
            .ToList();

        if (mode == ProviderAnswerMode.Record)
        {
            Directory.CreateDirectory(Path.Combine(Folder, EntriesFolder));
            Directory.CreateDirectory(Path.Combine(Folder, BodiesFolder));
        }
        else if (!Directory.Exists(Path.Combine(Folder, EntriesFolder)))
        {
            throw new DirectoryNotFoundException($"No recorded provider answers at {Folder}. Record a run first.");
        }
    }

    public ProviderAnswerMode Mode { get; }

    public string Folder { get; }

    /// <summary>Reads mode and folder from the environment the baseline run sets; null when neither is set.</summary>
    public static ProviderAnswerStore? FromEnvironment(string configDirectory)
    {
        var modeText = Environment.GetEnvironmentVariable(ModeVariable);
        var folder = Environment.GetEnvironmentVariable(FolderVariable);
        if (string.IsNullOrWhiteSpace(modeText) && string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(modeText) || string.IsNullOrWhiteSpace(folder)
            || !Enum.TryParse<ProviderAnswerMode>(modeText, ignoreCase: true, out var mode))
        {
            throw new InvalidOperationException($"{ModeVariable} must be 'record' or 'replay' and {FolderVariable} must name a folder.");
        }

        return new ProviderAnswerStore(mode, folder, LoadSecrets(configDirectory), Environment.GetEnvironmentVariable(MissLogVariable));
    }

    /// <summary>Every string value in the config folder's key files (<c>secrets/</c> and <c>.secrets/</c>).</summary>
    public static IReadOnlyList<string> LoadSecrets(string configDirectory)
    {
        var values = new List<string>();
        foreach (var name in new[] { "secrets", ".secrets" })
        {
            var folder = Path.Combine(configDirectory, name);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories))
            {
                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(File.ReadAllText(file));
                }
                catch (JsonException)
                {
                    // An unreadable key file still must not leak: treat its whole text as one secret.
                    values.Add(File.ReadAllText(file).Trim());
                    continue;
                }

                CollectStrings(node, values);
            }
        }

        return values.Where(value => value.Length >= MinimumSecretLength).Distinct(StringComparer.Ordinal).ToList();
    }

    private static void CollectStrings(JsonNode? node, List<string> values)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    CollectStrings(child, values);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    CollectStrings(child, values);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                values.Add(text.Trim());
                break;
        }
    }

    // -- keys and scrubbing -----------------------------------------------------------------------------------

    /// <summary>Method, URL without key values, and a hash of the request body without key values.</summary>
    public string KeyFor(HttpMethod method, Uri uri, byte[]? body)
    {
        var bodyHash = body is { Length: > 0 }
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Scrub(Encoding.UTF8.GetString(body)))))
            : "-";
        return $"{method.Method} {RedactUrl(uri)} {bodyHash}";
    }

    public string RedactUrl(Uri uri)
    {
        var text = uri.GetLeftPart(UriPartial.Path);
        var query = uri.Query.TrimStart('?');
        if (query.Length > 0)
        {
            var parts = query.Split('&').Select(part =>
            {
                var equals = part.IndexOf('=', StringComparison.Ordinal);
                var name = equals < 0 ? part : part[..equals];
                return equals >= 0 && KeyParameter().IsMatch(Uri.UnescapeDataString(name)) ? $"{name}={Redacted}" : part;
            });
            text += "?" + string.Join('&', parts);
        }

        return Scrub(text);
    }

    /// <summary>Replaces every known key value and every token field with <see cref="Redacted"/>.</summary>
    public string Scrub(string text)
    {
        foreach (var secret in _secrets)
        {
            text = text.Replace(secret, Redacted, StringComparison.Ordinal);
        }

        text = TokenField().Replace(text, match => $"\"{match.Groups[1].Value}\":\"{Redacted}\"");
        return FormKeyField().Replace(text, match => $"{match.Groups[1].Value}={Redacted}");
    }

    private static bool IsText(string? mediaType) =>
        mediaType is not null
        && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase));

    // -- record ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Saves one answer. The first answer for a key is kept, except that a rate-limit or server failure is replaced by a
    /// later real answer, so a replay never repeats a passing outage the recording happened to hit.
    /// </summary>
    public async Task RecordAsync(string key, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxRecordedBytes)
        {
            return;
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (body.LongLength > MaxRecordedBytes)
        {
            return;
        }

        var entryPath = EntryPath(key);
        lock (_keyLocks.GetOrAdd(key, _ => new object()))
        {
            if (File.Exists(entryPath) && !IsPassingFailure(ReadEntry(entryPath)?.Status ?? 0))
            {
                return;
            }

            var stored = IsText(response.Content.Headers.ContentType?.MediaType)
                ? Encoding.UTF8.GetBytes(Scrub(Encoding.UTF8.GetString(body)))
                : body;
            var bodyHash = Convert.ToHexString(SHA256.HashData(stored)).ToLowerInvariant();
            var bodyPath = Path.Combine(Folder, BodiesFolder, bodyHash + ".bin");
            if (!File.Exists(bodyPath))
            {
                WriteAtomically(bodyPath, stored);
            }

            var headers = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in KeptHeaders)
            {
                if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                {
                    headers[name] = values.Select(Scrub).ToList();
                }
            }

            var entry = new ProviderAnswer
            {
                Key = key,
                Status = (int)response.StatusCode,
                Headers = headers,
                Body = bodyHash,
            };
            WriteAtomically(entryPath, JsonSerializer.SerializeToUtf8Bytes(entry, EntryJson));
        }
    }

    private static bool IsPassingFailure(int status) => status is 408 or 429 || status >= 500;

    private static void WriteAtomically(string path, byte[] bytes)
    {
        // The Engine is stopped by killing it, so each file is written whole or not at all.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }

    // -- replay ---------------------------------------------------------------------------------------------

    /// <summary>The recorded answer, or null (and a line in the miss log) when this request was never recorded.</summary>
    public HttpResponseMessage? Replay(string key, HttpRequestMessage request)
    {
        var entry = ReadEntry(EntryPath(key));
        var bodyPath = entry?.Body is null ? null : Path.Combine(Folder, BodiesFolder, entry.Body + ".bin");
        if (entry is null || bodyPath is null || !File.Exists(bodyPath))
        {
            NoteMiss(key);
            return null;
        }

        var content = new ByteArrayContent(File.ReadAllBytes(bodyPath));
        var response = new HttpResponseMessage((HttpStatusCode)entry.Status) { RequestMessage = request, Content = content };
        foreach (var (name, values) in entry.Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, values))
            {
                content.Headers.TryAddWithoutValidation(name, values);
            }
        }

        return response;
    }

    private void NoteMiss(string key)
    {
        if (string.IsNullOrWhiteSpace(_missLog))
        {
            return;
        }

        lock (_missGate)
        {
            File.AppendAllText(_missLog, key + Environment.NewLine);
        }
    }

    private string EntryPath(string key) =>
        Path.Combine(Folder, EntriesFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".json");

    private static ProviderAnswer? ReadEntry(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ProviderAnswer>(File.ReadAllBytes(path));
    }

    // -- checks ---------------------------------------------------------------------------------------------

    public static int CountEntries(string folder)
    {
        var entries = Path.Combine(folder, EntriesFolder);
        return Directory.Exists(entries) ? Directory.GetFiles(entries, "*.json").Length : 0;
    }

    /// <summary>Saved files that contain any of the given key values (as text or URL-escaped). Must be empty.</summary>
    public static IReadOnlyList<string> FindLeaks(string folder, IEnumerable<string> secrets)
    {
        var needles = secrets
            .Where(secret => secret.Length >= MinimumSecretLength)
            .SelectMany(secret => new[] { secret, Uri.EscapeDataString(secret) })
            .Distinct(StringComparer.Ordinal)
            .Select(Encoding.UTF8.GetBytes)
            .ToList();
        if (needles.Count == 0 || !Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(file =>
            {
                var bytes = File.ReadAllBytes(file);
                return needles.Any(needle => bytes.AsSpan().IndexOf(needle) >= 0);
            })
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public static string Describe(ProviderAnswerMode mode, string folder, int misses) =>
        string.Create(CultureInfo.InvariantCulture, $"{mode.ToString().ToLowerInvariant()}: {CountEntries(folder)} answers, {misses} not recorded, {folder}");

    [GeneratedRegex(@"^(api[_-]?key|apikey|key|token|access[_-]?token|auth|authorization|password|passwd|secret|client[_-]?secret|signature|sig)$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyParameter();

    [GeneratedRegex(@"""(token|access_token|refresh_token|id_token|jwt|apikey|api_key|pin)""\s*:\s*""[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex TokenField();

    [GeneratedRegex(@"(?<=^|[?&])(api[_-]?key|apikey|token|access[_-]?token|password|secret|client[_-]?secret)=[^&\s""]*", RegexOptions.IgnoreCase)]
    private static partial Regex FormKeyField();
}

/// <summary>One saved answer on disk.</summary>
public sealed class ProviderAnswer
{
    public string Key { get; set; } = string.Empty;

    public int Status { get; set; }

    public SortedDictionary<string, List<string>> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Body { get; set; }
}

/// <summary>
/// Sits outermost on every Engine HTTP client while a baseline run asks for it. Recording passes each request to the
/// network and saves the answer; replaying answers from the saved folder and returns 503 for anything never recorded.
/// Requests to this machine (loopback) always pass through.
/// </summary>
public sealed class ProviderAnswerHandler(ProviderAnswerStore store) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } uri || uri.IsLoopback)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        byte[]? body = null;
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        var key = store.KeyFor(request.Method, uri, body);
        if (store.Mode == ProviderAnswerMode.Replay)
        {
            return store.Replay(key, request)
                ?? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    RequestMessage = request,
                    ReasonPhrase = ProviderAnswerStore.MissReason,
                    Content = new ByteArrayContent([]),
                };
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is not > ProviderAnswerStore.MaxRecordedBytes)
        {
            await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
            await store.RecordAsync(key, response, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }
}
