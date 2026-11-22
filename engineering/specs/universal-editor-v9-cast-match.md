# Spec: Editor v9, correct who a title's cast and crew point to (amends v8, v5)

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-11-22 · **Related docs:** [Product overview](../product/overview.md) · [Editor v8](archive/universal-editor-v8-badge-artwork-collections.md) · [Editor v5](archive/universal-editor-v5-header-navigation-and-supporting-files.md) · [TV artwork, cast and matching](tv-artwork-cast-and-matching.md) · issue #1244 (earlier wrong-person bug: #1161) · mockups `../design/mockups/editor-v9/cast-match-mockups.html` (screens A to F)

## 0. Why the fix is on the title
Shy's note (2026-11-21): the person page is correct. "Chris Evans, the radio presenter" is a real person and is not wrong as a person. What is wrong is that *Captain America's* cast credit pointed to him. So the fix belongs to the **title's credit**, not to the person. A credit is stored per title (a link between the file's title, the person and a role), so changing one credit does not touch the person or any other title. That also avoids the risk of one person record standing for two real people.

## 1. Goal
From a film, show or episode, the person using Library can see who each cast and crew credit is matched to, change a wrong one to the right Wikidata person, and have only that title change.

## 2. Scope
**In scope**
- A way into the fix from the title's own page (a tile's "..." menu) and from the title's editor (a People tab).
- A change step shaped like the Match tab (search, results with picture and description, comparison card, apply), judged against the title.
- Staging, Review changes, and keeping the choice through re-scans and refreshes.
- Remove a credit from this title.
- Desktop, tablet, phone.

**Out of scope**
- Editing the person page's Wikidata link (it stays as it is; see Q122).
- Adding a person who is not credited yet (follow-up, Q124).
- Merging or splitting person records.
- Characters (fictional entities) and Universe appearances.

## 3. Decisions made
Locked by Shy 2026-11-22 ("looks good, agree with all questions"): the recommended answer to every question in section 8 is chosen.
- **D1. The credit is the thing being corrected.** The change repoints one credit (title, role, person) to another Library person. If the right Wikidata person is not in Library yet, a person record is created and enriched like any new person. If they already are, the credit points to them; nothing is merged and nothing is refused.
- **D2. Two doors, one flow.** The tile's "..." menu on Cast and crew gets "Change person" (people who can edit; on every device that can edit; never a deep link). It opens the editor over the page on the People tab with that credit's change step open. "Open person" stays the tile's normal tap. The editor also has a **People** tab listing every credit (Q120: new tab).
- **D3. The People tab** lists each credit: headshot, name as read from the file or provider, role and character, who it is matched to (one line from Wikidata), a chip (Matched, Check, Not matched) and **Change person**. "Check" is shown when Wikidata does not list this title for that person; a hint, not a verdict. Long lists show the first rows and "Show all N".
- **D4. The change step is the Match step.** Search by name (filled in, not searched until Search), or paste a Wikidata link or ID; results with headshot, name, Wikidata's one-line description and years; the current match tagged "Current"; a card with a comparison table (name, whether Wikidata lists this film, role, born) and a verdict line; one button, **Use this person**. A bar at the top says which title is the evidence ("Captain America: The First Avenger (2011), cast as Steve Rogers"). Results Wikidata links to this title rank first. The card states plainly "Only this title changes. The person page and other titles keep their match."
- **D5. Staged, then Save changes.** Same Review changes as other edits. Review names the title, the credit, the old and new person and says other titles are not changed. No toast; after Save the title shows the normal Updating state.
- **D6. The choice sticks.** A corrected credit is locked for this title, so re-scans, refreshes and re-matching never put the wrong person back (the same rule as a hand-made match elsewhere in Library). The old person is remembered as turned down for this credit only.
- **D7. Remove from this title** is staged and reviewed like any change; it removes only this credit and is also kept through refreshes.
- **D8. Where the credit lives.** Credits are shown at the level they are stored: a show's cast on the show, an episode's guest cast on the episode, a film's on the film, an author on the book. The change applies at that level only (Q125).
- **D9. Who.** Anyone allowed to edit and to match metadata, on desktop, tablet and phone, not TV. Others see credits and matches with no buttons and one line saying why.
- **D10. Wording.** "People", "Change person", "Use this person", "Remove from this title", "This title". Never "credit link", "QID" or "reconcile" on screen.
- **D11. Phone.** One-line header, rows stack with a full-width Change person under a flagged row, stacked search, result card as a full-height sheet, Use this person at the bottom (v6 section 4a).

