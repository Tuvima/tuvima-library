---
title: "Configuration Reference"
description: "Look up every committed config file, field, and default used by Tuvima Library."
audience: administrator
category: "reference"
product_area: "configuration"
tags:
  - "config"
  - "json"
  - "defaults"
status: current
---

# Configuration Reference

## In this page

Look up every committed config file, field, and default used by Tuvima Library.

## Where this lives in the code

- `config`
- `config/schemas`
- `src/MediaEngine.Storage/ConfigurationDirectoryLoader.cs`
- `src/MediaEngine.Domain/Configuration`

All configuration lives in the `config/` directory as individual JSON files grouped by concern. Config files are committed to git. Provider secrets (API keys, passwords) go in `config/secrets/` (gitignored). Adding a new REST+JSON provider requires only a config file - no code changes.

---

## config/network.json (host names, proxy and app access)

Desired network state. Only the fields about which host names the Dashboard answers to, the reverse-proxy port and browser-based apps are listed here; the Settings > Network pages own the rest.

| Field | Type | Default | Description |
|---|---|---|---|
| `local.allowed_hostnames` | string[] | `[]` | Extra host names (name only: no `https://`, port or path; at most 32) the Dashboard answers to. IP addresses, `localhost`, home-network names (a single word such as `nas`, or anything ending in `.local`, `.lan`, `.home.arpa`, `.internal`, `.localhost`, `.localdomain`, `.home` or `fritz.box`), this computer's name, `local.preferred_server_name`, the host of `remote.public_hostname` and `TUVIMA_TAILSCALE_URL` are always allowed. Any other `Host` gets a 400 "This address isn't allowed for Tuvima Library" (this blocks DNS-rebinding attacks). `/health/live` is exempt. |
| `remote.proxy_port` | int or null | `null` (off) | Optional second Dashboard port for a reverse proxy on the same machine (Caddy, Tailscale Serve). Must differ from `local.port`. `X-Forwarded-*` headers are honoured only on this port (from loopback plus `trusted_proxies` / `trusted_proxy_networks`). On the main port those headers are ignored. Restart the Dashboard after changing it. `TUVIMA_PROXY_PORT` overrides it, and setting `TUVIMA_TAILSCALE_URL` without either defaults it to 5017. It listens on loopback only unless a non-loopback proxy is listed in `trusted_proxies` / `trusted_proxy_networks`. |
| `remote.public_hostname` | string or null | `null` | The one public address of this server, for example `https://tuvima.example.com`. It must be an `https` address with no path, query or fragment (plain `http` is accepted only for `localhost`). Passkeys, linked sign-in and password-reset emails are built from it, and **Test remote access** checks it. This is the only public-address setting; the older email-settings copy is gone. |
| `native_app_access.allow_browser_apps` | bool | `false` | Config file only. Lets packaged TV web apps (Tizen, webOS: page origin `null`, `file:`, `app:`, `tizen:` or `webos:`) call the app door cross-origin. Phone and tablet apps need no setting: the app door is always open. Leave this off unless you use a TV web app: it also lets any website read the pairing surface (a person must still approve the code on `/pair`). The settings page keeps the value when it saves. See the note below the table. |

`local.allowed_hostnames`, `remote.proxy_port`, `auth.trusted_local_networks` and the trusted proxy lists are read at startup: restart the Dashboard after changing them.

