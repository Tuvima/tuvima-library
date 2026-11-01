using System.Data;
using Dapper;

namespace MediaEngine.Storage;

/// <summary>
/// When a move merges one title into another (a file matched onto an entry that already has a Work, or an audiobook emptied by moving
/// its last file out), the people's own state about the old title is carried over to the surviving one before the old one is removed:
/// hearts and saves, plays, listening history, bookmarks, queue places, the hand-picked collections it was in, and personal tags. Nothing
/// the person did is lost to the merge. Delete removes this state; a move never does.
/// </summary>
internal static class WorkPersonalStateMerge
{
    /// <summary>
    /// Every table with a foreign key to <c>works(id)</c> that this merge, or the move itself, deals with, so a new table that points at a
    /// Work cannot be forgotten: a test compares this list with the data structure.
    /// </summary>
    internal static readonly IReadOnlySet<string> HandledTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Carried over here.
        "audiobook_bookmarks",
        "audiobook_chapter_title_overrides",
        "audiobook_listen_active_segments",
        "audiobook_listen_history",
        "bookmark_packs",
        "collection_items",
        "collection_list_entries",
        "companion_sessions",
        "media_asset_coverage",
        "music_play_active_segments",
        "music_play_stats",
        "player_device_states",
        "player_queue_items",
        "profile_work_preferences",
        // Handled by the move itself.
        "editions",
        "series_manifest_items",
        "works",
        // Provider data about the title that is removed with it (the surviving title keeps its own).
        "tv_episode_credits",
    };

    /// <summary>
    /// The per-person tables that name a title by <c>entity_id</c> with no foreign key, so <see cref="HandledTables"/> cannot see them.
    /// <see cref="Carry"/> re-points each. A test compares this list with every table that holds both a profile and an entity id.
    /// </summary>
    internal static readonly IReadOnlySet<string> HandledEntityIdTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "profile_reactions",
        "profile_saved_items",
    };

    /// <summary>
    /// Re-points the person's state from <paramref name="source"/> to <paramref name="target"/>. Where both have a row: a heart or save is
    /// kept if either had it and the most recent reaction wins, play counts are added and the later last-played kept, listening history and
    /// bookmarks are both kept, queue items are re-pointed, and the target's own preferences win.
    /// </summary>
    internal static void Carry(IDbConnection connection, IDbTransaction transaction, Guid source, Guid target)
    {
        var args = new { source, target };

        // Saved: kept if either title had it (a row the target already has simply stays).
        connection.Execute(
            "UPDATE OR IGNORE profile_saved_items SET entity_id = @target WHERE entity_id = @source;",
            args, transaction);

        // Reactions: the most recent one wins when both titles have one.
        connection.Execute(
            """
            UPDATE profile_reactions
            SET reaction = (SELECT s.reaction FROM profile_reactions s
                            WHERE s.profile_id = profile_reactions.profile_id AND s.entity_kind = profile_reactions.entity_kind AND s.entity_id = @source),
                updated_at = (SELECT s.updated_at FROM profile_reactions s
                              WHERE s.profile_id = profile_reactions.profile_id AND s.entity_kind = profile_reactions.entity_kind AND s.entity_id = @source)
            WHERE entity_id = @target
              AND EXISTS (SELECT 1 FROM profile_reactions s
                          WHERE s.profile_id = profile_reactions.profile_id AND s.entity_kind = profile_reactions.entity_kind
                            AND s.entity_id = @source AND s.updated_at > profile_reactions.updated_at);
            UPDATE OR IGNORE profile_reactions SET entity_id = @target WHERE entity_id = @source;
            """,
            args, transaction);

        // Plays: added together, the later last-played kept.
        connection.Execute(
            """
            UPDATE music_play_stats
            SET play_count = play_count + (SELECT s.play_count FROM music_play_stats s
                                           WHERE s.profile_id = music_play_stats.profile_id AND s.work_id = @source),
                last_played_at = CASE
                    WHEN (SELECT s.last_played_at FROM music_play_stats s
                          WHERE s.profile_id = music_play_stats.profile_id AND s.work_id = @source) > last_played_at
                    THEN (SELECT s.last_played_at FROM music_play_stats s
                          WHERE s.profile_id = music_play_stats.profile_id AND s.work_id = @source)
                    ELSE last_played_at END
            WHERE work_id = @target
              AND EXISTS (SELECT 1 FROM music_play_stats s WHERE s.profile_id = music_play_stats.profile_id AND s.work_id = @source);
            UPDATE OR IGNORE music_play_stats SET work_id = @target WHERE work_id = @source;
            UPDATE OR IGNORE music_play_active_segments SET work_id = @target WHERE work_id = @source;
            """,
            args, transaction);

        // Listening history, bookmarks and chapter names: all kept. Each row is its own record (or keyed by the file, which moves too).
        connection.Execute(
            """
            UPDATE audiobook_listen_history SET work_id = @target WHERE work_id = @source;
            UPDATE audiobook_bookmarks SET work_id = @target WHERE work_id = @source;
            UPDATE OR IGNORE bookmark_packs SET work_id = @target WHERE work_id = @source;
            UPDATE audiobook_chapter_title_overrides SET work_id = @target WHERE work_id = @source;
            UPDATE OR IGNORE audiobook_listen_active_segments SET work_id = @target WHERE work_id = @source;
            """,
            args, transaction);

        // Queue places and what a device is playing: re-pointed.
        connection.Execute(
            """
            UPDATE player_queue_items SET work_id = @target WHERE work_id = @source;
            UPDATE player_device_states SET work_id = @target WHERE work_id = @source;
            """,
            args, transaction);

        // Personal tags: the target's own row wins.
        connection.Execute(
            "UPDATE OR IGNORE profile_work_preferences SET work_id = @target WHERE work_id = @source;",
            args, transaction);

        // Hand-picked collections that held the title now hold the target, once.
        connection.Execute(
            """
            DELETE FROM collection_items
            WHERE work_id = @source AND collection_id IN (SELECT collection_id FROM collection_items WHERE work_id = @target);
            UPDATE collection_items SET work_id = @target WHERE work_id = @source;
            UPDATE collection_list_entries SET matched_work_id = @target WHERE matched_work_id = @source;
            UPDATE companion_sessions SET manual_work_id = @target WHERE manual_work_id = @source;
            UPDATE OR IGNORE media_asset_coverage SET work_id = @target WHERE work_id = @source;
            """,
            args, transaction);
    }
}
