using System.Text.Json;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Sandbox;

/// <summary>
/// A plugin whose code is a WebAssembly module (kind <c>sandboxed</c>). The module runs inside a sandbox with limits on
/// memory, steps and time and can reach nothing but the plugin services the plugin was approved for.
/// </summary>
internal sealed class SandboxPlugin(PluginManifest manifest, SandboxDefinition definition) : ITuvimaPlugin
{
    public PluginManifest Manifest { get; } = manifest;

    public SandboxDefinition Definition { get; } = definition;

    public IReadOnlyList<IPluginCapability> CreateCapabilities() =>
        [.. Definition.Jobs.Select(job => new SandboxJob(Definition, job))];

    /// <summary>Builds the plugin for a manifest, or returns the plain reason it cannot be built.</summary>
    public static (ITuvimaPlugin? Plugin, string? Error) TryCreate(PluginManifest manifest, string? pluginDirectory)
    {
        var (definition, error) = SandboxPluginParser.Parse(manifest, pluginDirectory);
        return definition is null ? (null, error) : (new SandboxPlugin(manifest, definition), null);
    }
}

/// <summary>
/// One recurring job of a sandboxed plugin. It runs the module's function with the plugin's settings, keeps the module's
/// log lines and result as the plugin's own record <c>sandbox/&lt;job&gt;</c>, and saves any <c>records</c> the result lists.
/// </summary>
internal sealed class SandboxJob(SandboxDefinition definition, SandboxJobDefinition job) : IPluginScheduledJob
{
    public const string RecordCollection = "sandbox";
    private const int MaxLogLines = 50;
    private const int MaxRecordsPerRun = 100;

    // Compiling and running a module is heavy; a few at a time is plenty for scheduled jobs and keeps the Engine responsive.
    private static readonly SemaphoreSlim RunSlots = new(2, 2);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string Kind => PluginCapabilityKinds.ScheduledJobKind;

    public string Key => $"sandbox-{job.Key}";

    public string DisplayName => job.Name;

    public PluginSchedule DefaultSchedule => job.Schedule;

    public async Task RunAsync(IPluginExecutionContext context, CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Serialize(new { plugin_id = context.PluginId, job = job.Key, settings = context.Settings }, Json);
        var host = new RunHost(context);

        // The module is plain computation, so it runs on its own thread and never holds up the caller.
        string? result;
        await RunSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            result = await Task.Factory.StartNew(
                () => WasmModuleRunner.Run(definition.ModulePath, job.Export, input, definition, host, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);
        }
        finally
        {
            RunSlots.Release();
        }

        await TrySaveAsync(context, RecordCollection, job.Key, JsonSerializer.Serialize(new { ran_at = DateTimeOffset.UtcNow, log = host.Lines, result }, Json), cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            return;
        }

        using var document = ParseResult(result);
        if (document is null || !document.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var record in records.EnumerateArray().Take(MaxRecordsPerRun))
        {
            if (record.ValueKind == JsonValueKind.Object
                && record.TryGetProperty("collection", out var collection) && collection.ValueKind == JsonValueKind.String
                && !string.Equals(collection.GetString(), RecordCollection, StringComparison.OrdinalIgnoreCase)
                && record.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String
                && record.TryGetProperty("json", out var value))
            {
                await TrySaveAsync(context, collection.GetString()!, key.GetString()!, value.GetRawText(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static JsonDocument? ParseResult(string result)
    {
        try
        {
            return JsonDocument.Parse(result);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Saving needs the plugin-records permission. Without it the run still counts; the output is simply not kept.
    private static async Task TrySaveAsync(IPluginExecutionContext context, string collection, string key, string json, CancellationToken cancellationToken)
    {
        try
        {
            await context.Records.PutAsync(collection, key, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or PluginLimitExceededException)
        {
            // Not approved to keep records, or the module listed one the record store refuses: that record is skipped.
        }
    }

    private sealed class RunHost(IPluginExecutionContext context) : ISandboxHost
    {
        public List<string> Lines { get; } = [];

        public void Log(string message)
        {
            if (Lines.Count < MaxLogLines)
            {
                Lines.Add(message.Length > 500 ? message[..500] : message);
            }
        }

        public byte[]? HttpGet(string url, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                Log($"http_get refused: {url} is not a web address.");
                return null;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = context.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > WasmModuleRunner.MaxHttpBytes)
                {
                    Log($"http_get: {uri.Host} answered {(int)response.StatusCode}.");
                    return null;
                }

                using var stream = response.Content.ReadAsStreamAsync(cancellationToken).GetAwaiter().GetResult();
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > WasmModuleRunner.MaxHttpBytes)
                    {
                        Log("http_get: the answer was larger than 1 MB and was dropped.");
                        return null;
                    }
                }

                return buffer.ToArray();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or HttpRequestException or PluginLimitExceededException or OperationCanceledException)
            {
                Log($"http_get refused or failed: {ex.Message}");
                return null;
            }
        }
    }
}
