Run this handoff end to end per `.claude/rules/end-to-end-runs.md`, building in cloud sessions per `.claude/rules/cloud-first.md`.

# Handoff: finish the Universes spec

> Written 2026-10-20 by the Universes coordinator session for Shy (product owner, not a developer), at the close of thread 2b of [the 2026-10-19 handoff](2026-10-19-finish-outstanding-work.md). The spec is [`engineering/specs/universe.md`](../specs/universe.md) (v4, Locked decisions in section 3 and section 7); it wins over this note wherever they disagree. Re-check `main` with `git fetch` and `git log` before each thread, and run the spec's section 6B collision check.

## 1. Where things stand

| Packet | State | Where |
|---|---|---|
| U-WP0, U-WP1, U-WP2, U-WP3 | Built | PR 268, PR 284 |
| U-WP4 Universe record, (Series, position) pairs, audiobook fallback | Built except the priority piece (see thread E) | PR 322 |
| U-WP5 Universe page, U-WP7 Engine actions, U-WP10 Also appears in, U-WP12 companion sessions | Built | PR 296, PR 289, PR 294, PR 286 |
| U-WP6 Connections with sources | Built | PR 318 |
| U-WP8 helper app screens (Android, Apple, TV web, Roku) | Built; Android parser tests PR 325 | PR 317 |
| U-WP9 lore plugin privacy, part 1 | Built | PR 323 |
| U-WP11 character properties, adaptation links, one spoiler rule everywhere, Spoilers preference | Built | U-WP11 PR (branch `claude/universe-wp11-spoilers-and-links`) |
| U-WP3b Series lock in Matching plus 4 U-WP3 review should-fixes | Done: merged in PR #365 (2026-10-21), awaiting Local checks; remediation open (thread H) | session "Universe collision check implementation" |
| U-WP9 part 2, U-WP13, U-WP14, U-WP15 | Not started | threads B to F |

Everything built is "Built, awaiting your Local checks": the checks are rows in [`engineering/validation/batch.md`](../validation/batch.md) (sittings 2 and 3 plus the U-WP4, U-WP9 part 1 and U-WP11 rows).

## 2. Order

