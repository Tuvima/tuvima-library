# Documentation overhaul implementation and acceptance

October 8, 2026. Implementation is complete on `codex/docs-platform-overhaul`; [Astra approved pre-merge signoff](final-review.md) with no unresolved blocking findings. This report supersedes the plan-only status and initial observations in the frozen inventory README, claim ledger, and reviewed plan. Those snapshots remain byte-preserved for auditability. Nothing has been merged or deployed.

## What readers receive

The documentation keeps its `/tuvima_library/` address and gains a branded Astro Starlight site with Guides, Develop, and Reference paths. It starts dark, supports a persistent light choice on desktop and phones, and searches through local Pagefind assets. The landing page directs people to installation, daily use, server management, or contribution. Its text, logo, and navigation work without product screenshots.

Daily guidance now explains Home, For Me, media lanes, View, Collections, search, playback, editing, review, and settings. Installation instructions retain all seven Docker persistence mounts, disclose the registry access uncertainty with a local-build fallback, and distinguish source code from a published Windows installer: no installer release currently exists. Metadata scoring, model sizes, Places network access, contributor rules, and access behavior were reconciled against the implementation.

The application itself is unchanged. Root contribution and security policies have mechanically checked site copies. The owner explicitly approved the security policy and enabling private reporting; GitHub confirms reporting is enabled. See [security verification](security-reporting-verification.json). GitHub Discussions is disabled, so current support links use Issues.

## Migration accounting

- 99 canonical published pages; 50 static redirects with the correct deployment base, canonical URLs, and preserved incoming fragments.
- 186 historical route decisions: 83 preserved, 50 redirected, 53 retired internal records. Three known retained heading bookmarks resolve; one historical interop-report fragment is intentionally retired with no published inbound link. This is not a claim that every historical engineering URL still publishes.
- 470 source records relocated to `engineering/`. One planned move remains at `docs/ui/ui-consistency-audit.md`, with an engineering pointer, because an existing test reads that original path. It stays outside the site.
- 368 authorized files deleted: 358 screenshots and 10 QA logs, removing about 47.22 MB from the working tree. This does not erase Git history. Other design assets and necessary non-image records were preserved.
- Both machine-consumed protected files retain their original SHA-256 hashes and paths. The Wikidata metadata adapter runs only on the staged copy.
- All 12 frozen inventory files remain unchanged. See [migration verification](migration-verification.json), [route decisions](route-decisions.json), and [integration exception](integration-exceptions.json).

## Verification

| Check | Result and evidence |
| --- | --- |
| Clean dependency install and docs gate | Node 24.14.1; `pwsh -File scripts/docs/build-docs.ps1 -InstallDependencies` passed. Five regression groups; Astro check 0 errors, 0 warnings, 0 hints; production output validates 99 pages and 50 redirects. Latest layout rebuild also passes. Logs in `.tmp/docs-overhaul/final-build.log` and `site-build-mobile-theme.log`. |
| Migration closure | Verifier passes with no source/publication/output orphans. Expected 149 HTML outputs, all 30 README documentation links, protected hashes, cleanup manifest, and route decisions checked. |
| Context generation | Regenerated ignored `.codex/context` twice from a non-root working directory. Consecutive documentation indexes match; MDX landing, descriptions, statuses, and protected-map adapter are present. [Evidence](context-verification.json). |
| Readability | MIT `textlens@1.0.11`, installed ad hoc outside website dependencies. 25 user/admin tutorial/install/guide pages average Flesch–Kincaid 8.61; pooled grade 8.61; target mean ≤9 passes. Includes technical-details prose, excludes code/tables/headings/navigation. [Per-page evidence](readability-verification.json). |
| Responsive presentation | Seven required pages × four sizes (1920×1080, 1440×900, 768×1024, 390×844) × two themes = 56 captures. Every page has one H1 and no document-level horizontal overflow. Visual inspection found no clipping or overlapping regions. Tables/code retain intentional internal scrolling. [Measurements](responsive-verification.json); uncommitted captures in `.tmp/docs-review/`. |
| Interactions | Browser verified Ctrl+K search, Docker results, Escape restoring the Search trigger, desktop topic changes, mobile menu, theme persistence after reload, an old provider heading redirect, and styled nested-404 recovery links. Native dialog prevents underlying page interaction; browser chrome remains reachable as normal. |
| Accessibility | Official Apache-2.0 Lighthouse 13.5.0: landing and Dashboard tour both 100/100 (threshold 95). Reports in `.tmp/docs-overhaul/lighthouse/`; [summary](lighthouse-verification.json). Manual keyboard checks supplement the score; this is not a full screen-reader certification. The final shared-theme-layout runs also score 100/100, with initial navigation requesting only the preview origin ([network evidence](lighthouse-network-verification.json)). |
| Licensing and dependencies | npm audit: zero vulnerabilities. Exact versions locked; complete package/font notices emitted at `licenses/THIRD-PARTY.txt`, including actual embedded font copyrights. No hosted font, search service, telemetry, or screenshot-zoom package. [npm audit](npm-audit.json). |
| Application gate | Restore and solution build pass (0 warnings/errors). Full test run: 5,124 passed, 34 skipped, one documentation-path failure. Restoring the audit source made that focused test pass 1/1: 5,125 unique passing cases overall. This describes the full run plus focused rerun, not a fresh full green run. Logs in `.tmp/docs-overhaul/dotnet-*.log`. |
| Plugin example | Both complete C# blocks in Build a Plugin compile verbatim against current plugin contracts for .NET 10, 0 warnings/errors. The processor sample is explicitly an adaptation sketch with placeholder types, not a complete compilable program. Evidence in `.tmp/docs-overhaul/plugin-smoke/`; compiled QA outputs cleaned. |
| .NET dependency audit | All 30 projects report no vulnerable packages, including transitives. |
| Application scope | No changes to `src/`, `tests/`, runtime configuration, container deployment, installer, or application/release workflows relative to current HEAD `66fe9a8c`. That commit contains unrelated owner changes made during this task; those were preserved. |

