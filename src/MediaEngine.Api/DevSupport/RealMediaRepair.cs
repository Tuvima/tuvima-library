using Dapper;
using MediaEngine.Contracts.Startup;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Services;
using MediaEngine.Processors.Models;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.DevSupport;

/// <summary>Offline repair of catalogue records. Source files are only enumerated.</summary>
public static class RealMediaRepair
{
    public static async Task RunAsync(string configDirectory, bool apply)
    {
        using var engine = ProcessInstanceLease.TryAcquire(ProcessInstanceLease.EngineLeaseName);
        if (!engine.IsAcquired) throw new InvalidOperationException("Stop the Engine before catalogue repair.");
        using var dashboard = ProcessInstanceLease.TryAcquire(ProcessInstanceLease.DashboardLeaseName);
        if (!dashboard.IsAcquired) throw new InvalidOperationException("Stop the Dashboard before catalogue repair.");
        var run = RealMediaHarness.Load(configDirectory) ?? throw new InvalidOperationException("A protected real-media run is required.");
        RealMediaHarness.ValidateConfiguration(configDirectory, run);
        var output = Path.Combine(run.OutputDirectory, "remediation");
        RealMediaHarness.RequireSeparate(run.SourceRoot, output);
        Directory.CreateDirectory(output);
        DapperConfiguration.Configure();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = run.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync();
        var rows = connection.Query<AssetRow>("""
            SELECT a.id AssetId, a.edition_id EditionId, e.work_id WorkId, a.file_path_root Path
            FROM media_assets a JOIN editions e ON e.id=a.edition_id JOIN works w ON w.id=e.work_id
            WHERE w.media_type='Audiobooks' AND a.status='Normal' ORDER BY a.file_path_root
            """).ToList();
        var hinted = new List<(AssetRow Row, Dictionary<string,string> Values)>();
        foreach (var row in rows)
        {
            if (!RealMediaHarness.Contains(Path.Combine(run.SourceRoot, "audiobooks"), row.Path)) continue;
            var local = connection.Query<ClaimRow>("""
                SELECT claim_key Key, claim_value Value FROM metadata_claims
                WHERE entity_id=@id AND provider_id=@provider AND is_current=1 ORDER BY claimed_at DESC
                """, new { id = row.AssetId, provider = Guid.Parse("a1b2c3d4-e5f6-4700-8900-0a1b2c3d4e5f") });
            var processed = AudiobookFolderHints.Apply(new ProcessorResult {
                FilePath=row.Path, DetectedType=MediaType.Audiobooks,
                Claims=local.Select(c => new ExtractedClaim { Key=c.Key, Value=c.Value, Confidence=1 }).ToList()
            }, Path.Combine(run.SourceRoot, "audiobooks"));
            var values = processed.Claims.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First().Value);
            if (values.ContainsKey("audiobook_recording_key")) hinted.Add((row, values));
        }
        var groups = hinted.GroupBy(x => x.Values["audiobook_recording_key"]).ToList();
        var descriptions = connection.Query<DescriptionRow>("SELECT entity_id EntityId, key Key, value Value FROM canonical_values WHERE key IN ('description','short_description','issue_description','synopsis','biography')")
            .Select(r => (Row:r, Clean:DescriptionText.Normalize(r.Value))).Where(x => x.Clean != x.Row.Value).ToList();
        var report = new { applied=apply, recordings=groups.Select(g => new {
            key=g.Key, title=g.First().Values["title"], assets=g.Select(x => x.Row.AssetId), works=g.Select(x => x.Row.WorkId).Distinct(),
            paths=g.Select(x => x.Row.Path) }), descriptions=descriptions.Count };
        RealMediaHarness.Save(Path.Combine(output, apply ? "apply-plan.json" : "dry-run.json"), report);
        Console.WriteLine($"{groups.Count} recordings, {hinted.Count} audiobook files, {descriptions.Count} HTML descriptions. Apply={apply}");
        if (!apply) return;
        var backupPath = Path.Combine(output, "library-before.db");
        if (!File.Exists(backupPath))
        {
            using var backup = new SqliteConnection($"Data Source={backupPath}");
            backup.Open(); connection.BackupDatabase(backup);
            foreach (var name in new[] { "libraries.json", "core.json", "writeback.json", RealMediaHarness.SettingsFile })
                if (File.Exists(Path.Combine(configDirectory, name))) File.Copy(Path.Combine(configDirectory, name), Path.Combine(output, name + ".before"), false);
        }
        using var tx = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        void Canonical(Guid id, string key, string value) => connection.Execute("""
            INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES(@id,@key,@value,@now)
            ON CONFLICT(entity_id,key) DO UPDATE SET value=@value,last_scored_at=@now
            """, new { id,key,value,now }, tx);
        foreach (var group in groups)
        {
            var parts = group.OrderBy(x => int.Parse(x.Values["audiobook_part_number"])).ToList();
            var target = connection.QueryFirstOrDefault<Guid?>("SELECT id FROM works WHERE parent_key=@key AND work_kind!='parent'", new { key=group.Key }, tx) ?? parts[0].Row.WorkId;
            // Keep old work rows and claims as redirect/history records. Asset IDs never change.
            connection.Execute("UPDATE works SET parent_key=@key,is_catalog_only=0 WHERE id=@target", new { key=group.Key,target }, tx);
            Canonical(target,"title",parts[0].Values["title"]);
            Canonical(target,"book_title",parts[0].Values["title"]);
            foreach (var part in parts)
            {
                foreach (var key in new[] { "track_title","title","book_title","audiobook_recording_key","audiobook_part_number","audiobook_part_count" })
                    Canonical(part.Row.AssetId,key,part.Values[key]);
                connection.Execute("UPDATE editions SET work_id=@target WHERE id=@edition",new { target,edition=part.Row.EditionId },tx);
            }
            foreach (var old in parts.Select(x => x.Row.WorkId).Distinct().Where(id => id != target))
            {
                Canonical(old,"merged_into_work_id",target.ToString("D"));
                connection.Execute("UPDATE works SET is_catalog_only=1 WHERE id=@old",new { old },tx);
                connection.Execute("UPDATE OR IGNORE collection_items SET work_id=@target WHERE work_id=@old",new { target,old },tx);
                connection.Execute("UPDATE series_manifest_items SET linked_work_id=@target WHERE linked_work_id=@old",new { target,old },tx);
            }
            var representative = parts[0].Row.AssetId;
            foreach (var part in parts.Where(p => p.Row.AssetId != representative))
                connection.Execute("""
                    UPDATE review_queue SET status='Dismissed',resolved_at=@now,resolved_by='system:recording-consolidation'
                    WHERE entity_id=@id AND status='Pending' AND trigger IN ('LowConfidence','RetailMatchFailed');
                    UPDATE identity_jobs SET state='ReadyWithoutUniverse',last_error='Identity is handled by the recording representative.',updated_at=@now
                    WHERE entity_id=@id AND state NOT IN ('Ready','ReadyWithoutUniverse','Failed','RetailNoMatch','QidNoMatch','QidNeedsReview');
                    """,new { id=part.Row.AssetId,now },tx);
            // One fresh identity request per recording; historical jobs remain as evidence.
            if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM canonical_values WHERE entity_id=@target AND key='recording_identity_repaired'",new { target },tx)==0
                && connection.ExecuteScalar<int>("SELECT COUNT(*) FROM identity_jobs WHERE entity_id=@representative AND state NOT IN ('Ready','ReadyWithoutUniverse','Failed','RetailNoMatch','QidNoMatch','QidNeedsReview')",new { representative },tx)==0)
                connection.Execute("""
                    INSERT INTO identity_jobs(id,entity_id,entity_type,media_type,state,pass,last_error,created_at,updated_at)
                    VALUES(@id,@representative,'MediaAsset','Audiobooks','Queued','Quick','Recording identity repair',@now,@now)
                    """,new { id=Guid.NewGuid(),representative,now },tx);
            Canonical(target,"recording_identity_repaired","2026-09-26");
        }
        foreach (var item in descriptions) Canonical(item.Row.EntityId,item.Row.Key,item.Clean);
        // Evidence-scoped repair of the rejected album identity captured in this run.
        // The application matching policy contains no title-specific exceptions.
        var album = Guid.Parse("72d33d61-bb89-4f49-a365-ba51bee38106");
        var wrongProvider = Guid.Parse("b3000003-d000-4000-8000-000000000004");
        if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM canonical_values WHERE entity_id=@album AND key='musicbrainz_release_id' AND value='2e77944a-1108-4ffd-adbf-591f5fa23031'",new { album },tx)==1)
        {
            var entities = connection.Query<Guid>("""
                SELECT @album UNION SELECT id FROM works WHERE parent_work_id=@album
                UNION SELECT a.id FROM media_assets a JOIN editions e ON e.id=a.edition_id JOIN works w ON w.id=e.work_id
                WHERE w.id=@album OR w.parent_work_id=@album
                """,new { album },tx).ToArray();
            foreach (var id in entities)
            {
                connection.Execute("""
                    UPDATE metadata_claims SET is_current=0,superseded_at=@now
                    WHERE entity_id=@id AND provider_id=@wrongProvider AND is_user_locked=0 AND is_current=1;
                    DELETE FROM canonical_values WHERE entity_id=@id AND winning_provider_id=@wrongProvider
                    AND NOT EXISTS (SELECT 1 FROM metadata_claims m WHERE m.entity_id=@id AND m.claim_key=canonical_values.key AND m.is_user_locked=1 AND m.is_current=1);
                    DELETE FROM bridge_ids WHERE entity_id=@id AND id_type='wikidata_qid' AND id_value='Q1139716';
                    UPDATE works SET wikidata_qid=NULL,wikidata_status='pending',wikidata_rejected_qids_json='["Q1139716"]'
                    WHERE id=@id AND wikidata_qid='Q1139716';
                    UPDATE identity_jobs SET resolved_qid=NULL,state='ReadyWithoutUniverse',last_error='Rejected album identity; retained verified retail metadata.'
                    WHERE entity_id=@id AND resolved_qid='Q1139716';
                    """,new { id,wrongProvider,now },tx);
            }
        }
        var violations = connection.Query<string>("PRAGMA foreign_key_check", transaction:tx).ToList();
        if (violations.Count > 0) throw new InvalidOperationException("Repair failed foreign-key validation; transaction rolled back.");
        tx.Commit();
        RealMediaHarness.Save(Path.Combine(output,"completed.json"),new { completed_at=now, recordings=groups.Count, assets=hinted.Count, descriptions=descriptions.Count, backup=backupPath });
    }
    private sealed class AssetRow { public Guid AssetId {get;set;} public Guid EditionId {get;set;} public Guid WorkId {get;set;} public string Path {get;set;}=""; }
    private sealed class ClaimRow { public string Key {get;set;}=""; public string Value {get;set;}=""; }
    private sealed class DescriptionRow { public Guid EntityId {get;set;} public string Key {get;set;}=""; public string Value {get;set;}=""; }
}
