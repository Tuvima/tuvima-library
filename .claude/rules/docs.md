---
paths:
  - "docs/**"
  - "website/**"
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
| `docs/` | users, administrators, developers | Yes — Astro Starlight site built from `website/`, served by GitHub Pages | front matter `status: current \| early-access \| target-state` |
| `engineering/` | Shy, planning chats, Claude Code | No (public on GitHub, not on the site) | header block `Status: Draft \| Locked \| In progress \| Built` (plus `Current` / `Historical` in the index) |

## Public docs site (`docs/` + `website/`)

- Docs follow Diátaxis; author sources stay under `docs/`, with `docs/index.mdx` as the landing page. Every published page needs `title`, `description`, `audience`, `category`, `product_area` and explicit `status`, validated at build time (`website/scripts/content-contract.mjs`).
- Add pages to `website/publication.json`; moves need redirects or an explicit retirement. Files not listed there are not published.
- The site address, base path and repo URL are defined once in `website/scripts/content-contract.mjs` (`origin`, `base`, `repoUrl`). Never hard-code them elsewhere in site code. `website/scripts/check-project-urls.mjs` (part of `npm run check`) fails the build when README, docs, engineering docs, CLAUDE/AGENTS files, or code, config and deployment files (`src/`, `config/`, `docker/`, `.github/`, Compose, Unraid template, installer) link to a different site address or repo name. In-app links read the address from `ProjectLinks` (Dashboard) rather than repeating it.
- Build with `pwsh -File scripts/docs/build-docs.ps1` (`-InstallDependencies` the first time; Node 24). Preview with `scripts/docs/serve-docs.ps1`. The toolchain is Astro Starlight, pinned in `website/package.json`.
- `CONTRIBUTING.md` → `docs/develop/contributing.md` and `SECURITY.md` → `docs/product/security.md` are generated mirrors; edit the root file, then run `node scripts/docs/sync-community-docs.mjs`.
- `docs/reference/wikidata-property-map.md` is byte-frozen. `docs/ui/ui-consistency-audit.md` must stay at that path (a test reads it).
- Keep current product screenshots absent until replacements are commissioned; review captures go in ignored `.tmp/`. See `docs/develop/writing-docs.md`.

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
| Feature / UI surface | relevant `docs/explanation/*` page; `docs/product/presentation-rules.md` for surface behaviour |
| Config field | `docs/reference/configuration.md` |
| Engine action (endpoint) | `docs/reference/api-endpoints.md` |
| Table/column | `docs/reference/database-schema.md` |
| File format / processor | `docs/reference/media-types.md` (+ `docs/guides/writing-a-processor.md`) |
| Provider | `docs/reference/configuration.md`, `docs/reference/providers.md`, `docs/guides/configuring-providers.md` |
| Terminology | `docs/reference/glossary.md` |
| Architecture | `docs/architecture/*.md`, `docs/architecture/architecture-summary.md`, `engineering/architecture/overview.md` |
| Major decision | new ADR in `engineering/architecture/decisions/` |
| Install/config/usage | `README.md`; repo map/startup → `AGENTS.md` |
| New tool/library | `.claude/rules/dependencies.md` |
