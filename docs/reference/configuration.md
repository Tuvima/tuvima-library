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

## config/network.json (request origin and host names)

Desired network state. Only the fields that decide where a Dashboard request may come from are listed here; the Settings > Network pages own the rest.

| Field | Type | Default | Description |
|---|---|---|---|
| `who_can_connect` | `this_computer` \| `home_network` \| `anywhere` | `home_network` | The one door rule: how far from this computer a visitor may be. The Dashboard refuses a request (403, `Cache-Control: no-store`; a short page for browsers, `{"error":"not_available_here"}` under `/api/v1/` and `/application-events/`) when it came from farther away than this: **This computer** admits only loopback, **Home network** adds private addresses and `auth.trusted_local_networks`, **Anywhere** also admits Remote visitors, who must still use HTTPS (plain HTTP gets 426). Only `/health/live` is exempt. It also gates Engine sign-in (Remote sign-in works only under `anywhere` over HTTPS) and the paired-app door (`native_app_access`). Saving `anywhere` runs the remote-access readiness checks; saving anything lower turns `native_app_access` off. A missing file means `home_network`; an unreadable or invalid file admits only this computer. Changes apply on the next request with no restart. |
| `local.allowed_hostnames` | string[] | `[]` | Extra host names (name only: no `https://`, port or path; at most 32) the Dashboard answers to. IP addresses, `localhost`, home-network names (a single word such as `nas`, or anything ending in `.local`, `.lan`, `.home.arpa`, `.internal`, `.localhost`, `.localdomain`, `.home` or `fritz.box`), this computer's name, `local.preferred_server_name`, the host of `remote.public_hostname` and `TUVIMA_TAILSCALE_URL` are always allowed. Any other `Host` gets a 400 "This address isn't allowed for Tuvima Library" (this blocks DNS-rebinding attacks). `/health/live` is exempt. |
| `remote.proxy_port` | int or null | `null` (off) | Optional second Dashboard port for a reverse proxy on the same machine (Caddy, Tailscale Serve). Must differ from `local.port`. Everything arriving on it counts as Remote, and `X-Forwarded-*` headers are honoured only on this port (from loopback plus `trusted_proxies` / `trusted_proxy_networks`). On the main port those headers are ignored. Restart the Dashboard after changing it. `TUVIMA_PROXY_PORT` overrides it, and setting `TUVIMA_TAILSCALE_URL` without either defaults it to 5017. It listens on loopback only unless a non-loopback proxy is listed in `trusted_proxies` / `trusted_proxy_networks`. |

`local.allowed_hostnames`, `remote.proxy_port`, `auth.trusted_local_networks` and the trusted proxy lists are read at startup: restart the Dashboard after changing them.

A request's origin is classified once, by the connection address and port: **This computer** (loopback), **Home network** (10/8, 172.16/12, 192.168/16, 169.254/16, IPv6 fe80::/10 and fc00::/7, plus `auth.trusted_local_networks` in `config/core.json`) or **Remote** (everything else, including Tailscale 100.64/10 addresses and anything on the proxy port). A connection arriving on the main port straight from a configured trusted proxy is treated as Remote (the visitor behind it is hidden there). Docker's default bridge gateway (172.17.0.1) counts as Home network, so a published container port can look local to the Dashboard; use `proxy_port` for proxies.

---

## config/core.json

Core Engine settings. Most changes are read at startup. Ingestion sources and destinations come from `config/libraries.json`; Settings > Libraries saves that model and asks the running Engine to hot-swap watchers.