| Step | Thread | Depends on | Runs beside | Needs from Shy |
|---|---|---|---|---|
| 1 | A: U-WP3b Series lock (**Done**, PR #365) | U-WP4 on main | B, C | nothing (decision already made 2026-10-20) |
| 1 | B: U-WP13 phone companion screen (Dashboard) | U-WP10, U-WP11, U-WP12 on main | A, C | nothing |
| 1 | C: U-WP14 subtitle text index | U-WP11 on main | A, B | nothing |
| 2 | D: U-WP9 part 2, link to the plugin card | Developer tools wave 3 (PR 336) on main | any | nothing |
| 3 | E: U-WP4 priority through Library-set weights | Developer tools WP20 on main | any | nothing |
| 4 | F: U-WP15 AI feasibility spike | U-WP14, Developer tools WP15, WP27, WP28 on main | any | reviews the report (go / no-go per feature) |
| 1 | H: reconcile the U-WP3b Series block with Details overlay D20 | A merged | any | the D20 decision (recommended: narrow the block) |
| 5 | G: close the spec | A to H merged, batch rows Validated | alone | the local validation sitting |

At most three build threads at once (cloud-first rule). A, B and C touch different areas: A the editor's Matching section and Series identity, B a new phone page and the companion Engine actions, C subtitle parsing and storage. If two of them need a data store upgrade step, the second to merge takes the next number.

Owned elsewhere, not in this handoff: the Details overlay session moves Universe "Edit" from the Editing drawer onto the page banner (its WP3); Developer tools waves 3 to 8 (session "Developer tools spec wave").

## 3. Briefs (for the coordinator to pass to cloud sessions)

### Thread A: U-WP3b, a Series chosen in Matching stays chosen

**Done** (PR #365, 2026-10-21). The visual check of the Series block and the real-media before/after comparison are Awaiting rows in `engineering/validation/batch.md`. Left as is: the sibling `membership-suggestions` action is not limited to Series the person can see (main has since retired that action). Merged as built at Shy's request (2026-10-21), with the Details overlay D20 conflict left for thread H.

```
Build U-WP3b of engineering/specs/universe.md. Read CLAUDE.md, the spec (U-WP3, section 4.3 and section 7) and .claude/rules/cloud-first.md first.
Shy decided 2026-10-20: a Series picked in the editor's Matching section is saved as a locked choice that the Engine always respects on later scans and re-identification.
Also fix the four should-fixes from the U-WP3 re-review: (1) key the Series ambiguity Needs Review item so it doesn't duplicate; (2) resolve a stale ambiguity item automatically once the Series becomes clear; (3) use one source for the tie-break series_qid; (4) add a test with a real stored book plus audiobook manifest.
If session "Universe collision check implementation" already has a branch (claude/universe-wp3b-series-lock), continue from it.
The tie-break change can change ingestion output: add a record-and-replay comparison on C:\Temp\real_import to engineering/validation/batch.md as an Awaiting row (expected: only Series placement for ambiguous items changes). Add a visual-check row for the Matching Series block.
Finish with the CI-strength build, the tests, the reviewer, then merge. Report what merged.
```

### Thread B: U-WP13, the phone companion screen (Dashboard first)
```
Build U-WP13 of engineering/specs/universe.md, Dashboard first (Q13 = a). Read CLAUDE.md, src/MediaEngine.Web/CLAUDE.md, the spec sections 4A.2 to 4A.4 and U-WP13, and .claude/rules/cloud-first.md.
Builds on: U-WP12 companion Engine actions (/api/v1/companion/devices, /session, /session/follow, /session/manual), U-WP10 Also appears in, U-WP11's spoiler rule and the per-profile Spoilers preference (GET/PUT /profiles/{id}/settings/spoilers).
Done when the 4A.3 table works at phone width for a film, an episode, a book chapter and an audiobook chapter, including manual mode and the spoiler switch (the switch reads and writes the same Spoilers preference; no second setting).
Characters stay off item pages (decision D6). Native app companion screens are not part of this thread: list them as a follow-up for Shy to plan.
Visual checks on a small generated library at phone, tablet and desktop widths; add device rows (iPhone, Android) to engineering/validation/batch.md.
Finish with the CI-strength build, the tests, the reviewer, then merge. Report what merged.
```

### Thread C: U-WP14, the subtitle text index
```
Build U-WP14 of engineering/specs/universe.md. Read CLAUDE.md, the spec (section 4A, U-WP14, section 6A point 6) and .claude/rules/cloud-first.md.
Parse subtitle files into time-stamped lines, extract embedded subtitles, store chapter times, and match names and aliases to Universe graph entities with plain matching (no AI). Read the Library's stored, normalised subtitle tracks, never one specific source.
Done when a test film's subtitle file yields "Frodo" and "Mordor" tags at the right time windows.
New storage goes through a numbered data store upgrade step (.claude/rules/data-store.md). Any new tool or library needs a licence check (AGPLv3-compatible).
This adds work during ingestion: add a record-and-replay comparison row to engineering/validation/batch.md (expected: new subtitle index rows only; no change to matching, Series or file placement).
Finish with the CI-strength build, the tests, the reviewer, then merge. Report what merged.
```

### Thread D: U-WP9 part 2, "Add Fandom details" opens the plugin card
The same item is listed in [the Developer tools waves 4 to 8 handoff](2026-10-20-developer-tools-waves-4-to-8.md), section 2, item 6. Build it once: whichever coordinator reaches it first checks `main` and the other handoff, then marks both done.
```
Build U-WP9 part 2 of engineering/specs/universe.md, after Developer tools wave 3 (PR 336: declared web destinations, rate-limited plugin web client, /settings/plugins?plugin={id}) is on main.
(1) The Universe page's "Add Fandom details" line (UniverseConnections.razor) links to /settings/plugins?plugin=tuvima.fandom-lore.
(2) Tighten FandomHostRule so only *.fandom.com subdomains pass (drop bare fandom.com), with a test; the plugin manifest keeps its two declared destinations (agreed with the Developer tools session).
(3) If wave 3 gave the lore plugin the rate-limited client, check the lore service uses it.
Update the U-WP9 part 1 row in engineering/validation/batch.md if the check wording changes. Finish with the CI-strength build, tests, reviewer, merge.
```

### Thread E: U-WP4's priority piece
```
After Developer tools WP20 (Library-set provider weights) is on main: make U-WP4's Universe precedence (retail wins display, Wikidata wins identity and order, lore only supplements) read its priorities from the WP20 weights instead of fixed values. See the U-WP4 Status line in engineering/specs/universe.md for exactly what was left. This changes ingestion: add a record-and-replay comparison row to engineering/validation/batch.md. Finish with the CI-strength build, tests, reviewer, merge.
```

### Thread F: U-WP15, AI feasibility spike (research only)
```
Run U-WP15 of engineering/specs/universe.md once U-WP14 and Developer tools WP15, WP27 and WP28 are on main. Research only, nothing shipped. Recaps would be an Official plugin asking for abilities (section 6A point 2); speaker identification needs a new ability first (point 3).
The spike needs three real titles and local hardware, so it is a local item: write the plan and the harness in the cloud, and add the run to engineering/validation/batch.md for a local sitting. Deliver a written report with samples, time per title, hardware needed, and a go / no-go per feature for Shy to review.
```

### Thread H: reconcile the U-WP3b Series block with Details overlay D20 (remediation)

**Done** (2026-10-21). Shy chose to keep the free search and "Remove from its Series" as rare overrides (recorded as D20a in the Details overlay spec and in the Universes spec section 7), with new wording: "Series override", a re-match-first line, "Pick a different Series", "Save override" and "Go back to the Library's choice". Nothing was narrowed; the brief below is kept for history.

**Why.** Two of Shy's decisions from 2026-10-20 disagree.
- **Details overlay D20:** "Series moves are dropped. A book, comic or movie is not moved into a Series by hand… Match chooses an entry of a list we already know, it does not invent membership." The only exception is Add anyway (D13).
- **Universes U-WP3b:** "When I pick a Series in Matching, save it as a locked choice the Engine always respects."

The Series block merged in PR #365 goes further than D20 allows: it offers a free search over any Series of the same media type, and a "Not in a Series" choice. Shy chose to merge as built and fix this later.

**Decision needed from Shy first.** The recommendation is to narrow the block so it fits both decisions:
- offer only the Series our known data already lists the title in (the manifest memberships, with their "In the main run" or "Also in this world" scope), plus the current Series;
- a pick is saved as a lock, as built;
- keep "Let the Library decide";
- remove the free search and "Not in a Series".

The other option is to keep the block as built and record it in the Details overlay spec as an exception to D20.

```
Remediate U-WP3b of engineering/specs/universe.md against Details overlay spec D20 (engineering/specs/details-overlay-and-edit-mode.md). Read CLAUDE.md, both specs, this thread and .claude/rules/cloud-first.md first. Shy's decision on this thread decides the shape; do not start before it.
If Shy chose "narrow":
- `EditorSeriesBlock.razor` (Components/MediaEditor/Sections) shows the current Series, the listed Series and "Let the Library decide"; remove the search box and "Not in a Series".
- `GET /metadata/{entityId}/series-placement` (`MetadataEndpoints.SeriesPlacement.cs`, `SeriesPlacementRepository.cs`) stops returning search candidates; drop the `q` parameter and the candidates list from `SeriesPlacementContracts.cs` (wire change: refresh fixtures).
- `PUT` accepts only a Series that lists the work, or its current Series (400 otherwise), and no null target.
- The data store lock (`works.series_placement_locked`, step 0025) and the rule stay as they are. A lock to "no Series" can then only come from an existing row; leave that path in the rule.
- Keep the Needs Review "Choose Series" path.
- Update the U-WP3b entry and Status in the Universes spec, the D20 line in the Details overlay spec ("a Series pick in Matching is limited to known lists and stays chosen"), `docs/reference/api-endpoints.md`, and the U-WP3b row in engineering/validation/batch.md.
If Shy chose "keep": record the exception in the Details overlay spec next to D20 and in the Universes spec section 7, and change nothing else.
Finish with the CI-strength build, tests, reviewer, merge.
```

### Thread G: close the spec
When A to H are merged and the local sitting marks the Universe rows Validated: set the spec's Status to Built, move it to `engineering/specs/archive/`, update `engineering/README.md`, mark this handoff Done, and give Shy a Plain English close-out.

## 4. What Shy owes

- **The local validation sitting** for the Universe rows in `engineering/validation/batch.md` (sittings 2 and 3, U-WP4, U-WP9 part 1, U-WP11, and later rows from threads A to F).
- **A data store reset on the desktop app before that sitting.** U-WP3, U-WP4 and U-WP11 changed stored data (U-WP11 renames "opponent" character links to "participant in"); pre-release resets are the agreed path, so no upgrade of old data was written.
- **Device checks:** companion on iPhone and Android (after thread B); the Universe page and Connections on TV; the helper app Universe screens on Android phone, Android TV, iPhone/iPad, TV web (Tizen, webOS) and Roku (sideload).
- **Known gap to decide on:** the iPhone/iPad test project isn't wired into the app's project file, so the new Swift tests from U-WP8 have never run. A small follow-up can wire it.
- **Later planning:** native app companion screens (after thread B proves the Dashboard version).
- **Decision for thread H:** narrow the Matching Series block to fit Details overlay D20 (recommended), or keep it as built as a recorded exception.
- **Open since 2026-10-19: character portraits.** The Q7 answer said to delete them, but cast credits still show character pictures through `/library/portraits/{id}`, so U-WP1 kept the action. Recommended: keep it, and record that as the Q7 answer for portraits.
