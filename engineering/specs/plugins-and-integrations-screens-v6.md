# Spec: Plugin and Integration pages (version 6: one look for every plugin and integration page)

> **Status:** Locked · **Version:** 6 · **Last updated:** 2026-11-04 (locked by Shy, "lock it") · **Related docs:** [Product overview](../product/overview.md) · Plugins and Integrations screens v5 (`engineering/specs/plugins-and-integrations-screens-v5.md`, Built, awaiting Local checks) · Plugin architecture (`engineering/specs/plugin-architecture.md`) · Design system (`engineering/design/design-system.md`) · Local check of 3 Nov, item 61 (`engineering/validation/local-check-2026-11-03.md`) · Mockups: `engineering/design/mockups/plugins-and-integrations-v6.html`

> **Version 6 change note (2026-11-04):** v5 works, and Shy's 3 Nov check passed the behaviour but called the look of the TMDB page and its connection, the StashDB page, the plugin file view and editor, and the Integrations page and tiles "terrible". This version changes **layout and look only**. It does not change what any plugin or integration does, the JSON-driven page model (v5 D46 to D52), or the checks and safety rules for file editing. It supersedes v5 D33's page order (as carried into v5 D46), v5 D54 and D55 for where the file lives and how the editor is framed, and adds a matching Integrations design. v5 decisions not named below stand. New decisions are D60 to D72; work packets WP32 to WP41.

## 1. Goal

Every plugin page and every integration page looks like it came from one hand: a clear sentence at the top saying whether it works, one obvious thing to do when it does not, settings you can scan, and developer material (the plugin file) kept out of the way until you ask for it.

Why this serves the product: **Ease of use** (one main action per state, status told in words), **Maintenance** (one shared page frame used by plugins and integrations, so they cannot drift apart again), **Reliability** (connect and test in one step, so a bad key or address is caught at once), **Privacy** (no change: what leaves the machine is still shown on the page).

## 2. Scope

**In**
- A shared page frame for plugin and integration pages: header, status sentence, tabs, side column (D60).
- The connection card for keys, sign-in and servers (D61, D62).
- Settings layout and the repeating-server table with its Add dialog (D63, D64).
- The Plugin file as its own tab, with the editor in a pop-over window and checks beside the text (D65, D66).
- Integration tiles, list, page and keys (D67, D68, D69).
- Phone behaviour and copy (D70).
- Public docs update.

**Out**
- What plugins do, the Engine's page model, the settings vocabulary, the file checks and locked fields (v5 stand).
- The New integration wizard's steps (kept; it only picks up the new look in WP39).
- Libraries page (being redone on its own).
- Community plugins (parked).

## 3. Decisions made

Agreed by Shy on 2026-11-04 (questions answered, spec locked).

**Benchmarks, and what we take from each**
- **Plex** (Settings, Linked accounts and Authorized devices): every outside service is one row with a plain state and a single Link or Unlink action. We take: one state, one action.
- **Jellyfin** (Plugins, Dashboard, API Keys): a plugin page is a short form with one Save; keys are a simple table with an Add button and a one-time reveal. We take: tables for repeating things, Save only when something changed. We avoid its dense, unlabelled forms.
- **Netflix** (Account, Devices): one task per screen, a big plain status, a single primary button. We take: the status sentence first and nothing competing with it.

