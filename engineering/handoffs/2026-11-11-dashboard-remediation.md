# Dashboard remediation after the 2026-11-11 frontend review

Run this handoff end to end per `.claude/rules/end-to-end-runs.md`.

> **Status:** In progress · **Approved by:** Shy, 2026-11-11 03:28Z ("remediate everything in this thread") · **Source:** the review "Dashboard review: built screens, components and architecture" (main `95e2c30c`), written from the code beside the approved mocks and Locked specs.

## Goal

Bring the built Dashboard back in line with its approved mocks and Locked specs, remove the leftovers the review listed, take the cheap speed gains, unify the duplicates, and then restructure the largest screens so the same drift does not return. No new features.

## Decisions taken where specs disagree (defaults; Shy may overturn any of them)

The rule used: the newest Locked spec and its mock win over older ones.

| # | Question | Default taken | Source |
|---|---|---|---|
| R1 | Series, collection and Universe tiles | Posters (214 by 321) with the count only, and "..." on every cover | One tile design D2, D3, Home marker 10 |
| R2 | Tile label style | Stays as built (13 px, mixed case): the type ladder starts at 12 px | Look v3 type ladder |
| R3 | Hero button size and hero height | Stay as built (56 px button, 70 percent height); older lines are struck from the rules files | Look v3 D12, Look pass |
| R4 | Editor window size | The wide editor window stays; it is the listed XL size | Editor v5, design system |
| R5 | Player panel close button | Stays 36 px inside player panels | Player v2 mock |
| R6 | View page Folders tab default | Read-only | View folders D4a |
| R7 | Phone audiobook tools when a book has no chapters | Chapters slot is left out; History does not stand in | Player v2 D26, screen 15 |
| R8 | Feedback after an action (replaces toasts) | A change you made that worked: the changed row or card glows, or the quiet "Saved" line where there is no row. A failure: an alert in place (in the window, or the page notice). A background result: the Activity indicator. No pop-up notices | The "no toasts" lines in the Libraries, Global bookmarks, Language and Collections specs |
| R9 | Editor header title size | Stays 24 px: the Look v3 type ladder has no 28 px step (the Editor v5 mock drew 28) | Look v3 type ladder |
| R10 | Icons in the detail hero row | Stay as built: the round buttons use the player glyph family, the main button and the folder button use Material | `.claude/rules/dashboard-surfaces-home-player.md` |
| R11 | First-run setup, provider sub-lines | Stay as the provider's own summary sentence; the mock's short area names would need new wording per provider | Setup mock, provider config |
| R12 | "..." on a Universe tile | No menu: a Universe takes overrides only and its Edit needs an administrator unlock on its own page, so a tile cannot open it | Never rule 1 |

## Threads, in order

Each thread is one or more proposed changes that merge when CI is green and the reviewer passes. Waves of one thread run one after another; at most two helpers at once.

| # | Thread | Depends on | Notes |
|---|---|---|---|
| 1 | Mock-match fixes | none | Packets M1 to M8 below |
| 2 | One feedback rule (R8) and the toast sweep | 1 | Rule written into `engineering/design/design-system.md`; sweep in batches by area; a ratchet test with a ceiling that only goes down, ending at 0 |
| 3 | Quick speed wins | none (different files from 1) | Video library and map styles loaded only where used; one shared tooltip listener; player code out of the all-purpose script |
| 4 | Cloud visual check trial | none | Start the app on the sample library in the cloud and capture screens at 1024, 1280, 1440 and phone width; report whether it works |
| 5 | Unification round | 2 | Hand-styled buttons, menus, tab bars, pills, confirmation windows, older window body, unlock window (security review), fifth floating surface, reader icon buttons, little-used layout parts, second icon system, Look counters lowered, new counters |
| 6 | Structure, part 1 | 5 | Editor and player get their own folders; rules files corrected; the 22 "legacy" notes closed |
| 7 | Structure, part 2 | 6 | One screen per proposed change: browse shell, music bar, collections editor, detail page, playlist page; photo and document editor merged into the main editor; one music table; one reader chrome |

