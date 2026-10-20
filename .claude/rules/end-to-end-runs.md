# Running handoffs and multi-wave work end to end

When Shy passes a session a handoff file (`engineering/handoffs/*.md`), or approves work that has more than one wave or thread (a Locked spec's waves, a plan with several parts), that session runs **all of it** to completion. Shy never has to paste the next prompt. Passing the handoff or approving the work is Shy's request to start every part of it and the yes to merge each part (CLAUDE.md Agent workflow step 9).

## The receiving session is the coordinator

1. **Make a run list.** Read the handoff or spec and list every thread, wave and packet in order, with what each depends on (the handoff's order table, the spec's "depends on" lines and waves). Write it to `.tmp/runs/<name>.md` with a status per item, so it survives compaction, and keep it current.
2. **Check for collisions first.** List running sessions (`list_sessions`) and unmerged branches. Work another live session already owns stays with it: message that session, agree who does what, and note it in the run list. Never build the same packet twice.
3. **Start what is ready.** Do one item here; start every other ready item as its own linked session (`start_session`, initiation `user_asked`, context `fresh`, its own worktree). Each brief is self-contained: the handoff's prompt for that thread (or the wave's packets), the facts it needs, and "finish with the merge, then report as Complete or Waiting on you (CLAUDE.md step 10); stop and report if blocked".
4. **Parallel limits.** At most three items working at once (each keeps its own limit of two helper agents), and only when they touch different files and areas. Waves of one spec go one after another. Only one ingestion baseline run uses the machine at a time, so items that need one take turns.
5. **Chain.** When a linked session reports, check the result yourself (`git fetch`, the proposed change merged, the spec status), update the run list, and start the next ready item straight away. Don't wait for Shy between items.
6. **Stop for Shy only when the work needs a decision:** a choice the handoff or spec doesn't make, new scope, a security or access issue found along the way, a failed licence check, ingestion differences that need approval, or an item marked as needing planning or something Shy owes (logs, device checks, sample media). Ask one question with a recommendation, and keep every unblocked item moving meanwhile. Items with no spec are planned, not built; list them at the end.
7. **Stalls.** If a linked session fails twice on the same problem, take it over here (model-routing two-strike rule). If the app holds hand-off notes because many automatic hand-offs came in a row, say in one line what is left; any message from Shy resumes the chain.
8. **Finish once.** When every item is merged or explicitly left for Shy, give one Plain English close-out for the whole handoff: what merged (with links), what is left and why, what Shy still owes. Mark the handoff Done in `engineering/README.md` and clean up the worktrees and branches. Close with the explicit status from CLAUDE.md step 10: "Complete: nothing left to do." only if nothing at all is left, otherwise "Waiting on you:" with the open items. Each linked session's report uses the same two states; one that reports Complete with an open follow-up is Waiting on you.

## Writing a handoff

- Put one line at the top: "Run this handoff end to end per `.claude/rules/end-to-end-runs.md`." The per-thread prompts below it are briefs for the coordinator to pass to sessions, not prompts for Shy to paste.
- For each thread, say what it depends on, what it can run beside, and anything it needs from Shy.
- A multi-wave spec is one thread that chains its waves, not one thread per wave for Shy to start.
