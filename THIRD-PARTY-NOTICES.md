# Third-party artwork and build tools

## Google Material Icons

The first-party Material icon catalog contains the used, unmodified SVG path data from Google Material Icons, distributed under the Apache License 2.0. Copyright Google LLC. The pinned extraction is from MudBlazor 9.0.0's Material icon constants; MudBlazor source is MIT licensed, copyright MudBlazor contributors.

Original artwork and license: <https://github.com/google/material-design-icons/blob/master/LICENSE>.
MudBlazor source and license: <https://github.com/MudBlazor/MudBlazor/blob/v9.0.0/LICENSE>.

The icon snapshot in `scripts/icons/material-icon-paths.json` supports deterministic generation after removal of the UI package. The SVG artwork is not modified.

## Font Awesome Free

Font Awesome Free SVG artwork under `src/MediaEngine.Web/wwwroot/icons/fontawesome` remains governed by the Creative Commons Attribution 4.0 International license for icons. Copyright Fonticons, Inc. Attribution: <https://fontawesome.com/>. License: <https://creativecommons.org/licenses/by/4.0/>.

## Native CSS utilities

The used base and spacing/display utilities in `native-utilities.css` retain snippets from MudBlazor 9.0.0 under its MIT license, with class ownership now in Tuvima Library. See the full retained MIT notice in `licenses/MudBlazor-MIT.txt`.

## NUglify (build only)

NUglify 1.23.3 is used only for Release CSS compilation and is excluded from Dashboard runtime assets. Copyright 2016 Alexandre Mutel. Its BSD 2-clause license and original Microsoft Ajax Minifier Apache 2.0 notice are preserved in `licenses/NUglify.txt`.

## Documentation site (build only)

The documentation site in `website/` is built with Astro (MIT), Astro Starlight (MIT), `starlight-sidebar-topics` (MIT), `starlight-image-zoom` (MIT), Pagefind search (MIT), `sharp` (Apache-2.0), and TypeScript (Apache-2.0). Exact versions are pinned in `website/package-lock.json`. None of these ship in the Engine or Dashboard.

## Montserrat and JetBrains Mono fonts

The documentation site self-hosts Montserrat (copyright The Montserrat Project Authors) and JetBrains Mono (copyright JetBrains s.r.o.), both licensed under the SIL Open Font License 1.1: <https://openfontlicense.org/>. The same font files are used by the Dashboard.