## Thread 1 packets

- **M1 Tiles (Complex).** R1; "..." on group and playlist tiles; the kind word leaves the New slot; "..." bare with a soft shadow at 44 px on every device; Continue shows a bare white play triangle.
- **M2 Libraries (Standard).** Edit window lead no longer says Rename; folder pop-over shows friendly name and Read-only or Managed tag; a kind that already has a library is greyed with "Add a folder to it instead"; Add folder from Edit is the same window as step 3; one safety line; headed table and footer actions in Edit; name only in the table; US spelling; "Contents"; "Where is it? · Movies".
- **M3 First-run setup (Standard).** Vault step content, Back and "Turn on the Vault"; "Name your Library"; loading and error states; Skip for now and Continue together; mock headings; "optional"; Local AI as a Later row; warning at the top of All set.
- **M4 Library Activity (Standard).** Area chips filter the whole card; "Some files are waiting"; Add library in the empty History; custom range and "All types"; "All libraries" chip without an arrow; one "Activity" title.
- **M5 Comic and PDF reader (Standard).** Tool order; phone "..." sheet; short count and no Two pages on a phone; two Direction choices; count right of the slider; token colors; "Settings" title.
- **M6 View folders (Standard).** "Add a Managed folder" opens Add folder with Managed chosen; mock headings from the strings file; medium Edit window; Folders button with a menu; notice wording and amber edge; safety lines in the footer; R6.
- **M7 Collections (Complex).** Save glows a row, no toasts; From a list in two columns with greyed tiles and three figures; one drop-or-paste field; one Order row; Name step and footer per the mock; wording to the strings file; token colors.
- **M8 Small ones (Standard).** Editor header title 28 px, item drop-down border at rest, footer wording from the strings file; R7; Sleep timer panel 380 px; hero Open tooltip not repeated; one icon family in the hero row; rules files' older hero sizes struck.

## Thread 1 result (built 2026-11-11)

All eight packets built, reviewed and verified (CI-strength build, 0 warnings; filtered tests pass). Left for later threads or for Shy:

- Collections: about 13 toasts for other outcomes remain (thread 2). "Not in your library" shows 0 and there are no per-type pills, because the Engine does not yet tell an unmatched title from one that is not owned (needs Shy: an Engine addition). "Keep it in step" is still a segmented control (thread 5).
- Tiles: unused model leftovers (`GroupSource` is still read by tests, `HasCustomArtwork`, `HoverLayout`) go in thread 6.
- Libraries: the library page's Add folder button still types its window wording in code (thread 5).
- Library Activity: "Review N" opens Needs Review unfiltered; the library page's Activity card is still whole-library.
- A mixed curated collection still opens the editor as "Books" (no neutral kind exists; thread 7 with the collections editor).

## Thread 4 result: cloud visual check trial (2026-11-11)

It works in part. A cloud session can assemble the app, start the Engine and Dashboard on a small generated library, pass first-run setup in a browser and capture styled screenshots at 1024, 1280, 1440 and phone width in about five minutes (`tools/Capture-DashboardScreens.mjs`; the start commands are in its header). Settings, setup, Libraries, Activity and empty states can be checked this way today.

Pages that need identified media (Home, Read, Watch, Listen, an item's page) stay empty, because the cloud machine's network rules block the metadata sources (Apple, MusicBrainz, TMDB, Wikidata), and the repository holds no recorded provider answers to replay. Two ways to close the gap, both for Shy to choose: allow those hosts in the cloud environment's network settings, or record one set of provider answers on the PC and commit it as test data for replay. Until then the batched Local check stays the proof for media pages.

## Local checks

Appended to `engineering/validation/batch.md` as each thread merges.

## Reserved for Shy

Nothing blocks the run. The unlock window change in thread 5 gets the security review the rules require before it merges.
