# 0011 — Personal activity is its own lasting record, and who sees whose is decided by one scope check

**Date:** 2026-10-19 (Shy; spec `engineering/specs/archive/settings-revamp.md` D15, D22, D25, D32, D34) · **Status:** Accepted

## What
What each person watches, listens to and reads, and what they add to My list, is written to a dedicated personal activity record (`personal_activity`). Each entry keeps its own snapshot of the item as it was — title, episode or chapter, year and a small cover copy (`personal_activity_artwork`) — so the history stays readable after the item is deleted from the library. It is kept forever: nothing prunes it automatically (the earlier 365-day clean-up of playback history was removed), and only a server administrator with an unlocked PIN can delete it, after a preview and typing DELETE, and every deletion is logged.

Who may see whose activity and taste is decided in the Engine by one scope check (`IPersonalScopeAuthorizer`): a member sees their own; a household administrator their own household; a server administrator anyone, including Everyone. Paired apps get the person's own only. Looking at other people is written to the never-pruned authorization audit log (at most once per viewer and scope every 30 minutes); exporting other people's activity is server-administrator only and always logged. The same Engine-side rule shapes Devices (members their own, household administrators their household's ordinary sign-ins, server administrators everyone's once unlocked).

## Why
Shy, 2026-10-19: history is a safety control as well as data — if a child watched something they should not have, the administrator must be able to see it and nobody should be able to wipe it (**Reliability**, **Privacy** in the sense of the household's own oversight). Tying history to media rows meant deleting a file erased its history. One scope check in the Engine instead of per-page rules keeps every screen consistent and stops a page from showing more than the person may see (**Maintenance**, **Privacy**).

## Trade-offs accepted
- The record grows without limit. It is small per entry, but a very large household over many years will notice it in the data store size.
- Snapshots go stale on purpose: a later title correction does not rewrite history.
- Music listening is grouped into sessions when it is written (30 minutes of silence, or another activity, ends a session); changing that rule later only affects new listening.

## Revisit if
Shy asks for an optional retention limit, a household outgrows a single data store, or a legal requirement to erase a person's history on request applies.
