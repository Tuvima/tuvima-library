# Model routing and plan-limit budget

Shy is on a Claude Max plan with 5-hour and weekly limits. **Optimise for correctness first, then token efficiency. Speed does not matter.** Usage scales with model price and tokens read, so: the expensive model thinks, cheaper models search, build, and check, and nobody re-reads what someone else already read.

## Who does what

| Work | Model | Effort | How |
|---|---|---|---|
| Questions, plans, wave planning, integrating, final diff review, CLAUDE.md/docs structure, spec writing | **Opus** (main session) | high; xhigh for architecture or cross-cutting design | The session itself |
| A Standard or Complex work packet | **Sonnet** | high (set in the agent file) | `builder` agent |
| A Mechanical work packet: renames, moves, config/JSON, fixtures, front matter, link fixes | **Haiku** | — | `mechanic` agent, or inline if tiny |
| Finding code, mapping call sites, "where/how is X done" | **Haiku** | — | `scout` agent |
| Restore/build/test runs, log and failure triage | **Haiku** | — | `verifier` agent |
| Review after each wave, or of large/high-risk changes | **Opus** | high | `reviewer` agent |
| Escalation | **Fable** | — | **Only after Shy approves**, for security/authorization, data-loss or migration risk, storage-epoch or wire-contract changes, or a problem Opus failed to solve twice |

Helper agents never make architectural or product decisions. If a packet or brief doesn't answer a design question, they stop and report back; Opus decides or asks Shy.

## Budget rules

1. **Do small work inline.** A change touching ≤3 files, or anything Opus already has in context, is done directly. Each helper reloads CLAUDE.md and re-reads code, so it only pays off for substantial or noisy work.
2. **Self-contained briefs.** A builder/mechanic brief is the spec's work packet plus anything it leaves implicit: exact files, symbols, signatures, Contracts/JSON names, tests to add, done-when check, and what must not change. Use `scout` first if facts are missing.
3. **At most 2 helpers at once.** Within a wave, run packets two at a time, each in its own worktree (`isolation: "worktree"`), and only when they touch different files.
4. **Keep noisy output out of Opus.** Build/test logs, large searches, and file dumps go through `verifier`/`scout`, which return short summaries with `file:line` anchors.
5. **Verify proportionally.** Builders run targeted tests per packet; the build-check hook catches compile errors at the end of each turn; run the full restore/build/test once at the end.
6. **Two-strike escalation.** A helper that fails twice on the same problem hands back to Opus with the full error. If Opus also fails twice, ask Shy about Fable.
7. **One feature per session.** Start a fresh session for unrelated work; compact at natural phase boundaries. Write plans and briefs to the scratchpad or `.tmp/` so they survive compaction.
8. **Read narrowly.** Read files and line ranges, not whole large documents (`AGENTS.md`, big Razor files, `schema.sql`). Use Grep before Read.
9. **Visual checks are expensive.** Iterate with text-based page reads and computed styles; screenshot only for final before/after proof (see `src/MediaEngine.Web/CLAUDE.md`).
