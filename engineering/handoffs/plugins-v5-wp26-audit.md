# Plugins v5, WP26 audit

Where each shipped plugin's settings description came from before WP26, whether it can be JSON-only, and what was done. Spec: `engineering/specs/plugins-and-integrations-screens-v5.md` (D51, D58).

## Compiled-in plugins

| Plugin | Settings description before | JSON-only? | Now |
|---|---|---|---|
| Commercial Skip | `SettingsSchema()` in `CommercialSkipPlugin.cs` (inline JSON string) | Yes | `src/MediaEngine.Plugin.CommercialSkip/commercial-skip.settings.json`; groups, units, paths, `requires` (Comskip, FFmpeg) |
| Fandom Lore | `SettingsSchema()` in `FandomLorePlugin.cs` | Yes | `src/MediaEngine.Plugin.FandomLore/fandom-lore.settings.json`; groups, labelled choices (`enum` + `enum_labels`), units, placeholder |
| Media Segments (Skip markers) | `SettingsSchema()` in `MediaSegmentPlugin.cs` | Yes | `src/MediaEngine.Plugin.MediaSegments/skip-markers.settings.json`; groups, units, `show_when` on each kind's advanced numbers |
| AI: Description insights | `SettingsSchema()` in `AiFeaturePlugins.cs` | Yes | `src/MediaEngine.Api/Services/Plugins/AiFeatures/description-insights.settings.json` |
| AI: Series order, Smart filing, Taste summary | None (no settings) | Already | Nothing to move |
| ListenBrainz | None. Its only setting is the per-person token, drawn from the C# manifest's `Secrets` + `CredentialScopes` | See below | Unchanged |
| Trakt | None. Client id, client secret and account are credentials in the C# manifest | See below | Unchanged |

The JSON ships as an embedded resource of each plugin's own assembly (`shipped-plugin-settings.<file>`), so it is inside every publish output and the Engine needs no file path. `ShippedPluginSettings.Read` (`src/MediaEngine.Plugins/ShippedPluginSettings.cs`) reads it and the plugin's `SettingsSchema` is that JSON; `PluginCatalog` and the page model read it the same way as before.

## Provider source files (`config/providers/*.json`)

| File | Before | Now |
|---|---|---|
| apple_api, comicvine, lrclib, mdblist, musicbrainz, openlibrary, subdl, tmdb, tvdb, youtube | No `settings_schema`. The Engine invented the tunables (language, timeout, interval, concurrency, address, weight, rate limit, cache time) in C# (`PluginPageModelInputs.Tunables`) | Each file carries its own `settings_schema` with those tunables (conditional ones present only where the file has the field); the C# description is deleted |
| stashbox | Own `settings_schema` for servers and fingerprint, tunables from C# | Tunables added to its schema |
| local_filesystem, wikidata_reconciliation | No schema; the Settings card is hidden for core sources | Left without one (nothing is shown) |

The values of the tunables (timeout, weight and so on) still come from the file's own fields; only the description moved. Saved key names are unchanged.

## Manifests

The only manifests that shipped are the compiled-in ones above (their manifests are C# objects). Private and installed plugins carry their own JSON and are not Library's to rewrite.

## What could not become JSON-only, and why

- **Credentials of ListenBrainz and Trakt.** They are declared in the C# manifest (`Secrets`, `CredentialScopes`, network destinations, permissions). That is the plugin's identity and Access card, not its settings description; the page model turns a per-person credential into a `secret` control with scope `profile`. Moving whole compiled-in manifests to JSON files is the "merge the two formats" job the spec leaves for later. The new test allows exactly this one case (a per-person secret) and nothing else.
- Default values (`DefaultSettings`) stay in C# (the plugin code reads them). A test checks every default a settings file states equals the saved default.

## Things to know

- The settings vocabulary does not know the JSON-Schema word `items`, so the Description insights lists no longer carry it (it was never read; validation uses `type`, `enum`, `minimum`, `maximum`).
- An older copy of a provider file in a person's config folder has no `settings_schema`, so its Advanced connection settings would not show until the file is replaced. Pre-release data is reset rather than converted (CLAUDE.md Never 6); `ShippedProviderFiles` only repairs missing or unreadable files.
