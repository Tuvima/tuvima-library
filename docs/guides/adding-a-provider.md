---
title: "How to Add a New Metadata Provider"
description: "Create or configure a metadata provider and wire it into Tuvima's enrichment flow."
audience: "developer"
category: "guide"
product_area: "providers"
tags:
  - "providers"
  - "hydration"
  - "extensibility"
---
This guide explains how to wire a new REST/JSON metadata source into the Tuvima Library
enrichment pipeline. For standard providers that return JSON from a public HTTP endpoint,
**no C# code is required** - you drop a config file and restart.

---

## Prerequisites

- Familiarity with the hydration pipeline (`docs/architecture/hydration-and-providers.md`)
- The target API's base URL, search endpoint, and response schema
- An API key if the provider requires one (stored under `config/secrets/{provider}.json`; never committed to git)

---

## Step 1 - Understand where providers fit

Identity providers run during the retail phase of the hydration pipeline, shown as **Stage 3: Retail Match** on the Ingestion page. Provider config still uses the older internal value `hydration_stages: [1]` for this retail phase.
They gather cover art, descriptions, ratings, and bridge IDs (ISBN, ASIN, TMDB ID, etc.)
that Stage 4 (Wikidata) later uses for precise QID resolution. Artwork-only,
lyrics, subtitle, and other follow-up providers should run as Stage 8/deep enrichment or text-track
enrichment instead; they must not unlock Wikidata identity by themselves.

The `ConfigDrivenAdapter` is the single implementation that reads every `config_driven`
provider config at startup and builds a live HTTP client for each one. Your config file
teaches it how to talk to the new API.

Provider configs live in:

```
config/providers/
  apple_api.json
  tmdb.json
  musicbrainz.json
  comicvine.json
  tmdb.json                    <- Movie identity, TV fallback/cross-reference, and Stage 8 artwork
  lrclib.json                  <- text-track enrichment
  subdl.json                   <- text-track enrichment
  wikidata_reconciliation.json    <- Stage 4 user-facing lookup
```

---

## Step 2 - Create the config file

Create `config/providers/my_provider.json`. The complete schema is documented below
with every field explained. Use `apple_api.json` (books/audio) or `tmdb.json` (video)
as your template depending on the media domain.

### Top-level identity fields

```jsonc
{
  "name": "my_provider",          // Internal key, matches filename stem
  "version": "1.0",
  "display_name": "My Provider",  // Shown in the Dashboard Settings UI
  "enabled": true,
  "weight": 0.75,                 // Provider-level confidence weight [0.0-1.0]
  "domain": "Ebook",              // "Ebook" | "Video" | "Music" | "Comics" | "Universal"
  "icon": "images/providers/my_provider.svg",
  "capability_tags": ["title", "author", "cover", "description"],
  "available_fields": ["title", "author", "year", "cover", "description", "isbn"],

  "field_weights": {
    "title":       0.75,
    "author":      0.80,
    "cover":       0.85,
    "description": 0.80,
    "isbn":        0.90,
    "year":        0.75
  }
}
```

`weight` scales every confidence value this provider emits before they enter the
Priority Cascade. A provider returning unreliable data should use 0.5-0.6; a
high-quality source like TMDB uses 0.8.

`field_weights` are per-field multipliers on top of the provider weight. Use higher
values for fields the API returns reliably (bridge IDs, structured dates) and lower
for fields that vary in quality (descriptions, genres).

### HTTP and authentication

```jsonc
{
  "endpoints": {
    "api": "https://api.myprovider.com/v2"
  },
  "throttle_ms": 300,          // Minimum milliseconds between requests
  "max_concurrency": 2,         // Parallel request cap
  "cache_ttl_hours": 72,        // null = no caching, integer = cache TTL
  "hydration_stages": [1],      // Stage 1 identity; use [3] only for post-identity enrichment providers

  "adapter_type": "config_driven",  // Always this value for JSON providers

  "http_client": {
    "timeout_seconds": 10,
    "user_agent": "Tuvima Library/1.0",
    "api_key": null,                   // Set to your actual key; keep this file gitignored
    "api_key_delivery": "bearer",      // "bearer" | "query_param" | "header" | null
    "api_key_param_name": null         // Query param name when delivery = "query_param"
  },

  "requires_api_key": false       // Shown in provider readiness
}
```

