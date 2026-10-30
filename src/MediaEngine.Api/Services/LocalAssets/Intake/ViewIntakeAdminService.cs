using System.Globalization;
using System.Text.Json;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Services.LocalAssets.Intake;

/// <summary>
/// What an administrator sees and does about the Import and Rejected areas on a View kind's page (Photo and video filing spec D1, D6, D8,
/// D9, D19): how many files are waiting, the refused files with their reasons, Scan now, removing a refused file early, the layout
/// photos are filed by, and the kind's Activity. Nothing here moves a file into a library; that is the filer's job.
/// </summary>
public sealed class ViewIntakeAdminService(
    ViewIntakeAreas areas,
    IViewIntakeRepository intake,
    ViewIntakeFiler filer,
    ISystemActivityRepository activity,
    IConfigurationLoader configuration,
    TimeProvider time)
{
    private static readonly string[] ActivityTypes =
    [
        SystemActionType.PersonalMediaAdded,
        SystemActionType.PersonalMediaSkipped,
        SystemActionType.PersonalMediaRefused,
        SystemActionType.PersonalMediaStray,
    ];

    public async Task<ViewIntakeSummaryDto> GetSummaryAsync(string contentKind, CancellationToken ct = default)
    {
        RequireKind(contentKind);
        var settings = configuration.LoadPhotoFiling();
        var section = areas.SectionPath(contentKind);
        var available = areas.EnsureFolders();
        var waiting = await intake.CountWaitingAsync(contentKind, ct).ConfigureAwait(false);
        var note = waiting == 0
            ? null
            : (await intake.GetByStateAsync([ViewIntakeStates.Waiting], 500, ct).ConfigureAwait(false))
                .Where(row => row.ContentKind == contentKind && !string.IsNullOrWhiteSpace(row.ReasonText))
                .Select(row => row.ReasonText)
                .FirstOrDefault();
        var rejected = await intake.GetRejectedAsync(contentKind, 200, ct).ConfigureAwait(false);
        var strays = (await intake.GetByStateAsync([ViewIntakeStates.Stray], 2000, ct).ConfigureAwait(false))
            .Count(row => row.ContentKind == contentKind);
        return new ViewIntakeSummaryDto(
            contentKind,
            section,
            areas.RejectedPath(contentKind),
            available,
            waiting,
            note,
            strays,
            Math.Max(1, settings.RejectedRetentionDays),
            ViewFilingLayout.EffectivePattern(settings.LayoutPattern),
            rejected.Where(row => row.State == ViewIntakeStates.Rejected)
                .Select(row => new ViewIntakeRejectedDto(
                    row.Id, row.OriginalName, row.ReasonText ?? ViewIntakeReasons.Sentence(row.ReasonCode ?? string.Empty),
                    row.UpdatedAt, row.ExpiresAt))
                .ToList());
    }

    /// <summary>Files what has settled right now instead of waiting for the next timed look.</summary>
    public async Task<ViewIntakeScanResultDto> ScanNowAsync(CancellationToken ct = default)
    {
        var pass = await filer.FilePendingAsync(ct).ConfigureAwait(false);
        var waiting = await intake.CountWaitingAsync(null, ct).ConfigureAwait(false);
        var parts = new List<string>();
        if (pass.Filed > 0)
        {
            parts.Add($"{Count(pass.Filed)} filed");
        }

        if (pass.Duplicates > 0)
        {
            parts.Add($"{Count(pass.Duplicates)} already in the library");
        }

        if (pass.Rejected > 0)
        {
            parts.Add($"{Count(pass.Rejected)} refused");
        }

        if (waiting > 0)
        {
            parts.Add($"{Count(waiting)} still waiting");
        }

        return new ViewIntakeScanResultDto(pass.Filed, pass.Duplicates, pass.Rejected, waiting,
            parts.Count == 0 ? "Nothing is waiting in the Import area." : string.Join(", ", parts) + ".");
    }

    /// <summary>Removes one refused file now instead of waiting out its time. Only a file in the Rejected area, never a library file.</summary>
    public async Task<bool> RemoveRejectedAsync(Guid id, CancellationToken ct = default)
    {
        var row = await intake.GetAsync(id, ct).ConfigureAwait(false);
        if (row is null || row.State != ViewIntakeStates.Rejected || !areas.IsInRejectedArea(row.CurrentPath))
        {
            return false;
        }

        if (File.Exists(row.CurrentPath))
        {
            File.Delete(row.CurrentPath);
        }

        await intake.DeleteAsync(id, ct).ConfigureAwait(false);
        return true;
    }

    public ViewFilingLayoutDto GetLayout() => LayoutDto(configuration.LoadPhotoFiling().LayoutPattern);

    public ViewMissingFileSettingsDto GetMissingFileSettings() => new(
        configuration.LoadPhotoFiling().MissingFileRetentionDays, PhotoFilingSettings.DefaultMissingFileRetentionDays);

    /// <summary>Saves how long records of missing files are kept. A negative number is refused; 0 means never remove.</summary>
    public ViewMissingFileSettingsDto SetMissingFileSettings(int retentionDays)
    {
        if (retentionDays < 0)
        {
            throw new ArgumentException("Choose a number of days of 1 or more, or turn removal off.", nameof(retentionDays));
        }

        var settings = configuration.LoadPhotoFiling();
        settings.MissingFileRetentionDays = retentionDays;
        configuration.SavePhotoFiling(settings);
        return GetMissingFileSettings();
    }

    /// <summary>Saves the layout. An unusable layout is refused with the reason; an empty one goes back to the default.</summary>
    public ViewFilingLayoutDto SetLayout(string? pattern)
    {
        var chosen = string.IsNullOrWhiteSpace(pattern) ? PhotoFilingSettings.DefaultLayoutPattern : pattern.Trim();
        if (!ViewFilingLayout.IsValidPattern(chosen, out var problem))
        {
            throw new ArgumentException(problem, nameof(pattern));
        }

        var settings = configuration.LoadPhotoFiling();
        settings.LayoutPattern = chosen;
        configuration.SavePhotoFiling(settings);
        return LayoutDto(chosen);
    }

    public async Task<ViewActivityDto> GetActivityAsync(string contentKind, int take, CancellationToken ct = default)
    {
        RequireKind(contentKind);
        var entries = await activity.GetRecentByTypesAsync(ActivityTypes, Math.Clamp(take, 1, 200) * 3, ct).ConfigureAwait(false);
        var rows = new List<ViewActivityEntryDto>();
        foreach (var entry in entries)
        {
            var (photos, videos) = Counts(entry.ChangesJson);
            if ((contentKind == ViewContentKinds.Photos && photos == 0 && videos != 0)
                || (contentKind == ViewContentKinds.PersonalVideos && videos == 0))
            {
                continue;
            }

            var kind = entry.ActionType switch
            {
                SystemActionType.PersonalMediaSkipped => "skipped",
                SystemActionType.PersonalMediaRefused => "refused",
                SystemActionType.PersonalMediaStray => "stray",
                _ => "added",
            };
            var text = string.IsNullOrWhiteSpace(entry.Detail) ? entry.CollectionName ?? "Personal media" : $"{entry.CollectionName}. {entry.Detail}";
            rows.Add(new ViewActivityEntryDto(entry.OccurredAt, kind, text));
            if (rows.Count >= take)
            {
                break;
            }
        }

        return new ViewActivityDto(rows);
    }

    private ViewFilingLayoutDto LayoutDto(string? pattern)
    {
        var effective = ViewFilingLayout.EffectivePattern(pattern);
        var example = ViewFilingLayout.FolderFor(effective, new DateTimeOffset(time.GetUtcNow().Year, 3, 15, 12, 0, 0, TimeSpan.Zero).ToLocalTime())
            .Replace(Path.DirectorySeparatorChar, '/') + "/IMG_4521.jpg";
        return new ViewFilingLayoutDto(effective, PhotoFilingSettings.DefaultLayoutPattern, example);
    }

    private static (int Photos, int Videos) Counts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (0, 0);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            int Read(string name) => document.RootElement.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
            return (Read("photos"), Read("videos"));
        }
        catch (JsonException)
        {
            return (0, 0);
        }
    }

    private static string Count(int count) => count.ToString("N0", CultureInfo.InvariantCulture) + (count == 1 ? " file" : " files");

    private static void RequireKind(string contentKind)
    {
        if (contentKind is not (ViewContentKinds.Photos or ViewContentKinds.PersonalVideos))
        {
            throw new ArgumentException("Choose photos or personal_videos.", nameof(contentKind));
        }
    }
}
