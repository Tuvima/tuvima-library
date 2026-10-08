# Tuvima Library documentation platform

The public documentation is authored in [`../docs/`](../docs/). This directory contains the Astro Starlight platform, not a second copy of the source content. Application development still uses .NET; Node 24 is needed only for this site and the existing CSS checks.

From the repository root:

```powershell
pwsh -File scripts/docs/build-docs.ps1 -InstallDependencies
pwsh -File scripts/docs/serve-docs.ps1 -Address 127.0.0.1:8000
```

Open `http://127.0.0.1:8000/tuvima_library/`. The build wrapper runs the same regression tests, Astro diagnostics, static build, and finished-site validation as CI. Native command failures propagate to its exit code. `npm ci` is optional after the first installation; rerun it after changing the lockfile.

## Source and navigation contract

`publication.json` is the explicit publication and navigation manifest. Each page declares a canonical path relative to `docs/`, a public slug without leading/trailing slashes, and a `group`/`section` location. The root slug is empty. `404` is the only error-page slug. Landing and 404 are exempt from sidebar placement. All other pages belong in Guides, Develop, or Reference. Manifest order determines topic and section order. Add a `label` only to override a page title in the sidebar.

The preparation script validates every published source, creates ignored `.generated/content/`, and records normalized metadata in `.generated/routes.json`. Generated copies contain explicit original-source edit URLs and the original source's latest Git commit date. Untracked pages have no invented update date. Neither engineering content nor an unlisted file is loaded into the public search index.

Every source page requires `title`, a meaningful `description` of at least 20 characters, `audience`, `category`, `product_area`, and an explicit `status`. Allowed values are enforced in both the preparation script and the Starlight collection schema. `reference/wikidata-property-map.md` is the single immutable legacy exception: the staged copy maps `summary` to `description` and supplies its current status. The source bytes remain untouched. `reference/approved-plugins.json` is copied without modification to its historical public path.

Plain Markdown pages stay `.md`. Use `.mdx` with explicit component imports only when a page needs Starlight components. Keep the same slug when changing extensions.

## Links, assets, and redirects

Author links as source-relative Markdown paths, including heading fragments. They continue to work when read on GitHub. The Markdown processor rewrites published page destinations to `/tuvima_library/` routes, existing repository-file destinations to GitHub, and local assets to a same-origin generated asset path. It also handles literal MDX `href`/`src`, HTML links/images, reference-style Markdown links, and root-relative documentation URLs. Root-relative hero action links receive the base during staging. Dynamic MDX expressions must construct the correct base themselves; the finished-site validator checks their rendered results.

Do not put documentation screenshots in the source or site. Existing screenshots are retired by the approved overhaul. Logos, icons, fonts, and useful diagrams remain supported. Local image/asset targets must exist. Original brand SVGs are reused; fonts and their OFL license notices are included locally. Pagefind is Starlight's local, same-origin search. Search requires the site assets to have loaded; this is not a PWA or a guarantee that uncached pages work offline.

Add old-to-new route pairs to `publication.json` under `redirects`, for example `"/library-operations/": "/guides/library-settings/"`. Keys and values omit the deployment base. Destinations must be real published pages. Astro emits static HTML redirects for GitHub Pages. Validation inspects their refresh and canonical destinations as well as the destination's existence; HTTP 200 alone is insufficient.

## Checks and deployment

`npm test` retains negative fixtures for missing metadata, duplicate public routes, missing output pages, invalid anchors, missing assets, and omitted base paths. `npm run build` also validates every emitted HTML link and local stylesheet resource, source edit links, page identity, canonicals, and redirects. External navigation is audited separately with bounded retries. A 429 is recorded as rate-limited/unknown rather than accepted as verified.

CI uploads a generic checked-site artifact on every relevant event and downloads it in the external audit job. Remote checks are advisory on pull requests, main pushes, and manual builds; they block the weekly audit. Deployment depends only on the structural build and runs only for a main push or main manual run. Deployment has its own serialization group and minimal write permissions. GitHub Pages must use **GitHub Actions** as its source.

Astro telemetry is disabled in the Node launcher. Runtime site resources are local; outbound links to sources, editing, and GitHub are deliberate navigation. Dependency versions are exact and locked. The parser override patches a transitive build-time complexity advisory; run the build and audit when updating it. See `LICENSES.md` for shipped asset and tool notices.