**Packaged TV apps (cross-origin), off by default.** A TV app packaged for Samsung Tizen or LG webOS runs from `file://`, `app://`, `tizen://` or `webos://` (or the `null` origin), not from the Dashboard's address, so its browser engine asks the Dashboard for permission before it calls the app door. That permission is a separate opt-in, `native_app_access.allow_browser_apps` in `config/network.json` (edit the file; there is no settings page for it yet). It is off in a new file. Turn it on only if you use a Tizen or webOS TV web app: any website can present the `null` origin or a file page, so while it is on a website could reach the Dashboard's anonymous pairing surface (`/.well-known/tuvima`, `/api/v1/oauth/device_authorization` and `/api/v1/oauth/token`) and start a pairing request. Nothing is paired until a person signs in, opens `/pair`, reads the exact device and scopes, and approves the code, so never approve a code you did not start on your own TV. With it on, the Dashboard answers the permission check (and adds the matching headers to the real request) for those origins only, and only for `/api/v1/**` and `/.well-known/tuvima`. The permission is as narrow as it can be: methods `GET`, `POST`, `PUT`, `DELETE` and the `OPTIONS` check, request headers `Authorization` and `Content-Type`, `Retry-After` readable by the app, no credentials (apps send a bearer token in a header, never a cookie, so the Dashboard's own sign-in cookie is never usable from another site), and a check is approved only for a route the app door itself allows. Ordinary web addresses (including `http://localhost`) get no permission. With it off nothing is added. An app served from the Dashboard's own address does not need any of this.

There is no setting for how far away a visitor may be. Tuvima is open from the start: the Dashboard answers every request whatever its origin and plain http is not refused, and Tuvima sign-in is always required (closed router ports are the gatekeeper). Keys from the retired reach and app-switch settings are not read; a value left in a user's `network.json` is ignored. Phone and tablet apps are always allowed. For access from outside use Tailscale or an HTTPS reverse proxy (see [Secure remote access](../guides/remote-access.md)).

---

## config/core.json

Core Engine settings. Most changes are read at startup. Ingestion sources and destinations come from `config/libraries.json`; Settings > Libraries saves that model and asks the running Engine to hot-swap watchers.

| Field | Type | Default | Description |
|---|---|---|---|
| `schema_version` | string | `"2.0"` | Config schema version. Used for migration compatibility checks. |
| `database_path` | string | `".data/database/library.db"` | Path to the SQLite database file. Relative paths resolve from `data_root`. |
| `data_root` | string | `""` | Root directory for generated Engine data (artwork, derived files, staging). Empty means `.data/` inside `library_root`. |
| `library_root` | string | `""` | Tuvima's own library folder, where the Engine places organized media and keeps `.data/`. The shipped file uses `{TuvimaData}/library`, so a fresh install follows the server's data location (see below). |
| `organization_template` | string | - | Default file organization template. Tokens: `{Category}`, `{Title}`, `{Qid}`, `{Ext}`. |
| `organization_templates` | object | - | Per-media-type templates. Keys: `default`, `Books`, `Audiobooks`, `Movies`, `TV`, `Comics`, `Music`. TV supports `{Series}`, `{Season}`, `{Episode}` and `{EpisodeCode}` (`s01e05`, empty unless both numbers are known) tokens; `{Episode}` reads `01-e02` for a file that covers episodes 1 and 2. Music supports `{Artist}`, `{Album}`, `{TrackNumber}` tokens. A `?` after a token (`{TrackNumber?}`) leaves it out when empty instead of writing "Unknown", and a dash separator right after an optional token (`{TrackNumber?} - {Title}`) goes with it, so an unnumbered file reads `Hidden Jam.flac`. |
| `server_name` | string | `"Tuvima Library"` | Display name shown in the Dashboard title bar and system status. |
| `language` | object | - | Language preferences. See sub-fields below. |
| `language.display` | string | `"en"` | UI display language (BCP-47 code). Controls Dashboard localization. |
| `language.metadata` | string | `"en"` | Language used for provider queries (titles, descriptions). |
| `language.additional` | string[] | `[]` | Additional content languages accepted (BCP-47 codes). |
| `language.accept_any` | bool | `true` | When true, files in any language are accepted without review. When false, language mismatches trigger a visible warning on Dashboard browse and detail surfaces. |
| `country` | string | `"US"` | Country code. Used by providers that return region-specific data (e.g., Apple API storefronts). |
| `date_format` | string | `"system"` | Date display format. `"system"` uses the OS locale. Accepts standard format strings. |
| `time_format` | string | `"system"` | Time display format. `"system"` uses the OS locale. |
| `plugin_catalog.enabled` | bool | `true` | The plugin directory switch (Settings > Plugins > Available plugins). When `false` the Engine makes no request to GitHub and shows the saved copy, if there is one. |
| `plugin_catalog.approved_plugins_url` | string | GitHub raw URL | HTTPS GitHub URL for the plugin directory list ([format](plugin-catalog.md)). The Engine accepts `raw.githubusercontent.com` or `github.com` sources only. |

**Server data location and the `{TuvimaData}` placeholder.** The shipped `config/core.json` and `config/libraries.json` contain no absolute paths. A path that starts with `{TuvimaData}` is replaced when the file is loaded by the server's data location: the `TUVIMA_DATA_DIR` environment variable if set, otherwise `%LOCALAPPDATA%\Tuvima\data` on Windows and `~/.local/share/tuvima` on Linux and macOS. Tuvima's own library folder (storage location id `media`, which also holds the View personal space) is created there automatically the first time the server runs. It is never shown in a folder picker or asked about in setup; if it cannot be created or written, the silent server check in setup fails with one message naming the folder and the fix. The Docker image ships its own `docker/config` files that use `/library`, so containers are unchanged. No import folder is shipped: the `incoming` storage location does not exist until someone sets one in Libraries. The developer real-media harness writes its own paths into `real-media-harness.json` and the configuration it prepares; it does not rely on shipped paths.

**Backward compatibility:** `language` accepts both legacy string form (`"language": "en"`) and the new object form. The deserializer handles both.

---

## config/ai.json

AI/ML subsystem configuration. Controls model selection, the resource profile and the audio pack.

### models

| Field | Type | Description |
|---|---|---|
| `models.{role}.catalog_key` | string | Selected `model_catalog` key for the role. Used to explain capabilities, validation gates, and selection rationale. |
| `models.{role}.file` | string | Model filename in the local model directory. |
| `models.{role}.download_url` | string | Source URL used by the local downloader. |
| `models.{role}.context_length` | int | Model context window size. |
| `models.text_fast` | object | Defaults to Qwen3 0.6B Q8 for on-demand, low-latency inference. |
| `models.text_quality` | object | Defaults to checksum-pinned Qwen3 1.7B Q5_K_M for batch ingestion tasks. |
| `models.text_scholar` | object | Defaults to Qwen3 4B Q4_K_M for hard enrichment when smaller roles fail validation. |
| `models.text_cjk` | object | Defaults to Qwen3 4B Q4_K_M for CJK and multilingual analysis. |
| `models.audio` | object | Defaults to Whisper Medium for timestamped speech-to-text, language detection, and sync. |
| `audio.language` | string | `"auto"` - automatic language detection. Set to a BCP-47 code to force a specific language. |

### model_catalog

The catalog lists current, baseline, candidate, experimental, and escalation models. Each entry declares runtime, license, intended roles, size, source URL, capabilities, and validation gates. Gemma 4 12B is cataloged as an escalation model, not a default role.

### role_requirements

Role requirements define the promotion gates for each role. Tuvima selects the smallest candidate that passes the role's latency, JSON validity, task accuracy, hallucination, WER, and timestamp drift requirements.

### AI features are plugins

Which Local AI features run is no longer set in `config/ai.json`. Smart labelling, Type helper, Series order, Vibe tags and Description insights are Official plugins, each off until an administrator turns it on (and approves what it uses) on **Settings > Plugins**. Their schedules and the words Vibe tags may use are plugin settings, saved under `.data/plugin-config/{plugin id}.json` in the library root:

| Plugin | Id | Default schedule |
|---|---|---|
| Series order | `tuvima.ai-series-order` | `0 3 * * *` |
| Vibe tags | `tuvima.ai-vibe-tags` | `0 4 * * *` |
| Description insights | `tuvima.ai-description-insights` | every 15 minutes |

The Vibe tags plugin keeps its vocabulary in the settings lists `books`, `movies_tv`, `music` and `comics`; an empty list falls back to the built-in words. The old `features`, `vibe_vocabulary` and `scheduling` sections of `config/ai.json` were removed.

---

## config/scoring.json

Priority Cascade thresholds. Controls how the Engine resolves metadata conflicts.

| Field | Type | Default | Description |
|---|---|---|---|
| `auto_link_threshold` | float | `0.85` | Minimum confidence score required for automatic promotion from staging to the organized library. Items below this threshold are held for review. |
| `conflict_threshold` | float | `0.6` | Claims below this confidence are excluded from cascade evaluation. |
| `conflict_epsilon` | float | `0.05` | If two claims are within this margin of each other, the field is flagged as conflicted rather than resolved. |
| `stale_claim_decay_days` | int | `90` | Number of days after which a claim's confidence begins decaying. |
| `stale_claim_decay_factor` | float | `0.8` | Multiplier applied to confidence on each decay cycle after `stale_claim_decay_days`. |

---

## config/hydration.json

Enrichment pipeline configuration. Controls stage concurrency, timeouts, retail matching thresholds, and optional deferred deep enrichment.

Current shipped default note: `two_pass_enabled` is `false`, so the optional two-pass mode is documented here but is not the default runtime path.

| Field | Type | Description |
|---|---|---|
| `stage_concurrency` | int | Maximum concurrent provider calls within each stage. |
| `stage1_timeout_seconds` | int | Per-provider HTTP timeout for the Stage 1 provider identity/enrichment worker. |
| `stage2_timeout_seconds` | int | Per-request timeout for the Stage 2 Wikidata worker. |
| `stage3_timeout_seconds` | int | Timeout for scheduled Stage 3 enrichment work, shown across the People, Relationships, and Artwork rows in the Ingestion page. |
| `retail_auto_accept_threshold` | float | Composite Retail score required for automatic acceptance. Current config: `0.90`. |
| `retail_ambiguous_threshold` | float | Composite Retail score below which a candidate is treated as no match. Scores from this threshold up to the auto-accept threshold go to review. Current config: `0.65`. |
| `auto_review_confidence_threshold` | float | Post-hydration confidence gate for creating review work. |
| `two_pass_enabled` | bool | Enables the optional two-pass identity mode. `false` means the normal inline identity pipeline remains the default path. |
| `pass1_core_properties_only` | bool | When two-pass mode is enabled, reduces Pass 1 to core properties only. |
| `pass2_idle_delay_seconds` | int | Delay between idle checks before processing Pass 2 work. |
| `pass2_rate_limit_ms` | int | Delay between Pass 2 items to respect external rate limits. |
| `pass2_stale_threshold_hours` | int | Age after which a pending Pass 2 item is considered stale. |
| `pass2_batch_size` | int | Maximum number of Pass 2 items processed in one batch. |
| `wikidata_batch_size` | int | Maximum entities per Wikidata batch API call. |
| `collection_rollup_relationship_types` | string[] | Relationship types that can create broader cross-media collection rollups. Defaults to `series`, `franchise`, `fictional_universe`, and `based_on`. |
| `local_match_enabled` | bool | Enables the local match shortcut before external calls. |
| `local_match_fuzzy_threshold` | float | Fuzzy threshold for local title+creator matching. |

---

## config/pipelines.json

Defines the ranked provider list and execution strategy per media type. This file replaced the legacy `config/slots.json` fixed 3-slot system and supports unlimited ranked providers.

| Field | Type | Description |
|---|---|---|
| `{MediaType}.strategy` | string | Execution strategy: `"Waterfall"` (first match wins), `"Cascade"` (all run, claims merge), or `"Sequential"` (chained, each feeds the next). |
| `{MediaType}.providers` | array | Ordered list of provider entries. Each entry has `rank` (int, execution order), `name` (string, must match provider config `name` field), `purpose`, and optional role gates such as `requires_identity`. |
| `{MediaType}.providers[].purpose` | string | Provider role in this media pipeline. `identity` candidates can own the selected identity; `enrichment` providers can still contribute fields, bridge IDs, artwork, and retained provider links. |
| `{MediaType}.providers[].requires_identity` | bool | When true, the provider only runs after an earlier configured identity provider has accepted an identity in the same pipeline stage. This supports chains such as MusicBrainz identity followed by Apple enrichment without hard-coding provider names in orchestration. |
| `{MediaType}.field_priorities` | object | Per-field provider priority overrides for this media type. Key = claim key (e.g., "cover", "description"). Value = ordered list of provider names. Checked before global `field_priorities.json`. |

Default strategies:
- **Waterfall:** Movies, TV, Comics
- **Cascade:** Books
- **Sequential:** Audiobooks, Music

All six media types (Books, Audiobooks, Movies, TV, Music, Comics) must have at least one provider entry.

Music is configured as a sequential Stage 1 chain: MusicBrainz has `purpose: "identity"` and Apple API has `purpose: "enrichment"`. That means MusicBrainz owns track, album, artist, release, and recording identity fields when available, while Apple can still win configured enrichment fields such as cover art, genre, storefront IDs, year, and retail metadata. This is a configuration rule, not a music-specific provider-order branch in orchestration code.

---

## Sequence Placement Configuration Contract

Sequence placement is structural, not title-specific. Configured providers may
map fields such as `comic_vine_volume_id`, `track_count`, `episode_count`,
`sequence_total`, `sequence_total_scope`, `disc_number`, `track_number`,
`issue_number`, and `series_position`; the Engine normalizes those into
`ordinal_sort`, shelf identity, and child identity keys by media type.

Do not add provider mappings or field-priority overrides that special-case a
specific title to fix a visible count. Fix the container classification,
provider mapping, or media-type resolver so a fresh ingestion works for the
whole class of media.

---

## config/field_priorities.json

Global per-field provider priority overrides used by Tier B of the Priority Cascade. Per-media-type overrides in `config/pipelines.json` take precedence over these global settings.

| Field | Type | Description |
|---|---|---|
| `field_overrides.{field}.priority` | string[] | Ordered list of provider names. First provider with a claim for this field wins. |

Common fields configured here: `cover`, `description`, `rating`, `narrator`, `duration`, `page_count`. Structured fields (title, author, year, genre, series) are handled by Tier C (Wikidata authority) and typically do not need Tier B overrides.

---

## Auto-filing rules

Auto-filing rules are not a configuration file. Each person makes their own on the Documents page (**Auto-filing rules** button; administrators also see the list in Settings > Libraries > View) (a household administrator can also make rules for the household's Shared Library). They are stored in the data store (`doc_rules`, `doc_rule_conditions`, `doc_rule_actions`, see the [database schema](database-schema.md)) and run as the `view.document_apply_rules` background job, one document at a time after its words are read. Nothing is sent anywhere. Rules use plain words with no patterns, and are limited to 200 rules per person, 20 conditions and 20 actions per rule. The Inbox suggestions use the same job and need no setting.

---

## config/libraries.json

Defines the clean schema 7 library model used by **Settings > Libraries**. The root contains explicitly approved `storage_locations`, `personal_library_policy`, `file_handling`, and `libraries`.

| Field | Type | Description |
|---|---|---|
| `schema_version` | string | Must be `7.0`. Older files (including `6.0`) are refused with a plain message and are not converted; move the file aside to start from a fresh default and add folders again. |
| `storage_locations` | array | Explicit server/container roots administrators may browse. Each entry has a stable `id`, friendly `label`, absolute `path`, and `allow_write` policy. |
| `personal_library_policy` | object | Administrator defaults and capability switches for the internal personal-library bridge, managed/existing storage, browser upload, reserved future intake producers, and default visibility. |
| `personal_library_policy.document_text_max_characters` | integer | Most characters of embedded text stored per View document (default 1000000); longer text is cut and flagged as truncated. |
| `personal_library_policy.document_text_max_pdf_pages` | integer | Most PDF pages whose text layer is read (default 2000). |
| `personal_library_policy.document_text_read_timeout_seconds` | integer | Longest one document may take to read before it is abandoned (default 60). |
| `file_handling.duplicate_policy` | string | What happens to an exact copy of a file already held, for every library: `keep_one` (default; the copy is indexed once and the incoming duplicate is removed where its folder is managed) or `keep_both` (both files stay). A name clash between different files always gives the incoming file a unique name. |
| `id` | GUID string | Stable library identity used by intake, indexing, access, and Dashboard editing. Required. |
| `name` | string | Configuration/admin name. For View, the profile-facing identity is the Personal Space, not this library record. |
| `category` | string? | Structured subtype such as Books, Movies, TV, Music, Audiobooks, or Comics. Personal libraries may omit it. |
| `kind` | string | Exactly `catalogued` or `personal`. |
| `area` | string | `read`, `watch`, `listen`, or `view`. |
| `presentation` | string | Catalogue, gallery, mixed gallery, timeline, video, documents, audio, or mixed presentation. |
| `metadata_policy` | string | `enriched`, `local_preferred`, `local_only`, or `manual`. The last two bypass external identity/provider work. |
| `media_types` | string[] | Structured media types expected by catalogued libraries. A View Personal Space may index mixed local media through its backing personal library. Each media type may belong to only one catalogued library; the Engine rejects a change that adds a second one (older duplicates already saved are tolerated). |
| `sources` | object[] | Stable source objects; see below. Array order has no meaning. |
| `primary_destination_source_id` | GUID string? | The one folder new media is filed into: a managed source of this library, or `null` when there is none. An as-is folder is never a destination. On save the first managed folder added becomes the destination; if it is removed or switched to as-is, the earliest-added remaining managed folder takes over, and with none left it is `null`. If the destination is unavailable, files wait in Import; there is no fallback to another folder. |
| `owner_profile_id` | GUID string? | Owning profile for personal access decisions. |
| `visibility` | string | `private` or `shared` for current product presentation. Shared View contribution is separately controlled by profile View policy. |
| `authorized_profile_ids` | GUID string[] | Explicit members of a shared library. |
| `accepted_intake_modes` | string[] | Accepted producer vocabulary. Incoming folder and browser upload have current paths; drag-and-drop, mobile backup, connected-device import, and direct API producers are reserved until a real client is implemented. |
| `organization_policy` | object | `mode`, optional `custom_template`, and `write_tags` (default `true`): write tags into this type's media files, only in managed folders, only for formats that can carry tags, and only for fields allowed by `writeback-fields.json`. |

Each source owns `id`, `path`, `management_mode`, `source_type`, `include_subdirectories`, `access_mode`, `conversion_state`, `conversion_operation_id`, `notes`, `device_id`, and `profile_id`. A folder is in one of two states. **Managed** (`managed_by_tuvima`): Tuvima moves, renames and tags files, including the ones already there. **As-is** (`existing_library`): read-only, never moved, renamed, tagged or deleted, and never a destination. `access_mode` follows the management mode and is set when the file is saved (managed is `writable`, as-is is `read_only`). `conversion_state` is `none`, `converting` or `converted` while an as-is folder is being switched to managed; `conversion_operation_id` names the organising job doing it (an `organise_folder` operation on the `folder_jobs` queue) and is set while converting and kept as `converted` once the job ends, so the log stays reachable. Both are owned by the Engine: a settings save keeps the values the Engine has, and saving a folder as As-is resets them to `none`. See the organising job in [Library model and intake](../architecture/library-model-and-intake.md). A folder merged into another managed folder also carries `merge_state` (`none`, `merging` or `merged`), `merge_operation_id` (the `merge_folder` operation while merging), `merge_target_source_id` (the managed folder it goes into), `merge_mode` (`copy` or `move`) and `merged_at` (when it finished). They are Engine-owned in the same way, apply to As-is and Managed folders alike, and a `merged` folder is no longer scanned or watched; see the merge job in the same architecture page. The optional import folder is the storage location with id `incoming`; when it is set, every media type needs a destination (a managed folder chosen as that type's destination) before import runs. Only **Managed by Tuvima** plus writable sources can authorize mutation, and only the destination (or a merge target the person started) can receive files. A hand-edited file must already be consistent (for example a managed folder says `writable`, a destination is a managed folder, and `conversion_state` other than `none` needs a managed folder); Tuvima derives these when it saves the file but rejects an inconsistent one when it loads it.

**Import folder.** One optional server folder where new media is dropped, always called the **Import folder**: the storage location with id `incoming`, writable, never overlapping a library folder and never offered as a library folder. Setting it creates six per-type folders inside it, `Movies`, `TV`, `Music`, `Books`, `Audiobooks` and `Comics` (point Sonarr, Radarr and similar programs at them). Removing it deletes nothing; files and folders stay on disk. The Engine watches the folder and files what arrives with the same pipeline as everything else: the file settles and is fingerprinted, scanned and identified, and then moves into exactly one managed folder for its type, the folder marked **New media** (the library's `primary_destination_source_id`), named with that type's preset. A file in a per-type folder gets that type as a strong prior; a file at the root is classified like any other. Rules that never bend:

- **One destination, no fallback.** An as-is folder is never a destination. If the marked folder is offline, full or not writable, the file stays untouched in the Import folder; nothing is filed into another managed folder, and it is filed when the folder returns.
- **No managed folder, no move.** A type with no managed folder leaves its files untouched (never moved, never deleted). `GET /ingestion/import-folder/waiting` returns one plain sentence for it, for example "3 files in Import\Comics can't be filed until Comics has a managed folder." The next pass (the watcher, the periodic sweep, or **Check folders for changes**) files them once a managed folder exists. This is a behaviour, not a state: there is no waiting status.
- **Unmatched or low-confidence files** stay in the Import folder and appear in Review with the badge "From the Import folder" (`from_import_folder` on a review item); they are filed when the review is resolved.
- **Duplicates** follow the global rule `file_handling.duplicate_policy`. Under `keep_one` only the copy in the Import folder is removed; under `keep_both` the import copy is filed beside the copy that is held with a unique name (a file is indexed once, so the kept copy is not a second library item; the history records where it went). A copy already held in a library folder is never deleted.
- **Safe moves.** On the same drive a file is renamed. Across drives it is copied to a temporary file beside its final name, checked against the fingerprint taken at ingestion, renamed into place, and only then removed from the Import folder, so an interrupted move never loses the file and a restart never leaves two copies. A name that is already taken always gets a unique suffix; nothing is overwritten. Each filing is first written to a small record in the Import folder's own `.data/filing` folder and cleared once the data store points at the new location, so a stop between the two is finished on the next start (the file is never treated as missing). The temporary copy sits beside the final name in the destination folder, where the watcher ignores it, rather than in a staging folder.
- **Cleanup stays inside.** Empty folders that a download program leaves behind are removed, but never the Import folder, a per-type folder or the working folder (`.data`) it keeps for Tuvima, and never anything outside the Import folder.

The Engine reads the folder settings again for every filing decision, so setting the Import folder, adding a managed folder, or marking a different New media folder applies without a restart.

Adding, removing or changing a library folder (its path or its **Include subfolders** setting) in Settings also starts or stops watching it straight away, and a new folder is scanned once for the files already in it. No Engine restart is needed. A read-only (As-is) folder is only read, and one that is not reachable, such as an offline drive, is logged and not created. See [Ingestion pipeline](../architecture/ingestion-pipeline.md).

Each enabled profile resolves one View Personal Space, backed by one internal
`personal` library bridge and any number of sources/devices. It indexes mixed
local assets and local metadata without entering catalogue matching, canonical
claims, providers, Wikidata, or Review Queue. Sources remain inputs to one
Personal Space rather than separate libraries. View exposes Photos, Folders,
Galleries, People, and Places. Folders presents source and directory identity
for browsing; source/device administration belongs in Settings.

View access is not inferred from library visibility. Administrator-managed
profile policy separately stores View enabled, access Shared View, include in
Shared View, and share Galleries. The active profile's last authorized scope
and timeline density are database preferences, not `libraries.json` fields.

---

## Dashboard/Engine View security settings

The Dashboard signs eligible Engine `/view` and `/collections` requests with
the active profile by using the same server-held API key as HMAC input. These
headers are server-to-server only and must not be generated from browser state.

| Setting | Default | Description |
| --- | --- | --- |
| `MediaEngine:Security:ViewProfileAssertionMaxSkewSeconds` | `120` | Engine acceptance window for trusted profile assertions; clamped to 1–300 seconds. |
| `View:MediaGrantKey` | Derived from the Engine API key | Optional Dashboard-only secret for same-origin View media grants. When neither key exists, a random process key is used and grants expire on restart. |
| `View:MediaGrantLifetimeSeconds` | `600` | View thumbnail/original grant lifetime; clamped to 30–900 seconds. |

The signed assertion binds profile ID, Unix timestamp, method, and exact
path/query. Same-origin media grants additionally bind profile, library, asset,
resource kind, and expiry. Neither mechanism makes a browser-supplied library,
Gallery, scope-profile, or asset ID authoritative.

---

## config/media_types.json

Declares supported extensions by media type. The scanner, initial sweep, generic processor fallback, media-type resolver, and playback fallback read this catalog so extension behavior stays consistent across ingestion and Dashboard playback.

| Field | Type | Description |
|---|---|---|
| `{MediaType}.extensions` | string[] | File extensions associated with the media type. Extensions can include or omit the leading dot. |
| `{MediaType}.aliases` | string[] | Alternate labels normalized to the media type where present. |

Ambiguous extensions can appear in more than one media type. The resolver uses folder context, embedded metadata, filename patterns, and stronger extension rules to pick a media type. Current governance rules treat `.m4b` as an audiobook fallback, `.mp3` as music when no stronger audiobook evidence exists, and video containers such as `.mkv` as movie fallbacks unless TV evidence is present.

---

## config/maintenance.json

Scheduled maintenance task configuration.

| Field | Type | Default | Description |
|---|---|---|---|
| `activity_retention_days` | int | `90` | How long system activity (Events) log entries are kept before the nightly prune removes them. 1 to 3650. Set on System > Logs > Retention. |
| `activity_max_events` | int | `500000` | The most system activity entries kept. Once exceeded, the nightly prune removes the oldest first (after the day limit is applied). 1,000 to 10,000,000. Personal activity is not covered. |
| `log_retention_days` | int | `90` | How long the Engine's log files (`tuvima-*.log`) are kept. 1 to 3650. Serilog's own 14-file limit is off, so this and `log_max_total_mb` are the only rules. |
| `log_max_total_mb` | int | `1024` | Total size of all Engine log files together, in megabytes. Once exceeded, the nightly clean-up removes the oldest files first; the file being written is never removed. 100 to 102,400. |
| `vacuum_interval_hours` | int | `168` | How often SQLite VACUUM runs to reclaim disk space (default: weekly). |
| `sync_interval_hours` | int | `24` | How often metadata writeback sync runs for pending changes. |
| `reconciliation_interval_hours` | int | `24` | How often the Wikidata reconciliation refresh cycle runs. |
| `rejected_retention_days` | int | `30` | How long explicitly rejected files remain in `.data/staging/rejected/` before deletion. |
| `storage_maintenance.enabled` | bool | `true` | Enables the nightly storage maintenance hosted service. |
| `storage_maintenance.search_cache_max_age_days` | int | `30` | Age threshold for provider, resolver, and search cache purges. |
| `storage_maintenance.image_cache_retention_days` | int | `30` | Age threshold for unused image cache rows/files. User-preserved images are not removed. |
| `storage_maintenance.claim_compaction_batch_size` | int | `5000` | Maximum duplicate non-user-locked metadata claims compacted in one pass. |
| `schedules.activity_pruning` | cron | `0 3 * * *` | Activity log and timeline cleanup. |
| `schedules.library_reconciliation` | cron | `0 5 * * *` | Check that known files still exist on disk. |
| `schedules.missing_universe_sweep` | cron | `0 4 * * 0` | Retry Universe matches for works that have none. |
| `schedules.rejected_file_cleanup` | cron | `0 4 * * *` | Delete rejected files past `rejected_retention_days`. |
| `schedules.universe_enrichment` | cron | `0 3 * * *` | Universe enrichment maintenance sweep. |
| `schedules.pass2_nightly_sweep` | cron | `0 2 * * *` | Nightly catch-up for deferred (pass 2) enrichment. Only the minute and hour are used. |
| `schedules.retag_sweep` | cron | `0 3 * * *` | Re-apply file tags after the tag settings change. |
| `schedules.storage_maintenance` | cron | `0 2 * * *` | Nightly storage maintenance schedule. |
| `schedules.plugin_segment_detection` | cron | `0 2 * * *` | Scheduled playback segment detection run by enabled segment plugins. |
| `schedules.log_file_retention` | cron | `30 3 * * *` | When Engine log files older than the limit are removed. |

Every recurring task with its schedule, next run and last run is listed by `GET /system/scheduled-tasks`. The Local AI schedules are plugin schedules (see `config/ai.json` above) and are listed here with the key `plugin:{plugin id}:{job}`. The keys `vibe_batch`, `series_check`, `description_intelligence`, `whisper_bake` and `taste_profile_update` were removed from the `schedules` defaults because no code read them.

Administrators can run the same maintenance manually with `POST /maintenance/storage/run?dryRun=true`.

---

## config/writeback.json

Controls how resolved metadata is written back into file tags.

| Field | Type | Default | Description |
|---|---|---|---|
| `enabled` | bool | `true` | Master switch. When false, no file tags are ever modified. |
| `write_on_auto_match` | bool | `true` | Write tags when the Engine automatically matches and promotes a file. |
| `write_on_manual_override` | bool | `true` | Write tags when a user manually resolves a conflict or selects a QID. |
| `write_on_universe_enrichment` | bool | `true` | Write tags after Wikidata/universe enrichment completes. |
| `backup_before_write` | bool | `false` | Create a `.bak` sidecar before modifying any file tags. Disabled by the standing pre-beta no-backup rule. |
| `fields_to_write` | string | `"all"` | Which fields to include in writeback. `"all"` writes every resolved canonical value. Can be set to a comma-separated list of field keys to restrict scope. |
| `exclude_fields` | string[] | `[]` | Fields excluded from writeback even when `fields_to_write` is `"all"`. |

---

## config/providers/

One JSON file per metadata provider. All provider files are self-contained - adding a new REST+JSON provider requires only dropping a config file and restarting. Provider secrets go in `config/secrets/` (gitignored).

| File | Provider | Stage | Language Strategy |
|---|---|---|---|
| `apple_api.json` | Apple API (books, audiobooks, music enrichment) | Stage 1 provider metadata and primary artwork; for music, configured after MusicBrainz as enrichment | `localized` |
| `comicvine.json` | Comic Vine (comics) | Stage 1 provider metadata and primary artwork | `source` |
| `musicbrainz.json` | MusicBrainz | Stage 1 music identity; also available to slower enrichment where configured | `source` |
| `tmdb.json` | TMDB (movies, TV) | Stage 1 provider metadata and primary artwork; Stage 8 ranked movie/TV artwork refresh | `localized` |
| `wikidata_reconciliation.json` | Wikidata | Stage 2 Wikidata | `both` |
| `local_filesystem.json` | Local file metadata (processors) | Stage 0 read media details | `source` |
Artwork config controls source access only. Display surfaces should still use
managed artwork URLs emitted by the Engine after accepted provider art is cached
under `.data/assets`; provider URLs are retained as provenance/source inputs,
not as stable UI image URLs.

### wikidata_reconciliation.json - Single Source of Truth

This file is the authoritative configuration for all Wikidata-related behaviour. In addition to the common provider fields above, it contains:

| Section | Description |
|---|---|
| `instance_of_classes` | Per-media-type P31 type allow-lists for Wikidata candidate filtering. TV includes miniseries (Q1259759), web series (Q526877), anime television series (Q63952888) and animated television series (Q117467246). Previously stored separately in `cirrus-type-filters.json` (now removed). |
| `edition_pivot` | Per-media-type rules for walking from Wikidata edition items to work items. Previously stored in `edition-pivot.json` (now removed). Keys: `audiobooks`, `books`, `music`. Each has `work_classes`, `edition_classes`, and `prefer_edition`. |
| `exclude_classes` | P31 classes to exclude from reconciliation results. |
| `bridge_resolution.scopes` | Entity-scoped bridge-ID policy. Each scope declares `target_ids`, `context_ids`, and whether constrained text fallback is allowed. Music uses `MusicTrack` to prefer recording/work/ISRC IDs before Apple track IDs, while release/release-group/artist IDs stay contextual unless resolving `MusicAlbum`. |
| `data_extension` | Properties fetched during the Data Extension API call after QID resolution. A property is read only when it is in the group for its entity type (`work_properties`, `character_properties`, and so on) and has a `property_labels` entry. Character relationships include significant person (P3342) and enemy (P7047); Works carry based on (P144) and derivative work (P4969), which become adaptation links between Works. |

**Removed config files (consolidated here):**
- `config/cirrus-type-filters.json` - merged into `instance_of_classes`
- `config/edition-pivot.json` - merged into `edition_pivot`
- `config/universe/wikidata.json` - property map moved to `docs/reference/wikidata-property-map.md`; instance_of_classes already present here
- `config/slots.json` - replaced by `config/pipelines.json`

### Common provider fields

| Field | Type | Description |
|---|---|---|
| `provider_id` | string (GUID) | Stable UUID. Matches foreign keys in `metadata_claims`. Never change after first use. |
| `enabled` | bool | Whether the provider is active. |
| `api_key` | string | API key or token. Leave empty for providers that do not require authentication. |
| `base_url` | string | Provider API base URL. |
| `priority` | int | Tier B cascade priority. Lower numbers = higher priority within the same tier. |
| `media_types` | string[] | Which media types this provider serves. |
| `language_strategy` | string | `"source"` - always English. `"localized"` - user's metadata language. `"both"` - query twice and merge. |
| `rate_limit_ms` | int | Minimum milliseconds between requests to this provider. |
| `cache_ttl_hours` | int | How long provider responses are cached in `provider_response_cache`. |
| `preferred_bridge_ids` | object | Per-media-type ordered bridge identifiers preferred by this provider. Stage 2 uses this to order bridge resolution, so music tries MusicBrainz IDs before Apple IDs. |

### Provider file sections

A provider file can describe everything the Library does with that provider, so a REST + JSON provider needs no new code. The sections below are all optional and sit beside the common fields. The shipped files are the worked examples: `tvdb.json` (sign-in, named endpoints, flows), `tmdb.json` (follow-up lookups), `comicvine.json` (ranking, result rules, sequence manifest) and `subdl.json` / `lrclib.json` (text tracks). The editor schema is `config/schemas/providers/provider.schema.json`.

A file is checked when it loads and when it is saved. A property the Library does not know, a path that is not allowed, or a rule that does not fit its kind is rejected with its place in the file (for example `auth.login.token_path`), and the file is not used.

| Section | Field names | What it does |
|---|---|---|
| `auth` (sign in, then a token) | `login.method` (`POST` only), `login.path` (starts with `/`), `login.body`, `login.token_path`, `login.lifetime_days`, `login.refresh_early_minutes`, `deliver.header`, `deliver.format`, `retry_on_unauthorized` (0 or 1) | One JSON sign-in request returns a token. `login.body` values are exactly one credential, `{api_key}`, `{pin}`, `{username}`, `{password}`, `{client_key}` or `{access_token}`, or plain text; `{pin?}` leaves the property out when that credential is blank. The token is kept in memory for `lifetime_days`, renewed `refresh_early_minutes` before it expires, and sent as `deliver.header` / `deliver.format` (`Bearer {token}`). A 401 signs in again and repeats the request once when `retry_on_unauthorized` is 1. Needs `api.allowed_hosts`. |
| `api` (named endpoints) | `allowed_hosts`, `data_path`, `cache_minutes`, `cache_max_entries`, `endpoints.{name}.path`, `.args`, `.defaults`, `.cache_minutes`, `.paging` (`start`, `max_pages`, `items_path`, `next_path`) | The API the sign-in and other parts of the Library call. `endpoints['api']` must be HTTPS on the default port, without credentials, on one of `allowed_hosts`, or no request is made. Argument kinds are `digits`, `alpha3`, `text` and `one_of:a,b,c`; a trailing `?` makes one optional, and a `[/{lang}]` segment is dropped when its argument is not given. |
| `follow_ups` (extra lookups) | `name`, `media_types`, `inputs`, `url`, `cache.store` (`response_cache` or `memory`), `cache.key`, `on_error` (`log_and_continue` or `propagate`), `add`, `required_paths`, `rules` | After a search strategy matched a result, one more request whose URL is built from `inputs` (each a list of sources tried in order: `claim:{key}`, `result:{path}`, `parent:{path}`, `request:{field}`, `hint:{key}`; a name ending `?` is optional) and whose answer becomes claims through `rules`. The URL may use `{base_url}`, `{api_key}` and `{locale}`; it is skipped when it needs a key that is not set. A `lookup` rule nests another follow-up. |
| `result_rules` and `search_strategies[].claim_rules` | `name`, `media_types`, `add`, `rules`; `drop`, `drop_non_english_text`, `derive` | Claim rules that plain `field_mappings` cannot express: creators listed with free-text roles (`role.map`), lists joined into one value, claims dropped or derived from the request. Rule kinds include `priority_pick`, `items`, `lookup` and `sequence`; `claim_key`, `json_path` / `json_paths`, `confidence`, `transform`, `template` and `join` shape one claim. `add` is `always`, `if_missing_key` or `if_missing_value` (default). |
| `candidate_ranking` (tie order) | `name`, `media_types`, `facts`, `require_facts`, `candidates` (`match`, `fields`, `score`, `identity`), `preferred_run`, `enrich_when_multiple`, `final_terms`, `order` | How one result is picked from a list when plain title matching cannot tell near-identical results apart. `facts` read values from the request; each candidate must pass `match`, earns a `score` from weighted terms (year bands, title overlap, counts), and `order` breaks ties as `field:desc` / `field:asc` (`score` is always available). |
| `sequence_manifest` | `enabled`, `url_template`, `fields`, `page_size`, `max_pages`, `container_kind`, `expected_total_kind` | Reads the whole list of members of a series from the provider, page by page, so a missing issue can be shown as a named gap. |
| `text_track` (subtitles and lyrics) | `kind` (`Subtitles` or `Lyrics`), `noun`, `api.allowed_hosts`, `api.accept`, `http.allow_auto_redirect`, `http.max_response_buffer_bytes`, `language.aliases`, `language.unknown`, `search`, `download`, `errors`, `messages` | Used with `"adapter_type": "text_track"`: one built-in adapter searches and downloads for any subtitle or lyrics provider that declares its requests, item paths, scoring and error codes here. `download.mode` is `payload` (the text came with the search) or `fetch` (downloaded from a link, only from `download.allowed_hosts`, only under `path_prefix`, up to `max_bytes`, with limits on archives). When `requires_api_key` is true, `endpoints['api']` must be an HTTPS address on the default port with no credentials and `api.allowed_hosts` must list at least its host, so the key only reaches that host; redirects elsewhere are refused. |

### Provider and plugin secrets

Keys, PINs, passwords and tokens never go in a committed file. One config-folder store holds them, kept apart from the data store, so they survive a data store reset and are left out of backups (`config/secrets/` is gitignored):

| Owner | File | Content |
|---|---|---|
| A provider | `config/secrets/{provider}.json` (the provider's `name`) | A flat JSON object of strings: `api_key`, `api_key_override`, `client_key`, `access_token`, `pin`, `username`, `password`. They are laid over the provider's file when it loads, so a blank `api_key` in the provider file can still have a working key here. |
| A plugin | `config/secrets/plugins/{id}.json` (the plugin id) | A flat JSON object of the names the plugin declared in `secrets`. A plugin reads only its own values. |

Names are lower-case letters, digits, `-`, `_` and `.`. The Dashboard writes these files when a key is saved; a safe connection check never echoes the value.

---

## Tools and requirements (environment)

| Variable | Values | Description |
|---|---|---|
| `TUVIMA_INSTALL_KIND` | `windows_installer`, `docker`, `source` | Tells the tools list how this install was set up, so the fix for a missing tool reads right (reinstall with the Windows installer, update the Docker image, or a package-manager command). The Windows installer sets `windows_installer` on the Engine service and the Docker image sets `docker`. When unset, a container is `docker` and anything else is `source`. |

The Engine keeps one catalogue of tools (FFmpeg, smooth streaming, Tesseract, the image engine, the local AI engine and each AI model). FFmpeg is always **required**, because every Library starts with Movies, TV, Music and Audiobooks set up. The catalogue drives the setup server check, `GET /settings/tools`, one summary line in the Engine log at start-up, and the `media_runtime` and `ai_models` health checks. A missing tool is reported and never blocks setup.

## config/transcoding.json

Controls FFmpeg discovery, adaptive playback packages, offline variants, concurrency, and cache retention.

| Field | Type | Default | Description |
|---|---|---|---|
| `ffmpeg_binary_path` / `ffprobe_binary_path` | string | `""` | Explicit executable paths. Empty searches, in order: `tools/ffmpeg/` in the Tuvima data folder, `tools/ffmpeg/` next to the Engine, `PATH`, then the operating system's standard install folders (Windows: `Program Filesfmpegin`, `C:fmpegin`, WinGet, Chocolatey and Scoop shims; macOS: `/opt/homebrew/bin`, `/usr/local/bin`). An explicit missing path fails capability detection instead of silently falling back. **Check again** in Settings (`POST /settings/tools/check`) searches again without a restart; work already running keeps the copy it started with. |
| `hardware_acceleration` | string | `"auto"` | `auto`, `nvenc`, `quicksync`, `vaapi`, `cpu`, or `none`. Auto probes usable hardware encoders; CPU/none select `libx264`. |
| `max_concurrent_transcodes` | int | `1` | Shared upper bound for adaptive package preparation. Clamped to 1-8. |
| `shadow_storage_limit_gb` | int | `500` | Maximum prepared-variant storage. Cleanup evicts least-recently-used ready packages when this bound is exceeded. |
| `variant_cache_path` | string | `".data/variants"` | Absolute path or path relative to `library_root`. Adaptive packages live below its `hls/` directory. |
| `variant_retention_days` | int | `30` | Adaptive packages not accessed within this period are reclaimed. A prepared offline variant is also stamped with `expires_at` (when it is prepared: now plus this many days, minimum 1) and removed with its file once that time passes, even when a paired app's download list still marks it as stored. The same background pass does both, so `cleanup_lru_enabled` switches both off. |
| `cleanup_lru_enabled` | bool | `true` | Enables retention, failed-package, staging, and storage-bound cleanup. |
| `adaptive_hls.profile_name` | string | `"tv-adaptive-hls"` | Human-readable cache profile prefix. The Engine automatically fingerprints the full adaptive profile, so segment or ladder changes prepare a new package. |
| `adaptive_hls.segment_seconds` | int | `6` | Target HLS segment duration and keyframe alignment interval. |
| `adaptive_hls.access_lifetime_minutes` | int | `240` | Lifetime of one package-scoped signed playback path. Clamped to 15-720 minutes. |
| `adaptive_hls.preparation_wait_seconds` | int | `20` | Maximum manifest-request wait before returning `hlsStatus: preparing`. |
| `adaptive_hls.cleanup_interval_minutes` | int | `15` | Background reclamation interval. |
| `adaptive_hls.renditions[]` | object[] | 1080p/720p/480p | Ordered H.264 ladder. Each item has `name`, `height`, `video_bitrate_kbps`, `max_rate_kbps`, and `buffer_size_kbps`; renditions above the source height are omitted. |

The Engine health and `/playback/diagnostics` responses report the HLS muxer, H.264, AAC, WebVTT, and preferred hardware encoder capabilities. A missing required video/audio capability makes adaptive delivery unavailable but does not disable compatible direct play.

---

## config/ocr.json

Controls text recognition (OCR) for scanned PDF pages and photos of paper. Tesseract runs as a child process on the Engine host; nothing is sent anywhere. Jobs are queued as `view.document_ocr` and run one file at a time.

| Field | Type | Default | Description |
|---|---|---|---|
| `enabled` | bool | `true` | Turns reading of scanned documents and photos on or off. It only runs when the helper is found. |
| `tesseract_binary_path` | string | `""` | Explicit executable path. Empty uses the packaged `tools/tesseract` binaries and then `PATH`. An explicit missing path fails capability detection instead of silently falling back. |
| `effort` | string | `"normal"` | `normal` or `low`. Low runs the helper at a lower process priority, limits it to one thread and pauses between files. |
| `languages` | string[] | `["eng"]` | Tesseract language codes to read with. Only languages whose data is installed are used. |
| `per_file_timeout_seconds` | int | `300` | Longest one file may take. Pages read before the limit are kept and the file is marked partly read. |
| `max_pages_per_file` | int | `500` | Most pages recognised from one PDF. Longer files are marked partly read. |
| `text_layer_min_characters` | int | `25` | A PDF page with fewer non-whitespace characters than this in its own text layer is drawn and recognised. Pages with a text layer are never re-read. |
| `render_dpi` | int | `300` | Resolution used to draw PDF pages. |
| `max_render_side_pixels` | int | `3500` | The longest side of a drawn page or photo is reduced to this many pixels. |
| `min_image_side_pixels` | int | `200` | Smaller images are skipped; they cannot hold readable text. |
| `min_image_confidence` / `min_image_words` | int | `55` / `3` | Text from a photo is kept only when its average confidence reaches the first value and at least the second number of words were recognised, so ordinary photos add no junk words. |
| `rotation_retry_confidence` | int | `40` | A first pass with a lower average confidence (0-100) triggers orientation detection and one retry. |
| `language_download_url` | string | pinned `tessdata_fast` commit | Where an extra language is downloaded from when the person asks in Settings. `{code}` is replaced with the language code. |
| `available_languages[]` | object[] | 17 languages | `code` and `name` of each language offered in Settings. |

English and orientation data are installed with the helper. Other languages are an explicit download from **Settings > Libraries > View > Documents** and are stored in `{DataRoot}/ocr/tessdata`, never in build output. Page images are written to `{DataRoot}/ocr/tmp` and removed as each file finishes.

The helper is found in this order: `tesseract_binary_path` when set, then `tools/tesseract/` in the Tuvima data folder, then `tools/tesseract/` next to the Engine, then `PATH`, then the standard install folders (Windows: `Program Files\Tesseract-OCR`, `Program Files (x86)\Tesseract-OCR`, `%LOCALAPPDATA%\Programs\Tesseract-OCR`; macOS: `/opt/homebrew/bin`, `/usr/local/bin`). Windows installers bundle it, and the container image installs the `tesseract-ocr` and `tesseract-ocr-eng` packages. From a source checkout on Windows run `pwsh -File tools/Download-Tesseract.ps1` (needs 7-Zip); on Linux or macOS install Tesseract with your package manager so it is on `PATH`. Without it, **Settings > Libraries > View > Documents** says **Text recognition is not installed** and reading text-layer documents carries on.

---

## config/tool_downloads.json

The exact builds of FFmpeg and Tesseract the Library may fetch when an administrator presses **Install for me** or **Download again** under **Settings > Tools and requirements** (the same builds as `tools/Download-FFmpeg.ps1` and `tools/Download-Tesseract.ps1`). Nothing is ever downloaded by itself. A missing file means the values below. The Local AI models and extra OCR languages keep their own download settings (`config/ai.json`, `language_download_url` in `config/ocr.json`).

| Field | Type | Default | Description |
|---|---|---|---|
| `ffmpeg.platform` | string | `"win-x64"` | Only `win-x64` is offered today; Linux and macOS keep the install-it-yourself text. |
| `ffmpeg.version` | string | `"9.0.1"` | Shown in the activity log. |
| `ffmpeg.url` | string | pinned BtbN release | The archive. Must be `https://github.com/...`. |
| `ffmpeg.sha256` | string | pinned | SHA-256 of the whole archive. |
| `ffmpeg.files[]` | object[] | `ffmpeg.exe`, `ffprobe.exe` | `name` and `sha256` of each program taken out of the archive by file name. Nothing else is unpacked and archive paths are never used to place a file. |
| `ffmpeg.license_names[]` | string[] | `LICENSE.txt`, `COPYING.GPLv3` | The licence file to keep, saved as `LICENSE.txt` beside the programs. |
| `tesseract.platform` / `version` | string | `"win-x64"` / `"5.4.0"` | As above. |
| `tesseract.installer` | object | pinned UB Mannheim setup | `name`, `url` and `sha256` of the setup program. It is downloaded to `{DataRoot}/tools/installers/`, checked again just before it runs, and deleted afterwards. Never unpacked. |
| `tesseract.executable_sha256` | string | pinned | The program the setup installs; recorded for reference, not checked. |
| `tesseract.language_files[]` | object[] | `eng`, `osd` from `tessdata_fast` | `name`, `url` and `sha256`; placed in `{DataRoot}/ocr/tessdata`. A file already there with the right checksum is kept. |

Every address must be `https` on `github.com` (no other host, no user name or port), every checksum must be 64 hexadecimal characters, every `name` must be a plain file name (no folders, drive or characters a file name cannot hold) and the Tesseract installer's name must end in `.exe`, or that tool's entry is dropped when the file is loaded and the install is not offered. A download that GitHub redirects is followed only over `https` to `github.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com` or `raw.githubusercontent.com`, for at most five hops; any other redirect is refused. FFmpeg is placed in `{DataRoot}/tools/ffmpeg` by staging the checked files and swapping the folder, so the old copy stays if anything fails; a saved FFmpeg or Tesseract location that no longer works is cleared after a successful install so the new copy is found. The install is offered only on 64-bit Windows, never in a container; a Windows-installer install is offered FFmpeg only (a Tesseract repair is a reinstall of the Library), and a run from source is offered both.

---

## config/ui/

UI settings are layered: global defaults -> device profile -> user profile -> resolved effective settings.

### config/ui/global.json

Global UI defaults applied to all devices and users unless overridden.

| Field | Type | Description |
|---|---|---|
| `theme` | string | Always `"dark"`. The Dashboard is dark-mode-only. |
| `accent_color` | string | `"#8B5CF6"` - fixed purple product chrome accent. EPUB reader highlight colors are configured separately. |
| `poster_aspect_ratio` | string | `"2:3"` - standard cover art card ratio. |
| `default_view_mode` | string | `"list"` or `"grid"`. Default media library view mode. |
| `app_store_links` | object | Where each Tuvima app can be installed: `app_store`, `google_play` and `roku`, each a web address. All three ship blank; the Apps page (Settings > Personal > Overview > Apps) shows a store button only once its link is a real `http` or `https` address. |

### config/ui/devices/*.json

One file per device profile: `web.json`, `mobile.json`, `television.json`, `automotive.json`.

Each file defines constraints appropriate for the device class: navigation style, poster size limits, swimlane behaviour, topbar variant, and SignalR reconnect policy.

The `shell` block of each file (and of `global.json`) holds `appbar_style`, `logo_variant`, `intent_dock_style` and `intent_dock_items`, the tab names shown on the TV dock. The default is `["Home", "Libraries", "Search", "Downloads", "Settings"]`. Libraries opens the last library surface used (a switcher sheet when tapped on one), Search opens the search overlay, and Downloads opens a page that says downloads live in the Tuvima app. Read, Watch, Listen, View and Collections remain valid names so settings saved before the five-tab dock still resolve; unknown names are ignored. A Kids profile always drops Settings. The Downloads tab itself is switched in code by `ShellDock.DownloadsMode` (`ExplainNativeApp`, `Hidden` or `Available`). The phone dock does not read this list: it is always Home, Search and My Library (`ShellDock.PhoneDockItems`), for every profile, and the phone top bar shows its Downloads icon only when `ShellDock.DownloadsMode` is `Available`. See *Phone navigation* in [presentation rules](../product/presentation-rules.md).

### config/ui/profiles/*.json

Per-user UI preference overrides. Stored server-side and merged into the resolved settings at circuit start. Controls: column visibility and order per media library media type (also persisted to localStorage), default sort column, sidebar collapsed state.

### config/ui/library-preferences.json

Library display preferences used by browse and discovery surfaces.

| Field | Type | Description |
|---|---|---|
| `missing_item_display` | object | Required per-media missing-member display policy. Each registered media type is configured explicitly; there are no runtime display defaults. |
| `view_modes` | object | Per-tab default groupings, such as `tv: "shows"` or `comics: "series"`. |
| `lane_group_display` | object | Per-lane display policy for lane group shelves. Watch configures the movie-only `Series` shelf (TV shows use their own fixed shelf), and Read defaults to `Series & Reading Lists`; these are lane groups, not top-level Collections tiles. |

Each `lane_group_display` entry supports `enabled`, `shelf_key`, `title`, `subtitle`, and `see_all_route`.

Each `missing_item_display` entry supports `enabled`, `default_visibility` (`shown` or `hidden`), `presentation` (`all` or `paged`), `page_size` (1-500), and `detail_hydration` (`owned_only`, `on_demand`, or `all`). Authoritative manifest completeness remains a data-integrity requirement and cannot be relaxed by configuration. `default_visibility` is the sole media-level default: it is not copied into SQLite. When a profile explicitly changes **Show missing** for one stable series container, the Dashboard stores that exception in `profile_sequence_preferences`; removing the exception immediately restores inheritance from this file. Comic policy defaults are recorded here as hidden, paged, and owned-only detail hydration so large runs remain lightweight.

### config/ui/playback-client.json

Browser playback mechanics for the Dashboard Listen host. These are not user listening preferences.

| Field | Type | Description |
|---|---|---|
| `popup_width` / `popup_height` | int | Mini-player popup dimensions. |
| `immediate_action_dedup_milliseconds` | int | Debounce window for immediate DOM audio actions. |
| `immediate_action_consume_milliseconds` | int | Window for treating immediate DOM actions as already handled by the host. |
| `lyrics_lead_milliseconds` | int | Client lyric highlighting lead in milliseconds; defaults to 150, bounded to 0–500. Native media time drives the main window; popup snapshots are extrapolated locally. |
| `audio_observer_interval_milliseconds` | int | Browser audio state observer cadence. |
| `audio_observer_minimum_interval_milliseconds` | int | Lower bound for observer cadence. |
| `seek_tolerance_seconds` | number | Allowed difference between target and actual audio position before applying a corrective seek. |
| `volume_step` | number | Keyboard and slider volume step. |
| `transport_ui_update_interval_milliseconds` | int | Minimum UI tick cadence for position-only transport updates. |
| `heartbeat_interval_seconds` | int | Player heartbeat interval for Engine progress sync. |
| `pending_transport_command_limit` | int | Maximum queued host transport commands while the Web audio host is unavailable. |
| `default_volume` | number | Initial Web player volume from 0 to 1. |

## Related

- [How to Configure Metadata Providers](../guides/configuring-providers.md)
- [How to Set Up Language Preferences](../guides/language-setup.md)
- [Developer Setup](../tutorials/dev-setup.md)
- [Wikidata Property Map](wikidata-property-map.md)

### Retail matrix follow-up

Retail identity confidence uses typed per-media `scoring.scopes` in `config/pipelines.json` and synchronized settings defaults. Eight scopes replace the global retail weights: books, audiobooks, movies, TV series, TV episodes, music tracks, music albums and comic issues. Missing optional values redistribute their weight and show Not provided; required creator evidence fails closed. Genre contributes nothing. Wrong-kind and structural gates apply after bonuses. Additive nullable `field_scores` exposes the complete durable breakdown. Accept/review thresholds remain 0.90/0.65. See `docs/architecture/scoring-and-cascade.md` for the normative matrices and the distinction from canonical trust scoring. No legacy fallback or data conversion is supported.
