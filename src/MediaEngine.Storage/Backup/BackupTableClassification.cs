using MediaEngine.Domain;

namespace MediaEngine.Storage.Backup;

/// <summary>
/// The one registry saying what a backup does with every data store table. A guardrail test fails when a table is
/// not listed here, so a new table cannot ship without a decision (Configuration, PersonalState, Catalogue or
/// Transient). The public description is the Backup and restore architecture page on docs.tuvima.com.
/// </summary>
public static partial class BackupTableClassification
{
    private static readonly IReadOnlyList<BackupTableRule> AllRules =
    [
        // ---------------------------------------------------------------------------------------------------------
        // Configuration: settings and access, kept as rows.
        // ---------------------------------------------------------------------------------------------------------
        Config("accounts", "Users (sign-ins) and the profile each one opens."),
        Config("account_credentials", "Password hashes, needed to sign in after a restore."),
        Config("account_external_logins", "Links between an account and an outside sign-in provider."),
        Config("account_passkeys", "Passkey public keys, needed to sign in after a restore."),
        Config("account_two_step", "Two-step sign-in data, needed to sign in after a restore; the secret is protected with this computer's Data Protection key, so the export writes it readable (or keeps it as it is when it cannot be read).", portable: ["secret_protected"]),
        Config("account_notice_dismissals", "Notices an account has dismissed, so they do not return."),
        Config("household_library_hidden", "Which libraries an administrator hid for a whole household (the household ceiling)."),
        Config("profile_admin_protections", "Optional extra lock (lock settings) on a profile's administrator rights."),
        Config("households", "Households that group profiles (and, through them, users)."),
        Config("profiles", "People using the Library, with their display, content limit and navigation settings.", excluded: ["avatar_image_path"]),
        Config("profile_credentials", "Profile PIN hashes, needed to switch profile after a restore."),
        Config("profile_grid_preferences", "How a profile sorts each grid."),
        Config("profile_hero_settings", "How a profile wants the Home hero to behave."),
        Config("profile_hero_plugin_switches", "Which plugins' Home slides a profile switched off."),
        Config("profile_notice_dismissals", "Notices a profile has dismissed, so they do not return."),
        Config("profile_sequence_preferences", "Whether a profile shows missing items in a series or sequence.", refs: [Other("container_key")]),
        Config("profile_view_policies", "What a profile may do in View (access, sharing, documents)."),
        Config("profile_view_preferences", "A profile's View display preferences."),
        Config("user_playback_settings", "A profile's playback settings."),
        Config("client_devices", "Paired devices and their capabilities (their tokens are not kept, so a device signs in again)."),
        Config("applications", "Registered outside applications and what kind of access each has."),
        Config("application_client_bindings", "Which client ids belong to which application."),
        Config("application_credentials", "Hashed application credentials, which are access settings."),
        Config("application_permission_grants", "Which permissions an application has been granted."),
        Config("application_webhooks", "Webhook destinations an application has registered; the signing secret is protected with this computer's Data Protection key, so the export writes it readable (or keeps it as it is when it cannot be read).", portable: ["secret_ciphertext"]),
        Config("approval_records", "Approvals an administrator has given, with the statement approved."),
        Config("plugin_lore_sources", "Lore sources a person approved or rejected; sources only discovered but never decided on are rebuilt.", keepWhere: "approved_at IS NOT NULL OR rejected_at IS NOT NULL"),
        Config("plugin_library_limits", "The libraries an administrator limited a plugin or source to."),
        Config("metadata_providers", "The list of metadata providers and whether each is switched on."),
        Config("provider_config", "Provider settings; rows marked secret are left out of a backup.", keepWhere: "is_secret = 0"),
        Config("notification_settings", "Notification channels and rules."),
        Config("backup_settings", "Backup schedule, destination and retention settings; the machine-encrypted destination logins are left out.", excludedJson: [new ExcludedJsonProperty("value_json", "EncryptedSecret")]),
        Config("doc_types", "Document types a person defined."),
        Config("doc_field_definitions", "Custom document fields a person defined."),
        Config("doc_field_choices", "The choices offered by a document field."),
        Config("doc_correspondents", "Correspondents (senders and recipients) a person defined for documents."),
        Config("doc_rules", "Rules that file documents automatically."),
        Config("doc_rule_conditions", "The conditions of a document filing rule."),
        Config("doc_rule_actions", "The actions of a document filing rule."),
        Config("tags", "Tags a person created for View items."),
        Config("view_personal_spaces", "Each profile's private View space."),
        Config("view_sources", "Folders and devices a View space reads from."),
        Config("view_source_policies", "Whether a View source appears in the timeline."),
        Config("view_devices", "Devices that back up into a View space."),
        Config("view_storage_labels", "Names a person gave to View storage locations."),
        Config("view_shared_library", "Which library a household shares in View."),
        Config("view_galleries", "Galleries a person built in View.", refs: [LocalItem("cover_item_id"), Coll("soundtrack_playlist_id")]),
        Config("view_gallery_items", "The View items in a person-built gallery.", refs: [LocalItem("item_id")]),
        Config("view_gallery_shares", "Who a person-built View gallery is shared with."),
        Config("doc_field_values", "Values a person entered in custom document fields.", refs: [LocalItem("item_id")]),
        Config("local_item_tag_links", "Which tags a person put on which View item.", refs: [LocalItem("item_id")]),
        Config("doc_item_details", "Filing details a person entered for a document (machine suggestions are rebuilt).", refs: [LocalItem("item_id")], excluded: ["suggestions_json"]),
        Config("excluded_source_files", "Files a person removed from the library and chose to keep out, so a rescan does not add them back.", excluded: ["title", "excluded_by_name"]),
        Config("collections", "Collections and playlists a person built; collections built from providers (universes and series) are rebuilt, but a person's switches on them (shown, featured, display changes) are kept.", refs: [Other("universe_id"), Coll("parent_collection_id")], excluded: ["cover_artwork_path", "background_artwork_path", "banner_artwork_path", "logo_artwork_path"], keepWhere: PersonBuiltCollections, choices: ["is_enabled", "is_featured", "display_overrides_json"]),
        Config("collection_items", "The titles in a person-built collection.", refs: [Work("work_id")], keepWhere: InPersonBuiltCollection),
        Config("collection_placements", "Where a person-built collection appears.", keepWhere: InPersonBuiltCollection),
        Config("collection_profile_audience", "Which profiles can see a person-built collection.", keepWhere: InPersonBuiltCollection),
        Config("collection_view_sources", "The gallery or rule feeding a person-built collection.", keepWhere: InPersonBuiltCollection),
        Config("collection_list_sources", "Where an imported list collection came from (file name, refresh choice, sync mode) and how many titles it matched.", keepWhere: InPersonBuiltCollection),
        Config("collection_list_entries", "Every entry of an imported list and which title in the library it matched.", refs: [Work("matched_work_id")], keepWhere: InPersonBuiltCollection),
        Config("fictional_entity_user_overrides", "A person's name and description corrections for a fictional thing, found again by its Wikidata id.", refs: [Fictional("entity_id")]),
        Config("narrative_root_user_overrides", "A person's name and description corrections for a universe (already keyed by its Wikidata id)."),
        Config("folder_first_checks", "The first check of each newly added folder: a pause waiting for an administrator's choice, and which folders have already passed, so a restore neither releases a paused folder nor starts the check again."),
        Config("backup_restores", "Which backups were restored and when, with counts of what was put back and what is still waiting."),
        Config("waiting_personal_state", "State from a restored backup that is still waiting for its title, kept with the time it started waiting, so a backup taken before it attaches does not lose it or restart its waiting period."),

        // ---------------------------------------------------------------------------------------------------------
        // Personal state: a person's own state tied to a title. Kept, re-attached by recognition data after a rescan.
        // ---------------------------------------------------------------------------------------------------------
        Personal("reader_bookmarks", "user_id", "Bookmarks a person placed in a book.", refs: [Asset("asset_id")]),
        Personal("reader_highlights", "user_id", "Highlights and notes a person made in a book.", refs: [Asset("asset_id")]),
        Personal("reader_statistics", "user_id", "A person's reading time and speed per book.", refs: [Asset("asset_id")]),
        Personal("audiobook_bookmarks", "profile_id", "Bookmarks a person placed in an audiobook.", refs: [Work("work_id"), Asset("asset_id")]),
        Personal("audiobook_listen_history", "profile_id", "What a person listened to in audiobooks.", refs: [Work("work_id"), Asset("asset_id")]),
        Personal("music_play_stats", "profile_id", "Play counts and last played time per album or track.", refs: [Work("work_id")]),
        Personal("consumption_history", "profile_id", "What a person watched, read or listened to, with progress.", refs: [Asset("asset_id")]),
        Personal("personal_activity", "profile_id", "A person's own activity feed (what they played and for how long).", refs: [Work("work_id"), Asset("asset_id")], excluded: ["artwork_key"]),
        Personal("player_device_states", "profile_id", "Where a person stopped on each device (playback positions); the live player session is left out.", refs: [Asset("asset_id"), Work("work_id")], excluded: ["player_session_id"]),
        Personal("user_states", "user_id", "A person's progress and extended state per file.", refs: [Asset("asset_id")]),
        Personal("user_taste_profiles", "user_id", "A person's taste profile.", refs: []),
        Personal("profile_reactions", "profile_id", "A person's reactions (likes and dislikes) to titles and other items.", refs: [Other("entity_id")]),
        Personal("profile_saved_items", "profile_id", "Items a person saved (favourites and watch later).", refs: [Other("entity_id")]),
        Personal("profile_taste_overrides", "profile_id", "Genres and tastes a person switched on or off.", refs: []),
        Personal("profile_work_preferences", "profile_id", "A person's own tags and preferences for a title.", refs: [Work("work_id")]),
        Personal("profile_person_preferences", "profile_id", "A person's own tags and preferences for a creator or performer.", refs: [Person("person_id")]),

        // ---------------------------------------------------------------------------------------------------------
        // Catalogue: anything a rescan rebuilds. Not kept, except a person's own choices and corrections on its rows.
        // ---------------------------------------------------------------------------------------------------------
        Catalogue("works", "Titles; rebuilt by a rescan, but display changes a person typed are kept. Matching and placement choices (locked or rejected matches, a locked series placement) are left to the rescan.", choices: ["display_overrides_json"]),
        Catalogue("editions", "Editions of a title; rebuilt by a rescan."),
        Catalogue("media_assets", "The files themselves; rebuilt by a rescan."),
        Catalogue("media_asset_chapters", "Chapters read from a file."),
        Catalogue("media_asset_coverage", "Which part of a work a file covers; rebuilt by a rescan, including coverage a person set by hand (moves are left to the rescan)."),
        Catalogue("media_artwork_writeback", "Artwork write-back state for files."),
        Catalogue("media_inspection_holds", "Inspection holds on files."),
        Catalogue("playback_inspection_cache", "Cached file inspection results."),
        Catalogue("playback_segments", "Intro, credits and recap segments found in files."),
        Catalogue("audio_fingerprints", "Audio fingerprints computed from files."),
        Catalogue("file_hash_cache", "Cached file hashes."),
        Catalogue("canonical_values", "Winning metadata values, rebuilt from claims."),
        Catalogue("canonical_value_arrays", "Winning list-type metadata values, rebuilt from claims."),
        Catalogue("metadata_claims", "What each provider said about a title; rebuilt by enrichment. Current claims a person made themselves (a locked field, a manual edit) are kept.", correctionWhere: PersonMadeClaims(), refs: [Other("entity_id")]),
        Catalogue("bridge_ids", "External ids found for a title."),
        Catalogue("entity_events", "Identity and enrichment history of a title."),
        Catalogue("entity_field_changes", "Field changes recorded in a title's history."),
        Catalogue("entity_relationships", "Relationships between titles, people and characters from Wikidata."),
        Catalogue("entity_relationship_qualifiers", "Details of those relationships."),
        Catalogue("entity_capability_states", "What enrichment has done for each title."),
        Catalogue("identity_jobs", "Identification and enrichment jobs."),
        Catalogue("unmatched_assets", "Files the Engine could not match, with when each is tried again and whether it was kept as it is; rebuilt by a rescan and the next lookup."),
        Catalogue("retail_match_candidates", "Retail match candidates for an identification job."),
        Catalogue("wikidata_bridge_candidates", "Wikidata candidates for an identification job."),
        Catalogue("deferred_enrichment_queue", "Titles waiting for enrichment."),
        Catalogue("enrichment_refresh_schedule", "When each title is next refreshed."),
        Catalogue("ai_feature_artifacts", "Results of optional AI features."),
        Catalogue("pending_person_signals", "People spotted in files, waiting to be matched."),
        Catalogue("review_queue", "Needs Review items; a rescan raises them again."),
        Catalogue("artwork_assets", "Artwork files known to the Library."),
        Catalogue("artwork_asset_context", "Where each artwork file is used."),
        Catalogue("artwork_asset_search", "Search index over artwork."),
        Catalogue("entity_artwork_links", "Which artwork belongs to which title or person. A cover a person picked from a provider is kept, found again by the artwork's provider and source address; uploads are not kept.", correctionWhere: "is_user_override = 1 AND artwork_asset_id IN (SELECT id FROM artwork_assets WHERE COALESCE(source_provider, '') <> 'user_upload' COLLATE NOCASE AND (NULLIF(TRIM(source_url), '') IS NOT NULL OR NULLIF(TRIM(provider_reference), '') IS NOT NULL))", refs: [Other("entity_id"), Artwork("artwork_asset_id")]),
        Catalogue("entity_assets", "Images attached to titles and people. A cover a person chose from a provider is kept, found again by provider and image address; uploads are not kept.", correctionWhere: "is_user_override = 1 AND COALESCE(source_provider, '') <> 'user_upload' COLLATE NOCASE AND image_url LIKE 'http%'", refs: [Other("entity_id")], excluded: ["id", "local_image_path", "local_image_path_s", "local_image_path_m", "local_image_path_l", "local_image_path_t", "is_locally_exported", "is_preferred_exported"]),
        Catalogue("image_cache", "Cached downloaded images."),
        Catalogue("image_cache_sources", "Where cached images came from."),
        Catalogue("character_portraits", "Character portraits."),
        Catalogue("character_performer_links", "Which performer plays which character."),
        Catalogue("fictional_entities", "Characters, places and other fictional things from Wikidata."),
        Catalogue("fictional_entity_work_links", "Where a fictional thing appears."),
        Catalogue("narrative_roots", "Universes and franchises from Wikidata."),
        Catalogue("persons", "Creators and performers; rebuilt from files and providers, but a person's display changes are kept.", choices: ["display_overrides_json"]),
        Catalogue("person_aliases", "Pseudonyms of people."),
        Catalogue("person_group_members", "Members of bands and groups."),
        Catalogue("person_media_links", "Which person worked on which file."),
        Catalogue("person_roles", "The roles a person has."),
        Catalogue("collection_relationships", "Relationships of provider-built collections."),
        Catalogue("series_manifest_hydrations", "Series information fetched from providers."),
        Catalogue("series_manifest_items", "The items of a series fetched from providers."),
        Catalogue("series_members", "Series membership fetched from providers."),
        Catalogue("tv_episode_credits", "Episode credits from providers."),
        Catalogue("audiobook_chapter_title_overrides", "Chapter titles; the ones a person renamed are kept, the ones read from the file are rebuilt.", correctionWhere: "title_source = 'Override'", refs: [Work("work_id"), Asset("asset_id")]),
        Catalogue("subtitle_index_assets", "Subtitle indexing state per file."),
        Catalogue("subtitle_index_tracks", "Subtitle tracks that were indexed."),
        Catalogue("subtitle_lines", "Indexed subtitle text."),
        Catalogue("subtitle_name_tags", "Characters found in subtitle text."),
        Catalogue("text_tracks", "Subtitle and lyric tracks found for files."),
        Catalogue("text_track_rejections", "Lyrics and subtitle results a person marked wrong for a file, so they are not picked again."),
        Catalogue("item_extras", "Guidebooks, pictures and clips found beside a title (its Extras); rebuilt by a rescan. Files a person moved between Extras and episodes in the editor are kept as corrections.", correctionWhere: "origin <> 'rule'"),
        Catalogue("local_items", "Photos, documents and other View items found on disk; a person's own choices on them (favourite, hidden, archived, trashed, capture date they set) are kept.", choices: ["favorite", "hidden", "archived_at", "trashed_at", "captured_at_user_override", "captured_at"], gatedBy: [("captured_at", "captured_at_user_override")]),
        Catalogue("local_files", "Files behind View items."),
        Catalogue("local_file_sources", "Where View files were found."),
        Catalogue("local_item_files", "Which files belong to which View item."),
        Catalogue("local_item_metadata", "Metadata read from View items; a place a person set on a photo and a description they wrote are kept as choices on the item (never the embedded coordinates, camera or lens details, or document text).", choices: ["description", "location_user_override", "latitude", "longitude", "location_name", "location_city", "location_region", "location_country", "location_country_code", "location_source"], gatedBy: [("latitude", "location_user_override"), ("longitude", "location_user_override"), ("location_name", "location_user_override"), ("location_city", "location_user_override"), ("location_region", "location_user_override"), ("location_country", "location_user_override"), ("location_country_code", "location_user_override"), ("location_source", "location_user_override")], choiceRow: LocalItem("item_id")),
        Catalogue("local_item_annotations", "Labels and detections found on View items; a name a person gave, or a detection they reviewed, is kept (the machine's own details are not).", correctionWhere: "source = 'user_assignment' OR reviewed_at IS NOT NULL", refs: [LocalItem("item_id")], excluded: ["confidence", "model_name", "model_version", "provenance_json"]),
        Catalogue("local_item_search", "Search index over View items."),
        Catalogue("local_item_search_keys", "Row numbers linking View items to their search index entries."),
        Catalogue("doc_item_field_sources", "Where document field values came from (tied to View items)."),
        Catalogue("view_shared_assets", "View items promoted to the shared library."),
        Catalogue("personal_activity_artwork", "Cached pictures for the activity feed."),
        Catalogue("provider_response_cache", "Cached provider answers."),
        Catalogue("resolver_cache", "Cached identification answers."),
        Catalogue("search_results_cache", "Cached search answers."),
        Catalogue("qid_labels", "Cached Wikidata labels."),

        // ---------------------------------------------------------------------------------------------------------
        // Transient: not kept and not rebuilt from files.
        // ---------------------------------------------------------------------------------------------------------
        Transient("recycle_bin_personal_state", "Progress kept for files waiting in the Recycle bin; the bin itself is not backed up, so these copies have nothing to come back to after a restore."),
        Transient("auth_sessions", "Sign-in sessions."),
        Transient("client_tokens", "Device and application tokens."),
        Transient("plugin_display_contributions", "Lines and badges plugins publish for the Library's pages; each plugin publishes them again when it next runs."),
        Transient("plugin_outbox", "Messages a plugin queued to send to an outside service; a queued share is worth little after a restore."),
        Transient("plugin_profile_connections", "Accounts a person linked to a plugin for their own profile; the values are protected with this computer's key, so each person links again after a restore."),
        Transient("service_credentials", "Service credentials; they are issued again."),
        Transient("account_invitations", "One-time invitation tokens."),
        Transient("password_recovery_codes", "One-time recovery codes."),
        Transient("password_reset_challenges", "One-time reset links."),
        Transient("two_step_challenges", "One-time sign-in challenges."),
        Transient("setup_codes", "One-time setup codes."),
        Transient("profile_admin_unlocks", "Temporary administrator unlocks on a profile."),
        Transient("device_pairing_requests", "Pairing requests from devices."),
        Transient("authorization_audit_events", "Access change audit log."),
        Transient("identity_audit_events", "Sign-in audit log."),
        Transient("system_activity", "System activity log."),
        Transient("transaction_log", "Change log."),
        Transient("ingestion_log", "Ingestion run log."),
        Transient("folder_organise_log", "Folder organise run log."),
        Transient("folder_kind_changes", "Plans of the change-what-this-folder-is jobs."),
        Transient("scheduled_task_runs", "Scheduled task run log."),
        Transient("media_operations", "Media operation queue."),
        Transient("media_operation_events", "Media operation log."),
        Transient("media_file_write_intents", "Pending file write-back jobs."),
        Transient("folder_first_check_files", "The files counted in a folder's first check."),
        Transient("file_write_runs", "When the file write-back schedule last ran."),
        Transient("encode_jobs", "Encode job queue."),
        Transient("alignment_jobs", "Book and audiobook alignment job queue."),
        Transient("adaptive_hls_packages", "Streaming copies prepared for playback."),
        Transient("offline_variants", "Offline copies prepared for devices."),
        Transient("device_downloads", "What each device has downloaded."),
        Transient("device_photo_downloads", "Which photos each device has downloaded."),
        Transient("playback_telemetry_sessions", "Playback telemetry."),
        Transient("audiobook_listen_active_segments", "Audiobook listening in progress."),
        Transient("music_play_active_segments", "Music playing in progress."),
        Transient("import_uploads", "Resumable uploads into the Import folder in progress, with the hidden working file each is written into."),
        Transient("import_upload_pieces", "Which pieces of each upload into the Import folder have arrived."),
        Transient("player_sessions", "Live player sessions."),
        Transient("player_queue_items", "Live player queues."),
        Transient("companion_sessions", "Companion screen sessions."),
        Transient("personal_status_commands", "Recent status changes kept for undo."),
        Transient("ai_usage_records", "AI usage telemetry."),
        Transient("provider_health", "Provider health telemetry."),
        Transient("provider_connection_checks", "Provider connection checks."),
        Transient("application_events", "Application event outbox."),
        Transient("application_webhook_attempts", "Webhook delivery attempts."),
        Transient("application_webhook_deliveries", "Webhook delivery queue."),
        Transient("plugin_event_cursors", "How far each plugin has read events."),
        Transient("plugin_records", "Plugin working data."),
        Transient("plugin_activity", "What plugins did and how they failed; fills again as they run."),
        Transient("plugin_lore_entities", "Lore entities found by plugins."),
        Transient("plugin_lore_relationships", "Lore relationships found by plugins."),
        Transient("media_editor_commits", "Editor save records, kept for repeat requests."),
        Transient("media_editor_commit_items", "Items of editor save records."),
        Transient("media_editor_commit_artwork", "Artwork of editor save records."),
        Transient("media_editor_edition_artwork_commits", "Edition artwork save records."),
        Transient("media_editor_preferred_artwork_commits", "Preferred artwork save records."),
        Transient("media_editor_music_pairing_commits", "Music pairing save records."),
        Transient("media_editor_music_pairing_commit_items", "Items of music pairing save records."),
        Transient("view_shared_contributions", "Shared library contribution requests."),
        Transient("view_shared_contribution_items", "Items of shared library contribution requests."),
        Transient("view_shared_contribution_events", "Shared library contribution log."),
        Transient("view_shared_transfers", "Shared library transfers in progress."),
        Transient("view_intake_files", "Photos and videos waiting in, or refused from, the Import area; the files themselves are never backed up."),
        Transient("view_duplicate_groups", "Duplicate photo groups found by the cleanup job; found again by the next run."),
        Transient("view_tidy_jobs", "Progress of Tidy this folder runs; a restored library is simply tidied again."),
        Transient("view_tidy_moves", "The rename ledger of a Tidy this folder run; it only matters while the run is going."),
        Transient("view_tidy_schedules", "Which folders are tidied every night; set again after a restore."),
        Transient("notifications", "Notifications already sent."),
        Transient("notification_channel_state", "Notification channel health."),
        Transient("notification_rate_marks", "Notification rate limiting marks."),
        Transient("backup_copies", "Records of backup copies already made."),
        Transient("ui_settings_cache", "Cached Dashboard settings."),
        Transient("onboarding_workflows", "First-run setup progress."),
        Transient("onboarding_steps", "First-run setup steps."),
        Transient("onboarding_sessions", "First-run setup sessions."),
        Transient("onboarding_restore_operations", "Restore operations started from first-run setup."),
        Transient("data_store_upgrades", "Which data store upgrade steps have run."),
        Transient("storage_metadata", "Data store bookkeeping."),

        // Videos Wave 3: the Vault. Settings and each person's own state are kept; the Vault catalogue is never backed up (a rescan rebuilds it).
        Config("profile_nsfw_settings", "How each profile's Vault mode is protected and when it turns off."),
        Config("vault_libraries", "Which libraries are Vault libraries and how they look things up."),
        Config("vault_section_grants", "Which Vault sections each profile may open."),
        Transient("vault_moves", "Move to Vault jobs; a job in flight is not worth restoring, the files are the record."),
        Transient("vault_move_items", "The items of a Move to Vault job."),
        Personal("bookmark_packs", "profile_id", "Bookmark packs a person imported for a title.", refs: [Work("work_id"), Asset("asset_id")]),
        Personal("bookmark_pack_items", null, "The bookmarks inside a pack (owned through the pack's profile).", refs: []),
        Transient("nsfw_session_unlocks", "Temporary Vault mode sessions."),
        Catalogue("vault_activity", "Quiet list of Vault ingestion activity."),
        Catalogue("vault_artwork", "Vault artwork."),
        Catalogue("vault_asset_performers", "Vault performers per video."),
        Catalogue("vault_asset_studios", "Vault studios per video."),
        Catalogue("vault_asset_tags", "Vault tags per video."),
        Catalogue("vault_field_sources", "Where each Vault field came from."),
        Catalogue("vault_file_fingerprints", "Vault file fingerprints."),
        Catalogue("vault_matches", "Vault lookup matches."),
        Catalogue("vault_performers", "Vault performers."),
        Catalogue("vault_studios", "Vault studios."),
        Catalogue("vault_tags", "Vault tags."),
        Catalogue("vault_galleries", "Vault image galleries."),
        Catalogue("vault_gallery_items", "Images inside Vault galleries."),
        Catalogue("vault_image_folders", "Vault image folders."),
        Catalogue("vault_image_items", "Vault images."),
        Catalogue("vault_image_libraries", "Vault image libraries."),
        Catalogue("vault_image_tags", "Vault image tags."),

        .. SearchIndexRules(),
    ];

