# Handoff: docs hub local checks (L1 to L3)

Run this handoff end to end per `.claude/rules/end-to-end-runs.md`. Start it in a new local session on SHY-DESKTOP (`E:\Source Code\tuvima-library`) when Shy says "do a local check" for the docs hub.

## Background (plain English)

All Tuvima help moved to one site, https://docs.tuvima.com/ (source: the `tuvima-docs` repository). Library's app, README and docs links now point there (PR #521); the old Library docs site is switched off. Spec: `engineering/specs/docs-hub.md` (status: Built, awaiting Local checks). The cloud could not check the three things below because they need the real app on a PC and a real phone.

## What to do

Use a small generated test library (never the real library). Claim the Engine slot first (`.claude/rules/one-engine-at-a-time.md`); only L1 needs the Engine. L2 and L3 need only a browser and Shy's phone.

1. **L1, Dashboard help links.** Start the Engine and Dashboard on the small test library. Open the account menu "Help" item, Settings > Plugins "Read the plugins guide", and a library's File handling "how writing works" link. Each must open docs.tuvima.com on the matching Library page; the File handling one must land on the "When changes reach your files" heading. Any not-found page is a failure. Release the Engine slot afterwards.
2. **L2, phone check.** Ask Shy to open https://docs.tuvima.com/ on a real phone. Expect: the Tuvima logo, a search button and a menu button; the menu drawer lists the areas and then Library's sections; search opens and the "search all" tick widens results; nothing runs off the screen sideways.
3. **L3, light and dark look.** At docs.tuvima.com in both themes check: home (Library card with its logo, Tuvima.Wikidata card with the library icon), the Library landing page (logo instead of title text), a Library Reference page, a Tuvima.Wikidata page, and Legal. Header items line up on one centre line; the Tuvima logo's text is readable in both themes.

## Report

For each of L1 to L3 say Pass or Fail with what was seen. A failure becomes one fix brief for a cloud session (docs problems go to the `tuvima-docs` repository, app link problems to this one). When all pass: mark the three rows in `engineering/validation/batch.md` Validated, move `engineering/specs/docs-hub.md` to `engineering/specs/archive/` with Status `Built`, update `engineering/README.md`, and close out with "Complete: nothing left to do." only if nothing else remains.

## Also remember

After NuGet shows Tuvima.Wikidata 3.10.1 (it does as of 2026-10-28), the package-validation baseline moves to 3.10.1 (done in `tuvima-wikidata` PR #4).

## Plain English summary

Three quick looks to make sure the new help site works the way a person would use it: the app's Help links land on the right pages, the site is easy to use on a phone, and it looks right in light and dark. Nothing else is outstanding for the docs move.
