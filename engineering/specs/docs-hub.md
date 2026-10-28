# Spec: One Tuvima documentation hub (docs.tuvima.com), starting with Library

> **Status:** Locked · **Version:** 2 · **Last updated:** 2026-10-28 · **Related docs:** [Product overview](../product/overview.md) · [Documentation rules](../../.claude/rules/docs.md) · [Documentation index](../README.md)

## 1. Goal

Anyone looking for help with a Tuvima product (Library today; ID, Social and others later) finds it in one public place with one look, one search box and one address, `docs.tuvima.com`, the way Microsoft Learn presents every Microsoft product. Library's and Tuvima.Wikidata's existing documentation moves there (no redirects: nobody uses the app yet, so the old address is simply retired), and adding the next product later is a checklist, not a project.

## 2. Scope

**In scope**
- The long-term shape of the hub: how products are laid out, navigated, searched, versioned, and how each product's writing reaches the site.
- Hosting at `docs.tuvima.com`, the choice between GitHub Pages, Cloudflare and self-hosting, and the DNS steps Shy does.
- Moving Library's public documentation (111 pages in `docs/`, the Astro Starlight platform in `website/`) and Tuvima.Wikidata's documentation (its `docs/` folder) into the `tuvima-docs` repository, with CI rebuilt there. No redirects (Shy, 2026-10-28: nobody is using the app yet; redirects would only create cleanup later).
- Re-pointing everything in `tuvima-library` that links to the docs (README, in-app help links, rules, PR template).
- A short "add a product" guide.

**Out of scope**
- `engineering/` (specs, plans, product overview) stays in `tuvima-library`, unpublished.
- Writing ID or Social documentation; those products do not exist yet. The hub only gets ready for them.
- Rewriting Library's page text (it is re-homed as is, apart from links).
- A user-facing site (library.tuvima.com, with Tuvima ID sign-in) for community features: a later follow-up (see section 8), not part of this spec.
- Accounts, comments, analytics or anything that collects reader data.

## 3. Decisions made

Locked by Shy on 2026-10-28 (answers recorded in section 8). Facts found in the code that shape the plan:

- **F1.** Library's docs site is Astro with Starlight, deployed to `https://tuvima.github.io/tuvima-library/`, built from `docs/` plus `website/publication.json` (the list of pages and their menu position). Every page already carries `audience`, `category`, `product_area` and `status` labels, validated at build time. The platform already does search (Pagefind, local), redirects, link checking and a weekly outside-link audit.
- **F2.** `tuvima-docs` currently holds only a one-line README, so nothing there needs preserving.
- **F3.** The Dashboard links into the docs with the fixed address in `ProjectLinks.DocsSite`, including a page and heading anchor (library settings, developer tools). Those links are re-pointed to the new address in WP6. No redirects (nobody uses released builds yet).
- **F4.** The Engine reads the approved-plugins list from `raw.githubusercontent.com/Tuvima/tuvima-library/main/docs/reference/approved-plugins.json` (default setting in `CoreConfiguration.cs` and `config/core.json`, also in three test files). That is data the app needs, not reading material.
- **F5.** CONTRIBUTING.md and SECURITY.md at the repository root are copied into docs pages by a script. GitHub requires those two files to stay at the root.
- **F6.** The "last updated" date on each page comes from the page's Git history, so moved pages will show the migration date. Accepted (Shy, 2026-10-28: dates do not matter right now).
- **F7.** The ExifTool metadata name `https://tuvima.github.io/tuvima-library/ns/xmp/1.0/` is an identifier written into users' files, not a web page. It must not change.

### Decisions

