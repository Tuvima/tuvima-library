# Spec: One shared look for pages (tabs, cards, filter bar, pills)

> **Status:** Built, awaiting your Local checks · **Version:** 1 · **Last updated:** 2026-10-29 (locked by Shy; built the same day) · **Related docs:** [Plugins and Integrations screens](plugins-and-integrations-screens.md) · [Library pages](library-pages.md) · [Settings and top navigation](settings-and-top-navigation.md) · [Design system](../design/design-system.md) · Bug log F76, F84, F90

## 1. Goal
Plugins, Integrations, the Libraries area and every Settings page that has tabs look and behave like one family. Today they do not: the tabs are boxed and heavy, cards differ in height, text wraps, stray content sits under the cards, and the word "Plugins" appears twice. Fix it once, in the shared parts, so every page that uses them changes together (Uniformity, Maintenance).

## 2. Scope
**In scope**
- One tab style (flat, underlined, optional icon and count), used by every tabbed page including the Settings flat-menu pages (F90) and the library page.
- One card (`AppListCard`): equal height per row, fixed anatomy, text clamping, a compact warning badge.
- One filter bar, summary strip, pager and pill/badge set, restyled to the target screenshot (Settings > Developer > Plugins).
- Applied to: Plugins, Integrations, Libraries overview, the library page tabs.
- Removing the duplicated "Plugins" / "Integrations" wording.

**Where the older library-setup items belong (checked 2026-10-29)**
- **F6** (setup wizard libraries step differs from Settings): this spec's packet 4 makes the wizard step use the same Libraries page parts as Settings, so the *look* matches. Making it literally the same screen and flow is part of the companion spec below.
- **F7, F10, F24** (Add folder dialog, too many steps, "go to music" prompt, too many info buttons) and F79 (folder picker as its own pop-up): these are *flow* changes. They go in a companion spec, "Library setup and add-folder flow", planned next and built after packet 4. Not covered here.
- **F5** (Tesseract and FFmpeg wording in the setup wizard): belongs to First-run prerequisites, not this spec.

**Out of scope**
- What libraries do: the Create library dialog, the folder picker pop-up, no per-profile library access (F79, F80, F83) are separate work. This spec only gives them the shared look.
- What plugins and integrations can do.
- Phone Developer area (stays desktop and tablet only).

## 3. What is wrong today (found in the code)
1. **Three competing tab styles.** `wwwroot/app.css` styles `.tl-tab` twice with `!important` (one flat underline, one boxed purple pill that wins because it comes later), and `native-structure.css` adds a 160px minimum width. Result: heavy boxed tabs that are wide and uneven. This is the "ugly tabs" Shy sees on Plugins and Libraries.
2. **Two tab components** (`AppTabs` and `AppCountTabs`) share those global rules, so a fix in one place was never applied to both.
3. **Card height** is set by content (`AppListCard` has no fixed rows), so a warning row or a long description makes one card taller.
4. **Text wraps** (`overflow-wrap: anywhere`, no clamp).
5. **Duplicate title:** the page header says "Plugins" and so does a back-link/breadcrumb and a summary label.

## 4. Decisions made
Shy answered the questions on 2026-10-29: D1 to D7 are decided as written below (columns, warning badge, title once, text clamping, flat tabs everywhere, show all until 24 or more, look-only scope).
- **D1 (Shy, 2026-10-29).** One tab: flat, transparent, 2px accent underline on the active tab, muted text, accent text when active, optional icon, count shown as small muted number (no pill). One definition in a single stylesheet; the two `!important` duplicates and the 160px minimum are deleted.
- **D2 (Shy, 2026-10-29).** `AppTabs` and `AppCountTabs` render the same markup and class; the Settings pages use them for sub-sections.
- **D3 (Shy, 2026-10-29).** Card grid is 3 columns on desktop, 2 on tablet, 1 on phone; every card in a row has the same height.
- **D4 (Shy, 2026-10-29).** Card anatomy, top to bottom: icon, name (one line, full name on hover), status pill, switch; description (two lines max); small category and Official pills; footer with Configure and "...". A missing requirement is a small amber badge that opens the detail, not an extra row.
- **D5 (Shy, 2026-10-29).** The page title is the only place the page name appears. Summary strip and tabs carry numbers only.
- **D6 (Shy, 2026-10-29).** Paging appears only above 24 items; below that, show all.
- **D8 (Shy, 2026-10-29).** Text is a little larger and clearer on the flat pages: tab labels go one step up the type scale, set once in the shared tab; card names and descriptions go up one step too, within the 12px minimum rule.
- **D7 (Shy, 2026-10-29).** One accent colour for the family (as in the Plugins and Integrations spec D1).

