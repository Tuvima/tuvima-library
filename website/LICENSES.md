# Documentation platform licenses

The documentation remains part of Tuvima Library under the repository's AGPLv3 license. The following compatible tools/assets retain their own licenses. Exact versions and transitive packages are recorded in `package-lock.json`.

| Component | Version | License | Role |
| --- | --- | --- | --- |
| Astro | 7.3.8 | MIT | Static site build |
| Astro Starlight | 0.42.6 | MIT | Documentation layout and components |
| @astrojs/markdown-remark | 7.3.2 | MIT | Markdown processor |
| Starlight Sidebar Topics | 0.9.0 | MIT | Reader-specific sidebar navigation |
| @astrojs/check | 0.9.10 | MIT | Build diagnostics |
| TypeScript | 6.0.3 | Apache-2.0 | Type checking |
| YAML | 2.9.1 | ISC | Strict source metadata preparation |
| Cheerio | 1.2.0 | MIT | Static HTML validation |
| PostCSS Selector Parser override | 7.1.6 | MIT | Patched transitive CSS build parser |
| Pagefind | 1.5.2 | MIT | Same-origin search assets and index |
| Expressive Code | 0.44.2 | MIT | Rendered code blocks and copy controls |
| Shiki | 4.5.0 | MIT | Code syntax highlighting |
| Montserrat | Repository font | SIL Open Font License 1.1 | Self-hosted headings |
| JetBrains Mono | Repository font | SIL Open Font License 1.1 | Self-hosted code font |

The font notices are retained verbatim beside the files in `src/fonts/Montserrat-OFL.txt` and `src/fonts/JetBrainsMono-OFL.txt`. The SVG logo/icon assets come from this repository's `assets/images/` directory. Starlight's built-in icons and syntax resources keep their upstream package notices. No screenshot-zoom library, hosted search service, or remote font service ships with this site.

The build emits complete package notices and the fonts' embedded copyright notices at `licenses/THIRD-PARTY.txt`. The public license page links to that same-origin download. `scripts/generate-notices.mjs` derives package text from the locked installed dependencies and checks that font notices match the bundled fonts.

Build-only transitive tools also include LGPL-3.0-or-later libvips/sharp assets, MPL-2.0 Lightning CSS, Python-2.0 argparse, BlueOak-1.0.0 utilities, CC0 data and 0BSD helpers. These are not browser-delivered native libraries. The lockfile records every package's license; its current scan has no missing license fields.
