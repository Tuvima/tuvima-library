using Dapper;
using MediaEngine.Contracts.Startup;
using MediaEngine.Domain;
using MediaEngine.Domain.Entities;
using MediaEngine.Processors.Contracts;
using MediaEngine.Processors.Processors;
using MediaEngine.Storage;
using MediaEngine.Storage.Services;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.DevSupport;

/// <summary>Source-preserving, repeatable recovery for identifier and contributor-evidence fixes.</summary>
public static class ContributorEditionRepair
{
    public static async Task RunAsync(string configDirectory, bool apply)
    {
        using var engine = ProcessInstanceLease.TryAcquire(ProcessInstanceLease.EngineLeaseName);
        using var dashboard = ProcessInstanceLease.TryAcquire(ProcessInstanceLease.DashboardLeaseName);
        if (!engine.IsAcquired || !dashboard.IsAcquired)
            throw new InvalidOperationException("Stop Engine and Dashboard before repairing catalogue identities.");
        var run = RealMediaHarness.Load(configDirectory) ?? throw new InvalidOperationException("A protected real-media run is required.");
        RealMediaHarness.ValidateConfiguration(configDirectory, run);
        DapperConfiguration.Configure();
        using var db = new DatabaseConnection(run.DatabasePath);
        using var conn = db.CreateConnection();
        var books = conn.Query<BookRow>("""
            SELECT a.id AssetId, a.file_path_root Path FROM media_assets a
            JOIN editions e ON e.id=a.edition_id JOIN works w ON w.id=e.work_id
            WHERE w.media_type='Books' AND a.status='Normal' AND a.is_orphaned=0
            """).ToList();
        var identifiers = new List<(Guid AssetId, string Key, string Value)>();
        foreach (var book in books)
        {
            if (!RealMediaHarness.Contains(run.SourceRoot, book.Path) || !File.Exists(book.Path)) continue;
            IMediaProcessor? processor = Path.GetExtension(book.Path).ToLowerInvariant() switch
            {
                ".epub" => new EpubProcessor(), ".azw3" => new AzW3Processor(), _ => null,
            };
            if (processor is null) continue;
            var processed = await processor.ProcessAsync(book.Path);
            if (processed.IsCorrupt) continue;
            identifiers.AddRange(processed.Claims
                .Where(claim => claim.Key is "calibre_uuid" or "isbn" or "asin" or "goodreads_id" or "google_books_id")
                .Select(claim => (book.AssetId, claim.Key, claim.Value)));
        }
        // One representative asset per unresolved canonical name limits redundant lookups.
        var contributorAssets = conn.Query<Guid>("""
            WITH credits AS (
                SELECT entity_id, key, value, value_qid FROM canonical_value_arrays
                UNION
                SELECT mc.entity_id, mc.claim_key, mc.claim_value, NULL FROM metadata_claims mc
                WHERE mc.is_current=1 AND LENGTH(mc.claim_value) BETWEEN 1 AND 200
                  AND NOT EXISTS (SELECT 1 FROM canonical_value_arrays cv
                      WHERE cv.entity_id=mc.entity_id AND cv.key=mc.claim_key)
            )
            SELECT DISTINCT AssetId FROM (
                SELECT MIN(a.id) AssetId, MIN(CASE WHEN cv.key='narrator' THEN 0
                    WHEN cv.key IN ('artist','album_artist') THEN 1 ELSE 2 END) Priority
                FROM credits cv
                JOIN editions e ON e.work_id=cv.entity_id OR EXISTS
                    (SELECT 1 FROM media_assets own WHERE own.id=cv.entity_id AND own.edition_id=e.id)
                JOIN media_assets a ON a.edition_id=e.id AND a.status='Normal' AND a.is_orphaned=0
                JOIN works w ON w.id=e.work_id
                WHERE cv.key IN ('author','narrator','artist','album_artist','director','cast_member','composer','screenwriter','producer')
                  AND (w.media_type <> 'Music' OR cv.key NOT IN ('author','narrator'))
                  AND NULLIF(TRIM(cv.value),'') IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM persons p WHERE
                    (p.name=cv.value COLLATE NOCASE OR p.wikidata_qid=cv.value_qid)
                    AND p.enriched_at IS NOT NULL)
                GROUP BY LOWER(cv.value)
            ) ORDER BY Priority, AssetId;
            """).ToList();
        Console.WriteLine($"Book identifiers: {identifiers.Count}; contributor recovery assets: {contributorAssets.Count}; apply: {apply}");
        if (!apply) return;
        var output = Path.Combine(run.OutputDirectory, "contributor-edition-repair");
        RealMediaHarness.RequireSeparate(run.SourceRoot, output);
        Directory.CreateDirectory(output);
        var backupPath = Path.Combine(output, $"library-before-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
        using (var backup = new SqliteConnection($"Data Source={backupPath}"))
        {
            backup.Open(); ((SqliteConnection)conn).BackupDatabase(backup);
        }
        var claims = new MetadataClaimRepository(db);
        var missingClaims = identifiers.Distinct().Where(item => conn.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM metadata_claims WHERE entity_id=@AssetId AND claim_key=@Key
                AND claim_value=@Value AND provider_id=@provider AND is_current=1
            """, new { item.AssetId, item.Key, item.Value, provider=WellKnownProviders.LocalProcessor }) == 0)
            .Select(item => new MetadataClaim
            {
                Id=Guid.NewGuid(), EntityId=item.AssetId, ProviderId=WellKnownProviders.LocalProcessor,
                ClaimKey=item.Key, ClaimValue=item.Value, Confidence=0.9,
            }).ToList();
        await claims.InsertBatchAsync(missingClaims);
        await db.ExecuteWriteAsync((connection, tx, ct) =>
        {
            foreach (var (assetId, key, value) in identifiers)
                connection.Execute("""
                    INSERT OR IGNORE INTO canonical_values(entity_id,key,value,last_scored_at)
                    VALUES(@assetId,@key,@value,@now)
                    """, new { assetId, key, value, now=DateTimeOffset.UtcNow.ToString("O") }, tx);
            return 0;
        });
        var merged = await new WorkIdentityReconciliationService(db).MergeDuplicateReadWorksByQidAsync();
        var operations = new MediaOperationRepository(db);
        foreach (var assetId in contributorAssets)
            await operations.EnsureAsync(new MediaOperation
            {
                OperationType = MediaOperationType.EnrichmentPeople,
                OperationKind = MediaOperationKind.Enrichment,
                EntityId = assetId, EntityKind = "media_asset", QueueName = "people",
                Status = MediaOperationStatus.Queued,
                Stage = "Contributor identity evidence recovery",
                IdempotencyKey = $"people-evidence-qids-v1:{assetId:D}",
                PositionKey = contributorAssets.IndexOf(assetId),
            });
        RealMediaHarness.Save(Path.Combine(output, "result.json"), new
        {
            mergedWorks = merged, queuedContributorAssets = contributorAssets.Count,
            identifiers = identifiers.Select(item => new { item.AssetId, item.Key, item.Value }), backupPath,
        });
        Console.WriteLine($"Merged {merged} duplicate works; queued {contributorAssets.Count} contributor assets. Backup: {backupPath}");
    }

    private sealed class BookRow
    {
        public Guid AssetId { get; set; }
        public string Path { get; set; } = "";
    }
}