- **D60 — One page frame.** Plugin pages and integration pages share: a header (logo 52 px, name, the 22 px tick, one status sentence, the switch), tabs, a main column and, on a computer, a slim side column. The page is centred, up to 1040 px wide, like the other Settings pages. The side column holds quiet facts (Used in, Who can use it, Version and Official or Private, Remove) so the main column holds only things you act on. The About card is retired; its content moves to the side column. One shared component draws the frame (WP32).
- **D61 — Connection card replaces Access.** One card, three states. *Not connected:* a grey row "Not connected" and one button, **Connect**. *Connected:* one green row "Connected · key ending 3f2a · tested 4 minutes ago" with **Test again** and a menu (Replace key, Remove key). *Needs attention:* an amber or red row with the reason in plain words and the one fix as the main button. Plugins that sign in to an account (Trakt, ListenBrainz) use the same row with **Connect account**; per-profile connections list each profile with Disconnect (household trust decision stands). Sign-up, help, terms and privacy links live in the dialog and the Privacy card, not floating on the page.
- **D62 — One Connect dialog: type, test, save (Shy, 2026-11-04, from his reference screenshot, local only).** Connect, Replace key and Add server all open the same dialog: a key icon or the plugin's logo, its title and one sentence, a **Get credentials** link, the provider's fields (one or two, such as API key and API secret), a **Test** button with its result beside it ("Connection verified." in green, or the reason in red), then **Cancel** and **Save**. **Save stays off until a test passes**, so a bad key is never saved. Success glows the connected row; no toast. Remove sits in the menu and asks first.
- **D71 — Calm pages (Shy, 2026-11-04: "the clutter, the lack of uniformity, the random floating text").** Every piece of text on a plugin page or tile lives inside a card, a labelled row or a tile slot; nothing floats loose. Each tile has five fixed slots: logo, name and switch; one sentence; what it does; tick and gear (with the **+ Add key** pill beside the tick when needed). Anything else moves to the plugin's page. The same row and card shapes are used on every plugin page.
- **D72 — Bigger "what it does" icons (Shy, 2026-11-04: "super small").** The coloured icons for what a plugin does are 34 px in a coloured rounded square on tiles and 40 px on the plugin page (with the name and sentence there), never small glyphs.

