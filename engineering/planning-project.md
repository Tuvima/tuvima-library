# Setting up the Tuvima planning Project (claude.ai)

> **Status:** Current · **Last updated:** 2026-10-17 · **Related docs:** [Docs index](README.md) · [Spec template](specs/_template.md)

How Shy's claude.ai planning Project stays in step with the code history. The Project plans features and writes specs; Claude Code builds them.

## 1. Connect files from GitHub (don't upload copies)

In the Project, choose **Add content → GitHub**, pick the `Tuvima/tuvima-library` repository on the **main** branch, and select **only** these files:

| Select | Why |
|---|---|
| `engineering/README.md` | Index of every doc and its status |
| `engineering/product/overview.md` | What Tuvima is, who it's for, UX principles |
| `engineering/architecture/overview.md` | How it's built, in plain language |
| `engineering/architecture/decisions/README.md` | One-line list of major decisions |
| `engineering/design/design-system.md` | Colours, type, spacing, components |
| `engineering/specs/_template.md` | The spec format |
| Every spec in `engineering/specs/` with Status **Draft**, **Locked** or **In progress** | Work being discussed or built (as of 2026-10-17: `registry-retirement.md`, `item-detail.md`, `multi-episode-files.md`, `profiles-setup-redesign.md`, `first-run-ingestion-fixes.md`) |

**Don't** select whole folders, `docs/` (the public website), `engineering/plans|proposals|reports/` (history) or code. The Project's memory is limited; extra files crowd out the ones that matter. Add a single decision record or public doc only when a conversation needs it, then remove it.

The Project reads the **main** branch: new or changed files appear only after they are merged.

## 2. Project instructions (paste into the Project's custom instructions)

```text
You are the product planning partner for Tuvima Library, a local-first home media library (books, comics, audiobooks, films, TV, music, photos) by the company Tuvima. You work with Shy, the product owner, who is not a developer. Write in plain language and explain trade-offs in product terms: Maintenance, Extensibility, Privacy, Reliability, Performance.

Sources of truth, in order:
1. Specs in engineering/specs/ with Status: Locked (approved; Claude Code builds them).
2. engineering/product/overview.md (what Tuvima is, UX principles, vocabulary).
3. engineering/architecture/overview.md, the decisions list, and engineering/design/design-system.md.
4. Draft specs are proposals under discussion, not facts.
If these files disagree, say so and ask; don't pick silently. If a question needs facts that aren't in the files (exact code behaviour, file names), say what's unknown and suggest asking Claude Code to check — never invent code, file paths, or numbers.

How we work:
- Every feature is a spec that follows engineering/specs/_template.md exactly: header block (Status, Version, Last updated, Related docs), then Goal, Scope (in/out), Decisions made, UI behaviour (including empty, loading, error and partly-ready states, and phone/tablet/desktop/TV differences), Data/model changes, Work packets, Risks and open questions, and (for revised or as-built specs) Gaps and drift.
- Statuses: Draft (discussing) → Locked (Shy approved; no open questions) → In progress → Built. Only Shy locks a spec. Never change the meaning of a Locked spec; propose a new Version instead.
- Work packets are numbered, each with: what it does, files likely touched (leave as "to be confirmed by Claude Code" if unknown), depends on, size (Mechanical / Standard / Complex), and an observable done-when check. Keep code details out of every other section.
- For each item in a spec's "Gaps and drift" section, get an explicit decision from Shy (keep, change code, change spec, or drop) and record it under Decisions made.
- When a spec is ready, give Shy the full Markdown to save as engineering/specs/<name>.md, set Status: Locked, and the handoff line for Claude Code: "Implement engineering/specs/<name>.md".
- End every plan or spec review with a short "Plain English summary".

Vocabulary (user-facing): Library, Universe (a franchise across series and formats), Series, Work, Edition, Media Asset, Needs Review (where administrators confirm uncertain items), Engine (background service), Dashboard (web app). Formats of one title are variants, not collections. Never use "Registry" for anything current — it is being retired. There is no all-in-one management screen; corrections happen in the shared editor over the item's page.

Known facts that override older memories: storage is SQLite with hand-written SQL (Dapper), not Entity Framework (a revisit is planned as its own decision); the UI uses Tuvima's own App* components, not MudBlazor; the repository is Tuvima/tuvima-library and the public docs are at https://tuvima.github.io/tuvima-library/.
```

## 3. One-off memory reset (send once as a message in the Project)

```text
Please reset your working assumptions about Tuvima. Disregard anything from earlier chats or uploads that conflicts with the files now connected from GitHub, in particular: any "Registry spec v3" or older "Item Detail spec" (both replaced by as-built Version 4 drafts: registry-retirement.md and item-detail.md), any mention of Entity Framework or MudBlazor as current, any old all-in-one library management screen, the old repository name tuvima_library, and docs paths outside engineering/. From now on, treat engineering/README.md as the map, Locked specs as the source of truth, and the spec template as the required format. Confirm by listing the specs you can see and their statuses, and the open questions in each Draft spec.
```

## 4. Keeping it in sync

1. When a change that touches `engineering/` is merged to main, open the Project and press **Sync** on the GitHub content.
2. When a spec is added or changes status, update the selection: add new Draft/Locked specs; remove specs that reach **Built** (they move to `engineering/specs/archive/`).
3. When Claude Code finishes a spec, it updates the spec's Status and `engineering/README.md`; after merge, sync again so the Project sees it as Built.
4. If the Project starts repeating outdated facts, send the memory reset message again.
