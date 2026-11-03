using System.Text.Json;
using MediaEngine.Contracts.Plugins;

namespace MediaEngine.Api.Services.Plugins.Files;

/// <summary>
/// Restore official plugins (Plugins and Integrations screens v5 D57). Lists each Tuvima plugin file that is missing, damaged or edited
/// and puts Tuvima's version back, using the same write as Reset (<see cref="PluginFileEditor"/>) and the same shipped copies as the
/// start-up repair (v4 D37, which stays). Only files that ship with Library are ever looked at, so a Private plugin is never touched, and
/// keys, switches and setting values live apart from these files, so they are never touched either.
/// </summary>
public sealed class PluginRestoreService(PluginFileService files, PluginFileEditor editor, ILogger<PluginRestoreService> logger)
{
    public const string Missing = "missing";
    public const string Damaged = "damaged";
    public const string Edited = "edited";

    public async Task<PluginRestorePreviewResponse> PreviewAsync(CancellationToken ct)
    {
        var response = new PluginRestorePreviewResponse();
        foreach (var (pluginId, target, reason) in await FindAsync(ct).ConfigureAwait(false))
        {
            if (reason is null)
            {
                response.MatchingCount++;
                continue;
            }

            response.Items.Add(new PluginRestoreItemDto
            {
                PluginId = pluginId,
                FileName = target.FileName,
                Reason = reason,
                Action = reason == Edited ? "reset" : "restore",
            });
        }

        return response;
    }

    public async Task<PluginRestoreResponse> RestoreAsync(CancellationToken ct)
    {
        var response = new PluginRestoreResponse();
        foreach (var (pluginId, target, reason) in await FindAsync(ct).ConfigureAwait(false))
        {
            if (reason is null)
            {
                continue;
            }

            var result = await editor.ResetTargetAsync(pluginId, target, ct).ConfigureAwait(false);
            var done = result.Saved && result.Outcome != PluginFileEditor.ReloadFailed;
            var outcome = !done ? "failed" : reason == Edited ? "reset" : "restored";
            response.Items.Add(new PluginRestoreResultItemDto { PluginId = pluginId, FileName = target.FileName, Outcome = outcome, Message = done ? string.Empty : result.Message });
            switch (outcome)
            {
                case "restored":
                    response.RestoredCount++;
                    break;
                case "reset":
                    response.ResetCount++;
                    break;
                default:
                    response.FailedCount++;
                    logger.LogWarning("Restoring the official file {FileName} of {PluginId} did not finish: {Outcome}", target.FileName, pluginId, result.Outcome);
                    break;
            }
        }

        return response;
    }

    private async Task<List<(string PluginId, PluginFileTarget Target, string? Reason)>> FindAsync(CancellationToken ct)
    {
        var found = new List<(string, PluginFileTarget, string?)>();
        foreach (var (pluginId, target) in files.OfficialFiles())
        {
            var text = await files.ReadTextAsync(target, ct).ConfigureAwait(false);
            string? reason;
            if (text is null)
            {
                reason = target.FullPath is not null && File.Exists(target.FullPath) ? Damaged : Missing;
            }
            else if (!IsJsonObject(text))
            {
                reason = Damaged;
            }
            else
            {
                reason = PluginFileComparer.Same(text, target.ShippedText!, target.Format == PluginFileFormat.SourceFile ? PluginFileComparer.SourceFileChoices : []) ? null : Edited;
            }

            found.Add((pluginId, target, reason));
        }

        return found;
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
