# Spec: Settings alignment (one home for every option, one form style)

> **Status:** Built, awaiting your Local checks · **Version:** 1 · **Last updated:** 2026-11-20 · **Related docs:** [Settings and setup simplified](settings-and-setup-simplified.md) · [Look v3.2 phone and tablet standards](../look-pass-v3-2-phone-and-tablet-standards.md) · [Presentation rules](../../../docs/product/presentation-rules.md)

Locked by Shy on 2026-11-20 after answering A on questions 160 to 169. Comes from a live page-by-page review of all 38 Settings addresses.

## 1. Goal

Every Settings option has one home, every page looks and saves the same way, and no old wording contradicts how Library now works at home. People find an option where they would look for it, and a phone gets the same pages as a computer.

## 2. Scope

**In scope**
- The page frame on every Settings page (no breadcrumb, title plus one line, tabs named for what is inside, banners once).
- One save rule for every Settings page.
- Moving or merging the duplicated options listed in D3 to D9.
- Wording and form clean-up (D10 to D12), including the three wording bugs.
- Naming the event lists (D8).
- Opening Developer Tools on a phone (D13).

**Out of scope**
- Plugins and Integrations screens beyond the one move in D9 (owned by the Plugins and Integrations screens specs).
- Phone layout work itself (cards, 48 px touch areas, trimmed phone header, tables as cards). That is the phone standards spec; this spec only removes the "computer only" gates it names and keeps its page list in sync.
- Look v3.1 tokens, shared sizes and the button hierarchy (owned there; no double fixes).
- Changing what a setting does. Only where it lives, how it is named and how it saves.
- First-run setup screens.

## 3. Decisions made

- **D1. Page frame (Q160 A).** No breadcrumb on any width. A page shows its title, one line of description, then its tabs. The first tab is named for what is in it, never for the page (General becomes Name and address; Libraries becomes Libraries list; Backups becomes Back up and restore; Profile keeps Overview). Reason: the menu already says where you are; saves phone space.
- **D2. One save rule (Q161 A).** Switches and choices save the moment they change, with a quiet inline "Saved" note (no toast). Typed and number fields save when you leave the field or press Enter. A page keeps a Save button only where several fields must apply together (a pair of times, a path plus a limit), and then it is the one shared "Save changes" button. Failed saves show an inline error and keep the old value. Reason: one habit everywhere; removes eight differently named buttons.
- **D3. Graphics card (Q162 A).** Tools owns the test and its result (the Computer capacity card). Playback > Advanced owns the choice of hardware acceleration. Playback > Quality shows one line ("Graphics card: not used, playing on the processor" with a link to Tools) and opens with remote streaming quality. The second "Test again" on Playback goes away.
- **D4. Households (Q163 A).** Create, rename and remove a household only on Users & Households > Households. Manage Profiles lists people and links to that tab; its "Add household" button is removed. Library access per household stays on the Households tab.
- **D5. Playback choices for this browser (Q164 A).** Maximum resolution, hardware decoding and surround sound move from Security & Devices > Devices to the profile's Preferences under Watching, labeled "On this device". "What this device can play" and its re-measure button move with them. Devices keeps the signed-in browsers and paired devices. The Household and Profile filters move to sit directly over the sessions list they filter.
- **D6. Paired phones and TVs (Q165 A).** One list on Security & Devices > Devices. The "native clients" row on Integrations is removed there and replaced by a one-line link to Devices.
- **D7. Email setup (Q166 A).** Notifications > Channels > Email is the only place to set up the mail server. Password recovery uses it. The "Recovery delivery" block on Users & Households > Advanced becomes a status line with a link.
- **D8. Event lists (Q167 A).** Keep all four, rename so each says whose events it shows: Profile > Activity stays "Activity" (what you did); General > Library activity stays (what was added); System > Logs > Events becomes "Server events"; the Notifications inbox becomes "Messages". The Logs tab also takes the bug-report export from System > Advanced as a button.
- **D9. Pieces on the wrong page.** "Restore official plugins" moves from Tools to Plugins (the row component stays; only where it is shown changes). The Tools page keeps the tool rows.
- **D10. Wording rules (Q169 A).** Sentence case on every Settings heading and button. People-facing text says "server" where it says "Engine", and "Playback" where it says "streaming". Times use time pickers that follow the region's 12 or 24-hour clock instead of typed 24-hour text. Sizes and limits show a unit next to the field. Jargon ("canonical origin", "Reverse Proxy Trust", "Authentication mode") is replaced by what the setting does; the "Data store version: step" line leaves Backups and stays on System > Health.
- **D11. Wording bugs (found in review, fixed here).** Security & Devices no longer says a password is needed beyond "this computer"; Users & Households > Advanced no longer says only this computer is local; both follow the standing decision that no-password profiles work on the whole home network and outside access needs a password and an explicit opt-in. The menu red dot on Security & Devices shows only when something really needs doing (for example outside access is on and no password is set), not for a deliberate no-password home setup. General's "apps will be linked once published" placeholder is removed until real links exist.
- **D12. Duplicate facts.** The numeric address and QR live on General only; Network > Home network and System > Health show the address as read-only text with a link to General. Network's three status tiles become one status line with Test connection beside it. The "Remote access is off" banner shows on Home network only. General's two status tiles are removed (the tabs already show that status). Scheduled tasks' "Change it in Libraries/Backups" labels become links; the owner label "Library. Library maintenance schedule" is reworded.
- **D13. Developer Tools on phones (Q168 A).** Open to administrators on a phone, with testers laid out as cards. Follows the phone-equals-desktop rule.
- **D14. Merges inside pages.** Local AI: Status and Models become one tab, with Advanced kept; one link to Plugins instead of two; the "switched off, See tools" banner shows once. Libraries: no ghost row when there are no libraries; each Matching provider row that says "Needs setup" gets a direct link to its place on Plugins. Profile: Linked accounts tab shows only when a plugin offers one; Import becomes a button on Overview that opens the existing Import page. Notifications: Channels and "What goes where" first, the inbox as a "Messages" strip below.
- **D15. Layout bugs.** The Computer capacity card on Tools keeps its text on readable lines at 1280 px; Users & Households > Advanced drops the doubled "Authentication / How people sign in" headings and replaces the Authentication mode dropdown with two plain switches ("Allow passwords", "Require sign-in for everyone") that save under D2.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal | Every page: title, one line, tabs, content. No breadcrumb. Switches save at once with "Saved". Hardware status is one line on Playback. |
| Empty | Libraries with none: an empty state and Add library, no ghost row. Linked accounts hidden until a plugin offers one. Messages strip says nothing yet. |
| Loading | Existing page loading state (no change). |
| Error | A failed save shows an inline message under the field and keeps the old value. Remote and tool problems keep their current inline notices. |
| Partly ready | Tools shows the test result; Playback shows the one-line status and link until the test has run ("Not tested yet"). |
| Phone | Every page and tab works; no "use a computer or tablet" page remains (including Developer Tools). Layout rules come from the phone standards spec. |
| Kid or limited profile | Unchanged: Kids see Profile only. |

