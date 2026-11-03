using System.Text.Json;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Api.Services.Plugins.Files;

/// <summary>
/// Reads and checks a plugin's file for the Plugin file card (Plugins and Integrations screens v5 D54, D55 first half). A plugin
/// defined by a file (a provider source file, a declarative or other manifest, a private upload) is read from disk; a compiled-in
/// plugin shows the JSON that ships with Library to describe it, when there is one. Everything here is read only: nothing is
/// written, so a check can run on every keystroke.
/// </summary>
public sealed class PluginFileService(
    PluginCatalog catalog,
    IConfigurationLoader loader,
    IShippedPluginFiles shipped,
    IShippedPluginDescriptions descriptions,
    ILogger<PluginFileService> logger)
{
    public const string SourceFileKind = "source_file";
    public const string ManifestKind = "manifest";
    public const string ShippedDescriptionKind = "shipped_description";

    /// <summary>Where the plugin's file is and what it is, or null when the plugin has no file to show.</summary>
    private sealed record Target(
        string Kind,
        PluginFileFormat Format,
        string FileName,
        string? FullPath,
        string? DescriptionText,
        bool Editable,
        string? ShippedText);

    /// <summary>The file's text with its state and findings, or null when the plugin does not exist or has no file to show.</summary>
    public async Task<PluginFileResponse?> ReadAsync(string pluginId, CancellationToken ct)
    {
        if (Locate(pluginId) is not { } target)
        {
            return null;
        }

        var text = await ReadTextAsync(target, ct).ConfigureAwait(false);
        var (state, problems) = Evaluate(target, text, installedText: text);
        return new PluginFileResponse { PluginId = pluginId, Text = text ?? string.Empty, State = state, Problems = problems };
    }

    /// <summary>Checks proposed text for the plugin's file. Writes nothing. Null when the plugin does not exist or has no file.</summary>
    public async Task<PluginFileCheckResponse?> CheckAsync(string pluginId, string proposedText, CancellationToken ct)
    {
        if (Locate(pluginId) is not { } target)
        {
            return null;
        }

        var installed = await ReadTextAsync(target, ct).ConfigureAwait(false);
        var (state, problems) = Evaluate(target, proposedText, installedText: installed);
        return new PluginFileCheckResponse { PluginId = pluginId, State = state, Problems = problems };
    }

    /// <summary>The state the Plugin file card shows (name, valid or the count of problems, differs from Tuvima's version), or null when the plugin has no file to show.</summary>
    public async Task<PluginFileStateDto?> StateAsync(string pluginId, CancellationToken ct)
    {
        if (Locate(pluginId) is not { } target)
        {
            return null;
        }

        var text = await ReadTextAsync(target, ct).ConfigureAwait(false);
        return Evaluate(target, text, installedText: text).State;
    }

    private (PluginFileStateDto State, List<PluginFileProblemDto> Problems) Evaluate(Target target, string? text, string? installedText)
    {
        PluginFileCheckResult result;
        if (text is null)
        {
            result = new PluginFileCheckResult([new PluginFileProblem(1, 1, PluginFileSeverity.Error, PluginFileProblemCodes.Rule, "The file could not be read. It may be missing or in use by another program.")]);
        }
        else
        {
            // Locked fields are measured against Tuvima's version; a private plugin has none, so against the file as it is installed.
            var baseline = target.ShippedText ?? (target.Editable ? installedText : null);
            var words = target.ShippedText is not null ? "Tuvima's version" : "the installed file";
            result = PluginFileChecker.Check(text, target.Format, target.FileName, baseline, words);
        }

        var differs = text is not null && target.ShippedText is not null && !PluginFileComparer.Same(
            text,
            target.ShippedText,
            target.Format == PluginFileFormat.SourceFile ? PluginFileComparer.SourceFileChoices : []);
        var state = new PluginFileStateDto
        {
            FileName = target.FileName,
            Kind = target.Kind,
            State = result.IsValid ? "valid" : "problems",
            ErrorCount = result.ErrorCount,
            NoteCount = result.NoteCount,
            DiffersFromShipped = differs,
            HasShippedCopy = target.ShippedText is not null,
            Editable = target.Editable,
        };
        var problems = result.Problems.Select(problem => new PluginFileProblemDto
        {
            Line = problem.Line,
            Column = problem.Column,
            Severity = problem.Severity == PluginFileSeverity.Error ? "error" : "note",
            Code = problem.Code,
            Message = problem.Message,
        }).ToList();
        return (state, problems);
    }

    private async Task<string?> ReadTextAsync(Target target, CancellationToken ct)
    {
        if (target.DescriptionText is not null)
        {
            return target.DescriptionText;
        }

        try
        {
            return target.FullPath is not null && File.Exists(target.FullPath) ? await File.ReadAllTextAsync(target.FullPath, ct).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The file {FileName} could not be read for the Plugin file card ({ErrorType})", target.FileName, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// The name of the provider source file that is this plugin's, or null. A file that no longer passes the loader's checks (which is
    /// exactly when the Plugin file card is needed) cannot be listed by the loader, so then the file is found by its name: a source
    /// file that ships with Library, or one whose own text says it is a source (a provider id and no plugin id).
    /// </summary>
    private string? SourceName(string pluginId, string directory)
    {
        try
        {
            return loader.LoadAllProviders().FirstOrDefault(provider =>
                provider.ProviderId is not null && provider.PluginId is null && string.Equals(provider.Name, pluginId, StringComparison.OrdinalIgnoreCase))?.Name;
        }
        catch (ConfigValidationException ex)
        {
            logger.LogWarning("A provider file failed its checks, so {PluginId} is found by file name instead: {Reason}", pluginId, ex.Message);
        }

        if (!PluginPackageRules.IsSafeId(pluginId))
        {
            return null;
        }

        var fileName = pluginId.ToLowerInvariant() + ".json";
        if (shipped.ReadSourceFile(fileName) is not null)
        {
            return pluginId.ToLowerInvariant();
        }

        var path = Path.Combine(directory, "providers", fileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("provider_id", out _) && !root.TryGetProperty("plugin_id", out _)
                ? pluginId.ToLowerInvariant()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("The provider file {FileName} could not be read to tell whether it is a source ({ErrorType})", fileName, ex.GetType().Name);
            return null;
        }
    }

    private Target? Locate(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return null;
        }

        if (catalog.Get(pluginId) is { Manifest.Hidden: false } registration)
        {
            if (!string.IsNullOrWhiteSpace(registration.ManifestPath))
            {
                return new Target(
                    ManifestKind,
                    PluginFileFormat.Manifest,
                    Path.GetFileName(registration.ManifestPath),
                    Path.GetFullPath(registration.ManifestPath),
                    DescriptionText: null,
                    Editable: true,
                    shipped.ReadManifest(registration.Manifest.Id));
            }

            return descriptions.Find(registration.Manifest.Id) is { } description
                ? new Target(ShippedDescriptionKind, PluginFileFormat.Manifest, description.FileName, FullPath: null, description.Text, Editable: false, ShippedText: null)
                : null;
        }

        var directory = loader.ConfigDirectoryPath;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var name = SourceName(pluginId, directory);
        if (name is null || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
        {
            return null;
        }

        var fileName = name + ".json";
        return new Target(
            SourceFileKind,
            PluginFileFormat.SourceFile,
            fileName,
            Path.Combine(directory, "providers", fileName),
            DescriptionText: null,
            Editable: true,
            shipped.ReadSourceFile(fileName));
    }
}