## 4. UI behaviour
Mockups are the reference; the build matches them one to one.

| Situation | What the person sees |
|---|---|
| Normal | Screen A (tiles and the "..." menu) and screen B (People tab rows). |
| Empty (title has no credits) | People tab: "No cast or crew yet. They appear after the title is matched." |
| Loading | The tab's normal skeleton rows. |
| Change step | Screen C. Nothing is searched until Search or Enter. |
| Nothing found | "Nothing found for 'name'. Try the full name, or paste a Wikidata link." |
| Error (Wikidata not answering) | "Wikidata did not answer. Try again in a moment. This credit is unchanged." |
| Staged | Screen D: the row shows the new person with a "Not saved yet" chip and Undo; the page behind does not change. |
| Review | Screen D right. |
| Not matched at all | Row chip "Not matched" and a Find person button into the same step. |
| Partly ready | After Save, the title shows Updating while the new person's headshot and facts arrive. |
| No permission | Credits and matches listed, no buttons, one line why. |

**Phone / tablet / desktop:** same window; phone is a full-screen sheet (screen F); tablet follows desktop and stacks columns below 1024 wide.

## 5. Data / model changes
- The credit already exists as a link between a file's title, a person and a role. The build adds a small record of the person's decision per credit (which person it was, which person it is now, locked, who and when) so refreshes respect it. Whether that is a new table or a flag on the existing link is the build's call; pre-release, so a reset is acceptable and there is no migration.
- A person left with no credits and no other references after a change is kept or tidied by the rule in Q121.
- New Engine connection points (names set in the build): list a title's credits with their matches, search Wikidata for a person judged against a title, and apply or remove a credit through the edit session. Wire shapes live in Contracts with fixtures and tests. The search reuses Tuvima.Wikidata's role-aware person search with a title hint; no change to that package is expected.

## 6. Work packets
### WP1 — Credits read model and the decision record
- **What it does:** Lists a title's credits with each matched person's one-line facts and the "Check" hint; stores a per-credit decision that survives refreshes.
- **Files likely touched:** `src/MediaEngine.Storage/` (person links), `src/MediaEngine.Api/Services/ReadServices/`, `src/MediaEngine.Contracts/`, a new upgrade step if needed (to be confirmed)
- **Depends on:** none · **Size:** Complex
- **Done when:** tests list credits for a film, and a locked credit survives a re-run of person enrichment.

### WP2 — Title-judged person search
- **What it does:** Search by name or pasted link, ranked with the title as evidence, with the comparison for one result (D4).
- **Files likely touched:** `src/MediaEngine.Api/Services/Matching/`, editor endpoints, Contracts, tests
- **Depends on:** WP1 · **Size:** Standard
- **Done when:** a test ranks the actor above the presenter for a film Wikidata lists the actor in.

### WP3 — Apply and remove through the edit session
- **What it does:** A change handler beside the other edit-session handlers: repoint or remove one credit, create-or-reuse the person, enrich a new person, lock the decision (D1, D6, D7).
- **Files likely touched:** `src/MediaEngine.Api/Services/EditSession/`, `PersonRepository`, `PersonEnrichmentWorker`, tests
- **Depends on:** WP1 · **Size:** Complex
- **Done when:** tests show only the one title changes, an existing person is reused, and a refresh keeps the choice.

### WP4 — People tab and change step (desktop)
- **What it does:** Screens B to E; the tab joins the title editor's config (D2, D3).
- **Files likely touched:** `config/editor.json`, `src/MediaEngine.Web/Components/Editor/`, `Services/Editing/`, localization, component tests; the editor guardrail tests and the editor rules text that lists tabs per kind
- **Depends on:** WP2, WP3 · **Size:** Standard
- **Done when:** component tests cover every row of the section 4 table and the guardrails pass.