- **D1 (hosting).** Serve the hub from `tuvima-docs` on GitHub Pages with the custom domain `docs.tuvima.com`. The site stays plain static files, so it can move to Cloudflare or anywhere else later by changing one DNS record.
- **D2 (layout).** One site and one address, organised by what kind of thing is documented, not by product only. Top level:
  - `docs.tuvima.com/` is the home: a short welcome, one search box, and cards for each area.
  - `/products/` lists the apps people use: `/products/library/…` now, later `/products/id/…`, `/products/social/…`. Each product keeps today's three groups (Guides, Develop, Reference).
  - `/libraries/` lists the developer libraries (NuGet packages other developers build with): `/libraries/wikidata/…` now (Reconciliation, Entity data, Series manifest, Graph, ASP.NET Core, Configuration, Changelog, migration guides).
  - `/legal/` holds the company-wide pages that belong to no one product: licence (AGPLv3 and what it means), security reporting, code of conduct, third-party notices and attributions, privacy principles. Product-specific privacy detail (what Library connects to) stays in the Library section and is linked from `/legal/`.
  - Later areas slot in the same way (for example `/company/` or `/community/`) without moving existing pages.
  Navigation follows the three-layer rule in section 4 (hub bar, item bar, breadcrumb), so a reader can always get back to the hub, the area or the product landing page in one click. Why this shape: a product, a developer library and a legal page have different readers and different lifespans, and keeping them apart means a policy page never lives under a product that may later be renamed or retired.
- **D3 (search).** One search index, defaulting to the product or library you are reading, with a "search all Tuvima" switch.
- **D4 (content home).** All hand-written pages live in `tuvima-docs`. Pages that are copies of files that must stay in a product repo (CONTRIBUTING, SECURITY) are fetched from that repo when the site builds, and the site rebuilds weekly and on demand. No cross-repo secrets are needed.
- **D5 (versions).** Show the latest only. "Versions" would mean keeping a separate copy of the docs for each release (say Library 1.0 and 2.0) so someone still on the old release reads matching pages. Not needed while everything is Early Access and nobody is on an old release. Every page already carries a status (current, early-access, target-state). Revisit when a product reaches a stable 1.0; Tuvima.Wikidata, which has versioned NuGet releases, will be the first candidate, and its migration guides already cover version changes.
- **D6 (old address).** No redirects. When the move is verified, the Library repository stops publishing its GitHub Pages site and Pages is switched off there, and all links inside the code, app and README are re-pointed to the new address.
- **D7 (approved-plugins list).** Stays where it is in `tuvima-library` for now (Shy, 2026-10-28). The docs site shows the plugin catalog page as now. Follow-up: a user-facing site (library.tuvima.com, signing in with Tuvima ID) is expected to take over this kind of thing later; track it as a separate plan.
- **D9 (what moves).** Library (`docs/`, 111 pages), Tuvima.Wikidata (`docs/`, 12 pages plus the benchmark file), the platform. `tuvima-wikidata` keeps its README and its architecture notes for developers; its public docs move to the hub and its README links point there.
- **D8 (look).** One Tuvima look for the whole hub, using Library's current theme, fonts and logos; each product gets its own name and icon on the product picker.

## 4. UI behaviour

| Situation | What the reader sees |
|---|---|
| Home (`docs.tuvima.com`) | The hub index, top to bottom: (1) Tuvima logo, one-line welcome ("Help for everything Tuvima") and the search box; (2) **Products** (a card each: icon, name, one line, status such as Early Access, "Get started" link; today Library); (3) **Libraries** for developers (Tuvima.Wikidata card); (4) **Legal and policies** (licence, security, privacy, code of conduct, third-party notices as a short link list); (5) "Popular starting points" (Install Library, Your first library, Report a security issue) taken from a short list in `catalog.json`; (6) footer with GitHub and the Tuvima site. Cards come from `catalog.json`, so adding ID or Social later means adding one row. |
| Navigation on every page (the rule) | Three layers, always in the same place. (1) **Hub bar** (top, never changes): the Tuvima logo (always links to `docs.tuvima.com`), area links Products · Libraries · Legal, search, light/dark switch. (2) **Item bar** (just under it, only inside a product or library): the item's name and icon linking to its landing page (for example "Library" linking to `/products/library/`), an item switcher dropdown to jump to another product or library, and that item's groups (Guides, Develop, Reference). (3) **Page**: left menu for the current group, headings on the right, and a breadcrumb above the title: Tuvima › Products › Library › Guides › page title, every part a link. On a phone the hub bar shrinks to the logo, search and a menu button; the drawer lists the same three layers top to bottom. Legal pages have the hub bar and breadcrumb but no item bar. The logo is the "back to the main site" button on every page, including 404. |
| Area landing pages (`/products/`, `/libraries/`, `/legal/`) | The same cards for just that area plus a one-paragraph explanation of what the area is, so the address bar and breadcrumbs make sense. |
| Inside a product or library | Header shows the area and item switchers and search; left menu shows that product's Guides, Develop, Reference groups, collapsed as today; right side shows the page's headings. |
| Search | Opens scoped to the current product; one click widens to all Tuvima. Results show the product name. |
| Phone / tablet / TV | Phone: menu in a drawer, product switcher at the top of the drawer. Reading only; no editing UI. TV: not a target. |
| Page removed or mistyped | A friendly not-found page with search and the product picker. |
| Error / offline | Static pages; if the host is down nothing loads (accepted, see risk R2). Pages already opened keep working. |
| Partly ready (a product has few pages) | The card says "Early Access" and its status label; no empty menus are shown. |

