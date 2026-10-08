# Tuvima Library documentation site

The published docs at <https://tuvima.github.io/tuvima_library/>, built with [Astro Starlight](https://starlight.astro.build/).

- **Content** lives in [`../docs/`](../docs/) as plain Markdown. `published.mjs` lists which folders join the site; plans, proposals, reports, and design-system working files stay out. Links to those files point at their GitHub copy.
- **Links** between pages use relative `.md` paths so they work on GitHub. `rehype-docs-links.mjs` turns them into site URLs and fails the build if a target does not exist. `scripts/check-links.mjs` then checks every internal link and section in the built output.
- **Front matter** is enforced by `src/content.config.ts`: `title`, `description`, `audience`, `category`, `product_area`.
- **Navigation** is the sidebar in `astro.config.mjs` (Guides, Develop, Reference). A page that is not listed there will not appear in the sidebar.
- **Moved or renamed pages** need an entry in `redirects.mjs`.
- **Theme and fonts** are in `src/styles/tuvima.css`. Fonts are self-hosted and search is local (Pagefind); the built site makes no third-party requests.

## Commands

Requires Node 24.

```bash
cd website
npm ci
npm run dev      # preview at http://localhost:4321/tuvima_library/
npm run check    # type-check
npm run build    # build to dist/ and check links
```

Or from the repo root: `scripts/docs/build-docs.ps1` and `scripts/docs/serve-docs.ps1`.
