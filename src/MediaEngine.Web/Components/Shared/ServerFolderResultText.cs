using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Components.Shared;

/// <summary>
/// The one sentence that says a chosen folder is usable ("Folder accessible, 126 video files found"). The folder pop-up and the Add library
/// screen read it from here, so the two never word the same check differently.
/// </summary>
public static class ServerFolderResultText
{
    /// <summary>The sentence for a folder the Engine says can be used; null for anything else (the reasons come from the Engine's own issues).</summary>
    public static string? For(ServerFolderValidationResultDto? result)
    {
        if (result is not { CanSelect: true, Exists: true, HasRead: true })
        {
            return null;
        }

        if (result.SupportedFileCountUnavailable)
        {
            return "Folder accessible. The file count isn't available right now.";
        }

        if (result.SupportedFileCount is not { } count || string.IsNullOrWhiteSpace(result.SupportedFileKindLabel))
        {
            return "Folder accessible";
        }

        var noun = result.SupportedFileKindLabel;
        if (result.SupportedFileCountCapped)
        {
            return $"Folder accessible, {DisplayFormat.FormatCount(count)}+ {noun} files found";
        }

        return count switch
        {
            0 => $"Folder accessible, no {noun} files found yet",
            1 => $"Folder accessible, 1 {noun} file found",
            _ => $"Folder accessible, {DisplayFormat.FormatCount(count)} {noun} files found",
        };
    }
}
