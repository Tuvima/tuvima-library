---
paths:
  - "docs/**"
  - "engineering/**"
  - "README.md"
  - "CONTRIBUTING.md"
  - "SECURITY.md"
  - "AGENTS.md"
  - "CLAUDE.md"
---

# Documentation rules

Tuvima has two documentation homes. Never mix them.

| Home | Audience | Published? | Status values |
|---|---|---|---|
| `tuvima-docs` repository (https://github.com/Tuvima/tuvima-docs) | users, administrators, developers | Yes: docs.tuvima.com, built and deployed from that repository | front matter `status: current \| early-access \| target-state` |
| `engineering/` | Shy, planning chats, Claude Code | No (public on GitHub, not on the site) | header block `Status: Draft \| Locked \| In progress \| Built` (plus `Current` / `Historical` in the index) |

## Public docs (live in `tuvima-docs`, not in this repository)

- Library's pages are under `content/products/library/` in `tuvima-docs`, published at https://docs.tuvima.com/products/library/. The platform, the writing guide (`CONTRIBUTING.md` there) and the page checks live in that repository; its `CLAUDE.md` holds the rules.
- A feature that needs documentation gets a companion proposed change in `tuvima-docs`, linked from the pull request here (the template has a box for it). Do not recreate a `docs/` site, `website/` or `scripts/docs/` in this repository.
- `docs/reference/` here holds only two data files the app and its tests need: `approved-plugins.json` (the Engine reads it from GitHub) and `plugin-host-v1.openapi.json` (a test checks it against the code). They are not reading material.
- `CONTRIBUTING.md` and `SECURITY.md` (and `CODE_OF_CONDUCT.md`, `THIRD-PARTY-NOTICES.md`) stay at the repository root because GitHub needs them there; `tuvima-docs` fetches them when the site builds. Edit the root file here.
- The address of the docs lives in `ProjectLinks.DocsSite` (Dashboard); do not repeat it in code. The ExifTool metadata name `https://tuvima.github.io/tuvima-library/ns/xmp/1.0/` is an identifier written into users' files, not a page: never change it.
- Guardrail tests that used to read docs pages (pipeline wording, the backup table list, presentation rules, attributions) were removed when the pages moved. Keep those pages current by hand when the matching code changes: for a new or reclassified table update the Backup and restore architecture page; for ExifTool or any new dependency update the Attributions page.
- Keep current product screenshots absent until replacements are commissioned; review captures go in ignored `.tmp/`.

## Internal planning docs (`engineering/`)

- `engineering/README.md` is the index: every doc, one line, with status. Update it whenever you add, move or change the status of a file.
- Specs live in `engineering/specs/`, follow `engineering/specs/_template.md`, and start with the header block (Status, Version, Last updated, Related docs). Built specs move to `engineering/specs/archive/`.
- Specs describe WHAT and WHY in plain language that a planning chat with no code access can follow. Exact file paths and code patterns belong only in work packets and `engineering/architecture/overview.md`.
- Never rewrite the body of a Locked spec. If the code must differ, stop and ask Shy; record decisions in the spec only when Shy has made them.
- Decisions go in `engineering/architecture/decisions/NNNN-short-name.md` (what, why, date, revisit-if).
- Engineering plans/reports from before 2026-10-17 stay in `engineering/plans/`, `proposals/`, `reports/` as history.

## Doc triggers — update in the same change

| Trigger | Update |
|---|---|
| Feature / UI surface | `tuvima-docs`: the relevant `explanation/*` page; `product/presentation-rules.md` for surface behaviour |
| Config field | `tuvima-docs`: `reference/configuration.md` |
| Engine action (endpoint) | `tuvima-docs`: `reference/api-endpoints.md` |
| Table/column | `tuvima-docs`: `reference/database-schema.md` (and `architecture/backup-and-restore.md` for a new table) |
| File format / processor | `tuvima-docs`: `reference/media-types.md` (+ `guides/writing-a-processor.md`) |
| Provider | `tuvima-docs`: `reference/configuration.md`, `reference/providers.md`, `guides/configuring-providers.md` |
| Terminology | `tuvima-docs`: `reference/glossary.md` |
| Architecture | `tuvima-docs`: `architecture/*.md`; `engineering/architecture/overview.md` here |
| Major decision | new ADR in `engineering/architecture/decisions/` |
| Install/config/usage | `README.md`; repo map/startup → `AGENTS.md` |
| New tool/library | `.claude/rules/dependencies.md` |