    /// <summary>
    /// Tables registered before they exist on main (Videos Wave 3 adds them). The guardrail tests allow a rule
    /// for a table that does not exist only if the table is named here.
    /// </summary>
    public static IReadOnlySet<string> RegisteredAhead { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every rule, by table name (case-insensitive).</summary>
    public static IReadOnlyDictionary<string, BackupTableRule> Rules { get; } = BuildRules();

    /// <summary>The rule for a table, or null when the table is not classified.</summary>
    public static BackupTableRule? Find(string table) => Rules.GetValueOrDefault(table);

    /// <summary>Every rule of one kind, in the order they are listed.</summary>
    public static IEnumerable<BackupTableRule> OfKind(BackupDataKind kind) =>
        AllRules.Where(rule => rule.Kind == kind);

    private static Dictionary<string, BackupTableRule> BuildRules()
    {
        var rules = new Dictionary<string, BackupTableRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in AllRules)
        {
            if (!rules.TryAdd(rule.Table, rule))
            {
                throw new InvalidOperationException($"Table '{rule.Table}' is classified twice in BackupTableClassification.");
            }
        }

        return rules;
    }

    // Collections built by a person (custom collections and playlists). Rows for universes and series come from providers.
    internal const string PersonBuiltCollections = "collection_type IN ('Custom', 'Playlist', 'PlaylistFolder', 'Smart', 'Set')";

