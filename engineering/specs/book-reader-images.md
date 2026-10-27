# Spec: Book reader pictures load reliably

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-10-27 · **Related docs:** [Product overview](../product/overview.md) · bug log entry F43

## 1. Goal
Pictures inside a book (illustrations, maps, inline figures, full-page covers) show up whenever you read it, on every device. Today they do not load.

## 2. Scope
**In scope**
- Find and remove the reason pictures fail in the current (default) EPUB reader.
- Make the new reader (the `?reader=new` one) the only reader, once an iPhone check passes, so there is no second code path to keep working.
- Say clearly on screen when a single picture cannot be shown, instead of an empty gap.

**Out of scope**
- PDF reading (PDFs draw their own pages; no evidence they are affected. Q1 asks Shy to confirm).
- Changes to the book-safety hardening of the new reader.

## 3. Decisions made
- **D1 (root cause, found by reading the code, not yet reproduced on a real book).** The current reader asks the Engine for a chapter, and the Engine rewrites every picture address inside it to point straight at the Engine's own address (`http(s)://<engine host>/read/<id>/resource/...`). The reader page then shows that HTML inside the Dashboard page. The browser loads each picture on its own, so (a) it cannot send the signed-in session the Engine demands for that address, and (b) the Engine's address, as the Dashboard server saw it, may not be reachable from the phone or other device at all. The result: text loads (the Dashboard fetches it with its credentials) but every picture fails. This affects every EPUB with pictures, not some.
- **D2.** The new reader does not have this problem: it downloads the book file through the Dashboard's own book route and unpacks pictures inside the browser, so no separate picture requests are made. Its tests cover pictures in Chromium and WebKit. So the fix is to finish and switch to the new reader, not to patch the old one.
- **D3.** A narrow weakness remains in the new reader: a picture whose declared type in the book is missing or odd is replaced by an empty file. Real books do this. Add a fallback that detects the type from the file name or its first bytes.

## 4. UI behaviour
| Situation | What the person sees |
|---|---|
| Normal | Pictures appear in place, sized to the page, on phone, tablet and desktop |
| A picture is damaged or unsupported | A small "Picture unavailable" placeholder in its place, the rest of the page unaffected |
| Loading | Text first, pictures fill in as pages open |
| Error (book missing) | Existing "This book is not available." message |

## 5. Data / model changes
None.

## 6. Work packets
### WP1 — Reproduce on a generated book
- **What it does:** Add a test EPUB with inline, CSS and cover pictures; show in a test that the old reader's chapter HTML points at the Engine address and fails without credentials, and that the new reader displays them.
- **Files likely touched:** `tests/MediaEngine.Processors.Tests`, `tests/MediaEngine.Web.Tests`, `src/MediaEngine.Api/DevSupport/EpubBuilder.cs`
- **Depends on:** none · **Size:** Standard
- **Done when:** a named test fails on the old route and passes on the new reader.

### WP2 — Type fallback in the new reader
- **What it does:** When a picture's declared type is empty or wrong, infer it (file extension, then first bytes) before hardening.
- **Files likely touched:** `src/MediaEngine.Web/wwwroot/js/book-reader.js`, `tests/MediaEngine.Web.Tests/JavaScript`
- **Depends on:** none · **Size:** Standard
- **Done when:** a book with untyped JPEG and PNG pictures shows them.

### WP3 — "Picture unavailable" placeholder
- **What it does:** Replace silent empty pictures with the placeholder in 4.
- **Files likely touched:** `src/MediaEngine.Web/wwwroot/js/book-reader.js`, reader CSS
- **Depends on:** WP2 · **Size:** Standard
- **Done when:** a book with one broken picture shows the placeholder only there.

### WP4 — Make the new reader the default
- **What it does:** Remove `?reader=new`, delete the old chapter-by-chapter reader and the Engine's picture-rewriting route if nothing else uses it, update docs.
- **Files likely touched:** `Components/Pages/EpubReader.razor`, `BookReader.razor`, `wwwroot/js/epub-reader.js`, `src/MediaEngine.Api/Endpoints/ReadEndpoints.cs`, `src/MediaEngine.Processors/EpubContentService.cs`
- **Depends on:** WP1–WP3 (Shy chose to switch now, before L1; L1 becomes a post-merge check) · **Size:** Complex
- **Done when:** one reader opens every book; build and guardrail tests pass.

## 7. Local checks
- **L1.** On an iPhone, open an illustrated EPUB from the real media folder (`Z:\Development\testing\media\real_media`) in the new reader. Pictures appear, taps and swipes turn pages. Failure: any blank gap or no page turn.
- **L2.** Repeat on desktop in the old reader to confirm the pictures really are missing there (confirms D1).

## 8. Risks and open questions
- **Risks:** Retiring the old reader removes features it may still have that the new one lacks (highlights, search). WP4 starts with a feature comparison.
- **Q1 (answered: A).** The affected book is an EPUB; PDF stays out of scope.
- **Q2 (answered: B).** Shy chose to switch to the new reader now, without waiting for the iPhone check. Risk: if iPhone taps or page turns fail, all iPhone readers are affected until fixed; L1 is the first post-merge check and a failure becomes a fix brief.

## Plain English summary
Pictures in books fail because the old reader makes your browser fetch each one separately from the Engine without proof you are signed in, so the Engine refuses them. The new reader opens the whole book once and pulls pictures out locally, so it avoids that. The plan is to prove it with a test book, patch one small gap (pictures with a missing label), show a tidy placeholder when a picture truly cannot load, and then switch everyone to the new reader now, as you chose. Your iPhone check happens afterwards.
