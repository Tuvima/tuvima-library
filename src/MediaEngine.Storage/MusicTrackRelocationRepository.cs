using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>Provider proof is captured by the API, never supplied as editable metadata.</summary>
public sealed record VerifiedMusicTrackRelocation(string OperationToken, Guid AssetId,
    Guid SourceEditionId, Guid SourceWorkId, Guid LibraryId, string IdentityRevision,
    string ReleaseId, string TrackId, string? RecordingId, string Album, string? Artist,
    string Title, int Disc, int Position, string ManifestJson);

/// <summary>Moves one physical file, keeping all other editions, files and parent overrides intact.</summary>
public sealed class MusicTrackRelocationRepository(IDatabaseConnection database)
{
    public Task<MediaEditorPlanCommitResult> CommitAsync(VerifiedMusicTrackRelocation move, CancellationToken ct = default)
    {
        if (!Guid.TryParse(move.OperationToken, out _) || move.AssetId == Guid.Empty
            || !Guid.TryParse(move.ReleaseId, out _) || !Guid.TryParse(move.TrackId, out _)
            || move.Disc < 1 || move.Position < 1 || string.IsNullOrWhiteSpace(move.Album)
            || string.IsNullOrWhiteSpace(move.Title))
        {
            throw new ArgumentException("Incomplete exact-release track proof.");
        }
        var token = "move:" + move.OperationToken;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(move))));
        return database.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            MediaEditorPlanCommitResult Conflict(string reason) => new(MediaEditorCommitOutcome.Conflict,
                [new(MediaEditorCommitOutcome.Conflict, move.AssetId, move.SourceWorkId, move.SourceWorkId, null, reason)]);
            MediaEditorPlanCommitResult Saved(Guid target, MediaEditorCommitOutcome outcome) => new(outcome,
                [new(outcome, move.AssetId, move.SourceWorkId, target, "pending")]);
            var receipt = conn.QuerySingleOrDefault<string>("SELECT request_hash FROM media_editor_music_pairing_commits WHERE operation_token=@token", new { token }, tx);
            if (receipt is not null)
            {
                return receipt == hash ? Saved(conn.QuerySingle<Guid>("SELECT work_id FROM media_editor_music_pairing_commit_items WHERE operation_token=@token AND asset_id=@AssetId", new { token, move.AssetId }, tx), MediaEditorCommitOutcome.Replayed)
                        : Conflict("This operation was already used for a different correction.");
            }
            var source = conn.QuerySingleOrDefault<Source>("""
                SELECT asset.edition_id AS EditionId, edition.work_id AS WorkId, asset.library_id AS Library,
                    work.parent_work_id AS ParentId, work.display_overrides_json AS Overrides,
                    COALESCE((SELECT value FROM canonical_values WHERE entity_id=work.id AND key='identity_revision'),'') AS Revision,
                    edition.format_label AS Format
                FROM media_assets asset JOIN editions edition ON edition.id=asset.edition_id
                JOIN works work ON work.id=edition.work_id
                WHERE asset.id=@AssetId AND asset.status='Normal' AND asset.is_orphaned=0
                  AND work.media_type='Music' AND work.work_kind='child';
                """, new { move.AssetId }, tx);
            if (source is null || source.EditionId != move.SourceEditionId || source.WorkId != move.SourceWorkId
                || !Guid.TryParse(source.Library, out var library) || library != move.LibraryId
                || source.Revision != move.IdentityRevision)
            {
                return Conflict("The selected file changed since the release was reviewed.");
            }
            if (conn.ExecuteScalar<int>("SELECT COUNT(*) FROM identity_jobs WHERE entity_id=@AssetId AND lease_owner IS NOT NULL AND lease_expires_at>@now", new { move.AssetId, now = DateTimeOffset.UtcNow.ToString("O") }, tx) > 0)
            {
                return Conflict("This file is currently being enriched. Wait for that operation to finish, then review again.");
            }
            if (conn.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM identity_jobs WHERE entity_id=@AssetId AND pass<>'Quick'
                  AND state NOT IN ('Ready','ReadyWithoutUniverse','Failed','RetailNoMatch','QidNoMatch','QidNeedsReview');
                """, new { move.AssetId }, tx) > 0)
            {
                return Conflict("A pending relationship operation still uses this file's current identity. Let it finish before correcting the release.");
            }

            // Exact provider identities are the only container merge evidence.
            var albums = conn.Query<Guid>("""
                SELECT DISTINCT work.id FROM works work
                LEFT JOIN bridge_ids bridge ON bridge.entity_id=work.id AND bridge.id_type='musicbrainz_release_id'
                LEFT JOIN canonical_values value ON value.entity_id=work.id AND value.key='musicbrainz_release_id'
                WHERE work.media_type='Music' AND work.work_kind='parent'
                  AND (bridge.id_value=@ReleaseId OR value.value=@ReleaseId);
                """, new { move.ReleaseId }, tx).ToArray();
            if (albums.Length > 1)
            {
                return Conflict("More than one album has this exact release identity; resolve that conflict first.");
            }
            var album = albums.SingleOrDefault();
            if (album != Guid.Empty && conn.ExecuteScalar<int>("""
                SELECT (SELECT COUNT(*) FROM bridge_ids WHERE entity_id=@album AND id_type='musicbrainz_release_id' AND id_value<>@ReleaseId)
                     + (SELECT COUNT(*) FROM canonical_values WHERE entity_id=@album AND key='musicbrainz_release_id' AND value<>@ReleaseId);
                """, new { album, move.ReleaseId }, tx) > 0)
            {
                return Conflict("The destination album has conflicting release evidence.");
            }
            if (album == Guid.Empty)
            {
                album = Guid.NewGuid();
                conn.Execute("INSERT INTO works(id,media_type,work_kind,ownership,wikidata_status) VALUES(@album,'Music','parent','Owned','pending')", new { album }, tx);
                Put(album, "album", move.Album); Put(album, "title", move.Album);
                if (!string.IsNullOrWhiteSpace(move.Artist))
                {
                    Put(album, "artist", move.Artist);
                }
                Put(album, "musicbrainz_release_id", move.ReleaseId);
                Put(album, "child_entities_json", move.ManifestJson);
                Bridge(album, "musicbrainz_release_id", move.ReleaseId);
            }
            var destinations = conn.Query<Guid>("""
                SELECT DISTINCT work.id FROM works work
                LEFT JOIN bridge_ids bridge ON bridge.entity_id=work.id AND bridge.id_type='musicbrainz_release_track_id'
                LEFT JOIN canonical_values value ON value.entity_id=work.id AND value.key='musicbrainz_release_track_id'
                WHERE work.parent_work_id=@album AND (bridge.id_value=@TrackId OR value.value=@TrackId);
                """, new { album, move.TrackId }, tx).ToArray();
            if (destinations.Length > 1)
            {
                return Conflict("The destination release track has duplicate identities.");
            }
            var target = destinations.SingleOrDefault();
            if (target != Guid.Empty)
            {
                // Throw before writes are committed: ExecuteWriteAsync rolls back the new album too.
                if (conn.ExecuteScalar<int>("SELECT COUNT(*) FROM editions edition JOIN media_assets asset ON asset.edition_id=edition.id WHERE edition.work_id=@target", new { target }, tx) > 0)
                {
                    return Conflict("The destination track already owns a file; this correction cannot overwrite or merge it.");
                }
                if (conn.ExecuteScalar<int>("""
                    SELECT (SELECT COUNT(*) FROM metadata_claims WHERE entity_id=@target AND (is_user_locked=1 OR provider_id=@manual))
                         + (SELECT COUNT(*) FROM canonical_values WHERE entity_id=@target AND winning_provider_id=@manual)
                         + (SELECT COUNT(*) FROM works WHERE id=@target AND NULLIF(display_overrides_json,'') IS NOT NULL);
                    """, new { target, manual = WellKnownProviders.UserManual }, tx) > 0)
                {
                    return Conflict("The destination track has user-managed metadata; review it before merging this file.");
                }
                if (conn.ExecuteScalar<int>("""
                    SELECT (SELECT COUNT(*) FROM bridge_ids WHERE entity_id=@target AND id_type='musicbrainz_release_track_id' AND id_value<>@TrackId)
                         + (SELECT COUNT(*) FROM canonical_values WHERE entity_id=@target AND key='musicbrainz_release_track_id' AND value<>@TrackId);
                    """, new { target, move.TrackId }, tx) > 0)
                {
                    return Conflict("The destination track has conflicting exact release-track evidence.");
                }
            }
            else
            {
                if (conn.ExecuteScalar<int>("""
                    SELECT COUNT(*) FROM works work JOIN editions edition ON edition.work_id=work.id
                    JOIN media_assets asset ON asset.edition_id=edition.id
                    WHERE work.parent_work_id=@album AND work.ordinal_sort=@sort;
                    """, new { album, sort = OrdinalNormalizer.Normalize(null, move.Disc, move.Position).SortValue }, tx) > 0)
                {
                    return Conflict("An owned track occupies this release position; review that file before adding this correction.");
                }
                target = Guid.NewGuid();
                conn.Execute("""
                    INSERT INTO works(id,media_type,work_kind,parent_work_id,ordinal,ordinal_sort,ownership,display_overrides_json,wikidata_status)
                    VALUES(@target,'Music','child',@album,@Position,@sort,'Owned',@Overrides,'pending');
                    """, new { target, album, move.Position, sort = OrdinalNormalizer.Normalize(null, move.Disc, move.Position).SortValue, source.Overrides }, tx);
            }
            var edition = Guid.NewGuid();
            conn.Execute("INSERT INTO editions(id,work_id,format_label) VALUES(@edition,@target,@Format)", new { edition, target, source.Format }, tx);
            CopyOverrides(move.SourceWorkId, target); CopyOverrides(move.SourceEditionId, edition);
            Put(target, "title", move.Title); Put(target, "track_number", move.Position.ToString()); Put(target, "disc_number", move.Disc.ToString());
            Put(target, "musicbrainz_release_track_id", move.TrackId); Bridge(target, "musicbrainz_release_track_id", move.TrackId);
            if (!string.IsNullOrWhiteSpace(move.RecordingId)) { Put(target, "musicbrainz_recording_id", move.RecordingId); Bridge(target, "musicbrainz_recording_id", move.RecordingId); }
            Put(edition, "musicbrainz_release_id", move.ReleaseId); Bridge(edition, "musicbrainz_release_id", move.ReleaseId);
            Put(target, "identity_provider", "musicbrainz"); Put(target, "identity_provider_item_id", move.TrackId); Put(target, "identity_revision", Guid.NewGuid().ToString("N"));
            // Bridge workers intentionally accept legacy asset-scoped evidence.
            // Remove old recording/container identifiers only on the corrected
            // asset so that evidence cannot reattach it to its former album.
            var identityKeys = new[] { "musicbrainz_id", "musicbrainz_release_id", "musicbrainz_release_group_id",
                "musicbrainz_recording_id", "musicbrainz_release_track_id", "musicbrainz_work_id",
                "apple_music_id", "apple_music_collection_id", "wikidata_qid", "wikidata_qid_scope",
                "qid_resolution_method", "identity_provider", "identity_provider_item_id", "identity_revision" };
            conn.Execute("""
                DELETE FROM bridge_ids WHERE entity_id=@AssetId AND id_type IN @identityKeys;
                DELETE FROM canonical_values WHERE entity_id=@AssetId AND key IN @identityKeys;
                UPDATE metadata_claims SET is_current=0,superseded_at=@now
                WHERE entity_id=@AssetId AND claim_key IN @identityKeys AND is_current=1;
                """, new { move.AssetId, identityKeys, now = DateTimeOffset.UtcNow.ToString("O") }, tx);
            // No source-album QID, cover override or parent claims are copied. The
            // existing destination identity remains authoritative; new rows reconcile afresh.
            conn.Execute("""
                UPDATE media_assets SET edition_id=@edition,writeback_status='pending',writeback_fields_hash=@token,
                    writeback_attempts=0,writeback_last_error=NULL,writeback_next_retry_at=NULL
                WHERE id=@AssetId AND edition_id=@SourceEditionId;
                UPDATE works SET ownership='Owned',is_catalog_only=0,work_kind='child',ordinal=@Position,ordinal_sort=@sort WHERE id=@target;
                UPDATE works SET ownership=CASE WHEN EXISTS(SELECT 1 FROM editions edition JOIN media_assets asset ON asset.edition_id=edition.id WHERE edition.work_id=@SourceWorkId) THEN 'Owned' ELSE 'Unowned' END,
                    is_catalog_only=CASE WHEN EXISTS(SELECT 1 FROM editions edition JOIN media_assets asset ON asset.edition_id=edition.id WHERE edition.work_id=@SourceWorkId) THEN 0 ELSE 1 END
                WHERE id=@SourceWorkId;
                """, new
            {
                edition,
                token,
                move.AssetId,
                move.SourceEditionId,
                move.SourceWorkId,
                target,
                move.Position,
                sort = OrdinalNormalizer.Normalize(null, move.Disc, move.Position).SortValue
            }, tx);
            var now = DateTimeOffset.UtcNow.ToString("O");
            conn.Execute("""
                INSERT INTO media_file_write_intents(asset_id,generation,operation_token,trigger,status,attempts,created_at,updated_at)
                VALUES(@AssetId,1,@token,'editor_commit','pending',0,@now,@now)
                ON CONFLICT(asset_id) DO UPDATE SET generation=generation+1,operation_token=excluded.operation_token,
                    trigger='editor_commit',status='pending',attempts=0,lease_expires_at=NULL,last_error=NULL,updated_at=excluded.updated_at;
                UPDATE identity_jobs SET state='RetailMatched',resolved_qid=NULL,selected_candidate_id=NULL,
                    lease_owner=NULL,lease_expires_at=NULL,last_error=NULL,next_retry_at=NULL,updated_at=@now
                WHERE entity_id=@AssetId AND pass='Quick' AND state NOT IN ('Ready','ReadyWithoutUniverse','Failed','RetailNoMatch','QidNoMatch','QidNeedsReview');
                INSERT INTO identity_jobs(id,entity_id,entity_type,media_type,state,pass,created_at,updated_at)
                SELECT @job,@AssetId,'MediaAsset','Music','RetailMatched','Quick',@now,@now
                WHERE NOT EXISTS(SELECT 1 FROM identity_jobs WHERE entity_id=@AssetId AND pass='Quick' AND state='RetailMatched');
                INSERT INTO system_activity(action_type,entity_id,entity_type,detail,changes_json)
                VALUES('MetadataUpdated',@AssetId,'MediaAsset','Selected file matched to an exact release track',@changes);
                INSERT INTO media_editor_music_pairing_commits(operation_token,request_hash,committed_at) VALUES(@token,@hash,@now);
                INSERT INTO media_editor_music_pairing_commit_items(operation_token,asset_id,edition_id,work_id,release_id,release_track_id)
                VALUES(@token,@AssetId,@edition,@target,@ReleaseId,@TrackId);
                """, new
            {
                move.AssetId,
                token,
                now,
                job = Guid.NewGuid(),
                hash,
                edition,
                target,
                move.ReleaseId,
                move.TrackId,
                changes = JsonSerializer.Serialize(new { source_work_id = move.SourceWorkId, target_work_id = target, release_id = move.ReleaseId, release_track_id = move.TrackId })
            }, tx);
            return Saved(target, MediaEditorCommitOutcome.Committed);

            void Put(Guid owner, string key, string value) => conn.Execute("""
                INSERT INTO canonical_values(entity_id,key,value,last_scored_at,winning_provider_id)
                VALUES(@owner,@key,@value,@now,@provider)
                ON CONFLICT(entity_id,key) DO UPDATE SET value=excluded.value,last_scored_at=excluded.last_scored_at,winning_provider_id=excluded.winning_provider_id
                WHERE (canonical_values.winning_provider_id<>@manual OR canonical_values.winning_provider_id IS NULL)
                  AND NOT EXISTS(SELECT 1 FROM metadata_claims claim WHERE claim.entity_id=@owner AND claim.claim_key=@key AND claim.is_user_locked=1 AND claim.is_current=1);
                """, new { owner, key, value, now = DateTimeOffset.UtcNow.ToString("O"), provider = WellKnownProviders.MusicBrainz, manual = WellKnownProviders.UserManual }, tx);
            void Bridge(Guid owner, string key, string value) => conn.Execute("""
                INSERT INTO bridge_ids(id,entity_id,id_type,id_value,provider_id,created_at) VALUES(@id,@owner,@key,@value,@provider,@now)
                ON CONFLICT(entity_id,id_type) DO UPDATE SET id_value=excluded.id_value,provider_id=excluded.provider_id;
                """, new { id = Guid.NewGuid(), owner, key, value, provider = WellKnownProviders.MusicBrainz, now = DateTimeOffset.UtcNow.ToString("O") }, tx);
            void CopyOverrides(Guid from, Guid to)
            {
                var keys = conn.Query<string>("SELECT claim_key FROM metadata_claims WHERE entity_id=@from UNION SELECT key FROM canonical_values WHERE entity_id=@from", new { from }, tx)
                    .Where(key => !ClaimScopeCatalog.IsParentScoped(key, MediaType.Music)
                        && !key.StartsWith("identity_", StringComparison.OrdinalIgnoreCase)
                        && !key.StartsWith("wikidata_", StringComparison.OrdinalIgnoreCase)
                        && !key.EndsWith("_id", StringComparison.OrdinalIgnoreCase)).ToArray();
                conn.Execute("""
                    INSERT INTO metadata_claims(id,entity_id,provider_id,decision_source_provider_id,claim_key,claim_value,confidence,claimed_at,is_user_locked,is_current)
                    SELECT randomblob(16),@to,provider_id,decision_source_provider_id,claim_key,claim_value,confidence,claimed_at,is_user_locked,is_current
                    FROM metadata_claims WHERE entity_id=@from AND is_current=1 AND (is_user_locked=1 OR provider_id=@manual)
                      AND claim_key IN @keys;
                    INSERT OR REPLACE INTO canonical_values(entity_id,key,value,last_scored_at,winning_provider_id)
                    SELECT @to,key,value,last_scored_at,winning_provider_id FROM canonical_values
                    WHERE entity_id=@from AND (winning_provider_id=@manual OR EXISTS(SELECT 1 FROM metadata_claims claim WHERE claim.entity_id=@from AND claim.claim_key=canonical_values.key AND claim.is_user_locked=1 AND claim.is_current=1))
                      AND key IN @keys;
                    INSERT OR REPLACE INTO canonical_value_arrays(entity_id,key,ordinal,value,value_qid)
                    SELECT @to,array.key,array.ordinal,array.value,array.value_qid FROM canonical_value_arrays array
                    JOIN canonical_values value ON value.entity_id=array.entity_id AND value.key=array.key
                    WHERE array.entity_id=@from AND array.key IN @keys AND (value.winning_provider_id=@manual OR EXISTS(
                        SELECT 1 FROM metadata_claims claim WHERE claim.entity_id=@from AND claim.claim_key=array.key AND claim.is_user_locked=1 AND claim.is_current=1));
                    """, new { from, to, keys, manual = WellKnownProviders.UserManual }, tx);
            }
        }, ct);
    }
    private sealed class Source
    {
        public Guid EditionId { get; set; }
        public Guid WorkId { get; set; }
        public Guid? ParentId { get; set; }
        public string? Library { get; set; }
        public string Revision { get; set; } = "";
        public string? Format { get; set; }
        public string? Overrides { get; set; }
    }
}
