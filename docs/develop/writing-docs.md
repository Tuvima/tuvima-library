---
title: Writing documentation
description: Write accurate task-focused documentation, update the publication manifest and verify links before publishing.
audience: developer
category: guide
product_area: documentation
status: current
---

# Writing documentation

Use this guide to add or improve a documentation page. A small edit takes a few minutes to write and verify; factual research may take longer.

## Choose the right source

Author published pages in `docs/`. `website/publication.json` lists the pages, their stable slugs, sidebar topics and redirects. Astro Starlight builds from an ignored generated copy; never edit that copy.

Put engineering plans, dated verification reports and design working files in `engineering/`. That folder is excluded from the website, but it remains public in the repository. Review captures belong under ignored `.tmp/`. Existing product screenshots are removed until better replacements are available; do not add empty image placeholders.

Use one page for one reader's task or question. Choose Guides for everyday use and administration, Develop for contributors and extension authors, or Reference for lookup material.

## Establish what is true

Check current code, configuration and accepted verification evidence. Use [Product Status](../product/status.md) and the [feature inventory](../product/feature-truth-inventory.md) as starting points, and correct discrepancies rather than repeating them.

Separate current behavior from partial or planned behavior. A source file or successful compile alone does not prove a deployment or user journey works. Do not turn historical acceptance caveats into present-tense promises.

## Write for the reader

- Address the reader as “you,” use active voice and lead with the outcome.
- Say what a procedure achieves and give a useful time estimate only when one can be supported.
- Prefer short sentences and paragraphs. Aim for grade-eight language in everyday tasks, while retaining precise technical terms where needed.
- Use task headings such as “Add a folder.” Explain product terms on first use and link the [glossary](../reference/glossary.md).
- Call the product “Tuvima Library” first, then “Tuvima.” Use Engine and Dashboard when distinguishing the two apps.
- Keep required commands, paths, permissions and warnings beside the step that needs them. Put optional internals in a Technical details disclosure or a reference link.
- Describe current behavior directly. Avoid dated change-log narration on maintained guides.
- End task and explanation pages with useful next steps. Policy pages may instead use their relevant reporting or license links.

## Add required metadata

Every published source needs these fields:

```yaml
---
title: Add a library folder
description: Add an existing media folder and understand how source permissions affect scanning and organization.
audience: user
category: guide
product_area: libraries
status: current
tags:
  - folders
---
```

| Field | Allowed values |
| --- | --- |
| `audience` | `user`, `administrator`, `developer`, `designer` |
| `category` | `landing`, `tutorial`, `installation`, `guide`, `explanation`, `reference`, `architecture`, `policy` |
| `status` | `current`, `early-access`, `target-state` |
| `product_area` | A meaningful nonempty area name |
| `description` | A meaningful description of at least 20 characters |
| `tags` | Optional list of strings |

`status` classifies the page; it does not make every feature mentioned on it current. Label partial and planned features visibly in the body. The protected Wikidata property map has one named build-time metadata adapter; other pages must provide their metadata directly.

Starlight renders the page title. Begin the body with an introduction and use second-level headings for sections. The preparation step removes an existing first-level Markdown title when migrating older pages.

## Choose a page template

These outlines are starting points. Omit sections that do not help the reader rather than filling them with boilerplate.

### Tutorial

```markdown
Opening: what you will accomplish and the expected time.
## Before you start
## Complete the first task
## Check the result
## If something goes wrong
## Next steps
```

### Installation

```markdown
Opening: who this installation method suits and its availability.
## Requirements
## Prepare storage and permissions
## Install and start
## Open the Dashboard
## Update and remove
## Troubleshoot
## Next steps
```

### Guide

```markdown
Opening: the task and result.
## Before you start
## Do the task
## Verify the result
## Technical details
## Next steps
```

### Explanation

```markdown
Opening: the concept and why it matters.
## Understand the model
## Follow an example
## Know the limits
## Next steps
```

### Reference

```markdown
Opening: who this reference serves and what it covers.
## Values or interfaces
## Defaults and constraints
## Examples
## Related guidance
```

### Architecture

```markdown
Opening: in this page, the subsystem and questions answered.
## Where this lives in the code
## Responsibilities and data flow
## Contracts and failure behavior
## Verification
## Related architecture
```

## Use Markdown and components deliberately

Keep simple pages as `.md` with numbered procedures and standard links. Use `.mdx` when a Starlight component materially improves the explanation. Import components explicitly:

```mdx
import { Steps, Tabs, TabItem, Aside, Badge, LinkCard } from '@astrojs/starlight/components';
```

Use `Steps` for a substantial procedure, `Tabs`/`TabItem` for genuine platform alternatives, `Aside` for notes or cautions, `Badge` for a visible state, and `LinkCard` for a useful next step. Markdown `:::note` asides are available without MDX. A plain numbered list or link is also valid and remains easier to read on GitHub.

Use native `<details><summary>Technical details</summary>…</details>` only for optional detail. Do not hide prerequisites or security warnings there. Test Markdown inside disclosures; MDX and Markdown handle embedded content differently.

## Keep links and assets working

Prefer source-relative links such as `../guides/adding-media.md#add-a-source`. The build resolves them through the publication manifest and adds `/tuvima_library/`. Component links and front matter actions need the same base-path checks.

For a move, update source references and add the old public route to `publication.json` redirects. Preserve important heading IDs or document an intentional retirement. Static redirects use an HTML refresh, so a 200 response alone does not prove they work.

Links to repository material outside the site become GitHub file links. Keep essential user instructions on the site instead of sending readers into an engineering report. New assets must be local, appropriately sized, licensed and accessible. Fonts and search resources stay on the same origin as the site.

## Keep shared policies synchronized

Edit `CONTRIBUTING.md` and `SECURITY.md` at the repository root. Their site versions are generated:

```sh
node scripts/docs/sync-community-docs.mjs
node scripts/docs/sync-community-docs.mjs --check
```

Do not hand-edit the generated copies. Keep new policy commitments within the product owner's approved scope.

## Verify and preview

From the repository root:

```powershell
pwsh -File scripts/docs/build-docs.ps1 -InstallDependencies
pwsh -File scripts/docs/serve-docs.ps1 -Address 127.0.0.1:8000
```

Or run `npm ci`, `npm run check`, `npm test` and `npm run build` in `website/`. Preview the production output with `npm run preview`; search indexing is part of the production build.

Check the rendered page on a phone and desktop in both themes. Follow its links, use the keyboard and test any changed redirects. Internal links, fragments, assets and required metadata are blocking checks. External-link failures need investigation and may be rate limits rather than broken destinations.

Regenerate local agent context with `pwsh -File scripts/docs/refresh-codex-context.ps1`. The generated `.codex/` files are ignored and must not be edited or committed. The historical `.agent/` mirror is retired.

## Next steps

- [Contribute a change](contributing.md).
- [Understand the architecture](../architecture/technical-overview.md).
- Read the [website tooling guide](../../website/README.md).
