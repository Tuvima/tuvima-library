using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Shared;

public readonly record struct AppHistoryPresentationDescriptor(string Icon, string ToneClass);

/// <summary>
/// Resolves history icons and tones from durable action keys, with the stored
/// category used only when an action key is not in this known-action matrix.
/// </summary>
public static class AppHistoryPresentation
{
    private static readonly IReadOnlyDictionary<string, string> ActionCategories =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ArtworkWritebackFailed"] = "error",
            ["FileRejected"] = "error",
            ["FileQuarantined"] = "error",
            ["MediaFailed"] = "error",
            ["BatchFailed"] = "error",
            ["WikidataMatchFailed"] = "error",
            ["RetailEnrichFailed"] = "error",

            ["ArtworkWrittenToFile"] = "artwork",
            ["CoverArtSaved"] = "artwork",
            ["HeroBannerGenerated"] = "artwork",
            ["ArtworkUpdated"] = "artwork",
            ["RetailEnriched"] = "metadata",

            ["WikidataMatched"] = "match",
            ["IdentityResolved"] = "match",
            ["MatchUpdated"] = "match",
            ["UniverseMatchRecovered"] = "match",
            ["NarrativeRootResolved"] = "match",
            ["PersonHydrated"] = "match",
            ["PersonMerged"] = "match",
            ["MediaEditorPairingCommitted"] = "match",

            ["ReviewItemCreated"] = "review",
            ["ReviewItemResolved"] = "review",
            ["Recovered"] = "review",
            ["ItemProvisional"] = "review",
            ["ItemUnrejected"] = "review",
            ["UserReportSubmitted"] = "review",
            ["UserReportResolved"] = "review",
            ["UserReportDismissed"] = "review",

            ["MetadataManualOverride"] = "manual",

            ["FileDetected"] = "file",
            ["FileIngested"] = "file",
            ["FileHashed"] = "verification",
            ["FileProcessed"] = "file",
            ["FileScored"] = "verification",
            ["FileQualityChecked"] = "verification",
            ["HashVerified"] = "verification",
            ["MovedToStaging"] = "move",
            ["Promoted"] = "file",
            ["PathUpdated"] = "move",
            ["DuplicateSkipped"] = "file",
            ["EntityChainCreated"] = "organization",
            ["FileExpired"] = "cleanup",
            ["StagedFileCleaned"] = "cleanup",
            ["ReconciliationMissing"] = "file",
            ["ReconciliationCompleted"] = "file",
            ["FolderCleaned"] = "cleanup",
            ["MediaAdded"] = "file",
            ["MediaRemoved"] = "cleanup",
            ["AutoPurge"] = "cleanup",

            ["MetadataExtracted"] = "metadata",
            ["MetadataHydrated"] = "metadata",
            ["MetadataRefreshed"] = "metadata",
            ["HydrationStage1Completed"] = "metadata",
            ["HydrationStage2Completed"] = "metadata",
            ["HydrationStage3Completed"] = "metadata",
            ["MetadataWrittenToFile"] = "writeback",
            ["MetadataTagsWritten"] = "writeback",
            ["HydrationStarted"] = "processing",
            ["HydrationCompleted"] = "metadata",
            ["HydrationEnqueued"] = "queued",
            ["BatchCreated"] = "queued",
            ["BatchCompleted"] = "verification",
            ["WeeklySyncStarted"] = "processing",
            ["SyncCompleted"] = "metadata",
            ["BridgeSyncUpdated"] = "metadata",
            ["CollectionCreated"] = "organization",
            ["CollectionAssigned"] = "organization",
            ["CollectionMerged"] = "organization",
            ["RelationshipDiscovered"] = "organization",
            ["UniverseXmlUpdated"] = "metadata",
            ["SidecarUpdated"] = "metadata",
            ["ActivityPruned"] = "cleanup",
            ["ConfidenceScored"] = "score",
            ["PersonFolderRenamed"] = "move",
            ["MediaUpdated"] = "metadata",
            ["AffiliateGenerated"] = "match",
            ["CharacterEnriched"] = "metadata",
            ["LocationEnriched"] = "metadata",
            ["OrganizationEnriched"] = "metadata",
            ["LoreDeltaChecked"] = "metadata",
            ["CanonDiscrepancyDetected"] = "metadata",
            ["CrawlStarted"] = "processing",
            ["CrawlFinished"] = "verification",
            ["ServerStarted"] = "system",
            ["ServerStopped"] = "system",
        };

    public static AppHistoryPresentationDescriptor For(string? eventType, string? category)
    {
        var resolvedCategory = !string.IsNullOrWhiteSpace(eventType)
            && ActionCategories.TryGetValue(eventType.Trim(), out var actionCategory)
                ? actionCategory
                : NormalizeCategory(category);

        return resolvedCategory switch
        {
            "artwork" => new(AppMaterialIcons.Outlined.Image, "is-artwork"),
            "match" => new(AppMaterialIcons.Outlined.Link, "is-match"),
            "file" => new(AppMaterialIcons.Outlined.Description, "is-file"),
            "review" => new(AppMaterialIcons.Outlined.TaskAlt, "is-review"),
            "manual" => new(AppMaterialIcons.Outlined.Notes, "is-manual"),
            "error" => new(AppMaterialIcons.Outlined.ErrorOutline, "is-error"),
            "queued" => new(AppMaterialIcons.Outlined.Schedule, "is-metadata"),
            "processing" => new(AppMaterialIcons.Outlined.Sync, "is-metadata"),
            "verification" => new(AppMaterialIcons.Outlined.FactCheck, "is-review"),
            "score" => new(AppMaterialIcons.Outlined.Assessment, "is-metadata"),
            "organization" => new(AppMaterialIcons.Outlined.AccountTree, "is-metadata"),
            "move" => new(AppMaterialIcons.Outlined.DriveFileMove, "is-file"),
            "cleanup" => new(AppMaterialIcons.Outlined.DeleteSweep, "is-file"),
            "writeback" => new(AppMaterialIcons.Outlined.Save, "is-metadata"),
            "system" => new(AppMaterialIcons.Outlined.PowerSettingsNew, "is-metadata"),
            _ => new(AppMaterialIcons.Outlined.EditNote, "is-metadata"),
        };
    }

    private static string NormalizeCategory(string? category) => category?.Trim().ToLowerInvariant() switch
    {
        "artwork" or "cover" => "artwork",
        "match" or "identity" => "match",
        "file" or "ingestion" => "file",
        "review" => "review",
        "manual" or "note" => "manual",
        "error" or "failure" => "error",
        "queued" or "pending" => "queued",
        "processing" or "sync" => "processing",
        "verification" or "quality" => "verification",
        "score" or "confidence" => "score",
        "organization" or "relationship" => "organization",
        "move" => "move",
        "cleanup" => "cleanup",
        "writeback" => "writeback",
        "system" => "system",
        _ => "metadata",
    };
}