If `api_key_delivery` is `"bearer"`, the adapter sends `Authorization: Bearer <key>`.
If `"query_param"`, it appends `?<api_key_param_name>=<key>` to every URL.
If `"header"`, set the header name in `api_key_param_name`.

### Provider GUID - pick one and never change it

Every provider needs a stable UUID. It is a foreign key in the `metadata_claims` table
(`provider_id` column). Changing it orphans historical claim rows.

```jsonc
{
  "provider_id": "bX000000-0000-4000-8000-000000000000"
}
```

Generate a UUID with `[System.Guid]::NewGuid()` (PowerShell) or any UUID v4 generator.
The `DatabaseConnection` seeds a row into `metadata_providers` on startup using this ID.
If you ever change the UUID, update `metadata_providers` directly or the FK will break.

### Media type scope

```jsonc
{
  "can_handle": {
    "media_types": ["Books", "Audiobooks"],
    "entity_types": ["Work", "MediaAsset"]
  }
}
```

Valid `media_types`: `Books`, `Audiobooks`, `Movies`, `TV`, `Music`, `Comics`.

### Search strategies

Search strategies tell the adapter which URL to call and in which order. The adapter
tries strategies in ascending `priority` order, stopping at the first one that returns
results. Define more specific lookups (by bridge ID) before broader title searches.

```jsonc
{
  "search_strategies": [
    {
      "name": "isbn_lookup",
      "priority": 0,
      "required_fields": ["isbn"],
      "url_template": "{base_url}/books?isbn={isbn}",
      "results_path": "items",
      "result_index": 0,
      "tolerate_404": true,
      "fetch_limit": 1,
      "max_results": 1,
      "media_types": ["Books"]
    },
    {
      "name": "title_search",
      "priority": 1,
      "required_fields": ["title"],
      "url_template": "{base_url}/search?q={query}&limit={limit}&lang={lang}",
      "query_template": "{title} {author}",
      "results_path": "results",
      "result_index": 0,
      "tolerate_404": false,
      "fetch_limit": 5,
      "max_results": 25,
      "media_types": ["Books", "Audiobooks"]
    }
  ]
}
```

**URL template tokens** (all resolved at runtime by `ConfigDrivenAdapter`):

| Token | Resolves to |
|---|---|
| `{base_url}` | The `endpoints.api` value |
| `{title}` | Lookup request title (URL-encoded) |
| `{author}` | Author from request metadata |
| `{query}` | `query_template` rendered with available fields |
| `{isbn}` | ISBN from request metadata |
| `{limit}` | `max_results` value |
| `{lang}` | Language code from `LanguagePreferences.Metadata` (e.g. `en`) |
| `{country}` | Country code derived from language setting |
| `{year}` | Year from request metadata |

`results_path` is a dot-separated JSON path to the array of results in the response
(e.g. `"results"` for `{ "results": [...] }`, or `"response.docs"` for nested paths).

`tolerate_404` controls whether a 404 status silently returns empty results (`true`)
or is treated as an error (`false`).

`required_fields` lists the claim keys that must be present in the lookup request for
this strategy to be attempted. The adapter skips the strategy when any required field
is missing.

### Field mappings

Field mappings declare which JSON property in the response maps to which internal
claim key, at what confidence, and with which optional transform.

```jsonc
{
  "field_mappings": [
    {
      "claim_key": "title",
      "json_path": "volumeInfo.title",
      "confidence": 0.75,
      "transform": null,
      "transform_args": null,
      "media_types": null
    },
    {
      "claim_key": "author",
      "json_path": "volumeInfo.authors",
      "confidence": 0.75,
      "transform": "array_first",
      "transform_args": null,
      "media_types": null
    },
    {
      "claim_key": "cover",
      "json_path": "volumeInfo.imageLinks.thumbnail",
      "confidence": 0.70,
      "transform": "replace",
      "transform_args": "zoom=1|zoom=5",
      "media_types": null
    },
    {
      "claim_key": "year",
      "json_path": "volumeInfo.publishedDate",
      "confidence": 0.75,
      "transform": "first_n_chars",
      "transform_args": "4",
      "media_types": null
    },
    {
      "claim_key": "isbn",
      "json_path": "volumeInfo.industryIdentifiers[?(@.type=='ISBN_13')].identifier",
      "confidence": 0.95,
      "transform": null,
      "transform_args": null,
      "media_types": ["Books"]
    }
  ]
}
```

