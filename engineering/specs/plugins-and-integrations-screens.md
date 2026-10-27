# Spec: Plugins and Integrations screens (one look, plus a wizard for new integrations)

> **Status:** Built, awaiting your Local checks · **Version:** 1 · **Last updated:** 2026-10-27 (locked by Shy; built the same day) · **Related docs:** [Product overview](../product/overview.md) · [Developer tools](developer-tools.md) (D-A, D-B, D-D, D-W, WP1, WP31) · [Design system](../design/design-system.md) · bug log entries F73 (Plugins revamp), F70 (icons on buttons and toggles), F69 (Library area page, shares the search and filter row), F46 (shared list spacing), F26 (menus)

## 1. Goal

Settings → Developer → **Plugins** and **Integrations** become two pages that look and behave like one family: a header with one main button, a summary strip, a search box with filters, tabs with counts, and a grid of cards. Plugins gain searching and filtering because there are now enough of them to need it. Integrations gain a three-step wizard so connecting another application is guided instead of a form. Both are built from the same shared parts so they stay consistent.

## 2. Scope

**In scope**
- **Upload plugin** (D5): a low-friction way to add a plugin from a file, with a clear warning.
- The Plugins page revamp (F73): summary strip, search, Status / Media / Sort filters, Official Tuvima switch (always shown), category tabs with counts, plugin cards, warning strips, paging.
- The Integrations page revamp: summary strip, search, Status / Access filters, All / Applications / Webhooks tabs, integration cards.
- The **New integration** wizard (Application, Access, Review and create), replacing today's inline form on the Integrations page.
- The shared page parts, built once as first-party `App*` components so the Library area redesign (F69) can reuse the search and filter row.
- The icon rule these two pages follow (a gear on Configure, a plus on the main button, a shield on Official), written down so F70 can adopt it app-wide. F70 itself (every button in the app) is **not** done here.

**Out of scope**
- Community plugins, the plugin directory and anything needing signing keys (parked). Installing from the directory stays switched off per the Developer tools build notes.
- Building the named starter integrations in D6 (only the list is targeted here).
- Changing what plugins or integrations can do, what permissions exist, or how approval works. Only how they are shown and created changes.
- Phones and TV: the Developer area stays administrator-only and desktop and tablet only (Developer tools D-Y).
- The Local AI and Developer Tools pages (they adopt the same look later, in their own change).

## 3. Decisions made

None yet; the choices Shy still needs to make are in section 7. Defaults below are what is built unless Shy says otherwise.

- **D1 (default).** One accent colour for both pages, taken from the design system, not blue on Plugins and purple on Integrations. Reason: Uniformity.
- **D2 (default).** Plugin cards keep the **switch** (turning a plugin on asks for approval, as today). Integration cards show a status label instead of a switch; turning one off or on is in its "..." menu, because an integration is a key someone holds, not a program that runs.
- **D3 (default).** The state card and its four health words (Working, Off, Needs attention, Broken) from the Developer tools spec stay the single source of truth; the new cards are a styling of that card, not a second card.
- **D4 (default).** Nothing is created or changed on first view. Opening either page is read-only.
- **D5 (Shy, 2026-10-27).** The main button on Plugins is **Upload plugin**, not Install. It lets an administrator add a plugin from a file on their computer, behind a plain warning ("Only upload plugins you trust. A plugin can read and change your library."). Uploaded plugins are labelled **Private** (not Official, not Verified), arrive switched **off**, and go through the same approval step as any plugin (what it can do, whether data leaves the machine) before they can be switched on. Reason: Privacy and Reliability, with the lowest friction that is still safe. Only (D11) plugins that run no code inside the Library (JSON-only "declarative" plugins) can be uploaded at first; plugins that run code need a signature and stay with the parked install work. Native (outside) integrations come later.
- **D6 (Shy, 2026-10-27).** The wizard will eventually ship a few **named starter integrations**; this spec only targets the list, they are built later. The Library is itself a replacement for Kodi and Plex, so no media-player starters. Target list (to confirm): Home Assistant (home automation), an automation tool webhook (n8n, Node-RED, Zapier), chat alerts through a webhook (Discord, Slack), a reading-app connection (OPDS feeds for KOReader and similar), and play-history sharing (Trakt, ListenBrainz). Until then the wizard offers Application and Webhook only.
- **D7 (Shy, 2026-10-27).** The Official Tuvima switch is **always shown**, because uploaded plugins are not Official and the switch separates them.
- **D8 (Shy, 2026-10-27).** The Plugins tab is called **Services**, not Integrations, so it does not collide with the Integrations page.
- **D9 (Shy, 2026-10-27).** Webhooks use the same three steps; step 2 chooses Events instead of Access.
- **D11 (Shy, 2026-10-27).** Uploaded plugins are declarative (JSON-only) for now; code-running plugins wait for signing and encrypted keys. "Enough utility to start."
- **D10 (Shy, 2026-10-27).** The plugin grid shows 12 per page; paging appears only beyond that.

