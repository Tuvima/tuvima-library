using MediaEngine.Domain.Services;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.Vault;

/// <summary>
/// Re-links the Vault bookmarks that came back with a restored backup to the videos a rescan found. Each waiting bookmark is pinned to its
/// canonical title (external IDs, the Stash-box record, running time and edition) and re-linked to the matching video; when it has no
/// canonical data, or the video has none yet, the exact file fingerprint decides. A bookmark whose video does not turn up stays waiting
/// and is counted, never named.
/// </summary>
public sealed class VaultBookmarkRelinkService(VaultBookmarkRelinkRepository repository, ILogger<VaultBookmarkRelinkService> logger)
{
    /// <summary>Tries every waiting bookmark against every Vault video found so far. Returns how many were re-linked.</summary>
    public async Task<int> RelinkAsync(CancellationToken ct = default)
    {
        var waiting = await repository.GetWaitingAsync(ct).ConfigureAwait(false);
        if (waiting.Count == 0)
        {
            return 0;
        }

        var videos = await repository.GetVaultVideosAsync(ct).ConfigureAwait(false);
        if (videos.Count == 0)
        {
            return 0;
        }

        var linked = 0;
        foreach (var pending in waiting)
        {
            ct.ThrowIfCancellationRequested();
            var saved = new VaultRelinkBookmark(
                pending.Identity.RunningTimeSeconds is null && pending.DurationSeconds is > 0
                    ? pending.Identity with { RunningTimeSeconds = pending.DurationSeconds }
                    : pending.Identity,
                new BookmarkPackBookmark(
                    pending.PositionSeconds, pending.EndSeconds, pending.ChapterTitle, pending.ChapterStartSeconds,
                    pending.Note, pending.Description));

            VaultRelinkCandidate? target = null;
            VaultRelinkDecision? best = null;
            foreach (var video in videos)
            {
                var decision = VaultBookmarkRelinkPlanner.Decide(saved, video);
                if (decision.Kind == VaultRelinkKind.NoMatch || decision.Bookmark is null)
                {
                    continue;
                }

                // A video with the same cut beats one the bookmark had to be moved onto.
                if (best is null || (best.Kind == VaultRelinkKind.Aligned && decision.Kind == VaultRelinkKind.Exact))
                {
                    best = decision;
                    target = video;
                }
            }

            if (best?.Bookmark is not null && target is not null
                && await repository.LinkAsync(pending, target, best.Bookmark, ct).ConfigureAwait(false))
            {
                linked++;
            }
        }

        if (linked > 0)
        {
            logger.LogInformation("Saved Vault bookmarks were re-linked to their videos.");
        }

        return linked;
    }
}
