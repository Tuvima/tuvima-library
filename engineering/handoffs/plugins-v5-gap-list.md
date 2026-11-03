# Plugins v5, WP22 gap list

What the hand-made Dashboard code shows per shipped plugin that the plugin's JSON could not say before WP22, and where it now lives. Written first, before the vocabulary was extended (spec `engineering/specs/plugins-and-integrations-screens-v5.md`, WP22).

Sources read: `ProviderDetail.razor`, `StashBoxSettingsCard.razor`, `PluginAppDetails.razor`, the generic panel in `PluginSettingsTab.razor` (`GetSettingEntries`, `ReadDefaultValue` and friends).

## The gaps

| # | Hand-made code | What it shows | Which plugins | What JSON could not say | Now said as |
|---|---|---|---|---|---|
| 1 | Generic panel (`PluginSettingsTab`) | A control per `settings_schema` property, from `type` only (bool, number, string, raw JSON box) | FandomLore, CommercialSkip, MediaSegments, AI feature plugins, private plugins | Control type beyond `type`, choices with labels (only bare `enum`), a unit, a placeholder (it printed the key), a heading, an order, a slider range | `control`, `choices` / `enum_labels`, `unit`, `placeholder`, `group`, `order`, `minimum`/`maximum` |
| 2 | Generic panel | Settings saved but not in the schema, as raw JSON text boxes | Any plugin with `default_settings` that the schema omits | "Hidden" settings | Not shown; reported as a thing to know on the Plugin file card |
| 3 | Generic panel | `advanced` settings collapsed; it also accepts the older spellings `ui:advanced` and `x-advanced`, and `label` / `help_text` for title and help | FandomLore, CommercialSkip | Nothing missing; kept | `advanced`; the older spellings are still read as aliases so no existing plugin changes |
| 4 | Generic panel | Every setting is household-wide | ListenBrainz token, Trakt sign-in are per person but live on Profile > Accounts | Whose setting it is | `scope: household / profile`; per-profile credentials also appear as `secret` controls with `scope: profile` |
| 5 | Generic panel | A missing tool is told once, in the plugin's health sentence (from `ToolRequirements`), not on the setting that needs it | CommercialSkip | "This setting needs a tool / Local AI / key" and why | `requires { kind, name, reason }` |
| 6 | Generic panel | Every setting is always shown, even one that only matters while another is on | CommercialSkip (FFmpeg fallback only matters when Comskip is off), FandomLore | Conditional display | `show_when { setting, equals }` |
| 7 | Generic panel | Cannot lock a value | Source tunables the Dashboard never lets you change | Read-only | `read_only` |
| 8 | `PluginAppDetails` | A card of keys: label, help, set / not set, replace, remove, link to Accounts | Trakt (client ID, client secret, account), ListenBrainz (token), any manifest with `secrets` | Min / max length, a pattern, scope per credential (it lives in `declaration.credential_scopes`), the sign-in method (in `secrets[].sign_in`), signup / help links | One credentials list (`PluginCredential`) fed by manifest `secrets` and by source `onboarding.credentials`; manifest gains optional `links` |
| 9 | `ProviderDetail` Access card | Key needed / saved / not saved, signup link, Test button | TMDB, TheTVDB, StashDB, SubDL, MDBList, ComicVine, Apple | Which test the plugin supports | `actions` (`test_connection`, `check_now`, `dry_run`); source files that carry `onboarding.authentication_probe` imply `test_connection`, every non-core source implies `check_now` |
| 10 | `ProviderDetail` Privacy card | Hosts it contacts, "your key is sent only to that address", Vault-only line, Terms and Privacy buttons | All sources | A sentence on what is sent to each host (sources have none; manifests have `network[].sends`) | Same `destinations` list for both formats; a source gets one plain default sentence per host |
| 11 | `ProviderDetail` Settings card | Language choice (Source / Localized / Both) | Every source | A labelled choice on a source file | Source tunable `language_strategy` as a `choice` |
| 12 | `ProviderDetail` Advanced connection | Timeout, minimum request interval, max concurrency, primary address (all editable) | Every source | Tunables of a source file as Advanced settings (D51) | `timeout_seconds`, `throttle_ms`, `max_concurrency`, `primary_endpoint`, all `advanced` |
| 13 | Nothing today | Weight, requests per second, cache time are in the source file but never shown | Every source | (Never shown) | Shown read-only under Advanced: `weight`, `requests_per_second`, `cache_ttl_hours` |
| 14 | `ProviderDetail` | Settings card hidden for the two core sources (always on) | Wikidata, Local files | "No settings of its own" | Page model leaves the Settings card out for a core source |
| 15 | `StashBoxSettingsCard` | A servers list with name, address, state badge, Replace key, Test connection, Remove (not for the default), an Add path | StashDB | A repeating group, its row actions, a locked default row, a list kept in the data store | `control: group_list` with `item_title`, `add_label`, `locked`, `row_actions`, `fields`, and `value_source: stash_box` |
| 16 | `StashBoxSettingsCard` | "Send a fingerprint of your file" switch, value in the data store | StashDB | A toggle whose value is not in the plugin's saved settings | `toggle` + `value_source` |
| 17 | `PluginSettingsTab` About card (line ~830) and `ProviderDetail` (line ~200) | An id check for `stashbox` adds a sentence to About and the servers card | StashDB | (a Dashboard code smell, not a JSON gap) | Removed later by WP24 (D52); About text is content from JSON and the servers card is a group list |

