# Spec: Needs Review redesign

> **Status:** Locked (Shy, 2026-11-12: "build it here") · **Version:** 1 · **Last updated:** 2026-11-12 · **Related docs:** [Product overview](../product/overview.md) · [Universal editor v3 (D9, D13: one Needs Review list and its notifier)](universal-editor-v3.md) · [Universal editor v5](archive/universal-editor-v5-header-navigation-and-supporting-files.md) · [Library activity](library-activity.md) · [Mockups](../design/mockups/needs-review-redesign.html)

## Plain-English walkthrough

**What it is today.** Needs Review is a tab under Settings, Library. It is one dense table with six columns (picture, item, why, best guess, last tried, Fix), a three-way switch (All / Not matched / Not sure), a library filter, a search box and two text links that act on everything shown. The reasons are written in engineering words ("canonical identity is missing", "retail matches need comparison"). Every row looks the same whether it needs one click or real work, so a long list gives no sense of where to start.

**What it becomes.** The same page, in the same place, rebuilt around one question: *what do you have to do?*

1. The list is split into four plain groups, quickest first: **Ready to confirm** (Library has one likely answer, you say yes), **Choose a match** (Library found two or three answers and will not guess), **No match found**, and **File problems** (a file that cannot be read or saved to).
2. Each row shows the cover, the name, where the file sits, and **one plain sentence** saying what is wrong.
3. Where Library has an answer, the answer sits in the row with its cover and a **Use this** button. Where it has several, the choices sit in the row.
4. **Fix** still opens the editor window over the page, on Match. Nothing about the editor changes here.
5. Files from the same show, album or book series share **one row** ("12 episodes"), so one show with a naming problem is one thing to fix, not twelve.
6. Tick boxes and one action bar replace the "for all" links.
7. The words "Not matched", "Not sure" and "unmatched" disappear. The only name is **Needs Review**.
8. A row you finish stays as one quiet "Matched to ..." line until you leave. No pop-up messages.
9. A phone can do everything a computer can on this page.

**What stays the same.** Where the page lives, who can see it (server administrators), the editor and Match, the rules for what the Engine sends to review, and the single count (owned by the Activity plan).

**Representative journeys.**
- *After a big import:* the tab says 40. Thirty are under Ready to confirm; tick the heading, press Use suggestions, 10 are left.
- *A film with a common name:* Dune.mkv shows two posters, 2021 and 1984. One click on the right one.
- *A whole season named badly:* one row, "The Continental, 8 episodes". Fix opens the show; matching the show sorts the episodes.
- *A sample clip:* "..." then Keep as it is. It leaves the list and is counted in the "hidden" line at the bottom.

**Acceptance (what Shy can observe).**
- A1. The page shows the four groups in the order above; an empty group is not drawn. (WP1, WP3)
- A2. No screen in Library shows "Not matched", "Not sure" or "unmatched" for these items. (WP3, WP6)
- A3. Every reason reads as a plain sentence with no engineering words. (WP2)
- A4. Use this resolves a row in one click and the count drops by the number of items resolved. (WP3)
- A5. A row with two or three choices lets you pick one without opening the editor. (WP4)
- A6. Files of one show, album or book series appear as one expandable row. (WP1, WP3)
- A7. Ticking rows shows the action bar; its actions touch only ticked rows. (WP5)
- A8. Kept items can be shown again and put back. (WP1, WP3)
- A9. The built page matches the mockups at 1024, 1280, 1440 and phone width. (WP7)

## 1. Goal

Make Needs Review quick to clear: an administrator sees at a glance what needs a yes, what needs a choice and what needs real work, in plain words, and can settle most items without leaving the list.

## 2. Scope

**In scope**
- The Needs Review page: layout, grouping, wording, row actions, ticking and the action bar, kept items, all states, phone layout.
- Small Engine additions the page needs: the group for each item, family grouping, more than one candidate where the Engine already found them, listing kept items.
- Removing "Not matched / Not sure / unmatched" wording wherever it is shown for these items.