    private const string InPersonBuiltCollection = "collection_id IN (SELECT id FROM collections WHERE " + PersonBuiltCollections + ")";

    /// <summary>
    /// The current claims a person made: ones they locked, or ones written by the manual-edit provider. The provider id
    /// is written as the same 16-byte blob <see cref="GuidSql.ToBlob"/> stores, built from the constant rather than by hand.
    /// </summary>
    private static string PersonMadeClaims() =>
        "is_current = 1 AND (is_user_locked = 1 OR provider_id = X'" + Convert.ToHexString(GuidSql.ToBlob(WellKnownProviders.UserManual)) + "')";

    private static BackupTableRule Config(
        string table,
        string reason,
        CatalogueReference[]? refs = null,
        string[]? excluded = null,
        string? keepWhere = null,
        ExcludedJsonProperty[]? excludedJson = null,
        string[]? choices = null,
        string[]? portable = null) =>
        new(table, BackupDataKind.Configuration, reason, refs ?? [], excluded ?? [], null, choices ?? [], null, [], null, keepWhere, excludedJson ?? [], portable ?? []);

    private static BackupTableRule Personal(string table, string? owner, string reason, CatalogueReference[] refs, string[]? excluded = null) =>
        new(table, BackupDataKind.PersonalState, reason, refs, excluded ?? [], owner, [], null, [], null, null, [], []);

