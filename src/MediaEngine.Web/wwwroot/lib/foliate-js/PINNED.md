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

`paginator.js` and `fixed-layout.js`: the book frame's `sandbox` attribute is no longer upstream's fixed `allow-same-origin allow-scripts`. It is `allow-same-origin` (no scripts) unless the Tuvima adapter (`js/book-reader.js`, `bookFrameSandbox()`) sets `globalThis.TUVIMA_BOOK_FRAME_SANDBOX` to exactly `allow-same-origin allow-scripts`, which it does only on **Apple WebKit** (iPhone, iPad, Safari; iPadOS reports as a Mac, so `needsScriptSandbox()` also looks at touch points). Chromium, Firefox and Android keep no `allow-scripts`. Upstream needs `allow-scripts` because of WebKit bug 218086 (events in sandboxed frames): without it taps, keys, swipe, selection, highlights and link clicks never reach the page in WebKit. Both files carry a short block marked `TUVIMA LOCAL PATCH`; any value other than the one exact string falls back to no scripts.

**Why this is safe enough, and what it costs.** `allow-scripts` together with `allow-same-origin` lets a script in the frame reach the page and even remove the sandbox, so on Apple devices book safety rests on the two other layers, not on the sandbox: (1) the adapter hardens every page before it is shown (scripts, handlers, `javascript:` addresses, frames, objects, embeds, meta refresh removed, SVG rebuilt from an allow-list) and (2) every page carries a Content-Security-Policy meta with `script-src 'none'`, `connect-src 'none'` and `frame-src 'none'`. `scripts/visual-qa/book-reader/run-checks.mjs webkit` runs the interaction checks and a page that tries every trick (inline script, handlers, `javascript:` links and frames, a nested frame and object, SVG script, a fetch and an image to the outside, and deleting its own CSP meta first) inside an `allow-scripts` frame: nothing runs and nothing leaves the page. **Checked in WebKit (2026-10-16)**, 31 of 31 checks (Playwright WebKit 26.5, Windows, iPhone-sized touch). Earlier (2026-10-15) the same run showed taps, keys, swipe, selection, highlights and link clicks failing with no scripts allowed.

- Pictures: relative, absolute-path, CSS `url()`, SVG `<image>`, the SVG cover and unsized / 100% / figure / full-page-cover pictures all load and keep a real size in WebKit, same as Chromium. The suspected `max-height: 100%` collapse in `setImageSize` (only used for vertical writing), the `img-src blob: data:` rule and the no-scripts sandbox did not reproduce an image problem.
- Taps, keys, swipe, text selection, highlights and link clicks work in WebKit because the frame is allowed scripts there; they did not with no scripts (WebKit bug 218086). Still **not verified on a real iPhone**.