| Field | Type | Default | Description |
|---|---|---|---|
| `schema_version` | string | `"2.0"` | Config schema version. Used for migration compatibility checks. |
| `database_path` | string | `".data/database/library.db"` | Path to the SQLite database file. Relative paths resolve from `data_root`. |
| `data_root` | string | `""` | Root directory for all internal Engine storage (`.data/`). Must be set before first run. |
| `library_root` | string | `""` | Root directory where the Engine places organized media after promotion. |
| `organization_template` | string | - | Default file organization template. Tokens: `{Category}`, `{Title}`, `{Qid}`, `{Ext}`. |
| `organization_templates` | object | - | Per-media-type templates. Keys: `default`, `Books`, `Audiobooks`, `Movies`, `TV`, `Comics`, `Music`. TV supports `{Series}`, `{Season}`, `{Episode}` tokens. Music supports `{Artist}`, `{Album}`, `{TrackNumber}` tokens. |
| `server_name` | string | `"Tuvima Library"` | Display name shown in the Dashboard title bar and system status. |
| `language` | object | - | Language preferences. See sub-fields below. |
| `language.display` | string | `"en"` | UI display language (BCP-47 code). Controls Dashboard localization. |
| `language.metadata` | string | `"en"` | Language used for provider queries (titles, descriptions). |
| `language.additional` | string[] | `[]` | Additional content languages accepted (BCP-47 codes). |
| `language.accept_any` | bool | `true` | When true, files in any language are accepted without review. When false, language mismatches trigger a visible warning on Dashboard browse and detail surfaces. |
| `country` | string | `"US"` | Country code. Used by providers that return region-specific data (e.g., Apple API storefronts). |
| `date_format` | string | `"system"` | Date display format. `"system"` uses the OS locale. Accepts standard format strings. |
| `time_format` | string | `"system"` | Time display format. `"system"` uses the OS locale. |
| `plugin_catalog.enabled` | bool | `true` | Enables the read-only approved plugin catalog lookup used by Settings > Plugins. |
| `plugin_catalog.approved_plugins_url` | string | GitHub raw URL | HTTPS GitHub URL for the approved plugin catalog JSON. The Engine accepts `raw.githubusercontent.com` or `github.com` sources only. |

**Backward compatibility:** `language` accepts both legacy string form (`"language": "en"`) and the new object form. The deserializer handles both.

---

## config/ai.json

AI/ML subsystem configuration. Controls model selection, feature toggles, vibe vocabulary, and enrichment scheduling.

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

### features

Boolean toggles. All default to `true` unless noted.

| Field | Default | Description |
|---|---|---|
| `smart_labeler` | `true` | AI-assisted file labeling during ingestion. Improves title/author extraction from filenames. |
| `media_type_advisor` | `true` | AI classification for ambiguous formats (MP3, MP4). |
| `qid_disambiguator` | `true` | AI-assisted candidate ranking during Wikidata reconciliation. |
| `vibe_tagger` | `true` | Generates vibe tags per work during batch enrichment. |
| `tldr_generator` | `true` | Generates TL;DR summaries from descriptions. |
| `description_intelligence` | `true` | Two-pass LLM analysis of descriptions: vocabulary extraction + person name extraction. Runs as background batch service (15-min cron). |
| `intent_search` | `true` | Natural-language search query parsing. Translates user queries to structured field filters. |
| `batch_manifest` | `true` | AI-assisted batch ingestion manifest building for folder drops. |
| `cover_art_hash` | `true` | Perceptual hashing (pHash) of cover art for visual matching during retail identification. |
| `audio_similarity` | `false` | Audio fingerprint similarity for music matching. Disabled by default (high CPU). |
| `subtitle_sync` | `false` | Subtitle alignment via Whisper transcription. Disabled by default. |

### vibe_vocabulary

Per-media-type lists of vibe descriptors across four dimensions. Each media type has 25-30 entries total.

| Key | Description |
|---|---|
| `{media_type}.theme` | Core conceptual themes (e.g., "redemption", "survival", "identity") |
| `{media_type}.mood` | Emotional atmosphere (e.g., "tense", "melancholic", "uplifting") |
| `{media_type}.setting` | Environment and world feel (e.g., "sprawling cities", "isolated wilderness") |
| `{media_type}.pace` | Narrative rhythm (e.g., "slow-burn", "relentless", "contemplative") |

### schedule

Cron expressions for background AI enrichment batches.

