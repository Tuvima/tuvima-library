# Spec: Phone and tablet standards (Look v3.2)

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-11-20 · **Related docs:** [Look v3.1](archive/look-pass-v3-1-controls-and-type.md) · [Design system](../design/design-system.md) · Locked by Shy 2026-11-20 (answers 110 to 119, all A)

## 1. Goal
The phone and tablet apps follow one written set of sizes, spacing and patterns that is checked automatically, every Settings page works fully on a phone, and the same set feeds the future Android and iOS apps. Most people use only a phone, so it has to feel complete and consistent.

## 2. Scope
**In scope:** device sizes, touch areas, control scale, spacing, phone page header, shelf header, page action row, sheets by default, hover guard, an automatic phone check, a one-file tokens source, Settings admin on a phone (Manage Profiles fix, library folders, users, plugins, import, integrations, tables as cards).
**Out of scope:** building the Android and iOS apps (they start after steps 1 to 3); the page-by-page Settings review for function and wording (its own thread); light theme; changes to the editor, players and readers beyond the shared rules.

## 3. Decisions made (Shy, 2026-11-19, option A on questions 110 to 119)
- **D1 (110).** Device sizes: Compact under 600, Medium 600 to 839, Expanded 840 and up; 1180 stays as a wide-page extra. Other widths are retired over time; the old-exception list only shrinks. The player's 720 rule moves to 600.
- **D2 (111).** Touch area is 48 by 48 on phone and tablet. Looks may be smaller; standard buttons stay 44 high, the one main action on a screen is 48.
- **D3 (112).** Every Settings page works fully on a phone with the same actions, as cards and sheets. Setup is the only exception. Technical file editors (plugin files, ingestion flow) come last in the parity step.
- **D4 (113).** A phone page has at most two layers above content. Inside Settings, details, My List and Search the bar is a back arrow, the page name (with a picker where it has siblings) and the avatar. Section chips stay on Home and the five area pages. Breadcrumbs are hidden on a phone.
- **D5 (114).** My List is in the bottom dock only on a phone, not the top bar.
- **D6 (115).** Control scale: heights 44 and 48 (36 only on desktop with a mouse); corners 10; full round only for avatars and pills; tab underline square; text fields 44 high with 16 px text; button text 14.
- **D7 (116).** One page action row: main action first in purple, others outlined at the same height, scrolls sideways if needed. Full-width stacking only in forms and sheets.
- **D8.** One shelf header: 20 px semibold title, optional 13 px description, "View all" in the accent color with a 44 px touch area, on every shelf.
- **D9.** Menus, pickers and dialogs are bottom sheets on Compact by default; full screen only for viewers, readers, the editor and Review changes.
- **D10.** Hover styles apply only on devices with a mouse; anything hover reveals is always visible on touch.
- **D11 (117).** One tokens file is the single source (sizes, spacing, radii, type jobs, colors, device sizes), generated into web CSS, the Android theme and the iOS theme. Android moves off the stock Material colors onto it.
- **D12 (118).** An automatic render check at 390 and 768 fails a change that adds a touch target under 44 (48 on the dock), sideways overflow, text under 12 px, a hover-only reveal, or "on a computer" text outside setup.
- **D13 (119).** Settings shows the page picker under 840 and the sidebar from 840.

## 4. UI behaviour
| Situation | What the person sees |
|---|---|
| Normal phone page | App bar, then at most one more row, then content starting near the top |
| Settings admin on phone | Same actions as desktop; lists as cards; edits open as sheets |
| Empty / loading / error | Unchanged; each keeps its current state card |
| Tablet 600 to 839 | Phone patterns with wider margins (24); 840+ gets sidebar and desktop bar |
| Setup | Unchanged; the only place "on a computer" wording may remain |

## 5. Data / model changes
New generated tokens file; no data-store change. Android and iOS themes are generated from it.

