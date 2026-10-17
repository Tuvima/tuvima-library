# 0008 — Public docs site in `docs/`; internal planning in `engineering/`

**Date:** 2026-10-08 (docs overhaul) and 2026-10-17 (planning layer) · **Status:** Accepted

## What
- `docs/` is the public documentation website (Astro Starlight in `website/`, served by GitHub Pages at https://tuvima.github.io/tuvima-library/). Pages are listed in `website/publication.json` and their front matter is validated.
- `engineering/` is internal: the index (`engineering/README.md`), product and architecture overviews, decisions, design system, mockups and **specs** (`engineering/specs/`, statuses Draft / Locked / In progress / Built). The claude.ai planning project reads the code history directly and plans specs here (`engineering/planning-project.md`).
- The site address and repo URL live once in `website/scripts/content-contract.mjs`; a check fails the docs build if any doc links to another address.

## Why
- **Maintenance:** internal drafts can't be published by accident, and the public site's checks don't fight planning statuses.
- **Reliability:** after the repo rename from `tuvima_library` to `tuvima-library` broke every docs link, one source of truth plus a failing check prevents a repeat.

## Revisit if
Tuvima gets a custom docs domain (then only the contract changes), or planning moves to a different tool.
