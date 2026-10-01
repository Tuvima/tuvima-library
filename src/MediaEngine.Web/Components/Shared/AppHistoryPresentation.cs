using MudBlazor;

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
            "artwork" => new(Icons.Material.Outlined.Image, "is-artwork"),
            "match" => new(Icons.Material.Outlined.Link, "is-match"),
            "file" => new(Icons.Material.Outlined.Description, "is-file"),
            "review" => new(Icons.Material.Outlined.TaskAlt, "is-review"),
            "manual" => new(Icons.Material.Outlined.Notes, "is-manual"),
            "error" => new(Icons.Material.Outlined.ErrorOutline, "is-error"),
            "queued" => new(Icons.Material.Outlined.Schedule, "is-metadata"),
            "processing" => new(Icons.Material.Outlined.Sync, "is-metadata"),
            "verification" => new(Icons.Material.Outlined.FactCheck, "is-review"),
            "score" => new(Icons.Material.Outlined.Assessment, "is-metadata"),
            "organization" => new(Icons.Material.Outlined.AccountTree, "is-metadata"),
            "move" => new(Icons.Material.Outlined.DriveFileMove, "is-file"),
            "cleanup" => new(Icons.Material.Outlined.DeleteSweep, "is-file"),
            "writeback" => new(Icons.Material.Outlined.Save, "is-metadata"),
            "system" => new(Icons.Material.Outlined.PowerSettingsNew, "is-metadata"),
            _ => new(Icons.Material.Outlined.EditNote, "is-metadata"),
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
