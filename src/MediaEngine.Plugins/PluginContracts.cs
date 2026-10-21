using System.Text.Json;

namespace MediaEngine.Plugins;

public interface ITuvimaPlugin
{
    PluginManifest Manifest { get; }
    IReadOnlyList<IPluginCapability> CreateCapabilities();
}

public interface IPluginCapability
{
    string Kind { get; }
}

public interface IPlaybackSegmentDetector : IPluginCapability
{
    bool CanAnalyze(PluginMediaAssetContext asset, IPluginExecutionContext context);
    Task<IReadOnlyList<PluginPlaybackSegment>> AnalyzeAsync(
        PluginMediaAssetContext asset,
        IPluginExecutionContext context,
        CancellationToken cancellationToken = default);
}

public interface IPluginSettingsSchemaProvider : IPluginCapability
{
    JsonElement GetSettingsSchema();
}

public interface IPluginHealthCheck : IPluginCapability
{
    Task<PluginHealthResult> GetHealthAsync(
        IPluginExecutionContext context,
        CancellationToken cancellationToken = default);
}

public interface IPluginExecutionContext : IAsyncDisposable
{
    string PluginId { get; }
    IReadOnlyDictionary<string, JsonElement> Settings { get; }
    IPluginMediaAccess Media { get; }
    IPluginHttpClient Http { get; }
    IPluginStorage Storage { get; }
    IPluginToolRuntime Tools { get; }
    IPluginAiClient Ai { get; }

    /// <summary>
    /// Read access to the secrets this plugin declared in its manifest (keys and logins an administrator
    /// entered). Needs the <c>secrets.plugin</c> permission.
    /// </summary>
    IPluginSecrets Secrets { get; }

    /// <summary>
    /// The plugin's own records in the Library's data store. Needs the <c>plugin-records</c> permission. Records are
    /// private to this plugin and bounded by <see cref="PluginLimitExceededException"/>.
    /// </summary>
    IPluginRecordStore Records { get; }
}

public interface IPluginSecrets
{
    /// <summary>
    /// Returns the value of a secret this plugin declared, or null when the name was not declared or is not set.
    /// Plugins cannot write or list secrets. Never log the returned value.
    /// </summary>
    Task<string?> GetAsync(string name, CancellationToken cancellationToken = default);
}

public interface IPluginMediaAccess
{
    bool Exists(PluginMediaAssetContext asset);
}

public interface IPluginHttpClient
{
    Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default);
}

public interface IPluginStorage
{
    string WorkingDirectory { get; }
    string GetPath(string relativePath);
    void CreateDirectory(string relativePath);
    IReadOnlyList<string> EnumerateFiles(string relativePath, string searchPattern);
    IReadOnlyList<string> ReadLines(string relativePath);
}

public interface IPluginToolRuntime
{
    Task<PluginToolResolution> ResolveToolAsync(
        string toolId,
        CancellationToken cancellationToken = default);

    Task<PluginToolRunResult> RunToolAsync(
        PluginToolResolution tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public interface IPluginAiClient
{
    Task<string?> InferTextAsync(
        string role,
        string prompt,
        PluginAiOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<T?> InferJsonAsync<T>(
        string role,
        string prompt,
        string grammar,
        PluginAiOptions? options = null,
        CancellationToken cancellationToken = default) where T : class;
}

public static class PluginPermissionIds
{
    public const string MediaRead = "media.read";
    public const string NetworkHttp = "network.http";
    public const string ProcessExecute = "process.execute";
    public const string ToolDownload = "tool.download";
    public const string AiInfer = "ai.infer";
    public const string PluginStorage = "storage.plugin";
    public const string PluginSecrets = "secrets.plugin";

    /// <summary>Keeps its own records in the Library's data store (<see cref="IPluginRecordStore"/>).</summary>
    public const string PluginRecords = "plugin-records";

    /// <summary>Runs its own recurring jobs (<see cref="IPluginScheduledJob"/>), listed in Scheduled tasks.</summary>
    public const string ScheduledWork = "scheduled-work";

    /// <summary>Is told when things happen in the Library: items added, ingestion finished, metadata changes (<see cref="IPluginEventSubscriber"/>).</summary>
    public const string LibraryEvents = "library-events";

    /// <summary>Is told what people watch, read and listen to (the <c>playback.*</c> events). <see cref="LibraryEvents"/> does not cover these.</summary>
    public const string WatchActivity = "watch-activity";

    /// <summary>Runs continuously in the background while switched on (<see cref="IPluginBackgroundService"/>).</summary>
    public const string BackgroundWork = "background-work";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        MediaRead,
        NetworkHttp,
        ProcessExecute,
        ToolDownload,
        AiInfer,
        PluginStorage,
        PluginSecrets,
        PluginRecords,
        ScheduledWork,
        LibraryEvents,
        WatchActivity,
        BackgroundWork,
    };
}
