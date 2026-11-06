# Tracking bugs in GitHub Issues

Rule (Shy, 2026-11-06): every bug Shy tracks is one GitHub issue in `Tuvima/tuvima-library`, on the board https://github.com/orgs/Tuvima/projects/8 (new issues are added automatically). Bug numbers no longer go into markdown files. Old numbers map to issues in `engineering/validation/bug-issue-map.md`; `local-check-2026-11-05.md` is read-only history.

## Opening a bug
- Use the "Tracked bug (Tuvima team)" form, or the same fields via a thread. Plain English title and body, US spelling, product words.
- Labels: one `area:*`, one `owner:*` (the thread that builds the fix), one `type:ui` or `type:logic`, and `state:planned`. Add `needs:shy` when it waits on a decision.
- Screenshots of the problem go in the issue, never into the code history.

## Fixing a bug
- The PR body lists `Fixes #n` (or `Part of #n` if only partly fixed). Set `state:in-build` when the build starts. Merging closes the issue.
- A bug is not done at merge. After merge, post the closure note and set `state:built-awaiting-check`.

## Closure note (post on the issue; plain English, no code names)
```
Delivered in PR #__ (main commit ____)
What changed: <2-4 sentences>
What you will notice: <one line>
Proof (UI bugs): Mock | Live table at 1024 / 1280 / 1440 / phone
Local check: Verified on <date> | Awaiting your check (batch.md row __)
Not fixed / follow-up: <issue links or "none">
```
- `type:ui` issues cannot be closed without the Mock | Live proof at all four widths.
- Only the batched visual review (or Shy) sets `state:verified` and closes a UI issue. A failed check reopens it with a comment saying what is still off.

## Where proof images live
- Branch `bug-evidence` (never merged into main), path `evidence/<issue number>/<name>-<width>.png`, linked in the closure note as a raw GitHub URL. Keep them after the issue closes so closure notes keep working; prune only if the branch grows large.
- The repo is public: screenshot only test media or sample covers, never anything private.
- Cloud tools cannot upload binary images; the visual review session on SHY-DESKTOP pushes them with git.

## batch.md
- Rows that verify a bug fix carry the issue number (`#n`). Rows that are not bugs stay as they are. Never track the same bug in both places.