## 5. Data / model changes

None to stored data. Settings values keep their current keys; "This browser" choices keep their per-device storage and are shown from profile Preferences. Menu entries and tab addresses change (retired addresses are not forwarded, pre-release).

## 6. Work packets

### WP1 — Page frame and save rule
- **What it does:** Removes the breadcrumb, renames first tabs (D1), adds the shared inline "Saved" and error note and the shared Save changes button (D2); applies to the shared frame first.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/Frame/SettingsPageFrame.razor`, `SettingsPageTabs.razor`, `Models/ViewDTOs/SettingsNav.cs`, shared `App*` setting row components (to be confirmed), tests.
- **Depends on:** none (shares styling with Look v3.1; follows its tokens)
- **Size:** Standard
- **Done when:** no Settings page renders a breadcrumb at 390, 768 or 1280; a render test confirms the first tab name differs from the page name on every page; a switch change shows "Saved" without a toast.

### WP2 — Apply the save rule to every page
- **What it does:** Converts each per-section save button to the D2 behavior or the shared Save changes button; sentence-case headings and buttons; unit labels; time pickers following region clock (D10).
- **Files likely touched:** General, Playback, Network, Users access, Local AI, Backups, System panels under `src/MediaEngine.Web/Components/Settings/` (to be confirmed), localized strings.
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** a guardrail test lists no button named "Save <thing> settings"; maintenance time fields are pickers; a heading-case test passes for Settings strings.

### WP3 — Graphics card, event lists and wrong-page moves
- **What it does:** D3 (Playback and Tools), D8 (renames and export button), D9 (Restore official plugins to Plugins), D12 scheduled-task links.
- **Files likely touched:** `PlaybackSettingsPage.razor`, `ToolsRequirementsTab.razor`, `ComputerCapacityPanel.razor`, `GraphicsHardwareCard.razor`, `ScheduledTasksPage.razor`, `LogsPage.razor`, `LogRetentionPanel.razor`, `RestoreOfficialPluginsRow.razor`, `PluginSettingsTab.razor`.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** Playback shows one hardware line with a link; Tools has one test; Restore official plugins appears on Plugins only; Logs has the export button.

### WP4 — People, devices and email homes
- **What it does:** D4 (households), D5 ("On this device" in Preferences), D6 (one paired-devices list), D7 (one email setup).
- **Files likely touched:** `ProfilesTab.razor`, `HouseholdsTab.razor`, `DevicesTab.razor`, `PairedDevicesSection.razor`, `ThisBrowserVideoSection.razor`, `PlaybackSettingsPage.razor`, integrations native clients row, `NotificationsPanel.razor`, `UsersAccessSettingsTab.razor`.
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** Add household exists only on the Households tab; This browser choices are in Preferences > Watching; the Integrations page has no native clients row; the Users Advanced email block is a link and status line.

### WP5 — Wording bugs and duplicate facts
- **What it does:** D11 wording, red dot rule, D12 address and banner changes, D15 layout fixes, D10 jargon replacement and "server" wording.
- **Files likely touched:** `SecuritySettingsPage.razor`, `UsersAccessSettingsTab.razor`, `SettingsMenuBuilder.cs`, `Pages/Settings.razor`, `GeneralPage.razor`, `NetworkOverviewPanel.razor`, `LocalNetworkSettingsPanel.razor`, `SystemSummaryPage.razor`, `ComputerCapacityPanel.razor`, `BackupSchedulePanel.razor`, strings.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** no string says "works only on this computer" or "only this computer is local"; the red dot test passes both ways; Tools capacity card text holds at least 20 characters per line at 1280; the address appears editable only on General.

### WP6 — Inside-page merges
- **What it does:** D14 (Local AI tabs, Libraries empty state and Matching links, Profile tabs and Import button, Notifications order).
- **Files likely touched:** `LocalAiSettingsTab.razor`, `LibrariesTab.razor`, `LibraryFolderTable.razor`, provider matching panel, `Settings/Overview/*`, `NotificationsPanel.razor`, `SettingsNav.cs`.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** Local AI has two tabs; zero libraries shows only the empty state; a "Needs setup" Matching row links to Plugins; Linked accounts is hidden when no plugin offers one.

### WP7 — Phone gates and Developer Tools
- **What it does:** Removes every remaining "computer or tablet" gate on Settings (including Developer Tools, D13) in step with the phone standards spec, and updates the phone page list in the guardrail.
- **Files likely touched:** `SettingsNav.cs` (`MobileAvailability`), `AdministratorSurfaceGate.razor`, per-page phone layouts, `SettingsGuardrailTests` (to be confirmed).
- **Depends on:** WP2 to WP6 and the phone standards foundation packets
- **Size:** Complex
- **Done when:** a test fails if any Settings page offers a "use a larger screen" message; all 38 addresses open with their full actions at 390 px.

### WP8 — Docs and index
- **What it does:** Updates `src/MediaEngine.Web/CLAUDE.md` Settings rows, the public Settings docs pages and the README index.
- **Files likely touched:** `src/MediaEngine.Web/CLAUDE.md`, `engineering/README.md`, `tuvima-docs` Settings pages.
- **Depends on:** WP1 to WP7
- **Size:** Mechanical
- **Done when:** docs describe the new tab names and homes; link check passes.

## 7. Local checks

- **L1.** Open every Settings page on a real phone with real libraries, plugins, devices and backups; nothing says "use a computer or tablet"; every action works.
- **L2.** On the real PC with a real graphics card: Tools shows the test once; Playback shows one hardware line and its link; Advanced still changes acceleration.
- **L3.** Pair a real phone or TV: it appears once, on Security & Devices > Devices.
- **L4.** Change a switch and a typed field on General, Playback and Network: "Saved" appears, nothing needs a Save button, a bad value keeps the old one.
- **L5.** Send a test email from Notifications; start a password recovery; both use the one mail setup.
- **L6.** With real events: Library activity, Server events, Messages and Profile > Activity each show their own list and names.

## 8. Risks and open questions

- **Risks:** Moving "This browser" choices to Preferences changes where people find resolution settings (the saved values stay). Instant saving can surprise someone who expects a button; the "Saved" note and inline errors mitigate this. WP7 depends on the phone standards work and must not duplicate its layout changes (Look v3.1 owns shared styling).
- **Open questions:** none from the review. Not decided here, listed for later: whether Linked accounts should move into Integrations when more than one plugin offers accounts.

## 9. Plain English summary

Each Settings option gets one home and every page looks and saves the same way: no breadcrumb, switches save instantly, typed fields save when you leave them. The graphics card test lives on Tools, households live on one tab, paired devices are in one list, email is set up once, and "Restore official plugins" moves to Plugins. Old "works only on this computer" wording is corrected to match how Library works at home, and every page, including Developer Tools, opens fully on a phone. No stored data changes.