### WP5 — Door from the title's page
- **What it does:** "Change person" in the Cast and crew tile menu (screen A), gated by the editor device rule and permission; opens the editor on the People tab.
- **Files likely touched:** `src/MediaEngine.Web/Components/Details/` (person credit card menu), launcher, tests
- **Depends on:** WP4 · **Size:** Standard
- **Done when:** a test opens the editor from a tile on desktop and phone; a TV client shows no menu entry.

### WP6 — Phone sheet
- **What it does:** Screen F; 44 px targets.
- **Depends on:** WP4 · **Size:** Standard
- **Done when:** component tests at phone width and the touch-target check pass.

### WP7 — Docs
- **What it does:** Editing guide in `tuvima-docs` ("Correcting the cast of a title"), editor rules text.
- **Depends on:** WP5 · **Size:** Mechanical
- **Done when:** the docs build passes and the page is in the manifest.

Waves: WP1 → (WP2, WP3) → WP4 → (WP5, WP6, WP7).

## 7. Local checks
- **L1.** On a small generated library with two actors sharing one name, open the film whose cast points to the wrong one: Cast and crew tile "..." then Change person. The editor opens on People with the search ready; choose the right person, Save changes. The film shows Updating, then the right headshot. The other film they share a name on is unchanged. Failure: any change before Save, or the old person returning after a refresh.
- **L2.** Same at phone width.
- **L3.** On real media (`Z:\Development\testing\media\real_media`), fix a credit known to be wrong; needs live Wikidata.
- **L4.** No network: the plain sentence shows and the credit is unchanged.

## 8. Risks and answered questions
- **Risk: a title with hundreds of credits.** The People tab pages its rows ("Show all N") and the search is per credit, so it stays quick.
- **Risk: Wikidata's film lists are incomplete.** "Check" and "Fits" are hints; a person missing from Wikidata's cast list is shown as "Could not confirm", never as wrong.
- **Q120 (answered: recommended option). Where does the People list live?**
  Context: editor tabs are Details, Artwork, Match, History; the header rules say tabs are fixed per kind, so a new tab needs your yes. Putting it inside Match would be a third source on the Match switch, which is about the title itself, not its people.
  - A. A new People tab on titles that have credits (recommended)
  - B. A "People" option in the Match tab's switch
- **Q121 (answered: recommended option). A person left with no titles after a change.**
  Context: if the radio presenter is credited nowhere else, his record is now unused. Keeping it is harmless but clutters People lists; tidying it deletes a record.
  - A. Tidy it automatically once nothing references it (recommended)
  - B. Keep it
- **Q122 (answered: recommended option). The person's own page.**
  Context: the person's editor lists a Match tab today but cannot change the link. The person is right, so a Match tab there invites the wrong fix.
  - A. Show the Wikidata match read-only, no change buttons, with a line "To fix a wrong credit, open the title" (recommended)
  - B. Remove the Match tab for people
- **Q123 (answered: recommended option). Which kinds in the first build?**
  Context: movies, shows and episodes show cast and crew; books show authors and narrators; music shows artists. The flow is the same, but each has its own credit rules.
  - A. Movies, shows, episodes now; books and music next (recommended)
  - B. All kinds at once
- **Q124 (answered: recommended option). Adding someone who is missing.**
  Context: "Remove from this title" is in. The reverse (the file's cast lacks someone) is a separate feature.
  - A. Not now (recommended)
  - B. Include "Add person" in this spec
- **Q125 (answered: recommended option). Show cast versus episode cast.**
  Context: a correction on the show's cast should not silently change guest roles on an episode.
  - A. Each level is corrected on its own; the show's People tab says "Guest cast is on each episode" (recommended)
  - B. Offer "also change on episodes where they appear"

## Plain English summary
You were right: the person is fine, the title's cast pointed to the wrong person. So the fix now starts on the title. On a film's Cast and crew, open a person's "..." menu and choose Change person (or Edit, then the new People tab). Library lists who each credit is matched to, flags the ones that look off, and lets you search for the right person with headshots and short descriptions, judged against that film. Pick them and press Save changes after the usual review. Only that title changes; the person page and every other title stay as they were, and Library remembers your choice so a refresh won't undo it. It works on phones. Six questions (Q120 to Q125) need your answers, each with a recommendation; please renumber if they clash with your range.
