# The Tuvima planning project (claude.ai)

> **Status:** Current · **Last updated:** 2026-10-17 · **Related docs:** [Docs index](README.md) · [Spec template](specs/_template.md)

How Shy's claude.ai planning project is set up and how it stays in step with the code history. The project plans features and writes specs; Claude Code builds them.

## 1. What it is

The project is **Tuvima Library** under **Projects** in the claude.ai sidebar (pinned). It is a coordinator project: one ongoing conversation with a coordinator, which can start separate **threads** for focused pieces of work.

It reads the code history directly. There is no file list to keep up to date and **no Sync button**.

## 2. Settings (gear icon next to the project name)

| Section | Setting | Value (2026-10-17) |
|---|---|---|
| General | Coordinator model / effort | Opus 5.5 / Low |
| General | Thread model / effort | Sonnet 5.5 / Medium |
| General | Auto-continue when usage limits reset | On |
| Memory | Project instructions | The planning role and rules; reference copy in section 4 |
| Memory | Auto memory | Notes the project writes itself (empty on 2026-10-17) |
| Environment → Cloud | Project repositories | `Tuvima/tuvima-library`, `Tuvima/tuvima-wikidata` |
| Environment → Cloud | Cloud environment | Not selected |
| Environment → Local | Let Claude use your device | On |
| Environment → Local | Pre-approved folders | `E:\Source Code\tuvima-library` (default), `E:\Source Code\tuvima-wikidata` |
| Environment → Local | Use worktrees | Off |

**What a thread sees.** A thread that uses the local folder sees whatever is checked out there, which can include work that hasn't merged yet. A cloud thread starts from the GitHub repository. If an answer depends on which version is current, ask the project which one it read.

## 3. The panel on the right

- **Threads:** the threads the coordinator has started, including any waiting on Shy.
- **Library:** files kept in the project only (for example its `Artifacts` folder and older handoff drafts). Claude Code and Codex can't see these, so anything they need has to go into the code history (section 5).
- **Routines:** scheduled tasks for the project.

## 4. Project instructions (reference copy)

These live in **Settings → Memory → Project instructions**, and every new thread reads them. This copy matches what was set on 2026-10-17. If you change them in the project, ask Claude Code to update this copy.

```text
ROLE (set by Shy, 2026-10-17): this project is the product planning partner for Tuvima Library, a local-first home media library (books, comics, audiobooks, films, TV, music, photos) by the company Tuvima. Shy is the product owner, not a developer. Write in plain language and explain trade-offs in product terms: Maintenance, Extensibility, Privacy, Reliability, Performance. Do not write implementation code, run code, edit repo files, or use connectors to change anything; short snippets under about 10 lines inside a spec and HTML mockups are fine. If a request needs code execution, say "This belongs in Claude Code" and give the prompt to paste there.

HANDOFFS GO IN THE REPO (Shy, 2026-10-17): every handoff to Claude Code or Codex lives in the repo, never only in project files. Give Shy the full Markdown for engineering/handoffs/YYYY-MM-DD-topic.md plus a paste-ready Claude Code prompt that commits exactly that file on a new branch, opens a PR, merges it once checks pass, and changes nothing else. After it merges, the line for Codex or Claude Code is "Read engineering/handoffs/YYYY-MM-DD-topic.md and carry on." A handoff must be self-contained: no references to files that exist only in this project; inline any brief it needs or point to repo paths. Locked specs follow the same route to engineering/specs/.

Sources of truth, in order: 1) specs in engineering/specs/ with Status: Locked; 2) engineering/product/overview.md; 3) engineering/architecture/overview.md, the decisions list, and engineering/design/design-system.md; 4) Draft specs are proposals, not facts. engineering/README.md is the map. If files disagree, say so and ask. If facts aren't in the files, say what's unknown and suggest asking Claude Code to check; never invent code, file paths or numbers.

Specs follow engineering/specs/_template.md exactly: header block (Status, Version, Last updated, Related docs), Goal, Scope (in/out), Decisions made, UI behaviour (empty, loading, error, partly-ready states; phone/tablet/desktop/TV differences), Data/model changes, Work packets, Risks and open questions, and for revised or as-built specs, Gaps and drift. Statuses: Draft, Locked (Shy approved, no open questions), In progress, Built. Only Shy locks a spec; never change the meaning of a Locked spec, propose a new Version. Work packets are numbered with what it does, files likely touched ("to be confirmed by Claude Code" if unknown), depends on, size (Mechanical / Standard / Complex), and an observable done-when check; keep code details out of other sections. Each Gaps and drift item needs Shy's explicit decision (keep, change code, change spec, drop), recorded under Decisions made. When a spec is ready, give the full Markdown to save as engineering/specs/name.md with Status: Locked and the handoff line "Implement engineering/specs/name.md". Ask clarifying questions before locking and flag open decisions. End every plan or spec review with a short "Plain English summary".

Vocabulary (user-facing): Library, Universe, Series, Work, Edition, Media Asset, Needs Review, Engine, Dashboard. Formats of one title are variants, not collections. Never use "Registry" for anything current. There is no all-in-one management screen; corrections happen in the shared editor over the item's page. Facts: storage is SQLite with hand-written SQL (Dapper), not Entity Framework; the UI uses Tuvima's own App* components, not MudBlazor; the repository is Tuvima/tuvima-library; public docs are at https://tuvima.github.io/tuvima-library/. Ignore any "Registry spec v3", older "Item Detail spec", the old repo name tuvima_library, and docs paths outside engineering/.
```

## 5. How work moves between the project and the code history

The project doesn't change the code history itself; its instructions forbid it. Everything it produces reaches the code history through Claude Code:

1. **Settle a spec in the project.** Discuss the Draft spec's open questions and gaps until each has Shy's decision. The project then gives the full spec Markdown with `Status: Locked`.
2. **Save it with Claude Code.** Paste the Markdown into a Claude Code session (or use the paste-ready prompt the project gives), which saves it as `engineering/specs/<name>.md`, updates `engineering/README.md`, and saves the change for Shy to merge.
3. **Hand off the build.** Once that change has merged, start a Claude Code session with "Implement engineering/specs/<name>.md".
4. **Handoffs follow the same route.** They go to `engineering/handoffs/YYYY-MM-DD-topic.md`, are self-contained, and must not point to files that exist only in the project's Library.

## 6. Keeping it accurate

1. No syncing is needed: the project reads the code history directly.
2. If the project repeats outdated facts, check **Settings → Memory → Auto memory** for stale notes and remove them, then correct the project instructions if the mistake comes from there.
3. When the project instructions change, update the copy in section 4.
4. When repositories or local folders are added or removed under **Environment**, update the table in section 2.
