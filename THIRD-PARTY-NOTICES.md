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

## SecLists common-password list (password rules)

Tuvima Library refuses known common passwords when an account password is set or changed. The list is the NCSC "100k most used passwords" file from SecLists (`Passwords/Common-Credentials/100k-most-used-passwords-NCSC.txt`), filtered to entries of 12 or more characters, lower-cased and deduplicated into `src/MediaEngine.Identity/Resources/common-passwords.txt`. SecLists is distributed under the MIT License, copyright Daniel Miessler. The licence text is in `licenses/SecLists-MIT.txt`.

## Documentation platform

The documentation site uses Astro 7.3.8, Starlight 0.42.6, Astro Markdown Remark 7.3.2 and Starlight Sidebar Topics 0.9.0 under MIT licenses. Browser assets include Pagefind 1.5.2, Expressive Code 0.44.2 and Shiki 4.5.0, also MIT licensed. Versions and transitive dependencies are locked in `website/package-lock.json`; installed packages retain their license notices.

Documentation checks use TypeScript 6.0.3 (Apache-2.0), Astro Check 0.9.10 (MIT), YAML 2.9.1 (ISC) and Cheerio 1.2.0 (MIT). The exact PostCSS Selector Parser 7.1.6 override is MIT licensed and addresses the audited nested dependency.

Documentation fonts are self-hosted Montserrat and JetBrains Mono under SIL Open Font License 1.1. Their original notices are retained in `website/src/fonts/Montserrat-OFL.txt` and `website/src/fonts/JetBrainsMono-OFL.txt`, and are included in the built site's `licenses/` directory. Montserrat is credited to its authors in its OFL notice; JetBrains Mono is copyright JetBrains s.r.o.