**Out of scope**
- The Match view inside the editor (Editor and Match redesign).
- Where the count is shown and how it is worked out for the Activity card, the banner and the Settings menu (#866, #867; Activity and Libraries plans). This spec only uses that one number.
- What the Engine decides to send to review (matching quality, #881; audiobooks without their own Wikidata entry, PR 904).
- Shared controls, menus, dialogs and fonts (shared look thread). This page uses them as they are.
- Any new correction screen. Editing stays in the editor.

## 3. Decisions made

- **D1. Group by what the person must do.** Four groups in this order: Ready to confirm, Choose a match, No match found, File problems. Reason: the list answers "where do I start" and puts the one-click items first (Performance for the person: fewer clicks per item).
- **D2. One name.** "Needs Review" only. The two kinds stop being shown. Reason: Shy, local check 11 Nov ("need review" only).
- **D3. Plain reasons.** Each of the Engine's reasons gets one short sentence in everyday words, kept in a wording file, not in code. Reason: Maintenance; Shy's rule that word lists live in config.
- **D4. Fewer controls.** Library filter (only with more than one library) and search. No kind switch. Bulk work is done with tick boxes and one action bar.
- **D5. Use this stays one click**, and shows why Library thinks so (a short reason or "82% sure").
- **D6. Files of one family share a row** (a show's episodes, an album's songs, a series' books or issues), expandable. The count still counts items.
- **D7. Kept items are hidden, not lost.** A line at the bottom shows how many are hidden and brings them back.
- **D8. Feedback is in the row.** A finished row becomes one line; a failed action leaves the row and shows the reason under it. No toasts (standing rule).
- **D9. Fix is unchanged.** It opens the editor on Match over this page. This page never becomes a correction screen (standing rule).
- **D10. Wikidata-only items leave the list.** An item matched to a provider that only lacks its Wikidata link is not shown; Library keeps retrying quietly. (Shy, 2026-11-11, Q2 = A.)
- **D11. Choices in the row** for two or three candidates. (Shy, 2026-11-11, Q3 = A.)
- **D12. A phone can do everything a computer can on this page** (Shy, 2026-11-11 and 2026-11-12): Use this, choices, Move to TV, Try again, Keep as it is, Fix, Delete and ticking several items. Shy's wider direction: most people use only a phone, so the phone should equal the computer in every way it can, except first-time setup. This overrides the earlier "Needs Review is computer-only" decision (2026-10-13). Fix on a phone opens the editor, whose phone shape belongs to the Editor and Match plan; until that is built, Fix does not appear on phones and nothing else waits for it.
- **D13. The file's own picture shows on every row.** A cover inside the file or beside it in its folder is shown, including for files Library could not match; the empty outline appears only when there is no picture at all. (Raised by Shy, 2026-11-11.)
- **D14. Q1 and Q4 agreed.** Four groups (D1) and one row per show, album or series (D6). (Shy, 2026-11-11.)

## 4. UI behaviour

Mockups: screens 1 to 5.

| State | What shows |
|---|---|
| Normal | Summary sentence, filters, the groups that have items, hidden-items line when relevant. |
| Empty | Tick icon, "Nothing needs review", one sentence. No filters. Count hidden everywhere. |
| Loading | Placeholder rows in the shape of the list. |
| Error | "Needs Review could not be loaded." with Try again. |
| Partly ready (import running) | One line: "Library is still working on N files, so more items may appear here." with a link to Activity. |
| Filter or search finds nothing | "No items match ..." and how many still need review. |
| Row finished | One line with a green tick: "04.mkv matched to ...". Stays until the page is left. |
| Row action failed | Row unchanged; red sentence under it with the reason. |
| Not an administrator | The existing "Needs Review is for server administrators" message. |

**Sizes.** 1280 and 1440: suggestion beside the item. Below about 1150 (covers 1024 and tablets): suggestion under the item. Phone: cards; the "..." menu is a bottom sheet holding Fix, Try again, Keep as it is, Show in Activity, Select and Delete; Select turns on tick boxes and the action bar; a card with no suggestion shows Fix as its main button. TV: not shown (Settings is not on TV).

**Row menu ("...").** Try again, Keep as it is, Show in Activity, Delete. Items that cannot apply are left out.

**Action bar (rows ticked).** Use suggestions, Try again, Keep as they are, Edit N items, Delete, close. Use suggestions acts only on ticked rows that have exactly one suggestion and says how many it will change before it does.

**The four groups and the Engine reasons they hold** (wording examples in the mockups):
- Ready to confirm: any item with exactly one suggestion.
- Choose a match: several close matches; a book claimed by two Series; a film that may be a TV show.
- No match found: nothing close enough; not enough information in the file; came from a merge unnamed.
- File problems: file cannot be read; details could not be saved into the file; two files wanted the same name; a batch edit that did not apply.

## 5. Data / model changes

- The Engine's review list gives each item its group and, for families, the family it belongs to with its item count.
- The list can return up to three candidates where the Engine already has them (if D11 is agreed). To be confirmed: today only one best guess and the film-or-TV suggestion are stored.
- The list can return kept items on request, and a kept item can be put back.
- The two "kind" totals stop being used by the Dashboard. No stored data changes; no migration.
- Wire types change, so Contracts tests and fixtures are updated in the same packet.

## 6. Work packets

| # | What it does | Files likely touched | Depends on | Size | Done when |
|---|---|---|---|---|---|
| WP1 | Engine list: the file's own picture for every row (D13), group per item, family grouping with counts, kept items on request and "put back". | `src/MediaEngine.Api/Services/ReadServices/NeedsReviewListReadService.cs`, `ReviewQueueReadService.cs`, `Endpoints/ReviewEndpoints.cs`, `src/MediaEngine.Contracts/Review/*`, Contracts fixtures | none | Standard | A test list with 3 episodes of one show, 1 ambiguous film and 1 unreadable file returns one family row of 3, the right group for each, total 5; a kept item appears only when asked for and returns after "put back". |
| WP2 | Plain wording for every reason, in one wording file with a test that no reason is missing. | `src/MediaEngine.Web/Services/Editing/ReviewIssuePresentationBuilder.cs`, `ReviewIssueClassifier.cs`, shared strings; config location to be confirmed | none | Mechanical | Every reason the Engine can raise has a sentence; a guardrail test fails if a new reason has none. |
| WP3 | The new page: summary, filters, groups, rows, family rows, finished and failed row states, hidden-items line, all states in section 4. | `src/MediaEngine.Web/Components/Settings/NeedsReviewPageContent.razor` (split into parts), its CSS, `MovieAsTvSuggestionCard` retired into the choice layout | WP1, WP2 | Complex | Component tests cover each state in the table; no "Not matched / Not sure" text is rendered. |
| WP4 | Choices in the row: Engine keeps up to three candidates for ambiguous matches and two-Series books; the page shows them and applies the pick. Only if D11 is agreed. | Engine identity pipeline where ambiguous reviews are raised (to be confirmed), `ReviewEndpoints.cs`, Contracts, the page | WP1, WP3 | Complex | An ambiguous film test item shows two choices; picking one matches it and the item leaves the list. |
| WP5 | Tick boxes and the action bar; removes the "for all" links. | The page, `ListEditLauncher` for Edit N items | WP3 | Standard | Actions touch only ticked rows; Use suggestions states the number first. |
| WP6 | Phone layout and removing the old words elsewhere (library "N files not identified" links, Activity links, docs). Fix and Delete on a phone follow the app's one device rule, so they appear when the Editor and Match plan opens editing on phones. | `SettingsNav.cs` (phone availability), library listing links, `docs/guides/resolving-reviews.md`, public docs page | WP3 | Standard | At phone width the page shows cards with Use this, the bottom-sheet menu and Select; a text search of the Dashboard finds no "Not matched"/"Not sure" labels for review items. |
| WP7 | Mock comparison page: mock beside the built page at 1024, 1280, 1440 and phone, on a small generated library. | test library seed, comparison notes | WP3 to WP6 | Mechanical | The side-by-side page is ready for the batched visual review. |

Waves: WP1 and WP2 together; then WP3; then WP4 and WP5 together; then WP6; then WP7.

## 7. Local checks

- L1. On real media after a fresh import, open Needs Review: groups appear in order, every reason reads plainly, covers show where Library has them.
- L2. Use this on three different kinds (an episode, a film, a song): each row turns into the "matched to" line and the tab, Activity pill and account menu numbers all drop together.
- L3. A show with several unmatched episodes is one row; Fix on it opens the show in the editor.
- L4. Keep as it is hides an item; "Show them" brings it back; putting it back returns it to its group.
- L5. Tick five rows, Try again: only those five are retried.
- L6. Mock beside live at 1024, 1280, 1440 and phone width.
- L7. On a real phone: the list reads well; Use this, the choices, Try again, Keep as it is and Select work. Fix and Delete appear once phone editing is built.

## 8. Risks and open questions

**Risks.** (1) Use suggestions on many rows can accept a wrong match in bulk; it only acts on ticked rows and states the number first, and a wrong match can still be fixed from the item's page. (2) Family grouping needs the Engine to know which show or album an unmatched file belongs to; where it does not, the file stays a row of its own. (3) Choices in the row (WP4) is the largest piece and touches matching; it can ship after the rest without holding it up. (4) This page and the Editor and Match redesign both touch Fix; this spec leaves Fix alone to avoid double work.

**Open questions for Shy.** None.

## Plain English summary

Needs Review stays where it is but stops being a wall of rows. It sorts what is waiting into four plain groups, starting with the ones that only need a yes. Each row says in one everyday sentence what is wrong and, where Library has an answer or a few answers, shows them with covers so you can pick in one click. Episodes of the same show collapse into one row. The words "Not matched" and "Not sure" go away, kept items can be brought back, and the editor is still where real fixing happens.