| Field | Default | Description |
|---|---|---|
| `enrichment_cron` | `"*/15 * * * *"` | How often the description intelligence batch service runs. |
| `vibe_cron` | `"0 * * * *"` | How often the vibe tagging batch runs. |
| `quality_enrichment_cron` | `"0 2 * * *"` | When heavy quality-model enrichment runs (Medium tier: overnight). |

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

## config/libraries.json

Defines the clean schema 6 library model used by **Settings > Libraries**. The root contains explicitly approved `storage_locations`, `personal_library_policy`, and `libraries`.

| Field | Type | Description |
|---|---|---|
| `schema_version` | string | Must be `6.0`. Older schemas are rejected and development state is reset/reingested rather than migrated. |
| `storage_locations` | array | Explicit server/container roots administrators may browse. Each entry has a stable `id`, friendly `label`, absolute `path`, and `allow_write` policy. |
| `personal_library_policy` | object | Administrator defaults and capability switches for the internal personal-library bridge, managed/existing storage, browser upload, reserved future intake producers, and default visibility. |
| `id` | GUID string | Stable library identity used by intake, indexing, access, and Dashboard editing. Required. |
| `name` | string | Configuration/admin name. For View, the profile-facing identity is the Personal Space, not this library record. |
| `category` | string? | Structured subtype such as Books, Movies, TV, Music, Audiobooks, or Comics. Personal libraries may omit it. |
| `kind` | string | Exactly `catalogued` or `personal`. |
| `area` | string | `read`, `watch`, `listen`, or `view`. |
| `presentation` | string | Catalogue, gallery, mixed gallery, timeline, video, documents, audio, or mixed presentation. |
| `metadata_policy` | string | `enriched`, `local_preferred`, `local_only`, or `manual`. The last two bypass external identity/provider work. |
| `media_types` | string[] | Structured media types expected by catalogued libraries. A View Personal Space may index mixed local media through its backing personal library. |
| `sources` | object[] | Stable source objects; see below. Array order has no meaning. |
| `primary_destination_source_id` | GUID string? | Explicit managed destination source ID. It is never inferred from array position. |
| `owner_profile_id` | GUID string? | Owning profile for personal access decisions. |
| `visibility` | string | `private` or `shared` for current product presentation. Shared View contribution is separately controlled by profile View policy. |
| `authorized_profile_ids` | GUID string[] | Explicit members of a shared library. |
| `accepted_intake_modes` | string[] | Accepted producer vocabulary. Incoming folder and browser upload have current paths; drag-and-drop, mobile backup, connected-device import, and direct API producers are reserved until a real client is implemented. |
| `duplicate_policy` | string | `skip_exact`, `keep_both`, or `replace_existing`. |
| `organization_policy` | object | Organization mode, optional custom template, and original-preservation policy. |

Each source owns `id`, `path`, `role`, `management_mode`, `source_type`, `include_subdirectories`, `access_mode`, `writeback_override`, `participates_in_organization`, and `intake_role`. **Existing library** sources must be read-only and are never moved, renamed, tagged, deleted, or used as destinations. Only **Managed by Tuvima** plus writable sources can authorize mutation.

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
| `activity_retention_days` | int | `60` | How long system activity log entries are kept before automatic pruning. |
| `vacuum_interval_hours` | int | `168` | How often SQLite VACUUM runs to reclaim disk space (default: weekly). |
| `sync_interval_hours` | int | `24` | How often metadata writeback sync runs for pending changes. |
| `reconciliation_interval_hours` | int | `24` | How often the Wikidata reconciliation refresh cycle runs. |
| `rejected_retention_days` | int | `30` | How long explicitly rejected files remain in `.data/staging/rejected/` before deletion. |
| `storage_maintenance.enabled` | bool | `true` | Enables the nightly storage maintenance hosted service. |
| `storage_maintenance.search_cache_max_age_days` | int | `30` | Age threshold for provider, resolver, and search cache purges. |
| `storage_maintenance.image_cache_retention_days` | int | `30` | Age threshold for unused image cache rows/files. User-preserved images are not removed. |
| `storage_maintenance.claim_compaction_batch_size` | int | `5000` | Maximum duplicate non-user-locked metadata claims compacted in one pass. |
| `schedules.storage_maintenance` | cron | `0 2 * * *` | Nightly storage maintenance schedule. |

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
| `instance_of_classes` | Per-media-type P31 type allow-lists for Wikidata candidate filtering. Previously stored separately in `cirrus-type-filters.json` (now removed). |
| `edition_pivot` | Per-media-type rules for walking from Wikidata edition items to work items. Previously stored in `edition-pivot.json` (now removed). Keys: `audiobooks`, `books`, `music`. Each has `work_classes`, `edition_classes`, and `prefer_edition`. |
| `exclude_classes` | P31 classes to exclude from reconciliation results. |
| `bridge_resolution.scopes` | Entity-scoped bridge-ID policy. Each scope declares `target_ids`, `context_ids`, and whether constrained text fallback is allowed. Music uses `MusicTrack` to prefer recording/work/ISRC IDs before Apple track IDs, while release/release-group/artist IDs stay contextual unless resolving `MusicAlbum`. |
| `data_extension` | Properties fetched during the Data Extension API call after QID resolution. |

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

