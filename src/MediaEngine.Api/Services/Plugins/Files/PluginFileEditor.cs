using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Api.Services.Plugins.Files;

/// <summary>
/// Writes a plugin's file when an administrator presses Save changes or Reset to Tuvima's version (Plugins and Integrations screens
/// v5 D55, D56). This is the one place that writes a plugin file, so the safety rules live here:
/// <list type="bullet">
/// <item>It writes only the plugin's own file, found by <see cref="PluginFileService"/>, and only when that file sits inside the
/// providers folder or the plugins folder. The path is never taken from the request.</item>
/// <item>Save re-runs the same check the editor ran (structure, Library's own rules, locked fields) on the Engine and refuses a file
/// with any blocking problem, whatever the Dashboard said.</item>
/// <item>The file is written to a temporary file beside it and moved into place, so a crash never leaves half a file; the previous
/// file is kept as <c>.bak</c>.</item>
/// <item>Afterwards the plugin is loaded again and its health check runs. If it cannot be loaded the answer says so and the page
/// offers Reset.</item>
/// </list>
/// Keys and setting values live in the secrets folder and the data store, never in the file, so neither action touches them; a
/// setting the new file no longer mentions keeps its saved value quietly in case it returns.
/// </summary>
public sealed class PluginFileEditor(
    PluginFileService files,
    PluginCatalog catalog,
    IConfigurationLoader loader,
    PluginCheckNowService checkNow,
    ILogger<PluginFileEditor> logger)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public const string Saved = "saved";
    public const string Reset = "reset";
    public const string Invalid = "invalid";
    public const string ReadOnly = "read_only";
    public const string NoShippedCopy = "no_shipped_copy";
    public const string ReloadFailed = "reload_failed";

    /// <summary>Checks <paramref name="proposedText"/> again here and, only when nothing blocks, writes it. Null when the plugin does not exist or has no file.</summary>
    public async Task<PluginFileSaveResponse?> SaveAsync(string pluginId, string proposedText, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposedText);
        if (files.Locate(pluginId) is not { } target)
        {
            return null;
        }

        var installed = await files.ReadTextAsync(target, ct).ConfigureAwait(false);
        if (!target.Editable || target.FullPath is null)
        {
            return Refused(pluginId, target, installed, ReadOnly, "This file ships inside Library and cannot be edited. You can view it.");
        }

        if (proposedText.Length > PluginFileChecker.MaxCharacters)
        {
            var (tooBig, tooBigProblems) = files.Evaluate(target, proposedText, installed);
            return Refused(pluginId, target, installed, Invalid, "The file is too large to save (over 512 KB). A plugin file is a small description, not data.", tooBig, tooBigProblems);
        }

        var (state, problems) = files.Evaluate(target, proposedText, installed);
        if (state.ErrorCount > 0)
        {
            return Refused(pluginId, target, installed, Invalid,
                state.ErrorCount == 1 ? "Nothing was saved: the file has 1 problem." : $"Nothing was saved: the file has {state.ErrorCount} problems.", state, problems);
        }

        if (!TryResolveSafePath(target, out var path))
        {
            return Refused(pluginId, target, installed, ReadOnly, "Library will only write a plugin's own file, and this one is not in a place it may write to.");
        }

        return await WriteAsync(pluginId, target, path, proposedText, Saved, "Saved.", ct).ConfigureAwait(false);
    }

    /// <summary>Puts Tuvima's pristine copy of the file back. Null when the plugin does not exist or has no file.</summary>
    public async Task<PluginFileSaveResponse?> ResetAsync(string pluginId, CancellationToken ct)
    {
        return files.Locate(pluginId) is not { } target ? null : await ResetTargetAsync(pluginId, target, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts Tuvima's pristine copy of a located file back. Also the one write that Restore official plugins (D57) uses, so Reset and
    /// Restore can never disagree: a source file keeps the person's own on/off switch, and a missing or unreadable file is simply replaced.
    /// </summary>
    internal async Task<PluginFileSaveResponse> ResetTargetAsync(string pluginId, PluginFileTarget target, CancellationToken ct)
    {
        var installed = await files.ReadTextAsync(target, ct).ConfigureAwait(false);
        if (!target.Editable || target.FullPath is null)
        {
            return Refused(pluginId, target, installed, ReadOnly, "This file ships inside Library and cannot be changed, so there is nothing to reset.");
        }

        if (target.ShippedText is null)
        {
            return Refused(pluginId, target, installed, NoShippedCopy, "This plugin has no Tuvima version to go back to. It is your own plugin.");
        }

        if (!TryResolveSafePath(target, out var path))
        {
            return Refused(pluginId, target, installed, ReadOnly, "Library will only write a plugin's own file, and this one is not in a place it may write to.");
        }

        var text = target.Format == PluginFileFormat.SourceFile ? KeepTheSwitch(target.ShippedText, installed) : target.ShippedText;
        return await WriteAsync(pluginId, target, path, text, Reset, "Tuvima's version is back.", ct).ConfigureAwait(false);
    }

    private async Task<PluginFileSaveResponse> WriteAsync(string pluginId, PluginFileTarget target, string path, string text, string outcome, string message, CancellationToken ct)
    {
        await WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            WriteAtomically(path, text);
            logger.LogInformation("The file of plugin {PluginId} was {Outcome} ({Bytes} bytes)", PluginLogText.Safe(pluginId), outcome, Utf8.GetByteCount(text));

            var reloadFailure = Reload(pluginId, target, path, text);
            var onDisk = await files.ReadTextAsync(target, ct).ConfigureAwait(false);
            var (state, problems) = files.Evaluate(target, onDisk, onDisk);
            var response = new PluginFileSaveResponse
            {
                PluginId = pluginId,
                Saved = string.Equals(onDisk, text, StringComparison.Ordinal),
                Outcome = outcome,
                Message = message,
                Text = onDisk ?? string.Empty,
                State = state,
                Problems = problems,
                Reloaded = reloadFailure is null,
            };

            if (reloadFailure is not null)
            {
                response.Outcome = ReloadFailed;
                response.Message = reloadFailure;
                response.Health = "needs_attention";
                response.HealthSummary = reloadFailure;
                return response;
            }

            var check = await checkNow.CheckAsync(pluginId, ct).ConfigureAwait(false);
            response.Health = check?.Result switch
            {
                "working" => "working",
                "needs_attention" => "needs_attention",
                _ => "not_checked",
            };
            response.HealthSummary = check?.Summary ?? string.Empty;
            if (response.Health == "needs_attention")
            {
                response.Message = $"{message} It loaded, but its check found a problem. Reset puts Tuvima's version back.";
            }

            return response;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>Loads the plugin again from what was written. Returns a plain sentence when it cannot be loaded, otherwise null.</summary>
    private string? Reload(string pluginId, PluginFileTarget target, string path, string written)
    {
        if (target.Format == PluginFileFormat.Manifest)
        {
            catalog.Reload();
            var registration = catalog.Get(pluginId);
            return registration is null
                ? "Library could not load the plugin from the saved file. Reset puts Tuvima's version back."
                : registration.LoadError is not null
                    ? $"Library saved the file but could not load the plugin: {registration.LoadError}"
                    : null;
        }

        // A source file is read again by the loader, which checks it with its own rules. When it refuses the file it quietly goes back
        // to the previous copy it kept; then the file on disk is no longer what was written, which is how a refusal shows here.
        var name = Path.GetFileNameWithoutExtension(path);
        try
        {
            if (loader.LoadProvider(name) is null)
            {
                return "Library saved the file but could not load the source from it. Reset puts Tuvima's version back.";
            }
        }
        catch (ConfigValidationException ex)
        {
            logger.LogWarning("The source file {FileName} was written but the loader refused it: {Reason}", PluginLogText.Safe(target.FileName), PluginLogText.Safe(ex.Message));
            return $"Library saved the file but could not load the source from it: {FirstProblem(ex)}";
        }

        string now;
        try
        {
            now = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning("The source file {FileName} could not be read back after saving ({ErrorType})", PluginLogText.Safe(target.FileName), ex.GetType().Name);
            return "Library saved the file but could not read it back.";
        }

        return string.Equals(now, written, StringComparison.Ordinal)
            ? null
            : "Library could not load the source from the new file, so it kept the previous one. The text shown is the file now in use.";
    }

    private static string FirstProblem(ConfigValidationException ex) =>
        ex.ValidationMessages.FirstOrDefault() is { } first && !string.IsNullOrWhiteSpace(first) ? first : "its settings did not pass Library's checks.";

    /// <summary>
    /// Only ever the plugin's own file: a provider source file directly inside the providers folder, or a <c>plugin.json</c> inside a
    /// folder of the plugins folder. Anything else (a path that climbs out, a link, another file name) is refused.
    /// </summary>
    private bool TryResolveSafePath(PluginFileTarget target, out string path)
    {
        path = string.Empty;
        if (target.FullPath is not { Length: > 0 } candidate || !string.Equals(Path.GetFileName(target.FileName), target.FileName, StringComparison.Ordinal))
        {
            return false;
        }

        var full = Path.GetFullPath(candidate);
        string root;
        string expectedName;
        if (target.Format == PluginFileFormat.SourceFile)
        {
            if (string.IsNullOrWhiteSpace(loader.ConfigDirectoryPath))
            {
                return false;
            }

            root = Path.GetFullPath(Path.Combine(loader.ConfigDirectoryPath, "providers"));
            expectedName = target.FileName;
            if (!full.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetDirectoryName(full), root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        else
        {
            root = Path.GetFullPath(catalog.PluginRoot);
            expectedName = "plugin.json";
        }

        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(full), expectedName, StringComparison.OrdinalIgnoreCase)
            || IsLink(full))
        {
            return false;
        }

        path = full;
        return true;
    }

    private static bool IsLink(string path)
    {
        try
        {
            return File.Exists(path) && File.ResolveLinkTarget(path, returnFinalTarget: false) is not null;
        }
        catch (IOException)
        {
            // A file that cannot even be inspected is not one to overwrite.
            return true;
        }
    }

    private static void WriteAtomically(string path, string text)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, text, Utf8);
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>The person's own on/off switch lives in a source file; putting Tuvima's version back must not flip it.</summary>
    private static string KeepTheSwitch(string shipped, string? installed)
    {
        if (installed is null)
        {
            return shipped;
        }

        try
        {
            var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
            using var current = JsonDocument.Parse(installed, options);
            if (current.RootElement.ValueKind != JsonValueKind.Object
                || !current.RootElement.TryGetProperty("enabled", out var switched)
                || switched.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return shipped;
            }

            if (JsonNode.Parse(shipped, documentOptions: options) is not JsonObject root)
            {
                return shipped;
            }

            var wanted = switched.GetBoolean();
            if (root["enabled"] is JsonValue existing && existing.TryGetValue<bool>(out var already) && already == wanted)
            {
                return shipped;
            }

            root["enabled"] = wanted;
            return root.ToJsonString(MediaEngine.Domain.Services.MediaEngineJson.Indented);
        }
        catch (JsonException)
        {
            // The installed file is not JSON, so there is no switch to keep.
            return shipped;
        }
    }

    private static PluginFileSaveResponse Refused(
        string pluginId,
        PluginFileTarget target,
        string? installed,
        string outcome,
        string message,
        PluginFileStateDto? state = null,
        List<PluginFileProblemDto>? problems = null) => new()
        {
            PluginId = pluginId,
            Saved = false,
            Outcome = outcome,
            Message = message,
            Text = installed ?? string.Empty,
            State = state ?? new PluginFileStateDto { FileName = target.FileName, Kind = target.Kind, HasShippedCopy = target.ShippedText is not null, Editable = target.Editable },
            Problems = problems ?? [],
        };
}