## Which current source-file settings were already shown (D51 question)

Already editable in `ProviderDetail`: language strategy, request timeout, minimum request interval (`throttle_ms`), max concurrency, primary address. Not shown anywhere: weight, `rate_limit.requests_per_second`, burst, `cache_ttl_hours`, `hydration_stages`, `field_weights`. WP22 shows weight, requests per second and cache time read-only under Advanced. Burst, hydration stages and field weights stay out: they decide how the Priority Cascade ranks sources and are not tunables for a person.

## Defaults chosen

- **Where the schema files live.** `config/schemas/plugins/`, next to `config/schemas/providers/provider.schema.json`: `plugin-settings.schema.json` (the vocabulary), `plugin-manifest-common.schema.json`, and one per kind: `plugin-official`, `plugin-declarative`, `plugin-service`, `plugin-sandboxed`. A provider source file keeps `providers/provider.schema.json`, which now allows `settings_schema`.
- **Control types are inferred when `control` is absent**, so every existing plugin keeps working unchanged: boolean is a toggle, a list of `enum` / `choices` is a choice, integer / number is a number, `format: uri` is a url, `format: path` a path, `format: secret` a secret, an array with choices is list of choices, `fields` makes a group list, anything else is text.
- **Wording defaults.** No `title` uses the key made readable ("request_delay_ms" becomes "Request delay ms"). No `placeholder`, `unit`, `group` or `description` means none. No `order` keeps the file's order.
- **Unknown words** (on the file, a setting, a field, a scope, a control, a row action, an `actions` entry, a `show_when` or `requires` that is malformed) are ignored and collected into `things_to_know` on the page model. Nothing throws.
- **Secrets are never values.** A `secret` control only says whether it is set (household) or nothing (profile). The pattern of a credential is kept out of the wire type (the existing source contract says it is "not returned to clients"); minimum and maximum length are sent.
- **The Settings card is present only when there is at least one control.** To meet "each of TMDB, LRCLIB, StashDB, Trakt, ListenBrainz and a private plugin has a Settings card", a credential each person links for themselves (Trakt account, ListenBrainz token) is also shown as a `secret` control with `scope: profile`, and every source has its tunables. A manifest plugin that declares no settings and no per-person credential has no Settings card.
- **Test actions.** A manifest plugin shows only the tests its `settings_schema.actions` declares. A source file with no `actions` word gets `check_now` (the Engine's existing check) and, when it has an `onboarding.authentication_probe`, `test_connection`. No new test-running endpoint was added: the buttons call the existing connection and check-now actions (WP24 wires them).
- **A group list's items come from a named value source** (`value_source`), an Engine-side reader registered once (`IPluginSettingValueSource`), so the Dashboard never names the plugin. `stash_box` serves the servers and the fingerprint permission. A group list with no value source shows no items.
- **Locked items.** `locked` in the plugin's JSON lists item ids; a value source can also mark an item `locked`. StashDB's JSON locks `stashdb`.
- **Page model for an unknown plugin id** returns 404; a hidden plugin is also not found.
- **Links on a manifest.** Manifests gain an optional `links { signup_url, help_url, terms_url, privacy_url }` (D48). Source files already have them in `onboarding`.
- **Privacy sentence for a source file** is one plain default per host ("What it is asked about (titles, names and identifiers)", plus "and your key" when it needs one), because source files carry no per-host sentence.
- **Declared but undescribed settings** (a `default_settings` key with no `settings_schema` entry) are not drawn; they are reported as things to know.

## Not done in WP22 (later packets)

- The Plugin file card and the file-state facts (WP27), the Dashboard generator (WP24), shipped JSON for the compiled-in plugins (WP26: their settings still come from C# and the page model reads them through the same reader).
- Saving: the page model describes controls and current values; the existing save actions stay as they are until WP24.
