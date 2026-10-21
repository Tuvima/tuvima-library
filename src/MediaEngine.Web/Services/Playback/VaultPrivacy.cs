namespace MediaEngine.Web.Services.Playback;

/// <summary>What the Dashboard shows outside the player for a Vault video (Videos spec D33): nothing that names it.</summary>
public static class VaultPrivacy
{
    /// <summary>The only title the browser tab, history, media controls, lock screen and shell notices show for a Vault video.</summary>
    public const string GenericTitle = "Video";

    /// <summary>
    /// The title of the player page. The real title shows only after the playback manifest has loaded for this work and says the file is
    /// not in the Vault; before that (and for a Vault file) it is generic, so a Vault title is never in the tab, even for a moment.
    /// </summary>
    public static string PageTitle(ListenQueueItem? current, Guid workId, string? detailTitle)
    {
        if (current is null || current.WorkId != workId || current.Manifest is null)
        {
            return string.IsNullOrWhiteSpace(detailTitle) ? "Player" : GenericTitle;
        }

        return current.IsVault || string.IsNullOrWhiteSpace(detailTitle) ? GenericTitle : detailTitle;
    }

    /// <summary>True while a Vault video is actually playing: only then does the player nudge Vault mode awake.</summary>
    public static bool ShouldKeepAwake(ListenQueueItem? item, bool isPlaying) => isPlaying && item is { IsVault: true };
}
