namespace MediaEngine.Domain.Enums;

/// <summary>
/// Tracks the current position of an identity job within the retail-first pipeline.
///
/// State machine:
/// <list type="bullet">
///   <item><see cref="Queued"/> → <see cref="RetailSearching"/></item>
///   <item><see cref="RetailSearching"/> → <see cref="RetailMatched"/> | <see cref="RetailMatchedNeedsReview"/> | <see cref="RetailNoMatch"/></item>
///   <item><see cref="RetailMatched"/> or <see cref="RetailMatchedNeedsReview"/> → <see cref="BridgeSearching"/></item>
///   <item><see cref="BridgeSearching"/> → <see cref="QidResolved"/> | <see cref="QidNeedsReview"/> | <see cref="QidNoMatch"/></item>
///   <item><see cref="QidResolved"/> → <see cref="Hydrating"/> → <see cref="Completed"/></item>
///   <item><see cref="Failed"/> — terminal, reachable from any state after max retries</item>
/// </list>
///
/// <see cref="RetailNoMatch"/> is terminal for one run of the pipeline. The file is marked Unmatched (<c>unmatched_assets</c>) and is
/// looked up again by a new job on a schedule, after a provider or plugin change, or on "Retry now"; a person can also advance it
/// (Fix Match or Provisional).
/// </summary>
public enum IdentityJobState
{
    /// <summary>Job created, waiting for the RetailMatchWorker to pick it up.</summary>
    Queued = 0,

    /// <summary>RetailMatchWorker has leased this job and is querying providers.</summary>
    RetailSearching = 1,

    /// <summary>A retail candidate scored ≥ auto-accept threshold (0.85). Ready for Stage 2.</summary>
    RetailMatched = 2,

    /// <summary>A retail candidate scored in the ambiguous band (0.50–0.85). Review open, but Stage 2 proceeds.</summary>
    RetailMatchedNeedsReview = 3,

    /// <summary>No retail candidate reached the ambiguous threshold. Pipeline stops. The file is marked Unmatched; no review item is created.</summary>
    RetailNoMatch = 4,

    /// <summary>WikidataBridgeWorker has leased this job and is resolving bridge IDs → QID.</summary>
    BridgeSearching = 5,

    /// <summary>A Wikidata QID was confirmed via bridge ID or constrained reconciliation. Ready for hydration.</summary>
    QidResolved = 6,

    /// <summary>Multiple viable QID candidates exist. Review created for user disambiguation.</summary>
    QidNeedsReview = 7,

    /// <summary>Retail matched but no acceptable Wikidata QID found. Item keeps retail data.</summary>
    QidNoMatch = 8,

    /// <summary>QuickHydrationWorker is running canonical hydration (Quick pass).</summary>
    Hydrating = 9,

    /// <summary>Stage 3 core universe enrichment is queued or actively running for this file.</summary>
    UniverseEnriching = 10,

    /// <summary>Pipeline completed successfully with applicable universe enrichment.</summary>
    Ready = 11,

    /// <summary>Pipeline completed successfully, but no Stage 3 universe path applied.</summary>
    ReadyWithoutUniverse = 12,

    /// <summary>Terminal failure after max retries. Requires manual intervention.</summary>
    Failed = 13,
}
