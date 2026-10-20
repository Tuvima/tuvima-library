# Local validation batch

Items built and merged in cloud sessions that still need a check on SHY-DESKTOP. Cloud sessions append rows; a local validation session works through them once per batch (`.claude/rules/cloud-first.md`). Status: Awaiting, Validated, Failed (with a fix brief).

| Item | PR | What to check | Status |
|---|---|---|---|
| Details overlay: Match step and moves (WP5) | #340 | On a small test library with two albums: open a song, Edit in the "..." menu, Match and Identity, "Move to another album". Pick the other album; the Engine should pre-select the entry with the same title and say "Same title". Confirm: the song takes that track's number, and in a managed folder the file is renamed and moved to the new album folder. Then try a taken entry (Move here stays off, "Add as another version" works) and Add anyway (no track number, listed last). As-is and protected folders: the file must not move on disk. Same for a TV episode into another season. | Awaiting |
| Details overlay: Batch section (WP6) | this thread's PR | Edit mode on a season page: tick 3 episodes, the bar says "3 episodes selected", Edit 3 opens the section; Language shows "Mixed" if the episodes differ. Add a tag, Apply: "3 updated" and the tag shows on all three. Replace and Remove behave as labelled. On an album page and an audiobook page the ticks sit in the title cell. Leave edit mode: ticks clear. | Awaiting |
