using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// QID-first person enrichment service.
///
/// Triggered by the hydration pipeline after Stage 1 (Reconciliation) resolves
/// person QIDs from Wikidata Data Extension properties (P50 author, P57 director,
/// P161 cast, P2093 narrator).
///
/// Only creates Person records for references that carry a confirmed Wikidata QID.
/// References without a QID (e.g. from raw file metadata before Wikidata match)
/// are silently skipped — no Person record is created until the media item has
/// a confirmed Wikidata identity.
///
/// For each QID-backed person reference:
///  1. Looks up or creates a <see cref="Person"/> record (QID-first lookup).
///  2. Links the person to the ingested media asset.
///  3. If the person has not yet been Wikidata-enriched, returns a
///     <see cref="HarvestRequest"/> with <see cref="EntityType.Person"/>
///     so the caller can decide whether to process it synchronously or
///     enqueue it for background processing.
///  4. Leaves person imagery to the canonical asset pipeline.
///
/// This service is intentionally lightweight: all heavy I/O runs later,
/// either synchronously (during review resolution) or asynchronously
/// (via the harvest queue during normal ingestion).
///
/// Spec: Phase 9 – Recursive Person Enrichment (QID-first).
/// </summary>
public sealed class RecursiveIdentityService : IRecursiveIdentityService
{
    private readonly IPersonRepository _personRepo;
    private readonly ILogger<RecursiveIdentityService> _logger;

    public RecursiveIdentityService(
        IPersonRepository personRepo,
        ILogger<RecursiveIdentityService> logger)
    {
        ArgumentNullException.ThrowIfNull(personRepo);
        ArgumentNullException.ThrowIfNull(logger);
        _personRepo = personRepo;
        _logger = logger;
    }

    // ── IRecursiveIdentityService ─────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<HarvestRequest>> EnrichAsync(
        Guid mediaAssetId,
        IReadOnlyList<PersonReference> persons,
        CancellationToken ct = default)
    {
        if (persons.Count == 0)
        {
            return Array.Empty<HarvestRequest>();
        }

        var pendingRequests = new List<HarvestRequest>();

        foreach (var reference in persons)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(reference.Name) &&
                string.IsNullOrWhiteSpace(reference.WikidataQid))
            {
                continue;
            }

            // QID-first: only create Person records for references with a confirmed
            // Wikidata QID.  Name-only references (from file metadata before Wikidata
            // match) are skipped — the canonical value still shows the author name on
            // the media card, but no Person entity is created until the QID is known.
            if (string.IsNullOrWhiteSpace(reference.WikidataQid))
            {
                _logger.LogDebug(
                    "Skipping person '{Name}' ({Role}) for asset {AssetId} — no QID",
                    reference.Name, reference.Role, mediaAssetId);
                continue;
            }

            try
            {
                var request = await ProcessPersonAsync(mediaAssetId, reference, ct).ConfigureAwait(false);
                if (request is not null)
                {
                    pendingRequests.Add(request);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A failure for one person must not prevent processing the rest.
                _logger.LogWarning(ex,
                    "Failed to process person '{Name}' ({Role}) for asset {AssetId}",
                    reference.Name, reference.Role, mediaAssetId);
            }
        }

        return pendingRequests;
    }

    private async Task<HarvestRequest?> ProcessPersonAsync(
        Guid mediaAssetId,
        PersonReference reference,
        CancellationToken ct)
    {
        // Normalize "Last, First" → "First Last" for consistent storage and search.
        var normalizedName = string.IsNullOrWhiteSpace(reference.Name)
            ? reference.WikidataQid!
            : RetailHints.NormalizeBibliographicPersonName(reference.Name);

        // QID is guaranteed non-null by EnrichAsync's guard above.
        // Serialize on QID to prevent concurrent threads from both missing
        // FindByQid → both calling CreateAsync → duplicate row.
        var personLock = PersonCreationLocks.ForQid(reference.WikidataQid!);
        await personLock.WaitAsync(ct).ConfigureAwait(false);
        Person? person;
        try
        {
            // Primary lookup: find by QID (guaranteed present).
            person = await _personRepo.FindByQidAsync(reference.WikidataQid!, ct)
                         .ConfigureAwait(false);

            if (person is not null)
            {
                // Add role to existing person if not already present.
                await _personRepo.AddRoleAsync(person.Id, reference.Role, ct).ConfigureAwait(false);
            }
            else
            {
                person = await _personRepo.CreateAsync(new Person
                {
                    Name = normalizedName,
                    Roles = [reference.Role],
                    WikidataQid = reference.WikidataQid,
                }, ct).ConfigureAwait(false);

                _logger.LogDebug(
                    "Created person record for '{Name}' ({Role}, {Qid}), id={Id}",
                    person.Name, reference.Role, reference.WikidataQid, person.Id);
            }
        }
        finally
        {
            personLock.Release();
        }

        if (person is null)
        {
            return null;
        }

        // 2. Link person to the media asset (INSERT OR IGNORE — idempotent).
        await _personRepo.LinkToMediaAssetAsync(mediaAssetId, person.Id, reference.Role, ct)
            .ConfigureAwait(false);

        // 3. If not yet enriched, or still missing visible profile data, return a harvest request for the caller to handle.
        //    QID is always present — the harvest service uses it directly for
        //    Data Extension property fetching (no name-based search needed).
        if (person.EnrichedAt is null || NeedsProfileBackfill(person))
        {
            var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = normalizedName,
                ["role"] = reference.Role,
                ["wikidata_qid"] = reference.WikidataQid!,
            };

            var harvestRequest = new HarvestRequest
            {
                EntityId = person.Id,
                EntityType = EntityType.Person,
                MediaType = MediaType.Unknown,
                Hints = hints,
            };

            _logger.LogDebug(
                "Person '{Name}' ({Id}) needs enrichment — returning harvest request",
                person.Name, person.Id);

            return harvestRequest;
        }

        return null;
    }

    internal static bool NeedsProfileBackfill(Person person)
        // Group membership changes independently of the group's biography and
        // artwork, so a previously enriched band must remain refreshable.
        => person.IsGroup
           || IsPendingPersonName(person.Name)
           || string.IsNullOrWhiteSpace(person.Biography)
           || (string.IsNullOrWhiteSpace(person.HeadshotUrl)
               && string.IsNullOrWhiteSpace(person.LocalHeadshotPath));

    private static bool IsPendingPersonName(string? name)
        => string.IsNullOrWhiteSpace(name)
           || name.StartsWith("Name pending (", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("Unknown Person (", StringComparison.OrdinalIgnoreCase)
           || (name.Length > 1 && name[0] is 'Q' && name.Skip(1).All(char.IsDigit));

    // ── Private helpers ───────────────────────────────────────────────────────

}