The default sandbox prevented some local sockets, native output replacement, and MSBuild pipes. Those checks were rerun through approved escalation; successful results above refer to the completed runs, not failed sandbox attempts.

## Limits and intentional deviations

- `dotnet format --verify-no-changes --no-restore` reports pre-existing formatting findings across 1,148 unchanged application/test files. No runtime formatting changes were introduced. Details in `.tmp/docs-overhaul/dotnet-ci-verification-report.md`.
- Remote link checks are advisory on PR/main/manual builds and blocking on the scheduled audit. Final audit: 72 verified, 16 source URLs that exist in this working tree but await publication on `main`, 51 GitHub rate-limited/unknown, one SubDL 403 not verified, and one TMDB settings destination that requires sign-in. None of the unknown results is counted as verified. See [remote evidence](external-link-verification.json).
- Docker CLI is unavailable here; container lifecycle, the local source-build fallback, and NAS setups were source-checked, not installed. The anonymous GHCR token endpoint returned 401, so public pull availability is not claimed; see [registry evidence](container-image-verification.json). Windows install/update/uninstall was not tested because no release exists. The docs make these limits visible.
- The README is approximately 146 lines rather than the attachment's 200–260 target. Removing screenshot galleries and keeping a focused introduction was more useful than padding its length. The full founder story and vision remain on their own page.
- The README's picture sources and links were checked locally; the unmerged README has not been visually rendered on GitHub. Production Pages, actual GitHub README rendering, and post-deployment smoke checks remain publication-stage verification after an authorized merge.
- Existing `.agent/` mirrors were retired by the owner's intervening change. Current root guidance and generated `.codex` context are updated; retired mirrors were not recreated.
- The owner's new `src/MediaEngine.Web/CLAUDE.md` stays unchanged under the no-`src/` scope. Its historical references at lines 7 and 145 still name `docs/reports/player-update-2026-10-03.md` and `docs/reports/css-ownership-2026-10-06.md`; the preserved destinations are now `engineering/reports/player-update-2026-10-03.md` and `engineering/reports/css-ownership-2026-10-06.md`. This is a recorded guidance-link exception, not a published-site link failure. The maintained capture example under `scripts/visual-qa/home-media-cards/README.md` now writes to ignored `.tmp/`.
- The final review is pre-merge signoff. No PR, merge, release, or live Pages deployment is claimed here. The existing main-only deployment gate remains intact.

## Plain-English completion summary

People now have a clearer route from installing Tuvima to using their library or building an extension. The instructions reflect today's product, old screenshots are gone, and useful existing reader links still work. Important historical work remains in the repository without cluttering the published site. No application behavior changed.