`json_path` supports dot notation and array indexing. The adapter resolves it against
the result object from `results_path[result_index]`.

**Available transforms** (implemented in `ConfigDrivenAdapter`):

| Transform | `transform_args` | Effect |
|---|---|---|
| `strip_html` | - | Strips HTML tags from the value |
| `first_n_chars` | `"4"` | Truncates to N characters (useful for ISO date -> year) |
| `array_first` | - | Takes the first element of a JSON array |
| `array_join` | `", "` | Joins array elements with separator |
| `to_string` | - | Converts numeric values to string |
| `regex_replace` | `"pattern\|replacement"` | Regex find-and-replace |
| `replace` | `"old\|new"` | Literal string substitution |

`media_types` on a mapping restricts it to specific media types. `null` applies to all.

The effective confidence for a claim is: `provider.weight x field_weights[claim_key] x mapping.confidence`.

### Bridge IDs and preferred lookup order

If the provider returns a bridge ID that Wikidata can use for precise QID resolution,
declare it in `preferred_bridge_ids`:

```jsonc
{
  "preferred_bridge_ids": {
    "Books": ["isbn", "my_provider_id"]
  }
}
```

The Wikidata Stage 2 adapter reads these to know which claim keys to look for when
building its edition-first QID lookup.

### Language strategy

```jsonc
{
  "language_strategy": "source"
}
```

Three options:

| Value | Behaviour | Use when |
|---|---|---|
| `source` | Always query in English | Provider has poor or no localization |
| `localized` | Query in the user's metadata language | Provider supports localized results |
| `both` | Query in metadata language, fall back to English on empty | Provider supports localization but has incomplete coverage |

`source` is the safe default for most providers. Use `localized` only when the API
genuinely returns localized titles and descriptions (e.g. TMDB, Apple API).

---

## Step 3 - Complete example: a hypothetical "BookHive" provider

`config/providers/bookhive.json`:

```json
{
  "name": "bookhive",
  "version": "1.0",
  "display_name": "BookHive",
  "enabled": true,
  "weight": 0.72,
  "domain": "Ebook",
  "icon": "images/providers/bookhive.svg",
  "capability_tags": ["title", "author", "cover", "description", "isbn", "year"],
  "available_fields": ["title", "author", "year", "cover", "description", "isbn", "genre"],
  "field_weights": {
    "title": 0.75,
    "author": 0.78,
    "cover": 0.82,
    "description": 0.78,
    "isbn": 0.92,
    "year": 0.76,
    "genre": 0.70
  },
  "endpoints": {
    "api": "https://api.bookhive.example.com/v1"
  },
  "throttle_ms": 400,
  "max_concurrency": 1,
  "cache_ttl_hours": 168,
  "hydration_stages": [1],
  "adapter_type": "config_driven",
  "provider_id": "ba000099-0000-4000-8000-000000000099",
  "requires_api_key": true,
  "http_client": {
    "timeout_seconds": 10,
    "user_agent": "Tuvima Library/1.0",
    "api_key": "YOUR_KEY_HERE",
    "api_key_delivery": "query_param",
    "api_key_param_name": "apikey"
  },
  "can_handle": {
    "media_types": ["Books"],
    "entity_types": ["Work", "MediaAsset"]
  },
  "search_strategies": [
    {
      "name": "isbn_lookup",
      "priority": 0,
      "required_fields": ["isbn"],
      "url_template": "{base_url}/isbn/{isbn}",
      "results_path": "data",
      "result_index": 0,
      "tolerate_404": true,
      "fetch_limit": 1,
      "max_results": 1,
      "media_types": ["Books"]
    },
    {
      "name": "title_search",
      "priority": 1,
      "required_fields": ["title"],
      "url_template": "{base_url}/search?q={query}&limit={limit}",
      "query_template": "{title} {author}",
      "results_path": "data.books",
      "result_index": 0,
      "tolerate_404": false,
      "fetch_limit": 5,
      "max_results": 20,
      "media_types": ["Books"]
    }
  ],
  "field_mappings": [
    {
      "claim_key": "title",
      "json_path": "title",
      "confidence": 0.75,
      "transform": null,
      "transform_args": null,
      "media_types": null
    },
    {
      "claim_key": "author",
      "json_path": "authors",
      "confidence": 0.75,
      "transform": "array_first",
      "transform_args": null,
      "media_types": null
    },
    {
      "claim_key": "cover",
      "json_path": "cover_url",
      "confidence": 0.80,
      "transform": null,
      "transform_args": null,
      "media_types": null
    },
    {
      "claim_key": "description",
      "json_path": "synopsis",
      "confidence": 0.78,
      "transform": "strip_html",
      "transform_args": null,
      "media_types": null
    },
    {
      "claim_key": "year",
      "json_path": "publish_date",
      "confidence": 0.76,
      "transform": "first_n_chars",
      "transform_args": "4",
      "media_types": null
    },
    {
      "claim_key": "isbn",
      "json_path": "isbn13",
      "confidence": 0.92,
      "transform": null,
      "transform_args": null,
      "media_types": ["Books"]
    },
    {
      "claim_key": "genre",
      "json_path": "genres",
      "confidence": 0.68,
      "transform": "array_join",
      "transform_args": ", ",
      "media_types": null
    }
  ],
  "preferred_bridge_ids": {
    "Books": ["isbn"]
  },
  "language_strategy": "source"
}
```

