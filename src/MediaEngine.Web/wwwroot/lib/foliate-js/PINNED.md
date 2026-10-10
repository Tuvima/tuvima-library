# foliate-js (pinned copy)

Source: https://github.com/johnfactotum/foliate-js
Pinned commit: `78914aef4466eb960965702401634c2cb348e9b1` (2026-05-01, "Use original hrefs for external links and add isExternal in fb2.js (#129)")
Licence: MIT (see `LICENSE`). The bundled `vendor/zip.js` is zip.js (BSD-3-Clause); its notice is in `licenses/zip.js-BSD-3-Clause.txt` at the repository root.

The upstream project has no tagged releases and says its API is not stable. Therefore:

- Files here are unmodified copies from the pinned commit, except for the one local patch below. Do not edit them otherwise; upgrade by copying a newer reviewed commit, re-applying the patch, and updating the hash above.
- Only `wwwroot/js/book-reader.js` imports from this folder. Everything else in the Dashboard talks to that adapter.

Copied: `view.js`, `epub.js`, `epubcfi.js`, `paginator.js`, `fixed-layout.js`, `progress.js`, `overlayer.js`, `text-walker.js`, `search.js`, `tts.js`, `footnotes.js`, `comic-book.js`, `vendor/zip.js`.

Deliberately left out (not used by Tuvima, add them when a packet needs them): `mobi.js`, `fb2.js`, `pdf.js` and `vendor/pdfjs/` (PDF.js, experimental upstream), `vendor/fflate.js` (MOBI only), `dict.js`, `opds.js`, `quote-image.js`, `uri-template.js`, the demo reader (`reader.html`, `reader.js`, `ui/`) and the upstream tests. `view.js` imports some of these lazily; they are only requested when a MOBI, FB2 or PDF file is opened, which Tuvima never does.

## Local patch (Tuvima)

`paginator.js` and `fixed-layout.js`: the book frame's `sandbox` attribute is `allow-same-origin` instead of upstream's `allow-same-origin allow-scripts` (one line each, marked `TUVIMA LOCAL PATCH`). With `allow-scripts` removed the browser itself refuses to run any script in a book frame, so book safety no longer rests on the clean-up in `js/book-reader.js` alone (which leaked twice in review). Upstream keeps `allow-scripts` because of WebKit bug 218086 (events in sandboxed frames); the parent page still attaches its own listeners to the frame documents, which works in Chromium. **Not yet verified on iOS Safari / WebKit** — check taps, keys and swipe on a real iPhone before this reader becomes the default. If it breaks there, the fallback is to restore `allow-scripts` for WebKit only and keep the clean-up and policy as the boundary.