## 4. UI behaviour

### 4a. Product walkthrough

An administrator opens Settings → Developer → Plugins. At the top: the title, "Manage extensions and integrations", and the **Upload plugin** button on the right. Below it a summary strip: how many plugins are installed, how many are switched on, how many need attention. Next a search box ("Search plugins by name or capability") with **Status** (All, Enabled, Disabled, Needs attention), **Media** (All media, Books, Comics, Movies, TV, Music and so on) and **Sort** (Name, Status, Category) beside it. Under that, category tabs with counts: All, Metadata, Media processing, Services, Utilities.

Each plugin is a card: icon, name, a one-line description, a category label, an Official Tuvima badge when it is one, and its switch with an Enabled or Disabled label. If something is missing, a warning strip sits on the card ("Requires Comskip or FFmpeg. Install it for full functionality."); pressing it opens Tools and requirements. At the bottom of the card: **Configure** (gear icon) and a "..." menu (Check health, View what it can access, Remove where allowed). Under the grid: "Showing 11 of 11 plugins", with page buttons only when there are more than 12.

On Integrations the page has the same skeleton: summary strip (integrations, active, needs attention), search, Status and Access filters, tabs (All, Applications, Webhooks), and cards. A card shows the name, its kind (User client, Application, Webhook), a status label (Enabled, Needs attention), an access label (Read only, Playback control, Library management, Custom access, Events only), how many keys are active, and when it was last used; a failing webhook shows "Delivery failed" and the last delivery time in red. **Manage** opens the integration; "..." offers Disable or Enable, Revoke keys and Delete.

**New integration** opens a full-page wizard with a three-step bar and Cancel / Back / Continue at the bottom:
1. **Application:** choose what you are connecting (Application or Webhook), give it a name and an optional icon. A webhook also takes the address to send events to.
2. **Access:** choose a preset (Read only, Playback control, Library management, Custom). Below it, "Included access" groups (Library, Playback, System) show how many permissions each holds and expand to list them. A side panel, **Access summary**, states in plain words what this means ("No write access", "Sensitive permissions excluded"). A webhook sees **Events** instead: which events it is sent.
3. **Review and create:** everything in one place, the key expiry (suggested one year, or 90 days, or never), then Create. The key is shown **once** with a Copy button and a plain note that it cannot be shown again.

### 4b. States

| Situation | What the person sees |
|---|---|
| Normal | The pages above. |
| Upload plugin | A dialog with the warning, a file chooser and Upload. A bad or unsupported file is refused in plain words and nothing is added. A good one appears as a Private, switched-off card; switching it on shows the approval step. |
| Empty (no plugins) | "No plugins installed" with a link to the guide. No filters shown. |
| Empty (no integrations) | A friendly card, "Connect another application securely to the Library", with the **New integration** button. |
| No results (search or filter) | "No plugins match" with a Clear filters button; counts on the tabs stay honest. |
| Loading | Skeleton summary strip and card placeholders; health fills in as checks return. |
| Error (Engine unreachable, not allowed) | One notice across the top with Retry; non-administrators see nothing (the page answers like one that does not exist, as today). |
| Partly ready | A card whose plugin needs a tool shows the warning strip and the plugin is still listed; the summary strip counts it under "need attention". |
| Wizard: leaving half-way | Cancel or leaving asks "Discard this integration?" (right-aligned buttons, per F35/F51). Nothing is saved until step 3 Create. |
| Wizard: create fails | Stays on step 3 with the plain-English reason and a Try again button; no key is shown. |
| Not administrator / phone | Pages and menu rows are absent (unchanged). |

