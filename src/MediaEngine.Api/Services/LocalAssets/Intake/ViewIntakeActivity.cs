using System.Globalization;
using System.Text.Json;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Services.LocalAssets.Intake;

/// <summary>
/// Writes what the filer skipped or refused to the Activity log (Photo and video filing spec D8, D12). Files that were filed are counted
/// by <see cref="PersonalMediaActivityRecorder"/> as they are indexed; this writes the other two outcomes as one row each per pass:
/// "3 photos already in your library" and "2 files refused". Rows hold counts and reasons only, never a file name, so a private
/// space's contents are not named to anyone else.
/// </summary>
public sealed class ViewIntakeActivity(ISystemActivityRepository activity, ILogger<ViewIntakeActivity> logger)
{
    public async Task RecordAsync(IntakePassResult pass, CancellationToken ct = default)
    {
        try
        {
            var duplicates = pass.Groups.Where(group => group.Outcome == IntakeOutcomes.Duplicate).ToList();
            if (duplicates.Count > 0)
            {
                var photos = duplicates.Count(group => group.ContentKind == ViewContentKinds.Photos);
                var videos = duplicates.Count - photos;
                await activity.LogAsync(new SystemActivityEntry
                {
                    ActionType = SystemActionType.PersonalMediaSkipped,
                    CollectionName = PersonalMediaActivityRecorder.Describe(photos, videos),
                    EntityType = "PersonalMedia",
                    Detail = "Already in the library, so not added again",
                    ChangesJson = JsonSerializer.Serialize(new { photos, videos, count = duplicates.Count }),
                }, ct).ConfigureAwait(false);
            }

            var refused = pass.Groups.Where(group => group.Outcome == IntakeOutcomes.Rejected).ToList();
            if (refused.Count > 0)
            {
                var photos = refused.Count(group => group.ContentKind == ViewContentKinds.Photos);
                var videos = refused.Count - photos;
                var reasons = refused.GroupBy(group => group.ReasonText ?? ViewIntakeReasons.Sentence(string.Empty))
                    .Select(reason => $"{reason.Count().ToString("N0", CultureInfo.InvariantCulture)} {reason.Key}");
                await activity.LogAsync(new SystemActivityEntry
                {
                    ActionType = SystemActionType.PersonalMediaRefused,
                    CollectionName = refused.Count == 1 ? "1 file refused" : $"{refused.Count.ToString("N0", CultureInfo.InvariantCulture)} files refused",
                    EntityType = "PersonalMedia",
                    Detail = string.Join(", ", reasons) + ". Kept in the Rejected area for a while.",
                    ChangesJson = JsonSerializer.Serialize(new { photos, videos, count = refused.Count }),
                }, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The Activity log could not record the skipped and refused media.");
        }
    }

    public async Task RecordStraysAsync(int count, CancellationToken ct = default)
    {
        if (count <= 0)
        {
            return;
        }

        try
        {
            await activity.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.PersonalMediaStray,
                CollectionName = count == 1 ? "1 unknown file in the Rejected area" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} unknown files in the Rejected area",
                EntityType = "PersonalMedia",
                Detail = "Library did not put these here, so they were left as they are.",
                ChangesJson = JsonSerializer.Serialize(new { photos = count, videos = 0, count }),
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The Activity log could not record the unknown files in the Rejected area.");
        }
    }
}
