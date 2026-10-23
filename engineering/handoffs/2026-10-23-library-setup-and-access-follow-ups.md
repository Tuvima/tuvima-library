# Handoff: follow-ups from library setup and Simpler access (2026-10-23)

Run this handoff end to end per `.claude/rules/end-to-end-runs.md`.

These came out of the library setup spec (`engineering/specs/library-setup-groups.md`, built through v32) and the Simpler access spec (`engineering/specs/simpler-access.md`, built through v3), with Users, profiles and households merged in #451. None of them blocks the Local checks in `engineering/validation/batch.md`.

Each thread below is a brief for a session. Threads marked **needs Shy** start with a question, not with code. The rest can start straight away. Threads touch different areas, so up to three can run side by side (limits in `.claude/rules/end-to-end-runs.md`).

## Order

| # | Thread | Depends on | Can run beside | Needs Shy |
|---|---|---|---|---|
| 1 | Unrecognised ratings for limited profiles | none | 3, 5, 7, 8 | Yes: map international ratings or treat unknown as adult-only |
| 2 | Use each kind's naming pattern when re-filing | none | 3, 5, 6 | Yes: which kinds differ from their media type's pattern |
| 3 | Add folder kind cards, icon guide, safety-pause wording | none | 1, 2, 4 | Yes: the pause button wording |
| 4 | Change-folder-kind edge cases | none | 3, 5, 6 | Yes: Movies to TV grouping |
| 5 | YouTube retry saves the cover | none | 1, 2, 4 | No |
| 6 | Title counts per named library | none | 2, 4, 7 | No |
| 7 | Flaky tests under load | none | any | No |
| 8 | Docker Music and Audiobooks naming templates | none | any | No |
| 9 | "Waiting to be filed" wording | none | 1, 5, 6 | Maybe, if the spec doesn't settle it |
| 10 | Settings drawer cancel handler | none | any | No |
| 11 | Delete the stray `wp7b1-kinds-ingestion` branch | none | any | No (Shy may do it by hand) |

## 1. Unrecognised ratings for limited profiles

**The problem.** Content limits treat a rating the ladder doesn't know as unrated. Both gaps were found during the Simpler access security review and predate it.
- (a) With "allow unrated" on, "R18+", "BBFC 18", "R21", "M18" and "III" show to a limited profile, such as a G Kids profile.
- (b) A combined rating such as "PG|R18+" ignores the unknown part and ranks PG.

**Where to look:**
- `src/MediaEngine.Domain/Aggregates/ProfileContentLimits.cs`: `Allows`, `RankOfRating` (around lines 67-78), and `IsUnrated`, which is already strict (blank, NR, Not Rated, Unrated).
- `src/MediaEngine.Api/Security/ContentLimitPolicy.cs`: `ContentLimitFilter.AllowsItem`.
- `ContentRatingSql`, for the SQL paths.

**The goal.** An unknown rating is never treated as unrated. A combined rating takes its highest known rank, and any unknown part means strictest.

**Ask Shy first:** map common international ratings onto the ladder, or treat every unknown rating as adult-only? Recommend mapping the common ones, with strictest for the rest.

**Done when:** tests cover R18+, BBFC 18 and "PG|R18+" for a G profile with allow unrated on and off, across lists, counts and the stream gate (`CatalogueResourceAuthorization`). A security review happens before merge.

## 2. Use each kind's naming pattern when re-filing

**The problem.** `config/library_kinds.json` gives each kind a `naming_template`, but the organiser never reads it. Filing takes the template from the media type, through these:
- `src/MediaEngine.Ingestion/Models/IngestionOptions.cs` `ResolveTemplate`;
- `src/MediaEngine.Ingestion/AutoOrganizeService.Refile.cs` `PlanRefileAsync`;
- `src/MediaEngine.Ingestion/Services/LibraryOrganizationPath.cs`.

So "Change what this folder is" between kinds that share a media type (Music to Other audio) moves nothing, against library setup D60.

**The goal.** Choose the template in this order: the user's custom template, then the kind template, then the media-type template.

**Ask Shy first:** which kinds should differ from their media type's pattern? Changing a shipped pattern re-files existing libraries.

**Done when:** a Music folder changed to Other audio re-files into the Other audio pattern, and kinds without their own template keep today's paths. A data-safety review happens before merge.

## 3. Add folder kind cards, icon guide, safety-pause wording

Three differences between the built Dashboard and the library setup spec:
1. **Kind cards (D39).** The Add folder wizard's kind cards (`src/MediaEngine.Web/Components/Settings/Libraries/AddFolderWizard.razor`, shared `LibraryKindCards`) don't show the Metadata provider icons. Group pages and the dialogs do. The likely cause is a `ShowMetadata` flag the wizard doesn't set.
2. **Icon legend (D41).** The legend under the Libraries pages shows only Read-only, Managed, Healthy and Warning. D41 also lists metadata provider, files as-is and Unmatched. There is no "Icon guide" link.
3. **Pause wording (D35).** The paused first-check line offers "Keep going" and "Remove the folder" (`LibraryFolderTable.razor`). The spec says "Keep going and leave them Unmatched". **Ask Shy** which to keep.

Keep the scoped CSS within its ceiling (`tests/MediaEngine.Web.Tests/Fixtures/style-ownership-baseline.json`). Do a visual check at desktop and phone width (`src/MediaEngine.Web/CLAUDE.md`), and update `docs/guides/library-settings.md`.

## 4. Change-folder-kind edge cases

"Change what this folder is" (library setup WP7b-3: D60, D69 and D70) has two edge cases.