---

## config/transcoding.json

Controls FFmpeg discovery, adaptive playback packages, offline variants, concurrency, and cache retention.

| Field | Type | Default | Description |
|---|---|---|---|
| `ffmpeg_binary_path` / `ffprobe_binary_path` | string | `""` | Explicit executable paths. Empty uses the packaged `tools/ffmpeg` binaries and then `PATH`. An explicit missing path fails capability detection instead of silently falling back. |
| `hardware_acceleration` | string | `"auto"` | `auto`, `nvenc`, `quicksync`, `vaapi`, `cpu`, or `none`. Auto probes usable hardware encoders; CPU/none select `libx264`. |
| `max_concurrent_transcodes` | int | `1` | Shared upper bound for adaptive package preparation. Clamped to 1-8. |
| `shadow_storage_limit_gb` | int | `500` | Maximum prepared-variant storage. Cleanup evicts least-recently-used ready packages when this bound is exceeded. |
| `variant_cache_path` | string | `".data/variants"` | Absolute path or path relative to `library_root`. Adaptive packages live below its `hls/` directory. |
| `variant_retention_days` | int | `30` | Packages not accessed within this period are reclaimed. |
| `cleanup_lru_enabled` | bool | `true` | Enables retention, failed-package, staging, and storage-bound cleanup. |
| `adaptive_hls.profile_name` | string | `"tv-adaptive-hls"` | Human-readable cache profile prefix. The Engine automatically fingerprints the full adaptive profile, so segment or ladder changes prepare a new package. |
| `adaptive_hls.segment_seconds` | int | `6` | Target HLS segment duration and keyframe alignment interval. |
| `adaptive_hls.access_lifetime_minutes` | int | `240` | Lifetime of one package-scoped signed playback path. Clamped to 15-720 minutes. |
| `adaptive_hls.preparation_wait_seconds` | int | `20` | Maximum manifest-request wait before returning `hlsStatus: preparing`. |
| `adaptive_hls.cleanup_interval_minutes` | int | `15` | Background reclamation interval. |
| `adaptive_hls.renditions[]` | object[] | 1080p/720p/480p | Ordered H.264 ladder. Each item has `name`, `height`, `video_bitrate_kbps`, `max_rate_kbps`, and `buffer_size_kbps`; renditions above the source height are omitted. |

The Engine health and `/playback/diagnostics` responses report the HLS muxer, H.264, AAC, WebVTT, and preferred hardware encoder capabilities. A missing required video/audio capability makes adaptive delivery unavailable but does not disable compatible direct play.

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

### config/ui/devices/*.json

One file per device profile: `web.json`, `mobile.json`, `television.json`, `automotive.json`.

Each file defines constraints appropriate for the device class: navigation style, poster size limits, swimlane behaviour, topbar variant, and SignalR reconnect policy.

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
