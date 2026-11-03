# Plugins v5 WP24: before and after, page by page

Spec: `engineering/specs/plugins-and-integrations-screens-v5.md` (D46, D49, D50, D52, D59). One generator
(`src/MediaEngine.Web/Components/Settings/Plugins/PluginSetupPage.razor`) now draws every plugin page from the page model the Engine builds
from the plugin's own JSON (`GET /plugins/{pluginId}/page`). The four pages below were drawn by `ProviderDetail`, `StashBoxSettingsCard`,
`PluginAppDetails` and a generic settings panel; all four are deleted. The rule "nothing lost" was checked line by line against the old
components; the one gap found is listed first.

## What had to be added to the Engine model

| Gap | Fix | Test |
|---|---|---|
| StashDB rows had no "key missing" state (the old card showed it from `HasKey`). | `key_missing` on a group row; the StashDB value source fills it. | `PluginSetupChangesTests.ARowTheValueSourceSaysHasNoKey_IsMarkedKeyMissing` |
| The page had no way to say where a setting is kept (the plugin's own settings, the source file, an outside value, the logging cap). | `stored_in` on each control. | `PluginSetupChangesTests.ThePageModel_SaysWhereEachSettingIsKept` |
| Group-list actions (add, remove, test, set a key) had no generic Engine action. | Six generic actions under `/plugins/{id}/page/...` and `/plugins/{id}/activity-cap`, all Manage-permission only. StashDB's actions are also Vault-gated. | `PluginEndpointAuthorizationTests` |
| No per-plugin logging cap (D59). | 200 entries or 14 days by default, kept in `core.json`, enforced when a line is written. | `PluginSetupChangesTests` (cap, bounds, store) and `PluginActivityRepositoryTests.TrimOlderThan...` |

## TMDB (a metadata source file)

| Before | After |
|---|---|
| Access: key status, Add key / Replace key / Remove key through the key dialog, links to get a key, last checked, Test connection. | Access card: the same, drawn from the credentials list; one Test connection button because the file declares one. |
| What it provides: media types, what it supplies, "Open Matching". | Provides card (the shared `PluginProvidesLine`) plus the same "Open Matching" line. |
| Settings: Language; Advanced connection with request timeout, minimum request interval, max concurrency, primary address; Save changes. | Settings card: Language; the same four as Advanced (collapsed); weight and rate limit shown read only; Save changes then Review changes. |
| Privacy: where searches go, whether the key is sent, terms and privacy links. | Privacy card: data leaving, destinations, Vault words, the same links, plus what it is allowed to do and its capabilities. |
| About: facts. | About card: facts, weight, who turned it on, what it needs, what needs it. |
| Health and jobs panel. | Dropped by D59. The Activity tab holds the failures; Check now is the Test connection button. |
| Activity: a flat list and a Check now button. | Activity: failures first, routine lines folded away, "Nothing has gone wrong." when empty. |

## StashDB (a metadata source file with servers)

| Before | After |
|---|---|
| Servers list: name, address, connection state, locked default server. | Group list with a row per server, state badge, locked row. |
| Per row: account key field, Replace key, Test connection, Remove (not for the default). | Per row: Add key or Replace key, Test connection, Remove (never on a locked row). Add a server form. |
| A server with no key. | "Key missing" with Add key; its Test connection is greyed. |
| "Send a fingerprint of your file" switch. | A toggle in the Settings card, saved through Save changes and Review changes. |
| Everything on the page of TMDB (language, advanced connection). | The same as TMDB. |
| Not Vault gated in the Dashboard. | Actions are Vault gated in the Engine, unchanged from before. |

## Trakt (a plugin manifest)

| Before | After |
|---|---|
| "Keys it needs": app client ID and secret, Set or Not set, Save, Replace, Remove. | Access card credentials, same behaviour. A saved value is never shown again. |
| Each person links their own account on Profile > Accounts. | A "Each person links their own" row with Open Accounts. |
| Generic settings panel built from the manifest's settings schema. | Settings card with every control type the manifest declares. |
| Privacy, About, capabilities, Health and jobs. | As TMDB above. |
| Test connection: none was declared. | None shown. A plugin that declares no test shows no test button. |

## ListenBrainz (a plugin manifest)

| Before | After |
|---|---|
| Account token as a per-person secret. | The same row, with Open Accounts. |
| Generic settings panel. | Settings card from the manifest. |
| Privacy, About, capabilities, Health and jobs. | As Trakt. |

## Defaults chosen where the spec was silent

- Settings that live outside a plugin's own settings are saved when Save changes is confirmed, not one by one.
- A group-list row action acts at once (it is not part of Save changes) and says what happened on the row.
- No toasts: a saved row glows; problems show inline.
- The logging cap bounds are 10 to 1000 entries and 1 to 90 days.
- The Activity tab shows the newest 100 lines.
