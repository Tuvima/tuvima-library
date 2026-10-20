using System.Text.Json;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

public interface IPluginExecutionContextFactory
{
    IPluginExecutionContext Create(string pluginId, string purpose);
    IPluginExecutionContext CreateForMedia(string pluginId, string purpose, PluginMediaAssetContext asset);
}

internal interface IPluginPermissionGate
{
    PluginRegistration Demand(string pluginId, string permission);
    PluginToolRequirement DemandTool(string pluginId, string toolId);
    PluginAiPermission DemandAi(string pluginId, string role, PluginAiOptions? options);
}

internal sealed class PluginPermissionGate(PluginCatalog catalog) : IPluginPermissionGate
{
    public PluginRegistration Demand(string pluginId, string permission)
    {
        if (!PluginPermissionIds.All.Contains(permission))
        {
            throw new UnauthorizedAccessException($"Unknown plugin permission '{permission}'.");
        }

        var registration = catalog.Get(pluginId)
            ?? throw new UnauthorizedAccessException("The plugin is not registered.");
        if (!registration.Enabled || registration.LoadError is not null)
        {
            throw new UnauthorizedAccessException("The plugin is disabled or unavailable.");
        }

        if (!registration.Manifest.Permissions.Contains(permission, StringComparer.Ordinal))
        {
            throw new UnauthorizedAccessException($"Plugin '{registration.Manifest.Id}' is not allowed to use '{permission}'.");
        }

        return registration;
    }

    public PluginToolRequirement DemandTool(string pluginId, string toolId)
    {
        var registration = Demand(pluginId, PluginPermissionIds.ProcessExecute);
        return registration.Manifest.ToolRequirements.SingleOrDefault(requirement =>
                   string.Equals(requirement.Id, toolId, StringComparison.Ordinal))
               ?? throw new UnauthorizedAccessException($"Plugin '{registration.Manifest.Id}' has no declared tool '{toolId}'.");
    }

