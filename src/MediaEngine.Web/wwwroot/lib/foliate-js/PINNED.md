# foliate-js (pinned copy)

Source: https://github.com/johnfactotum/foliate-js
Pinned commit: `78914aef4466eb960965702401634c2cb348e9b1` (2026-05-01, "Use original hrefs for external links and add isExternal in fb2.js (#129)")
Licence: MIT (see `LICENSE`). The bundled `vendor/zip.js` is zip.js (BSD-3-Clause); its notice is in `licenses/zip.js-BSD-3-Clause.txt` at the repository root.

The upstream project has no tagged releases and says its API is not stable. Therefore:

- Files here are unmodified copies from the pinned commit. Do not edit them; upgrade by copying a newer reviewed commit and updating the hash above.
- Only `wwwroot/js/book-reader.js` imports from this folder. Everything else in the Dashboard talks to that adapter.

Copied: `view.js`, `epub.js`, `epubcfi.js`, `paginator.js`, `fixed-layout.js`, `progress.js`, `overlayer.js`, `text-walker.js`, `search.js`, `tts.js`, `footnotes.js`, `comic-book.js`, `vendor/zip.js`.

Deliberately left out (not used by Tuvima, add them when a packet needs them): `mobi.js`, `fb2.js`, `pdf.js` and `vendor/pdfjs/` (PDF.js, experimental upstream), `vendor/fflate.js` (MOBI only), `dict.js`, `opds.js`, `quote-image.js`, `uri-template.js`, the demo reader (`reader.html`, `reader.js`, `ui/`) and the upstream tests. `view.js` imports some of these lazily; they are only requested when a MOBI, FB2 or PDF file is opened, which Tuvima never does.