    private static BackupTableRule Catalogue(
        string table,
        string reason,
        string[]? choices = null,
        (string Column, string Flag)[]? gatedBy = null,
        string? correctionWhere = null,
        CatalogueReference[]? refs = null,
        string[]? excluded = null,
        CatalogueReference? choiceRow = null) =>
        new(table, BackupDataKind.Catalogue, reason, refs ?? [], excluded ?? [], null, choices ?? [], correctionWhere, gatedBy ?? [], choiceRow, null, [], []);

    private static BackupTableRule Transient(string table, string reason) =>
        new(table, BackupDataKind.Transient, reason, [], [], null, [], null, [], null, null, [], []);

    private static CatalogueReference Work(string column) => new(column, CatalogueTarget.Work);

    private static CatalogueReference Asset(string column) => new(column, CatalogueTarget.MediaAsset);

    private static CatalogueReference Coll(string column) => new(column, CatalogueTarget.Collection);

    private static CatalogueReference Person(string column) => new(column, CatalogueTarget.Person);

    private static CatalogueReference LocalItem(string column) => new(column, CatalogueTarget.LocalItem);

    private static CatalogueReference Other(string column) => new(column, CatalogueTarget.Other);

    private static CatalogueReference Artwork(string column) => new(column, CatalogueTarget.ArtworkAsset);

    private static CatalogueReference Fictional(string column) => new(column, CatalogueTarget.FictionalEntity);
}
