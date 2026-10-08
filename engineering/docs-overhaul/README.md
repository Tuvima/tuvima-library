# Documentation overhaul inventory and factual baseline

This is the durable WP2 checkpoint for the authorized documentation overhaul. It remains in the public repository for maintainers and is excluded from the documentation website. The user authorized implementation across all phases after the reviewed plan; [authorization.json](authorization.json) binds that authorization to the exact manifest hashes. The earlier product-owner amendment separately authorized screenshot removal. The inventory worker does not move or delete source files.

Readers gain clearer current instructions without losing unfinished work or necessary measurements. Existing screenshots disappear from the finished documentation; reusable brand, font, icon and design files remain. Engineering records move outside the published site, while product rules, architecture summaries and protected machine-consumed sources retain their authority. Runtime, media, databases, release engineering and `tools/reports/` cleanup remain outside scope.

## Exact scope and actions

The frozen working-byte snapshot is based on commit `66fe9a8cca5c5b1411cab17c038dfe7cf1ed1798`. It contains 846 individual paths, with byte counts, SHA-256 hashes, Git author dates where available, purpose, current-authority assessment, completion evidence, references, dependencies, destination and action. The checkout included pre-existing/user edits, so a file hash is a working-byte baseline rather than a claim that every byte equals the base commit. The four explicitly preserved new user authorities were included even when not yet tracked during initial inspection.

| Action | Files | Bytes | Reason |
| --- | ---: | ---: | --- |
| KEEP-MOVE | 471 | 6,665,571 | Preserve uncertain/unfinished work, retained verification, all non-image evidence and reusable design dependencies under `engineering/`. |
| DELETE | 368 | 47,216,631 | Remove existing documentation/QA screenshots and generated QA logs; preserve results and repair references. |
| PUBLISH | 6 | 81,627 | Preserve the two protected sources, two public design references and the newly added presentation rules and architecture summary. |
| KEEP-IN-PLACE | 1 | 20,247 | Preserve the newly authored Dashboard `CLAUDE.md` at its source location. |
| FOLD | 0 | 0 | No semantic deletion proposed without proof that its current facts are fully preserved elsewhere. |

Removal reduces the checkout by about 45.03 MiB (47.22 MB); moves reclaim no space. This does not rewrite or shrink Git history. The exact list is [cleanup-actions.md](cleanup-actions.md); the complete evidence-bearing record is [file-manifest.json](file-manifest.json). The 15 screenshot assets and one hash-identical `docs/design-system/assets/screenshots/epub-reader.png` copy are recorded explicitly. QA captures account for the other screenshot removals. No legitimate non-screenshot design raster was found in this candidate set.

Conservative retention is deliberate: completion alone does not prove a plan/report obsolete, missing inbound links do not prove unfinished work disposable, and JSON does not need both a report reference and a script consumer to remain useful. The entire Access execution record and its non-image dependencies remain together. Current player/CSS/migration reports remain with their measurements and recorded limitations. Logs and images can be removed only after retained reports keep their factual results and no longer point to removed captures as available evidence.

## Routes, fragments and references

[route-manifest.json](route-manifest.json) captures 186 old Markdown/MDX publication candidates, including every actual page omitted from navigation, old directory and `index.html` addresses, all source ATX heading fragments, known incoming fragment links, audience, destination and final-status decisions. [mkdocs-baseline.yml](mkdocs-baseline.yml) preserves navigation, `not_in_nav`, base URL and old Markdown extension configuration before removal.

Current public pages retain their paths unless they have a named successor: `settings-architecture` becomes `architecture/settings`, `artwork-architecture` becomes `architecture/artwork`, and the old `providers` stub leads to `reference/providers`. Engineering records are intentional public-route retirements rather than redirects to unrelated generic pages. Their repository files survive under the exact move list. This classification does not promise that every working-note bookmark still opens a published page.

Anchor capture follows Python-Markdown/MkDocs Unicode slug rules, duplicate `_n` suffixes and explicit heading IDs. Exceptional markup, setext headings and plugin-generated anchors still require the integration owner's old-render comparison; source-derived fragments are not claimed as rendered-browser proof. The special old 404 is `/tuvima_library/404.html`; one stale `not_in_nav` entry, `design-system/ui_kits/dashboard/README.md`, had no source at baseline and is recorded rather than invented as a previously published page.

[reference-ledger.json](reference-ledger.json) records the 3,847 scanned textual sources, resolved relative links/assets, literal repository paths, source line numbers, screenshot references, unresolved candidates, protected hashes and separately inspected local generated context. Computed/dynamic paths and plain prose filename mentions require semantic review. All retained non-image dependencies are preserved even where a parser cannot prove an edge. The retired `.agent` mirror is excluded from authority, and `.codex/context` is regenerated rather than hand-edited or committed. Ignored QA captures/logs and `tools/reports/` are not cleanup targets.

## Factual writing decisions

[claim-ledger.json](claim-ledger.json) records evidence and limitations for installation, persistence, local AI, Places, Access, For Me, View sharing, security reporting and protected files. Its most consequential corrections are:

- The repository release API returned an empty array through the integration owner's GitHub connector. No published installer download should be advertised. Installer lifecycle is unverified, and the release workflow omits the local script's bundled-AI property and FFmpeg preparation.
- Maintained Docker Compose preserves seven mounted roots and publishes Dashboard port 5016. Model downloads, native libraries and setup entry have distinct prerequisites and timings.
- Current Places code uses bundled MapLibre, attempts an external OpenFreeMap style, and falls back to local Natural Earth country data. The old claims that no map/MapLibre/third-party tile integration exists are stale; unconditional offline/privacy claims would be inaccurate.
- Current Access uses accounts, profile grants, applications and authentication controls. Its top acceptance record supersedes historical partial checkpoints, while explicitly preserving the limit that the normal development data store was not cut over.
- For Me has separate personal shelves, and its current Favorites view filters loved songs. Avoid silently promising a cross-media Favorites browser.
- The private-vulnerability-reporting API returned `enabled: false` through the integration owner's GitHub connector. Enabling it, supported-version scope and response commitments remain owner decisions. No contact address, response deadline or usable private report form is invented here.

## Reproduction and checks

Run `node engineering/docs-overhaul/build-inventory.mjs` only before cleanup in a fresh baseline checkout. The script refuses to overwrite the frozen manifest unless `--replace-baseline` is deliberately supplied. Do not regenerate after migration and call the resulting different paths the old inventory. `finalize-inventory.mjs` records authorization and readable exact action lists from the captured baseline; it does not re-read mutable documentation sources. Run `node engineering/docs-overhaul/verify-inventory.mjs` to check structural consistency and authorization hashes without touching source.

## Plain-English completion summary

Every file proposed for removal or relocation now has a durable record. Old screenshots and logs can be removed while unfinished work, reusable design material and important measurements stay available. The writing baseline also identifies outdated installation, map and access claims so the new documentation can describe what readers can actually use.