### 4c. Devices

Desktop and tablet only. At tablet width the card grid drops to two columns and the filter row wraps; the wizard's Access summary moves below the presets.

### 4d. Accessibility

Tabs, switches, filter menus and the wizard step bar are keyboard-reachable with visible focus; the step bar announces "Step 2 of 3, Access"; health and warnings are never colour-only (icon plus words).

## 5. Data / model changes

- Plugins need a **category** (Metadata, Media processing, Services, Utilities) and an optional list of **media types** they apply to, declared in the plugin description and returned with the plugin summary. Built-in plugins get theirs; hand-added plugins without a category appear under Utilities and under "All media". (Existing media-type data on provider sources is reused where present.)
- Integrations need a short list of **kind** (user client, application, webhook) and a **permission preset name** shown on the card; both already exist or are derivable from today's applications and presets. No new permissions.
- No stored data changes. Pre-release; a reset is acceptable if one is needed.

## 6. Work packets

Built in the cloud and validated locally as one batch. Waves: WP1 → (WP2 ∥ WP4) → (WP2b ∥ WP3 ∥ WP5, two at a time) → WP6.

### WP1 — Shared page parts
- **What it does:** Builds the reusable page header (title, subtitle, main button), summary strip, search-and-filter row, tab strip with counts, card shell, warning strip and pager as `App*` components with component-scoped styles, and records the icon-and-button rule used by them.
- **Files likely touched:** `src/MediaEngine.Web/Components/Shared/*`, `engineering/design/design-system.md`, `tests/MediaEngine.Web.Tests/*` (to be confirmed)
- **Depends on:** none · **Size:** Standard
- **Done when:** component tests cover each part (empty, one item, many items, long names, keyboard) and the design-system page lists them.

### WP2 — Plugins page
- **What it does:** Replaces the Installed-plugins panel with the new page: search, Status / Media / Sort, Official switch (see Q4), category tabs with counts, plugin cards, warning strips opening Tools and requirements, paging. Adds category and media types to the plugin description and summary.
- **Files likely touched:** `PluginSettingsTab.razor`, `src/MediaEngine.Contracts/Plugins/*`, plugin descriptions for built-in plugins, Engine plugin catalog (to be confirmed)
- **Depends on:** WP1 · **Size:** Complex
- **Done when:** with 11 sample plugins, searching "music" shows MusicBrainz, the Media filter and tabs narrow the list, counts match, and a missing tool shows the strip; wire fixtures updated.

### WP2b — Upload plugin
- **What it does:** Adds the Upload plugin dialog and an Engine action that accepts a plugin package, checks it with the existing package rules (size caps, safe unpacking, description must be valid, kind allowed), records it as Private and switched off, and leaves turning it on to the normal approval step. Only declarative plugins are accepted at first.
- **Files likely touched:** `PluginPackageInstaller` and its endpoint, `PluginSettingsTab.razor`, tests (to be confirmed)
- **Depends on:** WP2 · **Size:** Complex (security-sensitive: independent security review before merge)
- **Done when:** a valid declarative plugin uploads, shows as Private and off; a package with code, an oversize or unsafe archive, or a clashing id is refused; a non-administrator cannot upload.

### WP3 — Plugin detail ("Configure")
- **What it does:** Gives the page Configure opens (settings, health and jobs, what it can access, remove) the same look, a back link and breadcrumb, and the "..." menu actions.
- **Files likely touched:** `PluginSettingsTab.razor` detail section, `PluginPage.razor` (to be confirmed)
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** every action reachable today is still reachable and tested.