Privacy: no analytics, no third-party scripts, fonts and search stay on the same site (as today). Outside links remain deliberate clicks.

## 5. Data / model changes

None in the app. New data files in the docs repo: an area-and-item list (`catalog.json`: area, id, name, icon, one-line description, path, status) and one page manifest per item (today's `publication.json`, moved under Library, and one new one for Tuvima.Wikidata).

## 6. Work packets

Built in the cloud; DNS and Pages settings are Shy's steps (section 7). Waves run end to end per the repo rules: wave 1 (WP1, WP2), wave 2 (WP3, WP4, WP5 in parallel where files differ), then Shy's DNS steps and a check that the new site is live, then wave 3 (WP6, WP7).

### WP1 — Hub platform in tuvima-docs
- **What it does:** Moves the Astro/Starlight platform (tests, validators, fonts, licences, theme, logos) from `tuvima-library/website/` into `tuvima-docs`. Site address and base path come from one setting. Adds `catalog.json`, the home index page and the three area landing pages (section 4), all generated from the catalog. Adds a `CLAUDE.md` for the docs repo (rules moved from `.claude/rules/docs.md`).
- **Files likely touched:** new files in `tuvima-docs` (`site/`, `catalog.json`, `CLAUDE.md`); to be confirmed.
- **Depends on:** none
- **Size:** Standard
- **Done when:** `npm run build` in `tuvima-docs` passes its own tests and validator with a home index and the three area landing pages generated from the catalog, with working links to each item's start page.

### WP2 — Area, item switchers and scoped search
- **What it does:** The three-layer navigation from section 4 (hub bar with logo, area links and search; item bar with landing link, item switcher and groups; breadcrumbs from the manifests) and phone drawer; each item's menu built from its own manifest; search scoped to the current item with an "all Tuvima" option; item name in results. First checks (a one-day trial) whether the existing sidebar-topics plugin can carry the two levels; if not, falls back to one generated menu per item. Records the choice in the packet notes.
- **Files likely touched:** Starlight config, header components, a search wrapper; to be confirmed.
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** with a dummy second product (removed before merge) the switchers, menu and scoped search behave as in section 4 at phone and desktop widths; text-based page checks plus final before/after screenshots.

### WP3 — Move Library's content
- **What it does:** Copies the 111 pages and assets from `tuvima-library/docs/` to `tuvima-docs` under `products/library/`; moves the manifest; rewrites links to repository files (AGENTS.md, LICENSE, source code) into full GitHub addresses; "edit this page" points at `tuvima-docs`. Pulls the company-wide pages (licence, security reporting, code of conduct, third-party notices and attributions, privacy principles) out into `/legal/`; the Library section links to them. The CONTRIBUTING and SECURITY root-file copies become a build-time fetch from the repo (D4). Keeps `approved-plugins.json` in `tuvima-library` (D7). No redirects and no date preservation.
- **Files likely touched:** `tuvima-docs/products/library/**`, `tuvima-docs/legal/**`, manifests, the fetch script; to be confirmed.
- **Depends on:** WP1, WP2
- **Size:** Standard
- **Done when:** every one of today's public pages exists at its new home (a script compares the old list with the new, none missing, and lists any that moved to Legal); the validator (links, anchors, metadata) is green.

### WP4 — CI and deployment in tuvima-docs
- **What it does:** Build and validate on every proposed change, deploy to GitHub Pages on merge to main, rebuild weekly (refreshes the fetched root files and runs the outside-link audit), upload a checked-site artifact per proposed change. Drops the "refresh context" step if its script turns out to be unneeded after WP3 (confirm what it does first).
- **Files likely touched:** `tuvima-docs/.github/workflows/*`; to be confirmed.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** a merge to main publishes the site to GitHub Pages and the weekly run is green.

### WP5 — Move Tuvima.Wikidata's docs
- **What it does:** Copies the 12 pages from `tuvima-wikidata/docs/` (reconciliation, entity data, series manifest, graph, ASP.NET Core, configuration, architecture, performance, changelog, both migration guides) to `libraries/wikidata/`, adds front matter and a manifest, links its README and NuGet package description to the new address (a version bump per that repo's rules, since package text changes), and updates that repo's `CLAUDE.md`/`AGENTS.md` (kept identical) to say docs live in `tuvima-docs`. The benchmark data file stays in its repo.
- **Files likely touched:** `tuvima-docs/libraries/wikidata/**`, `tuvima-wikidata/docs/**` (removed), README, both package files, `CLAUDE.md`, `AGENTS.md`; to be confirmed.
- **Depends on:** WP1, WP2
- **Size:** Standard
- **Done when:** all 12 pages build at `/libraries/wikidata/…`; the Wikidata repo builds and its offline tests pass.

### WP6 — Re-point tuvima-library
- **What it does:** Changes `ProjectLinks.DocsSite` and the page links in the Dashboard (and tests), all docs links in README.md, SECURITY.md, CONTRIBUTING.md and the PR template to the new address; deletes `docs/` (except `approved-plugins.json`, D7), `website/` and `scripts/docs/`; removes the docs publishing job and switches Pages off for the repository; rewrites `.claude/rules/docs.md`, `CLAUDE.md`, `AGENTS.md` and `engineering/README.md` to say public docs live in `tuvima-docs` and that a feature that needs docs gets a companion proposed change there. Leaves the ExifTool metadata name untouched (F7) and historical files in `engineering/docs-overhaul/` alone.
- **Files likely touched:** `src/MediaEngine.Web/Services/Navigation/ProjectLinks.cs`, Razor pages using it, `README.md`, `.github/**`, `CLAUDE.md`, `.claude/rules/docs.md`; to be confirmed.
- **Depends on:** WP3, WP4 and the new site live (section 7)
- **Size:** Standard
- **Done when:** full build and tests pass; a search of the repo finds no remaining `tuvima.github.io/tuvima-library` except the ExifTool metadata name and historical files.

### WP7 — Writing guide and "add a product" checklist
- **What it does:** In `tuvima-docs`: how to write and move pages, how to add a product, library or legal page (copy the template folder, add a row to `catalog.json`, add a manifest, add the "docs needed" box to that repo's PR template), plain-English style rules and the product vocabulary.
- **Files likely touched:** `tuvima-docs/CONTRIBUTING.md`, template folder; to be confirmed.
- **Depends on:** WP3
- **Size:** Mechanical
- **Done when:** a dry run adding a dummy product follows only the checklist and builds.

## 7. Local checks and Shy's steps

**Shy's one-time steps (cloud cannot do these).** DNS is on Azure (Azure DNS, in the Azure portal: DNS zones, `tuvima.com`, Record sets):
1. Add the domain-verification TXT record GitHub shows under the organisation's Settings → Pages → Add a domain (prevents someone else claiming `docs.tuvima.com`). The record name and value are shown there; in Azure it is a TXT record set with that name and value.
2. Add one record set: type CNAME, name `docs`, alias `tuvima.github.io`, TTL 1 hour. (Not an Azure "alias record set" to a resource; a plain CNAME.)
3. In `tuvima-docs` → Settings → Pages: Source = GitHub Actions; Custom domain = `docs.tuvima.com`; tick Enforce HTTPS once available (the certificate can take up to an hour after DNS is right).
4. After WP1 to WP5 are done and `docs.tuvima.com` shows the content, say "cut over", and wave 3 starts. Until then the old site is untouched.

**Local checks (added to `engineering/validation/batch.md` when built):**
- L1. On SHY-DESKTOP, open the Dashboard (small test library), use the help links in Settings → Libraries (file handling), Plugins (guide) and the account menu; each should land on the right page and heading at `docs.tuvima.com`.
- L2. Check the hub on a real phone: menu drawer, switchers, search.
- L3. Look at the home page and one page each from Library, Tuvima.Wikidata and Legal in light and dark.

## 8. Risks and open questions

**Risks**
- **R1. Old links break.** Accepted on purpose (no redirects); README, app and code links are re-pointed in WP6, and the validator checks every link on the new site.
- **R2. One host.** GitHub Pages is run by GitHub; if it is down the docs are too. Moving later is a DNS change (D1).
- **R3. Docs drift from code.** With docs in another repo, a feature can ship without its page. Mitigated by the PR template "docs needed" box and the build thread opening a companion change in `tuvima-docs`; the project already has all three repos in scope. Not fully solvable by tooling.
- **R4. Pages limits.** Sites cap at 1 GB and about 100 GB/month of traffic (soft). The docs are a few MB; fine for years.
- **R5. Structure guesses.** Which Library pages count as company-wide (Legal) is a judgement call made in WP3 and listed in its notes for Shy to skim.

**Follow-up (not in this spec):** a user-facing site (`library.tuvima.com`, signing in with Tuvima ID) for community features such as the approved-plugins list; needs its own plan.

### Hosting comparison (plain terms)

| | GitHub Pages + `docs.tuvima.com` (recommended) | Cloudflare Pages | Self-host (own server) |
|---|---|---|---|
| Cost | Free (public repo) | Free tier | Server, power, domain/TLS upkeep |
| Maintenance | Nothing to patch | Nothing to patch; one more account | You patch, monitor and renew |
| Reliability | GitHub's global network | Cloudflare's global network | Down when your server or home internet is down, exactly when people need install help |
| Privacy | No tracking by default; GitHub sees ordinary web logs | Same; optional analytics off | Full control; your home address and ports exposed unless carefully fronted |
| Performance | Fast worldwide CDN | Fast; edge redirects | Depends on your connection |
| Redirects | Page-level only | True 301s and rules | Anything |
| Extensibility | Static only (enough for docs) | Static plus small server functions | Anything, at a cost |
| Preview of changes | Downloadable artifact | Live preview address per change | Build your own |
| Setup by Shy | One DNS record plus two settings | Move or point DNS plus account setup | Server, certificates, deployments |

Microsoft Learn's quality comes from structure and design, not from hosting it yourself; the hub gets that from sections 3 and 4 and runs as static files anywhere.

### Questions answered (Shy, 2026-10-28)

Hosting on GitHub Pages with the custom domain (1a); DNS on Azure (1b); all pages in `tuvima-docs` (2); address structure to be thought through by area, not just product (3, see D2); versions explained and latest-only recommended (4); Tuvima.Wikidata moves now under its own area (5); approved-plugins list stays for now, user-facing site is a follow-up (6). Redirects and date preservation dropped.

Locked 2026-10-28: structure, home index and three-layer navigation as written (D2, section 4); latest version only (D5). Nothing open.

## Plain English summary

Library's and Tuvima.Wikidata's help pages move out of their code repositories into one shared Tuvima docs site at `docs.tuvima.com`. The site is organised into Products (Library now, ID and Social later), Libraries (developer packages like Tuvima.Wikidata) and Legal (licence, security, privacy and similar). Hosting is GitHub Pages with your own domain, which costs nothing and needs nothing maintained; you add two DNS records in Azure and flip two settings. There are no redirects, so the old address is switched off and the app and README links are updated to the new one. Your planning documents stay in the Library code, unpublished, and the user-facing Tuvima ID site is a separate later plan.