1. **Leftover cached pictures after a crash.**
   - In `src/MediaEngine.Api/Services/Libraries/FolderKindChangeRunner.cs` (the drop step, around line 415), the provider pictures' cached image files are listed only after the drop commits.
   - A crash between the two leaves them on disk for good. No data is lost; it only wastes disk.
   - Fix: save the list of files to delete before the drop is committed (in the ledger detail or the `folder_kind_changes` plan), so a resumed run removes them through `MoveOwnedFileCleaner`. Add a crash-between-steps test.
2. **Movies to TV.**
   - Files are re-placed with `HierarchyResolver`. A file with no show or season hints in its canonical values lands under a standalone work, not a show.
   - **Ask Shy** whether that's acceptable, or whether the change should parse show, season and episode from file names (the existing filename parser) first.

A data-safety review happens before merge.

## 5. YouTube retry saves the cover

**The problem.** A YouTube file retried in place that now finds its video ID gets its details but no cover. The re-read path never runs the cover save that normal ingestion does.

**Where to look:**
- `src/MediaEngine.Api/Services/Unmatched/UnmatchedAssetService.cs` (`RetryAsync`);
- the YouTube re-read path;
- wherever normal ingestion saves the provider cover.

**Fix:** reuse one shared cover-save method; don't copy it.

**Done when:** a test shows a retried YouTube file that finds its ID ends with its cover saved.

## 6. Title counts per named library

**The problem.** Kinds with several named libraries (D51, for example two Videos libraries) show the kind's total on every library, instead of each library's own count. This affects the Settings › Libraries group and type pages (`LibraryGroupPage.razor`, `LibraryFoldersPanel.razor`, and the Engine read behind them).

**Fix:** count per library id, keeping the access rules (hidden libraries, Vault), so counts never reveal what a viewer can't see.

**Done when:**
- tests show two Videos libraries with their own counts;
- a library hidden from a profile isn't counted for that profile;
- a visual check at desktop width passes.

## 7. Flaky tests under load

These failed during full runs on 2026-10-23 and passed when re-run alone:
- `tests/MediaEngine.Api.Tests/DocumentOcrTests.cs`: `SlowFile_IsStoppedAtTheTimeLimit_AndThePagesReadSoFarAreKeptAsPartial` (a timing assertion around line 320);
- `tests/MediaEngine.Api.Tests/RealTesseractTests.cs`: `ReadsRealTextFromAnImage` (`TimeoutException`, `OcrService.cs:100`);
- `tests/MediaEngine.Web.Tests/VaultExperienceTests.cs:525`: `LockingNsfwMode_WipesTheSearchBoxAndTheRecentSearches`;
- `tests/MediaEngine.Web.Tests/UiShellRenderTests.cs`: `MainLayoutInvalidatesChangedBookmarkLeasesWhileSuccessfulReplayDismissesAndRestoresFocus` (around line 416) and `PhoneMenu_LinksToMyLibraryAndTheDockTabIsSelectedThereAndOnItsGrids` (bUnit `UnknownEventHandlerIdException`);
- `MatchStepTests.ChoosingATakenEntry` (seen earlier).

**How to fix them:**
- For the OCR tests, inject a clock or time provider, or use a generous time limit that can be configured.
- For the bUnit tests, use `WaitForAssertion` and find elements again after each re-render.

Never skip, disable or quarantine a test.

**Done when:** each test project runs about 20 times in a loop with zero failures.

## 8. Docker Music and Audiobooks naming templates

**The problem.** `docker/config/core.json` has older Audiobooks and Music organisation templates than `config/core.json`, so Docker installs file these types differently from other installs. PR #412 aligned Comics and Videos only. Library setup D59 says Docker should behave like every other install.

**What to do:**
- Align the two templates.
- Extend the Storage test that compares the Docker defaults, so every media type's template must match.
- Run the container smoke test (`tests/container/smoke.sh`, through the docker-publish workflow).

## 9. "Waiting to be filed" wording

Library setup WP15 made filing moves wait for the batch (D62), and left the status wording unchanged.

**What to do:**
1. Read `engineering/specs/batched-writeback.md` D12 and library setup's amendment note on D10.
2. Grep for "Waiting to be filed" and for the write-intent status text.
3. Change each string in one place, and update `docs/guides/library-settings.md` and the bUnit tests.

If the spec doesn't settle the wording, ask Shy, offering two options.

## 10. Settings drawer cancel handler

**Background.** On 2026-10-21 a cloud build compiled `AppDrawer`'s `@oncancel:preventDefault` as a literal attribute (SDK 10.0.402), and Settings pages crashed in the browser. Visual checks on 2026-10-23 opened Settings without a crash, so it may already be fixed.

**How to check.**
- Look at `src/MediaEngine.Web/Components/Shared/AppDrawer*`.
- On a small generated library, never real media (`.claude/rules/one-engine-at-a-time.md`), open Settings › Users and Sign-in › Invite user.
- Press Escape, then click the backdrop, and check the console.

**If it still fails:** fix it with a Razor-correct binding, plus a bUnit test that no literal `@oncancel` attribute is rendered. **Otherwise:** close the thread with a one-line note.

## 11. Delete the stray `wp7b1-kinds-ingestion` branch

**Background.** This branch was pushed to the remote by mistake on 2026-10-22. Its work was merged in #426, and the cloud proxy refused to delete it. On 2026-10-23 a check found no commits on it that aren't on main.

**Steps.**
1. Confirm again with `git log origin/main..origin/wp7b1-kinds-ingestion`.
2. Delete it on GitHub (Branches page, bin icon) or with `git push origin --delete wp7b1-kinds-ingestion`.

Never delete it if unmerged work appears; ask Shy instead.
