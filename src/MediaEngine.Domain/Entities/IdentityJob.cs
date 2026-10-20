namespace MediaEngine.Domain.Entities;

/// <summary>
/// A durable identity resolution job for a single staged media asset.
///
/// Created by <c>IngestionEngine</c> after file scan/classification/staging.
/// Tracks the asset through Stage 1 (retail match), Stage 2 (Wikidata bridge),
/// and Quick hydration. Survives engine restarts — replaces the former in-memory
/// <c>BoundedChannel</c> queue.
///
/// State transitions are recorded via <see cref="State"/> and <see cref="UpdatedAt"/>.
/// </summary>
public sealed class IdentityJob
{
    /// <summary>Unique identifier for this job.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The media asset being identified.</summary>
    public Guid EntityId { get; set; }

    /// <summary>Entity type (e.g. "MediaAsset", "Edition").</summary>
    public string EntityType { get; set; } = "";

    /// <summary>Media type (e.g. "Books", "Movies", "Music").</summary>
    public string MediaType { get; set; } = "";

    /// <summary>Current pipeline state. See <see cref="Enums.IdentityJobState"/>.</summary>
    public string State { get; set; } = "Queued";

    /// <summary>Enrichment pass: "Quick" or "Universe".</summary>
    public string Pass { get; set; } = "Quick";

    /// <summary>Number of processing attempts (for retry tracking).</summary>
    public int AttemptCount { get; set; }

    /// <summary>Entity/content failures that count toward the poison-work budget.</summary>
    public int PoisonAttemptCount { get; set; }

    /// <summary>The stable category assigned to the most recent unsuccessful outcome.</summary>
    public string? LastOutcomeCategory { get; set; }

    /// <summary>Worker name that currently holds the lease. Null when unleased.</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>When the current lease expires. Null when unleased.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>FK to the accepted retail candidate. Set after Stage 1 acceptance.</summary>
    public Guid? SelectedCandidateId { get; set; }

    /// <summary>Confirmed Wikidata QID after Stage 2. Null until bridge resolution succeeds.</summary>
    public string? ResolvedQid { get; set; }

    /// <summary>Last error message from a failed attempt.</summary>
    public string? LastError { get; set; }

    /// <summary>Backoff scheduling: earliest time this job should be retried.</summary>
    public DateTimeOffset? NextRetryAt { get; set; }

    /// <summary>When this job was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this job was last updated (state transition, lease, error).</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