### WP4 — Integrations page
- **What it does:** Restyles the list as cards with summary strip, search, Status / Access filters and tabs; Manage and "..." actions; failing-webhook and key-expiry warnings use the existing health rules.
- **Files likely touched:** `ManagedAccessApplications.razor`, `UsersAccessSettingsTab.razor`, `SettingsMenuBuilder.cs` (to be confirmed)
- **Depends on:** WP1 · **Size:** Complex
- **Done when:** the four example integrations render as in the design; Needs attention counts match `IntegrationHealthRules`.

### WP5 — New integration wizard
- **What it does:** The three-step full page, with presets, expandable permission groups, access summary, review, expiry choice and one-time key reveal, replacing the inline create form.
- **Files likely touched:** new wizard components under `Components/Settings/`, existing application endpoints in the Engine (unchanged), tests (to be confirmed)
- **Depends on:** WP4 · **Size:** Complex
- **Done when:** a Read only integration can be created end to end; Back keeps choices; Cancel saves nothing; the key shows once; a refused permission combination is explained.

### WP6 — Docs, tests and visual proof
- **What it does:** Updates the Using Plugins and Integrations guides, settings docs and the engineering index; adds the Local checks to the batch list.
- **Depends on:** WP3, WP5 · **Size:** Mechanical
- **Done when:** docs build passes; no doc still describes the old layout.

## Local checks (go to `engineering/validation/batch.md`)
1. Plugins page on the real library at desktop and tablet widths: search, every filter, tabs, paging, warning strips with a really missing tool (FFmpeg, Comskip).
2. Integrations: create a Read only integration through the wizard, copy the key, use it from another application, confirm Last used updates; make a webhook fail and see "Delivery failed".
3. Look and feel against the supplied designs, light and dark.

## 7. Risks and open questions

**Upload is a partial reopening of parked work (Shy, 2026-10-27).** Install was switched off when community plugins were parked (it waits on encrypted plugin keys, EK1/EK2, and the Tuvima release key). Upload brings back one slice of it: add a plugin from a file you already have, on your own machine.

*Security and privacy trade-offs of unsigned uploads:*
- A plugin that runs code inside the Library has the server's own trust: it could read the whole library, other profiles' activity, and stored keys. An unsigned upload cannot be proven to come from who it claims. So **code-running plugins are not uploadable** in this spec; only declarative (JSON-only) plugins are, which the Library interprets and which cannot run their own code. (Reliability, Privacy.)
- Even declarative plugins can contact web addresses and ask for permissions. The approval step stays mandatory, lists every address and what is sent there, and the plugin arrives **off** and labelled **Private**. Declared addresses are enforced, as today.
- Keys a plugin needs live in the config folder in plain text until EK1/EK2 are built. Upload therefore tells the person that, and the page never shows a stored key back.
- Uploading is administrator-only, recorded in History (who, when, what), and the package goes through the existing size, safe-unpack and id checks.

*What stays parked:* the plugin directory and its Install button, signed Verified and Official installs, the Tuvima release public key, encrypted plugin keys (EK1/EK2), community listings, code-running or sandboxed uploaded plugins (WP21), and updates of uploaded plugins (a newer upload with the same id replaces the old only after a fresh approval).

No open questions. Code-running plugins are not uploadable for now (D11).

Risk: uploading is the first way a person adds code-adjacent files through the Dashboard; it needs the security review in WP2b and the approval step stays mandatory.

## 8. Gaps and drift

- The design shows an "Install plugin" button; changed to **Upload plugin** (D5) because directory installs are intentionally switched off (WP19).
- The design names Home Assistant and Kodi as examples; the Library ships no such presets today, so they are illustrations, not features.
- Today's Plugins page has summary cards, a plugin picker and a detail stack rather than a grid; this spec replaces that layout.

## Plain English summary

Plugins and Integrations are redesigned to match your mock-ups: a title, a one-line status strip, search and filters, tabs with counts, and a grid of cards. Plugins get the search and filters you asked for. Adding an integration becomes a short three-step guide (what is it, what may it do, review and create) that shows the key once. Nothing about what plugins or integrations can do changes, only how they look and are set up. Instead of the Install button there is an Upload plugin button with a warning; uploaded plugins arrive off and labelled Private. It is built in the cloud and you check it on your PC in one batch.
