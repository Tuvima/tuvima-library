# Cloud first, validate locally in one batch

Rule (Shy, 2026-10-20): all work is planned and built in cloud sessions. Local sessions are for validation only, done once per batch of merged work.

## Cloud sessions
- Build every spec, packet and wave, write tests and docs, run restore, the CI-strength build and the filtered tests (`Category!=LiveProvider`), run the reviewer, and merge per CLAUDE.md step 9.
- While building, run only the affected test project; run the full CI-strength build and tests once at the end.
- Never start the Engine against real media. A visual or Engine check, if a spec needs one, uses a small generated library only.
- Known Linux-only red checks (the external-links audit) are not regressions; do not "fix" them inside unrelated work. Tests that can only pass on Windows carry `[Trait("Category", "WindowsOnly")]` and are excluded from the cloud test command.
- Each spec has a "Local checks" section: what Shy or a local session must try on SHY-DESKTOP (real media, `C:\Temp\real_import`, Windows paths, devices, visual proof). A cloud session ends by appending those checks to the batch list, not by asking for validation now.
- Status wording: "Complete" only when the Local checks list is empty; otherwise "Built, awaiting your Local checks" (merged and listed in the batch).
- Log how long setup, restore, build and test took in the first progress update, so cloud-versus-PC speed is known rather than guessed.

## Concurrency
- At most 3 build threads at once (each with at most 2 helper agents), only on different files and areas. Waves of one spec run one after another.

## Batch list
- File: `engineering/validation/batch.md` (one table: item, PR, what to check, status). Cloud sessions append; they do not run it.

## Local test pack (every build thread's final summary)
1. What merged: PR links and the main commit to pull.
2. Exact commands: `git pull`, the CI-strength build, the specific test projects, and the Engine/Dashboard start commands with a small test library (never the real library).
3. Steps to try in plain English: where to click, what should appear, what counts as a failure.
4. Needs real files or devices: what to run on `C:\Temp\real_import`, which devices, and why the cloud could not check it.
5. Status wording per the rule above. The same list is appended to `engineering/validation/batch.md`.

## Local validation (Shy asks for it when the batch is ready)
1. One local session pulls main and runs the CI-strength build and full tests.
2. It claims the Engine slot once (`one-engine-at-a-time.md`), runs the batch list on a small test library, plus real-media items on `C:\Temp\real_import`.
3. It reports pass/fail per item. Failures become one fix brief each, sent back to cloud sessions with the Engine log attached.
4. Items that pass are marked Validated and their specs move to archive.

## Exceptions that start local
- A bug seen on real media whose cause is unknown: find the cause locally (log, repro), then hand the fix to the cloud with a failing test.