## 6. Work packets
### WP1 — Tokens file and generator
- **What it does:** one tokens source with a generator to CSS, Android and iOS; Android theme uses it.
- **Files likely touched:** `src/MediaEngine.Web/wwwroot/tuvima.tokens.css`, `clients/android/**`, `clients/apple/**`, a generator under `tools/` (to be confirmed)
- **Depends on:** none · **Size:** Complex
- **Done when:** a test fails if the generated files differ from the source; the Android mobile app shows Library colors.
### WP2 — Device sizes and touch area
- **What it does:** D1 and D2: one device-size definition shared by CSS, C# and JS; the touch-area rule applies to every pressable element, not a hand list.
- **Files likely touched:** `wwwroot/app.css` (tap-target block), `Services/Playback/PlaybackFormFactor.cs`, `Services/Theming/DeviceContextService.cs`, `ResponsiveBreakpointGuardrailTests.cs` and its allowlist
- **Depends on:** WP1 · **Size:** Complex
- **Done when:** Plugins and Matching show no target under 44 at 390; the old-width list is shorter; a 650 px window gets a tablet player.
### WP3 — Automatic phone check
- **What it does:** D12 and D10 as a render test over pages that render without media.
- **Files likely touched:** `tests/MediaEngine.Web.Tests/` (new), `tools/validate-touch-targets.js`, `LookRatchetGuardrailTests.cs`
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** adding a 30 px button or a hover-only reveal turns the check red.
### WP4 — Page header and shelf header
- **What it does:** D4, D5, D8, D13.
- **Files likely touched:** `Components/Navigation/*`, `Components/Settings/Frame/*`, shelf header components, `Components/Pages/Settings.razor`
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** Settings > Libraries at 390 shows content within the first 200 px; every shelf says "View all".
### WP5 — Control scale, action row, sheets by default
- **What it does:** D6, D7, D9.
- **Files likely touched:** `Components/Shared/App*`, `AppDialogService.cs` default, View and Collections page headers
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** the control scale test shows only 44/48 heights and the four radii; every dialog opens as a sheet at 390.
### WP6 — Settings parity
- **What it does:** D3: Manage Profiles layout fix and Add person, library folders, Users, Plugins, Integrations, Import, tables as cards (16 tables).
- **Files likely touched:** `Components/Settings/**` (about 10 files with "on a computer" gates), `AppTable` card mode
- **Depends on:** WP2 · **Size:** Complex (two packets: profiles/folders/users, then plugins/integrations/tables)
- **Done when:** no "on a computer" text outside setup; Manage Profiles at 390 shows the person's name, role chips and a full-width Edit button without squeezing, and an Add person button.

Waves: WP1; WP2; then WP3 with WP4; then WP5 with WP6. Native apps follow.

## 7. Local checks
- **L1.** Editor, Review changes, Match at 390 and 768 on a real title.
- **L2.** Video, music, full player and the three readers at 390 and 768.
- **L3.** Watch, Listen, View, Collections with real content; the empty band above Home's first shelf.
- **L4.** Vault on a phone.
- **L5.** Tap a tile and a button on a phone and an iPad: nothing stays highlighted.
- **L6.** Every dialog on a phone is a sheet; notch and gesture bar are cleared.
- **L7.** Mock | Live at 1024, 1280, 1440 and phone for the changed pages.

## 8. Risks and open questions
- **Risks:** a 48 px touch area can overlap neighbors in dense rows (checked by the render test); moving the player to 600 changes the 600 to 720 window; opening Settings admin on phones widens what a phone can change (the Engine's permission check still decides who).
- **Open questions:** none. A separate Settings page-by-page review may adjust WP6's page list; the build asks Shy before going beyond this spec.

## Plain English summary
Write the phone sizes down once, check them automatically, trim the top of each phone page, make titles and buttons look the same everywhere, and let every Settings page work on a phone. The same file will later feed the Android and iPhone apps.


## Evidence behind the decisions (cloud measurement, 2026-11-19, test data)
42 pages measured at 390, 768 and 1280 px: 34 had no target under 44 at 390. Small targets: Plugins 26, Matching 7, design preview 5, Libraries 2, Playback, Integrations, Network and Library activity 1 each. Manage Profiles is broken at 390 (name squeezed one letter wide, Edit cut to "E..."). About 39 "on a computer or tablet" read-only spots in Settings. About 25 distinct layout widths in use (the guard test carries 146 old exceptions). 16 of 25 tables have no phone card mode. 79 dialogs, 31 opt in to sheets. Android phone app uses the stock Material dark colors and hand-typed dp values. Places map labels are 9 to 10 px.