## 5. UI behaviour
Empty: the shared empty state with one action. Loading: card skeletons of the same height. Error: the shared error state. Partly ready (a plugin still checking): a muted "Checking" pill, same card size. Desktop 3 columns, tablet 2, phone 1 (tabs scroll sideways, no wrapping). Keyboard: arrow keys, Home and End move between tabs (kept). Visual proof at phone, tablet and desktop widths.

## 6. Data / model changes
None. Styling and component structure only.

## 7. Work packets
1. **Tab consolidation (Standard).** Replace the three `.tl-tab` rule sets with one flat underlined style; delete the `!important` duplicates and `min-width: 160px`; make `AppTabs` and `AppCountTabs` share the markup. Files: `wwwroot/app.css`, `wwwroot/native-structure.css`, `Shared/AppTabs.razor`, `Shared/AppCountTabs.razor(.css)`. Depends on: none. Also adds (asked for by the Settings flat-menu spec): **route-linked tabs** (a tab can be a link with its own address, so deep links and back/forward work) and **larger tab labels** (one step up the type scale, 14 to 16px, set once in the shared tab; Shy: text a little larger and clearer on flat pages) and **sideways scrolling on phone** (the tab row scrolls, never wraps or truncates). Done when: Plugins, Libraries and Logs tabs look identical, the old boxed tab appears nowhere, a route-linked tab survives reload and the Back button, and a phone-width tab row scrolls sideways.
2. **Card, pills, warning badge (Standard).** Fixed-height rows, clamp rules, compact amber badge, one pill set. Files: `Shared/AppListCard.razor(.css)`, `AppWarningStrip`, badge styles. Depends on: none (parallel with 1). Done when: six cards of mixed description length and one with a warning are the same height.
3. **Filter bar, summary strip, pager (Mechanical).** Align spacing, heights and selects to the screenshot; pager only above 24. Files: `AppSearchFilterRow`, `AppSummaryStrip`, `AppPager`. Depends on: 1.
4. **Apply to pages (Standard).** Plugins, Integrations, Libraries overview, library page tabs; remove duplicated wording; breadcrumb carries the path. Files: `Settings/PluginSettingsTab.razor`, `Settings/ManagedAccessApplications.razor`, `Settings/Libraries/*`. Depends on: 1, 2, 3. Done when: the three pages match the target screenshot side by side.
5. **Design-system preview and guardrail (Mechanical).** Update `DesignSystemPreview`, `engineering/design/design-system.md`, and add a test that fails if a page defines its own tab style. Depends on: 4.

Packets 1 and 2 run together; Settings flat-menu work (F90) uses packet 1's tab and waits for it.

## 8. Local checks
Open Plugins, Integrations, Libraries and a library page on SHY-DESKTOP at desktop width and a narrow window: tabs identical, cards equal height, no stray content under cards, nothing wraps, the page name appears once.

## 9. Risks and open questions
- Deleting global `!important` tab rules can change other tabbed pages (Logs, Chronicle explorer, Browse). They all pick up the new look, which is intended; the visual check covers each.
- The questions in the thread (card columns, warning, title, long text, tabs, paging, scope) decide the defaults above.

## Plain English summary
The ugly tabs come from three separate tab styles fighting each other in the app's stylesheet. We replace them with one flat underlined tab, make every card the same height with tidy text, and use the same search bar, summary strip and pills everywhere. Then Plugins, Integrations and Libraries (and the new flat Settings pages) all look like they belong together.
