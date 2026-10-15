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

## QRCoder (invitation QR codes)

QRCoder 1.6.0 draws the QR code that Users & Access shows with an invitation link. It runs inside the Dashboard, so the link never leaves the computer. Copyright Raffael Herrmann, MIT License.

## PdfPig and Open XML SDK (document text search)

PdfPig 0.1.16 (Apache License 2.0) reads the embedded text layer of PDFs, and DocumentFormat.OpenXml 3.5.1 (Microsoft, MIT License) reads text from .docx, .xlsx and .pptx files, so View documents are searchable. Both run inside the Engine on the user's computer; document text is never sent anywhere.

## hls.js (adaptive video playback)

hls.js 1.7.3 (Dailymotion and the hls.js contributors, Apache License 2.0) plays adaptive HLS video in browsers that cannot play it natively, in the Dashboard player and in the TV web client. Its file is bundled unmodified under `src/MediaEngine.Web/wwwroot/vendor/hls/` and `clients/tv-web/vendor/` together with its license, and is always loaded from the app itself, never from a CDN. Project: <https://github.com/video-dev/hls.js>.

## PDF.js (Documents viewer)

PDF.js (pdfjs-dist 5.4.149, Mozilla, Apache License 2.0) draws PDFs and finds words inside them in the View Documents viewer. Its files are bundled unmodified under `src/MediaEngine.Web/wwwroot/vendor/pdfjs/` together with its license, and are always loaded from the Dashboard itself, never from a CDN. The document is read in the browser on the user's computer; nothing is sent to any other service.

## Tesseract OCR, tessdata_fast, PDFtoImage and PDFium (scanned document search)

Scanned PDF pages and photos of paper are read with Tesseract OCR (Apache License 2.0, <https://github.com/tesseract-ocr/tesseract>), run as a separate program on the user's computer. The Windows installer bundles the UB Mannheim build (v5.4.0.20240606) with the libraries it needs (Leptonica, libpng, libjpeg-turbo, libtiff, libwebp and others), each under its own permissive or LGPL licence and shipped unmodified as separate files; the Apache 2.0 licence is installed under `licenses/tesseract`. English and orientation language data come from tessdata_fast (Apache License 2.0, <https://github.com/tesseract-ocr/tessdata_fast>), pinned to commit `923915d4`. PDF pages are drawn with PDFtoImage 5.4.0 (MIT License, <https://github.com/sungaila/PDFtoImage>), which uses PDFium (BSD 3-Clause License, copyright The PDFium Authors) through the bblanchon.PDFium packages. Everything runs on the user's computer; nothing is uploaded, and the only network use is an optional language download the user starts in Settings.

## foliate-js (book and comic reader)

The in-browser book reader uses a pinned copy of foliate-js (commit `78914aef`, with one documented one-line safety patch per frame file; see its `PINNED.md`) under `src/MediaEngine.Web/wwwroot/lib/foliate-js/`, to open EPUB and comic (CBZ) files and keep standard reading positions. Copyright (c) 2022 John Factotum, MIT License (`src/MediaEngine.Web/wwwroot/lib/foliate-js/LICENSE`). It bundles zip.js (@zip.js/zip.js), copyright (c) 2022 Gildas Lormeau, BSD 3-Clause License, retained in `licenses/zip.js-BSD-3-Clause.txt`. Project: <https://github.com/johnfactotum/foliate-js>.

## SecLists common-password list (password rules)

Tuvima Library refuses known common passwords when an account password is set or changed. The list is the NCSC "100k most used passwords" file from SecLists (`Passwords/Common-Credentials/100k-most-used-passwords-NCSC.txt`), filtered to entries of 12 or more characters, lower-cased and deduplicated into `src/MediaEngine.Identity/Resources/common-passwords.txt`. SecLists is distributed under the MIT License, copyright Daniel Miessler. The licence text is in `licenses/SecLists-MIT.txt`.

## Documentation platform

The documentation site uses Astro 7.3.8, Starlight 0.42.6, Astro Markdown Remark 7.3.2 and Starlight Sidebar Topics 0.9.0 under MIT licenses. Browser assets include Pagefind 1.5.2, Expressive Code 0.44.2 and Shiki 4.5.0, also MIT licensed. Versions and transitive dependencies are locked in `website/package-lock.json`; installed packages retain their license notices.

Documentation checks use TypeScript 6.0.3 (Apache-2.0), Astro Check 0.9.10 (MIT), YAML 2.9.1 (ISC) and Cheerio 1.2.0 (MIT). The exact PostCSS Selector Parser 7.1.6 override is MIT licensed and addresses the audited nested dependency.

Documentation fonts are self-hosted Montserrat and JetBrains Mono under SIL Open Font License 1.1. Their original notices are retained in `website/src/fonts/Montserrat-OFL.txt` and `website/src/fonts/JetBrainsMono-OFL.txt`, and are included in the built site's `licenses/` directory. Montserrat is credited to its authors in its OFL notice; JetBrains Mono is copyright JetBrains s.r.o.
