using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IMovedItemStateCarry"/>. A move re-adds a title as a new item (Vault sections D36), so what each
/// profile had done with the old one is saved first and written against the new one once it exists. This is a separate step from the
/// Vault lookup path: it runs only for an administrator's move, and it never creates people, series or collections.
/// </summary>
public sealed class MovedItemStateCarry(IDatabaseConnection database) : IMovedItemStateCarry
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public Task<string> CaptureAsync(Guid? workId, Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var snapshot = new Snapshot
        {
            UserStates = [.. connection.Query<UserStateRow>(
                "SELECT user_id AS ProfileId, content_hash AS ContentHash, progress_pct AS ProgressPct, last_accessed AS LastAccessed, extended_properties AS Extended FROM user_states WHERE asset_id = @assetId;",
                new { assetId })],
            Consumption = [.. connection.Query<ConsumptionRow>(
                "SELECT profile_id AS ProfileId, session_key AS SessionKey, experience AS Experience, started_at AS StartedAt, updated_at AS UpdatedAt, progress_pct AS ProgressPct FROM consumption_history WHERE asset_id = @assetId;",
                new { assetId })],
            Bookmarks = [.. connection.Query<BookmarkRow>(
                "SELECT user_id AS UserId, chapter_index AS ChapterIndex, cfi_position AS CfiPosition, label AS Label, created_at AS CreatedAt FROM reader_bookmarks WHERE asset_id = @assetId;",
                new { assetId })],
            Highlights = [.. connection.Query<HighlightRow>(
                "SELECT user_id AS UserId, chapter_index AS ChapterIndex, start_offset AS StartOffset, end_offset AS EndOffset, cfi_range AS CfiRange, selected_text AS SelectedText, color AS Color, note_text AS NoteText, created_at AS CreatedAt FROM reader_highlights WHERE asset_id = @assetId;",
                new { assetId })],
        };

        if (workId is { } work)
        {
            snapshot.WorkPreferences = [.. connection.Query<WorkPreferenceRow>(
                "SELECT profile_id AS ProfileId, local_tags_json AS LocalTagsJson, updated_at AS UpdatedAt FROM profile_work_preferences WHERE work_id = @work;",
                new { work })];
            snapshot.Saved = [.. connection.Query<SavedRow>(
                "SELECT profile_id AS ProfileId, entity_kind AS Kind, saved_at AS SavedAt, position AS Position FROM profile_saved_items WHERE entity_id = @work;",
                new { work })];
            snapshot.Reactions = [.. connection.Query<ReactionRow>(
                "SELECT profile_id AS ProfileId, entity_kind AS Kind, reaction AS Reaction, updated_at AS UpdatedAt FROM profile_reactions WHERE entity_id = @work;",
                new { work })];
        }

        return Task.FromResult(JsonSerializer.Serialize(snapshot, Json));
    }

    public Task RestoreAsync(string snapshotJson, Guid? newWorkId, Guid newAssetId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotJson);
        var snapshot = JsonSerializer.Deserialize<Snapshot>(snapshotJson, Json) ?? new Snapshot();
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            foreach (var row in snapshot.UserStates)
            {
                connection.Execute(
                    """
                    INSERT OR IGNORE INTO user_states (user_id, asset_id, content_hash, progress_pct, last_accessed, extended_properties)
                    SELECT @ProfileId, @newAssetId, @ContentHash, @ProgressPct, @LastAccessed, @Extended
                    WHERE EXISTS (SELECT 1 FROM profiles WHERE id = @ProfileId);
                    """,
                    new { row.ProfileId, newAssetId, row.ContentHash, row.ProgressPct, row.LastAccessed, row.Extended }, transaction);
            }

            foreach (var row in snapshot.Consumption)
            {
                connection.Execute(
                    """
                    INSERT OR IGNORE INTO consumption_history (id, profile_id, asset_id, session_key, experience, started_at, updated_at, progress_pct)
                    SELECT @id, @ProfileId, @newAssetId, @SessionKey, @Experience, @StartedAt, @UpdatedAt, @ProgressPct
                    WHERE EXISTS (SELECT 1 FROM profiles WHERE id = @ProfileId);
                    """,
                    new { id = Guid.NewGuid(), row.ProfileId, newAssetId, row.SessionKey, row.Experience, row.StartedAt, row.UpdatedAt, row.ProgressPct }, transaction);
            }

            foreach (var row in snapshot.Bookmarks)
            {
                connection.Execute(
                    "INSERT INTO reader_bookmarks (id, user_id, asset_id, chapter_index, cfi_position, label, created_at) VALUES (@id, @UserId, @newAssetId, @ChapterIndex, @CfiPosition, @Label, @CreatedAt);",
                    new { id = Guid.NewGuid(), row.UserId, newAssetId, row.ChapterIndex, row.CfiPosition, row.Label, row.CreatedAt }, transaction);
            }

            foreach (var row in snapshot.Highlights)
            {
                connection.Execute(
                    """
                    INSERT INTO reader_highlights (id, user_id, asset_id, chapter_index, start_offset, end_offset, cfi_range, selected_text, color, note_text, created_at)
                    VALUES (@id, @UserId, @newAssetId, @ChapterIndex, @StartOffset, @EndOffset, @CfiRange, @SelectedText, @Color, @NoteText, @CreatedAt);
                    """,
                    new { id = Guid.NewGuid(), row.UserId, newAssetId, row.ChapterIndex, row.StartOffset, row.EndOffset, row.CfiRange, row.SelectedText, row.Color, row.NoteText, row.CreatedAt }, transaction);
            }

            if (newWorkId is { } work)
            {
                foreach (var row in snapshot.WorkPreferences)
                {
                    connection.Execute(
                        """
                        INSERT OR IGNORE INTO profile_work_preferences (profile_id, work_id, local_tags_json, revision, updated_at)
                        SELECT @ProfileId, @work, @LocalTagsJson, 0, @UpdatedAt
                        WHERE EXISTS (SELECT 1 FROM profiles WHERE id = @ProfileId);
                        """,
                        new { row.ProfileId, work, row.LocalTagsJson, row.UpdatedAt }, transaction);
                }

                foreach (var row in snapshot.Saved)
                {
                    connection.Execute(
                        """
                        INSERT OR IGNORE INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at, position)
                        SELECT @ProfileId, @Kind, @work, @SavedAt, @Position
                        WHERE EXISTS (SELECT 1 FROM profiles WHERE id = @ProfileId);
                        """,
                        new { row.ProfileId, row.Kind, work, row.SavedAt, row.Position }, transaction);
                }

                foreach (var row in snapshot.Reactions)
                {
                    connection.Execute(
                        """
                        INSERT OR IGNORE INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at)
                        SELECT @ProfileId, @Kind, @work, @Reaction, @UpdatedAt
                        WHERE EXISTS (SELECT 1 FROM profiles WHERE id = @ProfileId);
                        """,
                        new { row.ProfileId, row.Kind, work, row.Reaction, row.UpdatedAt }, transaction);
                }
            }
        }, ct);
    }

    private sealed class Snapshot
    {
        public List<UserStateRow> UserStates { get; set; } = [];
        public List<ConsumptionRow> Consumption { get; set; } = [];
        public List<BookmarkRow> Bookmarks { get; set; } = [];
        public List<HighlightRow> Highlights { get; set; } = [];
        public List<WorkPreferenceRow> WorkPreferences { get; set; } = [];
        public List<SavedRow> Saved { get; set; } = [];
        public List<ReactionRow> Reactions { get; set; } = [];
    }

    private sealed class UserStateRow
    {
        public Guid ProfileId { get; set; }
        public string? ContentHash { get; set; }
        public double ProgressPct { get; set; }
        public string LastAccessed { get; set; } = string.Empty;
        public string? Extended { get; set; }
    }

    private sealed class ConsumptionRow
    {
        public Guid ProfileId { get; set; }
        public string SessionKey { get; set; } = string.Empty;
        public string Experience { get; set; } = string.Empty;
        public string StartedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        public double ProgressPct { get; set; }
    }

    private sealed class BookmarkRow
    {
        public string UserId { get; set; } = string.Empty;
        public int ChapterIndex { get; set; }
        public string? CfiPosition { get; set; }
        public string? Label { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
    }

    private sealed class HighlightRow
    {
        public string UserId { get; set; } = string.Empty;
        public int ChapterIndex { get; set; }
        public int StartOffset { get; set; }
        public int EndOffset { get; set; }
        public string? CfiRange { get; set; }
        public string SelectedText { get; set; } = string.Empty;
        public string Color { get; set; } = string.Empty;
        public string? NoteText { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
    }

    private sealed class WorkPreferenceRow
    {
        public Guid ProfileId { get; set; }
        public string? LocalTagsJson { get; set; }
        public string UpdatedAt { get; set; } = string.Empty;
    }

    private sealed class SavedRow
    {
        public Guid ProfileId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string SavedAt { get; set; } = string.Empty;
        public int? Position { get; set; }
    }

    private sealed class ReactionRow
    {
        public Guid ProfileId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Reaction { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
    }
}
