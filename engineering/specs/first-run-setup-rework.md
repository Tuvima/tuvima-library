# Spec: First-run setup rework (Welcome, step order, Libraries table, compact picker, metadata tiles)

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-11-03 (Shy locked 01:04Z) · **Related docs:** Archived `first-run-setup-flow.md` (v3, built) · `settings-and-setup-simplified.md` (built; its D8 area cards are replaced here) · local check 2026-11-02 items 1-13, 15, 39 · Mockups: [first-run-setup-rework.html](../design/mockups/first-run-setup-rework.html) (also https://claude.ai/artifact/61sfszbLBBL8dV9rZt2qa9)

> Source: Shy's walk of setup on 2 Nov 2026. The reference images Shy supplied exist only on SHY-DESKTOP, so this draft works from Shy's written descriptions; layouts will be matched to the images when they are committed. Small bugs (name/address mismatch, Change button, picker speed, path warnings) are in the bug build thread and are not repeated as work here, except where the new design depends on them.

## Plain-English product walkthrough

**Today** setup opens straight on a form, the Tools summary is one grey line, the Libraries step is four bland cards, the folder picker is tall and slow, Metadata comes after Libraries so the "nothing can identify your movies" warning always shows, and the Vault choice comes too late to affect which library kinds are offered.

**After:**
1. **A real Welcome.** Before step 1, a full-screen intro plays Shy's animation (recoloured) and lands on the tagline **"One Library. Every Story."** with one friendly sentence on what Library does. Three short cards (Find, Connect, Enjoy) say what setup does, in marketing-friendly words. A "Private by default" panel explains: runs on your own computer, files and details stay on your server, nothing sent unless you turn on a service that needs it, with a "Read how privacy works" link to the public docs privacy page (docs.tuvima.com, opens in a new tab). Buttons: Get started, and "Restore a backup instead". The old Welcome form becomes step 1, **Your Library** (name, language, region).
2. **New order:** Your Library, Your profile, Tools, **Vault (optional)**, **Libraries**, Metadata, All set. Vault now comes first so Vault kinds appear in Add library only when it is on.
3. **Tools** shows coloured summary chips (ready / needs setup / downloading) and a line of detail per tool, using the same row as Settings › Tools.
4. **Libraries** is one table (Library, Area, Folders, Status) with one **+ Add library** button. In setup the status is "Pending" and the "in your collection" figure is hidden. Settings › Libraries uses the same layout and shows Scanning and counts. This replaces the four area cards.
5. **Add library** is a four-step wizard: pick an area from a list of large horizontal rows with icon, colour and a description (Watch, Listen, Read, View; Vault only if on), pick the type from the same style of list for that area, choose the folder (compact picker), then a check after adding. New areas or types are one more segment or row.
6. **The folder picker is "dumb" and compact:** a fixed-height plain list of folders, no scanning while browsing, so it opens instantly. After Add, Library looks inside and says Looks right / Heads up (contents don't match the kind) / Already used (exact same folder in another library). Path-name warnings ("contains TV") are removed.
7. **Metadata** shows tiles that need a key first, then those that don't; all tiles the same size, at most three to a row, left-aligned (five tiles sit 3 then 2). The held-import warning is not shown mid-setup (libraries say Pending); it shows on All set only if still no source.
8. **One Connect dialog for any source:** one or several fields, a Test button, and Save only after the test passes.
9. **App-launch splash:** the same animation, short, shown while the Dashboard connects (like Plex). It never delays a ready app.

**Stays the same:** profile step, setup code, home-network rule, Restore, tool detection and downloads, folder safety rules, Vault PIN rules, "nothing leaves /setup".

## Scope
**In:** intro, step order, Tools summary, Vault-before-Libraries, Libraries table and Add library wizard (shared with Settings), compact picker and post-add check, metadata tile layout and key dialog, launch splash.
**Out:** the bug fixes in the bug build thread; Library Activity page (#14, #36); Needs Review merge (#37); JSON-driven plugin settings (own planning thread; the key dialog here must adopt its field definitions when ready); player; Home/detail redesign.

## Decisions made
Shy, 2026-11-03: agreed to all four recommendations (D1 to D4); D5 to D11 added during review.
- **D1.** Keep Libraries before Metadata. Libraries show "Pending" mid-setup; the held-import warning shows on All set only if no source is on by then.
- **D2.** The intro animation is committed to the repo at `engineering/design/assets/intro-animation.html`, then shrunk and recoloured in the build.
- **D3.** Setup may create Vault libraries right after the Vault is turned on in the same session; security review in the build.
- **D5 (Shy, 00:45Z).** Welcome copy is product-marketing in tone, built around the tagline "One Library. Every Story.", a short explainer, and a Private-by-default panel with a docs link.
- **D6 (Shy, 00:48Z).** The Vault step says plainly what it is for and what it unlocks: adult content is only available inside the Vault, nothing adult appears elsewhere, turning it on makes adult video and picture kinds available when adding a library, everything is behind its own PIN. It never calls the Vault a "fifth area" (people do not know the first four).
- **D7 (Shy, 00:50Z).** Add library asks the area first (large horizontal rows with icons, colours and descriptions), then the type from the same style of list, instead of one screen of type columns, so future types fit without looking sparse.
- **D8 (Shy, 00:54Z).** View is the personal area: photos, personal videos and personal documents, kept per person (one profile's documents are separate from another's, like pictures). Documents leave Read. Documents that belong to existing media (for example course notes beside programming videos) are shown as extras on that media's page and open in the right player or reader; they are not a View library.
- **D9 (Shy, 00:55Z).** The Vault is described as a place for anything you want hidden, adult content included, not as adult-only. In Add library the Vault row carries a small (i) icon, with a mouseover and keyboard-focus tooltip: shown only after the Vault is turned on; locked behind its own PIN; hidden from kids profiles.
- **D10 (Shy, 00:57Z).** Metadata tiles: at most three per row, left-aligned, never centred (this replaces the earlier "odd last tile centred" ask). One column on phones.
- **D11 (Shy, 01:02Z).** The compact picker simplifies the existing one and keeps what it already has: a start view listing media locations and drives on the server, a breadcrumb back to This server, and Type a path (which also takes network paths the server can reach). Only the in-picker scanning is removed.
- **D4.** Full intro plays once at first run; the launch splash is a short version.

## UI behaviour (summary; full screens in the mockups)
| Screen | Normal | Empty | Loading | Error | Phone |
|---|---|---|---|---|---|
| Intro | animation, 3 points, Get started | n/a | animation is the loading state | n/a (static fallback if animation fails) | same, stacked; reduced motion = still frame |
| Libraries | table, Add library | "No libraries yet" with Add library | skeleton rows | "Could not load" + Try again | read-only: "Add libraries from a computer" |
| Add library | 4 steps | n/a | picker shows list instantly | check shows one of 3 results | editing is desktop-only (unchanged) |
| Metadata | two groups of equal tiles | all no-key: only the second group | tile skeletons | Try again | one column |
| Key dialog | fields, Test, Save | n/a | Test shows spinner | failure reason inline, Save stays off | n/a |

## Data / model changes
- Intro and splash are Dashboard-only; no new Engine step. The intro is skipped on reload once any step is past Your Library.
- Step keys reorder to welcome, profile, tools, vault, libraries, metadata, done (pre-release: restart in-progress setups, no conversion).
- Vault libraries may be created in setup after the Vault is turned on in the same session (today refused). Needs a security review (unlocked state, PIN just created).
- Folder check after add is a new Engine action returning fit (fits / mismatch / already used) with counts; the picker listing calls no scanner.
- Key dialog: Save requires a successful test result for the exact values entered.

## Work packets
1. **WP1 Step order + Vault first** (Complex, security): reorder keys; Vault kinds only when Vault on; Engine accepts Vault library in setup after Vault enabled. Depends: none. Done: Vault kinds absent when off, present when on; guardrails green.
2. **WP2 Intro + Your Library + splash** (Standard): intro page, step 1 rename, animation converted to a small recoloured asset (reduced-motion safe), launch splash. Depends: animation file committed (Q2). Done: intro shows once; splash never delays a ready app.
3. **WP3 Tools summary** (Standard): coloured chips and detail via the shared row. Depends: none.
4. **WP4 Libraries table + Add library wizard** (Complex): shared table for setup and Settings; Pending state; remove four area cards and "Change what this folder is" (#39). Depends: WP1.
5. **WP5 Compact picker + post-add check** (Complex): dumb listing, fixed height, three result states, exact-duplicate error only. Depends: WP4.
6. **WP6 Metadata tiles + one Connect dialog** (Standard): grouping, equal tiles, centred last, no mid-setup warning, test-then-save. Depends: WP1; coordinate with JSON-driven plugin settings thread.
7. **WP7 All set, docs, tests, batch** (Standard): warning on All set only if no source; public setup guide; Local checks to `engineering/validation/batch.md`.
Waves: 1: WP1 + WP3. 2: WP2 + WP4. 3: WP5 + WP6. 4: WP7.

## Local checks
Fresh data folder at 1440 and 1280 px: intro and Get started; Vault off then on changes Add library choices; picker opens instantly on a large drive; the three check results; Metadata tile alignment with 3 + 5 tiles; test-then-save with a wrong then right key; splash on launch; Settings › Libraries matches the setup table.

## Risks and open questions
- D8 moves the Documents type from Read to View in the Add library catalog and wording (small, in WP4). Per-person documents (and an optional shared documents library) are NOT built here: Shy asked for a separate thread (2026-11-03 00:54Z). This spec only moves the Documents label to View in Add library.
- Vault in setup widens what the setup session may do (security review required, not a Shy gate).
- Skipping the area cards gives up the "teach the four areas" idea from the simplified spec; the table's Area column and the Add wizard's grouped choices keep it.
- Animation (about 980 KB) must be shrunk before use; I have not seen it.

## Plain English summary
Setup gets a proper welcome with your animation, a better order (Vault first, then Libraries), a single Libraries table shared with Settings, a fast simple folder picker that checks after you add, and tidier metadata tiles with one test-then-save key dialog. Nothing is built until you approve.