- **D63 — Settings you can scan.** Each setting is a row: name and one line of help on the left, the control on the right (stacked on a phone). Groups get a heading. **Save changes** is off until something changes and then shows Review changes as before. Advanced stays collapsed with the amber gear.
- **D64 — Repeating items are a table.** A group list (v5 D49), such as StashDB servers, draws as a table: tick, name, address, key state, Test and ⋯. Add server sits beside the heading and opens a dialog that **tests the connection before Add server is enabled**. The default server shows a lock and has no Remove. A server without a key shows one amber pill, **+ Add key**, in the Key column (no separate "Key missing" label: the pill is both the state and the click target, Shy 2026-11-04); the actions column is always the same two controls (Test, greyed until a key exists, and ⋯), so no row has a button that wraps or changes its shape (Shy, 2026-11-04). The same rule applies to the Keys table on integration pages.
- **D65 — The Plugin file is a tab.** A third tab, **File**, after Set up and Activity, shown only on a computer for administrators (Shy's rule: editing is desktop only; on tablet and phone the tab is absent). It shows file name, Valid or problem count, whether it differs from Tuvima's version, a full-width coloured read-only view with line numbers and, at the top right, **Edit file**. Compiled-in plugins show the shipped JSON read-only.
- **D66 — The editor is a pop-over window.** **Edit file** opens the same kind of window as Edit on an item: fixed **Cancel** and, once valid, **Review changes**, then **Save changes**; the page behind does not change. Text on the left; a **Checks** column on the right listing each problem with its line (click to jump), plus a "Locked: id, kind, network hosts and permissions" note. **Reset to Tuvima's version** is a quiet button in the footer. Review changes is a window with the line-by-line difference. This replaces v5 D55's sticky bar; the checks, locked fields, reload-and-check after saving, and keys being kept all stand.
- **D67 — Integration tiles match plugin tiles.** Same tile and list family as Plugins: logo or kind icon, name, switch, one sentence, then plain-word access chips ("Reads library", "Playback", "Progress") and "Last used 3 hours ago". Footer: tick bottom left, gear bottom right (amber when something needs you, with a pill such as "Deliveries failing"). The summary strip is dropped; tabs All, Applications, Webhooks keep their counts; search and Cards or List stay; one main button, **+ New integration**. Applications and webhooks get distinct icon colours from the tokens.
- **D68 — Integration page.** Same frame as D60. Tabs unchanged: Overview, Access, Keys, Deliveries (for webhooks and server integrations), Activity. **Overview** says in one sentence what the app can do and shows "Connect it with" (the address, with Copy) in the side column. **Access** groups permissions by area as switches with one plain line each and a coloured icon. **Keys** is a table (tick, name, created, last used, ⋯ with Rotate and Revoke behind a confirmation); **Generate key** opens a short dialog and the new key is shown once with **Copy key**. Delete integration lives in the side column, behind a confirmation.
- **D69 — Shared parts, and a test that keeps them shared.** Header, status row, side column, tab bar, table and the one-time key dialog are shared components. A guardrail test fails if an integration or plugin page draws its own header or status row. Fix the code, not the test.
- **D70 — Phone, tablet and words.** Phone and tablet: one column, status first, side column below, read only (no File tab, no editor, no key generation, as today). No toasts; the changed row glows. Square pills, coloured icons, info (i) tooltips for long help, sentence case for body text, Title Case for page and tab names. Never "Registry"; say Library, not Tuvima, for the product.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal | Header with tick and status sentence, tabs, cards and the side column as in the mockups. |
| Empty | Integrations with none: "No integrations yet" and **+ New integration**. A plugin with no settings has no Settings card. A plugin file tab always has its file. |
| Loading | Skeleton rows in the cards; header shows the name at once. |
| Error | Engine unreachable: the page-level error with Retry. A failed test: the Connection row turns amber or red with the provider's reason in plain words. A bad file: the Checks column lists lines; Save stays off. |
| Partly ready | Tests running show "Testing…" in the row. A plugin waiting for Local AI or tools says so in the status sentence, not as extra pills. |

Phone, tablet and desktop differences are in D65 and D70.

## 5. Data / model changes

None to stored data or the wire. Only presentation: new page frame and card components. One Engine read may be added if "key ending" and "last used" are not already on the page model (WP33, WP38 confirm; if needed, a small Contracts change with fixture update).

## 6. Work packets

Waves: **A** = WP32 ∥ WP36; **B** = WP33 ∥ WP34; **C** = WP35 ∥ WP37; **D** = WP38 ∥ WP39; **E** = WP40; **F** = WP41.

### WP32 — Shared page frame (D60, D69)
- **What it does:** One component for header, status row, tabs, main and side column, used by plugin pages and integration pages. Retires the About card; guardrail test.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/Plugins/PluginSetupPage.razor`, `src/MediaEngine.Web/Components/Pages/PluginPage.razor`, `Settings/ManagedAccessApplications.razor`, a new shared component and CSS, tests (to be confirmed).
- **Depends on:** none · **Size:** Standard
- **Done when:** a bUnit test renders a plugin page and an integration page and both contain the shared frame; the guardrail test passes; nothing names a plugin.

### WP33 — Connection card (D61, D62)
- **What it does:** Replaces the Access card with the three states, Save and test, the ⋯ menu, account connect and the per-profile list.
- **Files likely touched:** `Settings/Plugins/PluginSetupPage.razor` (AccessCard), `Shared/Providers/ProviderOnboardingDialog.razor`, `EngineApiClient.PluginSetup.cs`, tests.
- **Depends on:** WP32 · **Size:** Standard
- **Done when:** bUnit tests cover not connected, connected, refused and account-connected; a bad key is not saved; no toast is used.

### WP34 — Settings rows and server table (D63, D64)
- **What it does:** New row layout, Save only when changed, group list as a table, Add server dialog with test first, lock on default.
- **Files likely touched:** `Settings/Plugins/PluginSettingsCard.razor`, `PluginSettingControl.razor`, `PluginGroupList.razor`, tests.
- **Depends on:** WP32 · **Size:** Standard
- **Done when:** tests show Add server stays off until the test passes and the default server has no Remove.

### WP35 — Plugin file tab and editor window (D65, D66)
- **What it does:** File tab (desktop, administrators), full-width viewer, editor as a pop-over window with Checks column and jump-to-line, Review changes window, Reset in the footer.
- **Files likely touched:** `Settings/Plugins/PluginFileCard.razor`, `PluginFileEditorPanel.razor`, `PluginFileViewer.razor`, `PluginFileDiffView.razor`, tests.
- **Depends on:** WP32 · **Size:** Complex
- **Done when:** tests show no File tab at phone and tablet width; an invalid file cannot reach Review changes; Cancel leaves the page unchanged; the v5 file tests still pass.

### WP36 — Integration tiles and list (D67)
- **What it does:** Tiles and rows in the plugin family, access chips, last used, tick and gear, summary strip removed.
- **Files likely touched:** `Settings/ManagedAccessApplications.razor` and CSS, `AppListCard` use, tests.
- **Depends on:** none · **Size:** Standard
- **Done when:** a test renders working, failing-deliveries, off and no-integrations states.

### WP37 — Integration page (D68)
- **What it does:** Overview, Access, Keys, Deliveries and Activity in the shared frame; Keys table; one-time key dialog; Connect it with.
- **Files likely touched:** `Settings/ManagedAccessApplications.razor`, `ManagedApplicationWebhooks.razor`, tests.
- **Depends on:** WP32 · **Size:** Standard
- **Done when:** tests show the key appears once, Rotate and Revoke ask first, and Delete is in the side column.

### WP38 — Visual proof
- **What it does:** Screenshots of TMDB, StashDB, the file tab and window, the Integrations list and page, at desktop and phone width on a small generated library, published as one page for Shy.
- **Depends on:** WP33 to WP37 · **Size:** Standard
- **Done when:** the page of screenshots exists and was compared with the mockups.

### WP39 — Wizard look (small)
- **What it does:** New integration wizard picks up the new header and spacing; steps unchanged.
- **Files likely touched:** `Settings/NewIntegrationWizard.razor` and CSS.
- **Depends on:** WP36 · **Size:** Mechanical
- **Done when:** existing wizard tests pass unchanged.

### WP40 — Public docs
- **What it does:** Update the plugin and integration pages in `tuvima-docs` to the new screens, with the reasoning (why a key is tested on save, why the file is a tab and desktop only, why a key is shown once).
- **Depends on:** WP33 to WP37 · **Size:** Standard
- **Done when:** the docs build passes its link and anchor check.

### WP41 — Close-out
- **What it does:** CI-strength build and tests, Local checks appended to `engineering/validation/batch.md`, README index, spec status.
- **Depends on:** all · **Size:** Standard
- **Done when:** CI green and batch rows exist.

## 7. Local checks

- **L1.** Open TMDB with no key: two steps appear; paste a wrong key, Save and test: it is not saved and says why. Paste the right key: the green Connected row appears.
- **L2.** Open StashDB: servers are a table; Add server stays off until the test passes; the default has a lock.
- **L3.** File tab: view, Edit file opens a window, break a number, the line shows in Checks, Review changes stays off; fix it, review, save; the plugin still works and keeps its key.
- **L4.** Integrations: tiles match Plugins; open one, generate a key, copy it, close, and confirm it cannot be shown again.
- **L5.** Phone-sized window: one column, no File tab, no editing.
- **L6.** Look and feel against the mockups on SHY-DESKTOP.

## 8. Risks and open questions

- **Risks:** The first diagnosis came from the code and Shy's one-line notes; Shy then named the main complaints (D71, D72). Moving the file to a tab hides it from people who looked for it at the bottom; the Set up page can link to it ("Plugin file").
- **Open questions** (each with a recommendation):
  - None. Shy answered on 2026-11-04: the clutter, lack of uniformity and small icons were the main complaints (D71, D72); side column, File tab, editor window and same tiles agreed; Connect is one type, test and save dialog (D62).

## Plain English summary

Plugin and integration pages get one shared look. At the top you see whether it works, in a sentence. Connecting TMDB becomes two steps and one button that saves and tests your key at once. Settings become tidy rows, StashDB servers become a table, and the plugin's file moves to its own tab and edits in a window like editing an item. Integrations use the same tiles and page as plugins, with keys in a table and a new key shown once. Nothing about what plugins do changes.