---

## Step 4 - How the runtime loads your config

At startup, `ConfigDrivenAdapter` scans `config/providers/` for JSON files where
`adapter_type == "config_driven"`. For each file it:

1. Deserialises `ProviderConfiguration` from the JSON.
2. Registers an HTTP client named after `provider_id`.
3. Seeds a row in `metadata_providers(id, name, version, is_enabled)` via `DatabaseConnection`
   - the `INSERT OR IGNORE` means existing rows are never overwritten.
4. Makes the adapter available as `IExternalMetadataProvider` through DI.

Nothing else needs to change. Restart the Engine and the provider is active.

---

## Step 5 - Verify the provider works

Use the debug lookup endpoint (development environment only) to run a live enrichment
pass without writing anything to the database:

```http
POST http://localhost:61495/debug/lookup
Content-Type: application/json

{
  "title": "The Name of the Wind",
  "author": "Patrick Rothfuss",
  "mediaType": "Books",
  "isbn": "9780756404741"
}
```

The response includes every claim the provider returned along with its source,
confidence value, and which search strategy fired. If no claims appear, check:

- `enabled: true` in your config file.
- The `can_handle.media_types` list includes the media type you're querying.
- The `required_fields` for at least one strategy are satisfied by your request.
- The API key is set correctly if `requires_api_key: true`.
- Check `engine.log` (Serilog rolling log) for HTTP-level errors from the adapter.

You can also browse all registered providers via the Settings -> Providers screen in
the Dashboard, or inspect the `metadata_providers` table directly:

```http
GET http://localhost:61495/swagger  -> Providers section -> GET /settings/providers
```

---

## Reference: complete field listing

| Field | Type | Required | Default | Notes |
|---|---|---|---|---|
| `name` | string | yes | - | Matches filename stem |
| `version` | string | yes | - | Informational only |
| `display_name` | string | yes | - | Shown in Settings UI |
| `enabled` | bool | yes | - | `false` skips this provider entirely |
| `weight` | float | yes | - | Provider-level multiplier [0.0-1.0] |
| `domain` | string | yes | - | Ebook / Video / Music / Comics / Universal |
| `adapter_type` | string | yes | - | Must be `config_driven` |
| `provider_id` | UUID string | yes | - | Stable FK - never change after first use |
| `hydration_stages` | int[] | yes | - | `[1]` for Stage 1 identity, `[3]` for post-identity enrichment, `[2]` only for the Wikidata reconciliation provider |
| `can_handle.media_types` | string[] | yes | - | Restricts to named media types |
| `can_handle.entity_types` | string[] | yes | - | Work / MediaAsset |
| `endpoints.api` | string | yes | - | Base URL for all requests |
| `throttle_ms` | int | no | 250 | Min ms between requests |
| `max_concurrency` | int | no | 1 | Parallel request cap |
| `cache_ttl_hours` | int? | no | null | null = no caching |
| `requires_api_key` | bool | no | false | Shown in provider readiness |
| `language_strategy` | string | no | `"source"` | source / localized / both |
| `preferred_bridge_ids` | object | no | - | Keys used by Wikidata Stage 2 |

---

## Related

- [Providers Reference](../reference/providers.md)
- [Hydration Pipeline, Provider Architecture and Enrichment Strategy](../architecture/hydration-and-providers.md)
- [Configuration Reference](../reference/configuration.md)