    public PluginAiPermission DemandAi(string pluginId, string role, PluginAiOptions? options)
    {
        var registration = Demand(pluginId, PluginPermissionIds.AiInfer);
        var permission = registration.Manifest.AiPermissions.SingleOrDefault(candidate =>
            string.Equals(candidate.Role, role, StringComparison.Ordinal));
        if (permission is null)
        {
            throw new UnauthorizedAccessException($"Plugin '{registration.Manifest.Id}' is not allowed to use AI role '{role}'.");
        }

        if (options?.MaxTokens is { } requestedTokens &&
            (requestedTokens <= 0 || permission.MaxTokens <= 0 || requestedTokens > permission.MaxTokens))
        {
            throw new UnauthorizedAccessException("The plugin AI request exceeds its declared token limit.");
        }

        if (options is { } requested &&
            !string.Equals(requested.ResourceClass, permission.ResourceClass, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The plugin AI request uses an undeclared resource class.");
        }

        if (options is { } scheduled &&
            !string.Equals(scheduled.Schedule, permission.Schedule, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The plugin AI request uses an undeclared schedule.");
        }

        if (string.Equals(options?.Schedule, "on-demand", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(role, "text_scholar", StringComparison.OrdinalIgnoreCase)
                || string.Equals(permission.ResourceClass, "ai-heavy", StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnauthorizedAccessException("Heavy AI roles cannot be used by on-demand plugin calls.");
        }

        return permission;
    }
}

internal sealed class PluginExecutionContextFactory(
    PluginCatalog catalog,
    IPluginPermissionGate permissions,
    PluginToolRuntime tools,
    PluginAiClient ai,
    IHttpClientFactory httpClients,
    IConfigSecretsStore secrets) : IPluginExecutionContextFactory
{
    public IPluginExecutionContext Create(string pluginId, string purpose) => CreateCore(pluginId, purpose, null);

    public IPluginExecutionContext CreateForMedia(string pluginId, string purpose, PluginMediaAssetContext asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        permissions.Demand(pluginId, PluginPermissionIds.MediaRead);
        return CreateCore(pluginId, purpose, asset);
    }

    private IPluginExecutionContext CreateCore(string pluginId, string purpose, PluginMediaAssetContext? asset)
    {
        var registration = catalog.Get(pluginId)
            ?? throw new UnauthorizedAccessException("The plugin is not registered.");
        if (!registration.Enabled || registration.LoadError is not null)
        {
            throw new UnauthorizedAccessException("The plugin is disabled or unavailable.");
        }

        var root = Path.Combine(Path.GetTempPath(), "tuvima-plugins", SafeSegment(registration.Manifest.Id, nameof(pluginId)), SafeSegment(purpose, nameof(purpose)), Guid.NewGuid().ToString("N"));
        var storage = new BoundPluginStorage(registration.Manifest.Id, root, permissions);
        return new PluginExecutionContext(
            registration.Manifest.Id,
            registration.Settings,
            new BoundPluginMediaAccess(registration.Manifest.Id, asset, permissions),
            new BoundPluginHttpClient(registration.Manifest.Id, permissions, httpClients),
            storage,
            new BoundPluginToolRuntime(registration.Manifest.Id, registration.Settings, storage, permissions, tools),
            new BoundPluginAiClient(registration.Manifest.Id, permissions, ai),
            new BoundPluginSecrets(registration.Manifest.Id, permissions, secrets));
    }

    private static string SafeSegment(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
        {
            throw new ArgumentException("Plugin context path segments must be simple names.", parameter);
        }

        return value;
    }
}

internal sealed class PluginExecutionContext(
    string pluginId,
    IReadOnlyDictionary<string, JsonElement> settings,
    IPluginMediaAccess media,
    IPluginHttpClient http,
    IPluginStorage storage,
    IPluginToolRuntime tools,
    IPluginAiClient ai,
    IPluginSecrets secrets) : IPluginExecutionContext
{
    public string PluginId { get; } = pluginId;
    public IReadOnlyDictionary<string, JsonElement> Settings { get; } = settings;
    public IPluginMediaAccess Media { get; } = media;
    public IPluginHttpClient Http { get; } = http;
    public IPluginStorage Storage { get; } = storage;
    public IPluginToolRuntime Tools { get; } = tools;
    public IPluginAiClient Ai { get; } = ai;
    public IPluginSecrets Secrets { get; } = secrets;

    public ValueTask DisposeAsync()
    {
        if (Storage is BoundPluginStorage bound)
        {
            bound.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class BoundPluginMediaAccess(string pluginId, PluginMediaAssetContext? allowedAsset, IPluginPermissionGate permissions) : IPluginMediaAccess
{
    public bool Exists(PluginMediaAssetContext asset)
    {
        permissions.Demand(pluginId, PluginPermissionIds.MediaRead);
        if (allowedAsset is null || asset.AssetId != allowedAsset.AssetId ||
            !string.Equals(Path.GetFullPath(asset.FilePath), Path.GetFullPath(allowedAsset.FilePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The media asset is outside this plugin invocation.");
        }

        return File.Exists(allowedAsset.FilePath);
    }
}

internal sealed class BoundPluginHttpClient(string pluginId, IPluginPermissionGate permissions, IHttpClientFactory clients) : IPluginHttpClient
{
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead, CancellationToken cancellationToken = default)
    {
        permissions.Demand(pluginId, PluginPermissionIds.NetworkHttp);
        if (request.RequestUri is not { IsAbsoluteUri: true } uri || uri.Scheme is not ("http" or "https"))
        {
            throw new UnauthorizedAccessException("Plugin HTTP requests require an absolute HTTP or HTTPS URL.");
        }

        return clients.CreateClient("plugin_http").SendAsync(request, completionOption, cancellationToken);
    }
}

internal sealed class BoundPluginStorage(string pluginId, string root, IPluginPermissionGate permissions) : IPluginStorage
{
    private bool _disposed;

    private string Root
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            permissions.Demand(pluginId, PluginPermissionIds.PluginStorage);
            Directory.CreateDirectory(root);
            return Path.GetFullPath(root);
        }
    }

    public string WorkingDirectory => Root;
    public string GetPath(string relativePath) => ContainedPath(relativePath);
    public void CreateDirectory(string relativePath) => Directory.CreateDirectory(ContainedPath(relativePath));
    public IReadOnlyList<string> EnumerateFiles(string relativePath, string searchPattern)
    {
        var path = ContainedPath(relativePath);
        return Directory.Exists(path) ? Directory.EnumerateFiles(path, searchPattern, SearchOption.TopDirectoryOnly).ToList() : [];
    }
    public IReadOnlyList<string> ReadLines(string relativePath) => File.ReadAllLines(ContainedPath(relativePath));

    internal bool Contains(string path)
    {
        var candidate = Path.GetFullPath(path);
        var normalized = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) || string.Equals(candidate, Root, StringComparison.OrdinalIgnoreCase);
    }

    private string ContainedPath(string relativePath)
    {
        var candidate = Path.IsPathRooted(relativePath)
            ? Path.GetFullPath(relativePath)
            : Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!Contains(candidate))
        {
            throw new UnauthorizedAccessException("Plugin storage path escapes the plugin working directory.");
        }

        return candidate;
    }

    internal void Dispose()
    {
        _disposed = true;
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class BoundPluginToolRuntime(
    string pluginId,
    IReadOnlyDictionary<string, JsonElement> settings,
    BoundPluginStorage storage,
    IPluginPermissionGate permissions,
    PluginToolRuntime runtime) : IPluginToolRuntime
{
    private readonly HashSet<string> _resolved = new(StringComparer.OrdinalIgnoreCase);

    public async Task<PluginToolResolution> ResolveToolAsync(string toolId, CancellationToken cancellationToken = default)
    {
        var requirement = permissions.DemandTool(pluginId, toolId);
        var result = await runtime.ResolveToolAsync(pluginId, requirement, settings,
            () => permissions.Demand(pluginId, PluginPermissionIds.ToolDownload), cancellationToken).ConfigureAwait(false);
        if (result.IsAvailable && result.ExecutablePath is { } path)
        {
            _resolved.Add(Path.GetFullPath(path));
        }

        return result;
    }

    public Task<PluginToolRunResult> RunToolAsync(PluginToolResolution tool, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        permissions.Demand(pluginId, PluginPermissionIds.ProcessExecute);
        if (!tool.IsAvailable || tool.ExecutablePath is not { } path || !_resolved.Contains(Path.GetFullPath(path)))
        {
            throw new UnauthorizedAccessException("Only a tool resolved by this plugin invocation may be executed.");
        }

        if (!storage.Contains(workingDirectory))
        {
            throw new UnauthorizedAccessException("Plugin tools must run inside the plugin working directory.");
        }

        return runtime.RunToolAsync(path, arguments, workingDirectory, timeout, cancellationToken);
    }
}

/// <summary>
/// Gives one plugin read access to the secrets it declared, only with the <c>secrets.plugin</c> permission and
/// only from its own owner scope. Plugins cannot write or list secrets. Values are never logged here.
/// </summary>
internal sealed class BoundPluginSecrets(string pluginId, IPluginPermissionGate permissions, IConfigSecretsStore store) : IPluginSecrets
{
    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        var registration = permissions.Demand(pluginId, PluginPermissionIds.PluginSecrets);
        if (string.IsNullOrWhiteSpace(name)
            || !registration.Manifest.Secrets.Any(secret => string.Equals(secret.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return await store.GetAsync(SecretOwner.Plugin(registration.Manifest.Id), name, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class BoundPluginAiClient(string pluginId, IPluginPermissionGate permissions, PluginAiClient client) : IPluginAiClient
{
    public Task<string?> InferTextAsync(string role, string prompt, PluginAiOptions? options = null, CancellationToken cancellationToken = default)
    {
        permissions.DemandAi(pluginId, role, options);
        return client.InferTextAsync(pluginId, role, prompt, options?.MaxTokens, cancellationToken);
    }

    public Task<T?> InferJsonAsync<T>(string role, string prompt, string grammar, PluginAiOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        permissions.DemandAi(pluginId, role, options);
        return client.InferJsonAsync<T>(pluginId, role, prompt, grammar, options?.MaxTokens, cancellationToken);
    }
}
