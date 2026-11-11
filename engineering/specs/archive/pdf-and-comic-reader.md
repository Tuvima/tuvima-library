# Spec: PDF and comic reader

> **Status:** Built (awaiting Local checks L1 to L10 in the validation batch) · **Version:** 2 · **Last updated:** 2026-11-10 (built) · **Related docs:** [Product overview](../../product/overview.md) · [Global bookmarks](../global-bookmarks.md) (stage 3 waits for this) · [Book reader pictures](../book-reader-images.md) · [Unified player v2](unified-player-v2.md) (Bookmarks panel) · [Universal editor v5](universal-editor-v5-header-navigation-and-supporting-files.md) (Bookmarks tab) · [Look pass v3](../look-pass-v3.md) · Mockups: [`../design/mockups/pdf-comic-reader.html`](../../design/mockups/pdf-comic-reader.html)

## 1. Goal

A comic or a PDF in Library opens and reads inside Library, on a computer, tablet or phone, the same way an EPUB book does today. Library remembers the page, shows it under Continue, and page bookmarks join the shared bookmark model, which finishes the last stage of Global bookmarks.

**What exists today (checked in the code on main `aebfb52`, 2026-11-10)**

- Read on a book opens the reader at one address for every title. The reader opens EPUB only; a comic or a PDF gets "The new reader opens EPUB books for now."
- The Engine already hands a signed-in reader the file itself for `.epub`, `.cbz`, `.cbr` and `.pdf`, piece by piece, with the same access checks as every other reader call. `.cb7` is not on that list.
- Libraries accept `.epub`, `.pdf` and `.azw3` as Books and `.cbz`, `.cbr` and `.cb7` as Comics. The Engine can already open all three comic archive kinds (it counts pages and takes the cover from them at import).
- Two reading tools are already in the Dashboard with their license checks done: the book reader's library (which includes a CBZ page reader) and PDF.js (used by View > Documents, with find).
- The shared bookmark store already accepts a "page" kind, and `config/bookmarks.json` already says how close two page bookmarks must be to count as the same spot (same page), close (2 pages) or far (10 pages).
- `.azw3` books have no reader. This spec does not add one (see Scope).

## 2. Scope

**In scope**
- Reading comics (`.cbz`, `.cbr`, `.cb7`) and PDFs that are titles in a Books or Comics library.
- One page reader screen with the book reader's top line and panels, so reading an EPUB, a comic and a PDF feels like one product.
- Page layouts (one page, two pages, scrolling), fit, zoom, reading direction (including right to left for manga), background.
- A page slider with a page preview, and a Pages panel of small page pictures.
- PDF search and PDF contents (the PDF's own outline).
- Remembered page per profile, Continue reading, percent on tiles, finished state.
- "Next issue" at the end of a comic that belongs to a Series.
- Page bookmarks on the shared model: the reader's Bookmarks panel (Everyone and Mine), marks on the page slider, and page bookmarks in the editor's Bookmarks tab. This is Global bookmarks stage 3.
- Phone, tablet and desktop, keyboard and touch. A TV remote's arrow and OK keys work in the Dashboard.

**Out of scope**
- Highlights and notes on PDFs or comics (EPUB keeps them).
- Guided panel-by-panel view, automatic cropping of margins, image filters, translation.
- Reading `.azw3`, `.mobi` or DRM-protected files.
- Password-protected PDFs (they show a plain message), filling in PDF forms, printing.
- The View > Documents viewer and PDFs shown as Extras or supporting files. They keep what they have today (D20).
- Downloads for offline reading and the native phone, TV and car apps.
- Editing page order or deleting pages.

## 3. Decisions made

Proposed by Claude and agreed by Shy on 2026-11-10 ("agree with all": Q1 to Q5 all answered A, recorded in D5, D7, D11, D14 and D20).

- **D1. One page reader, same address.** Read on a comic or PDF opens the same address as a book. Library picks the right reader from the file kind. The top line, panels, colors and phone sheets are the book reader's, so there is one reading look. Reason: uniformity and Maintenance.
- **D2. Comics are served as pages by the Engine.** For all three comic archive kinds the Engine lists the pages and hands out one page picture at a time, at the size the screen needs, through the Dashboard with the same access checks as the book file. Reason: CBR and CB7 cannot be opened in a browser at all; one path for all three means one behavior to test (Reliability), a phone never has to hold a whole 500 MB comic (Performance), and the same path gives the small page pictures for the slider and the Pages panel. The CBZ reader inside the book reader's library is not used.
- **D3. PDFs are drawn in the browser with PDF.js,** which Library already ships for View > Documents, reading the file piece by piece through the existing reader path. Reason: no new tool; text search and the outline come with it.
- **D4. Page order in a comic** is natural file-name order (page 2 before page 10), pictures only, ignoring folders named like system leftovers. If the comic's own information file names a front cover page, that page is first.
- **D5. Layout** (Shy, Q1 A): Auto by default. Auto shows two pages side by side when the window is wider than it is tall and at least 1180 wide, otherwise one page. The cover is always alone. A page that is itself wider than it is tall (a printed double-page spread) is always shown alone. Scrolling is a third choice that stacks pages down the screen. The choice is remembered per title, per profile.
- **D6. Fit and zoom.** Fit is Page (whole page visible) or Width (fill the width and scroll down). Pinch, double-tap, Ctrl and the mouse wheel, and the plus and minus keys zoom from 100% to 400%; drag moves a zoomed page; turning the page returns to the chosen fit.
- **D7. Reading direction** (Shy, Q2 A): Left to right or Right to left. Right to left swaps which side of the screen means "next", swaps the two pages of a spread and reverses the slider. Library sets it from the comic's own information when that says manga; the choice a person makes is remembered for the whole Series, per profile. PDFs are always left to right.
- **D8. Turning pages.** Desktop: arrow keys, Space and Shift+Space, Page Up and Down, Home and End, the mouse wheel in Scrolling, and a click on the left or right third of the page. Touch: swipe, or tap the left or right third. A tap or click in the middle third shows or hides the top line and slider; they hide by themselves after 3 seconds of reading. Esc closes an open panel first, then the reader.
- **D9. Page slider.** A slider along the bottom with "Page 12 of 180" (or "Pages 12–13 of 180" in two-page layout) and a small page preview above the thumb while dragging or hovering. Bookmark marks on the slider are off until the person turns on "Show bookmarks on the bar" (Global bookmarks D16).
- **D10. Pages panel.** A grid of small page pictures with page numbers; the current page is outlined, bookmarked pages carry a bookmark mark; choosing one goes there. Pictures load as they scroll into view.
- **D11. PDF extras** (Shy, Q3 A): Search (the count of matches, a list with the page number and the words around each match, matches marked on the page) and Contents (the PDF's own outline, when it has one). No highlights or notes. A scanned PDF with no text says "This PDF has no text to search."
- **D12. Position and progress.** Library saves the page per profile and title a moment after each page turn and on closing. Opening returns to that page. Percent is page over pages. Reaching the last page marks the title finished, the same rule as a book. Restart in the "..." menu opens at page 1.
- **D13. Next issue.** After the last page of a comic in a Series, one more turn shows a card: the next owned issue's cover and name, "Read next", and "Back to" the Series. Nothing is shown when there is no next owned issue; the last turn then does nothing. PDFs and comics outside a Series have no card.
- **D14. Bookmarks** (Shy, Q4 A: built with the reader): the top line has Bookmark this page (press again on a bookmarked page to remove yours) and a Bookmarks panel with Everyone and Mine, each row showing a small picture of the page, "Page 48", an optional label, whose it is and the date. Promote, Find bookmarks and Export stay in the editor's Bookmarks tab for administrators (Editor v5), which gains page bookmarks with the page closeness rule already in `config/bookmarks.json`. This closes Global bookmarks stage 3.
- **D15. Background.** Black, Dark or Light around the page, Black by default, remembered per profile. The page itself is never recolored.
- **D16. Safety.** Comic pages are sent only as pictures of known picture kinds; anything else inside an archive is never served. An archive with an absurd number of entries or absurdly large pages is refused with the "can't open" message rather than exhausting the machine. PDFs never run scripts, and links inside a PDF open only after the same "leave Library?" check the book reader uses. Access is the existing rule for reading a title (library access, content rating limits, Vault); nothing new.
- **D17. Where unpacked pages live.** CBR and CB7 archives are slow to jump around in, so on first open the Engine unpacks that comic's pages into its temporary cache and clears the oldest when the cache passes a size set in a config file. Nothing is written beside the person's files (Read-only folders stay untouched).
- **D18. Sizes.** Page pictures are requested at the size of the screen (and its sharpness), capped, and small pictures for the slider and Pages panel at one fixed small size; originals are sent as they are when already small enough or when zoomed in.
- **D19. No new settings page.** Layout, Fit, Direction, Background and "Show bookmarks on the bar" live in the reader's own Settings panel. Limits and cache size live in a config file.

- **D20. Other PDFs** (Shy, Q5 A). Only PDFs that are titles in a Books or Comics library use this reader. View > Documents and PDFs shown as Extras or supporting files keep their own viewers.

## 4. UI behaviour

Numbers in brackets point at the mockups (screen, marker).

**Opening.** Read, Continue or Resume on a comic or PDF opens the reader full screen on the saved page. The top line shows Back, the title with the issue or author under it, and the tools [1]: Search (PDF only), Contents (PDF with an outline only), Pages, Bookmark this page, Bookmarks, Settings. The bottom line is the page slider [1.6]. Both fade after 3 seconds and return on a middle tap, mouse movement to the top or bottom edge, or any key that is not a page turn.

**Comic, desktop [screen 1].** Two pages side by side under Auto on a wide window, the cover alone. Clicking the left or right third turns the page.

**PDF, desktop [screen 2].** One page fitted to width under Auto when the PDF's pages are portrait and the window is not wide enough for two. The Search panel opens on the right at 400 wide with results; choosing a result goes to the page and marks the words.

**Settings panel [screen 3].** Layout (Auto, One page, Two pages, Scrolling), Fit (Page, Width), Direction (Left to right, Right to left; comics only), Background (Black, Dark, Light), Show bookmarks on the bar (switch).

**Pages panel [screen 4] and Bookmarks panel [screen 5].** As D10 and D14.

**End of an issue [screen 6].** As D13.

**Phone [screen 7].** One page, top line with Back, title and a single "..." that holds the tools in a bottom sheet when they do not fit (Pages, Bookmarks and Settings stay visible when width allows). Panels are bottom sheets. Swipe or tap the edges to turn; pinch and double-tap to zoom. In landscape, Auto shows two pages on a tablet and one on a phone.

**Tablet.** As desktop with touch; panels are side panels in landscape and bottom sheets in portrait.

**TV remote in the Dashboard.** Left and right turn pages, OK shows the top line and slider, where left, right and OK move between and use the tools, Back closes a panel and then the reader.

**Administrators and household members** see the same reader. The only difference is in the editor's Bookmarks tab (administrators).

| Situation | What the person sees |
|---|---|
| Normal | The saved page, top line and slider fading after 3 seconds |
| Empty (a comic with no pictures, a PDF with no pages) | "There are no pages in this file." and Back to Library |
| Loading | The background at once, a spinner where the page will be, then the page; while turning, the next page appears as soon as it arrives and the slider never blocks. Neighbouring pages are fetched ahead so a normal turn is instant |
| Error (Engine unreachable) | "Library can't reach the Engine. Your place is saved." with Try again and Back; the page already on screen stays |
| Error (not allowed, not found, file missing from disk) | "This title isn't available." and Back to Library, the same as the book reader |
| Error (damaged file, password-protected PDF, refused archive) | "This file can't be opened. It may be damaged or protected." and Back to Library; a single damaged page inside a good comic shows "This page can't be shown" in place of the page and reading continues |
| Partly ready (CBR or CB7 opened for the first time, still unpacking) | The first pages as they become ready; pages not ready yet show the spinner; the slider and Pages panel work, with empty small pictures filling in |
| Partly ready (PDF search still running) | "Searching…" with results so far |

## 5. Data / model changes

- **Saved page.** The existing saved reading position for a title gains a page form (page number and page count) next to the book form. Existing libraries are not affected; comics and PDFs simply start with no saved page.
- **Bookmarks.** Page bookmarks use the shared bookmark store's "page" kind that already exists. No new store.
- **Remembered reader choices.** Layout per title and profile; direction per Series and profile; background, fit and "Show bookmarks on the bar" per profile. Stored with the profile's existing reading preferences.
- **Page facts.** Each comic's page list (order, picture size, cover page) is worked out when first opened and kept in the Engine's cache, not the catalog. The manga flag is read from the comic's own information file at open.
- **Unpacked page cache.** Temporary, size-limited, safe to delete at any time, not part of backup.
- **Engine connection points.** New: a comic's page list, one comic page picture at a requested size, and `.cb7` joins the kinds the Engine will hand to a reader. The Dashboard passes these through for the signed-in person only, as it does for the book file.
- No reset or reingest is needed.

## 6. Work packets

File names marked "to be confirmed" were not all opened during planning; the build scouts them first.

### WP1 — Engine: comic pages
- **What it does:** Lists a comic's pages (D4) and serves one page picture at a requested width (D2, D18) for `.cbz`, `.cbr`, `.cb7`; unpack cache with a config size limit (D17); refusal limits (D16); adds `.cb7` to readable kinds. Same scope, access and rate rules as the book file.
- **Files likely touched:** `src/MediaEngine.Api/Endpoints/ReadEndpoints.cs`; new `src/MediaEngine.Api/Services/Reading/ComicPageService.cs` (+ interface, to be confirmed) and its `AddTuvima*` module; `src/MediaEngine.Contracts/Reading/` (page list type) + wire fixtures; `config/reader.json` (new: cache size, limits, widths); `tests/MediaEngine.Api.Tests/`; `docs/reference/api-endpoints.md`.
- **Depends on:** none
- **Size:** Complex (security and archives)
- **Done when:** tests show a generated CBZ, CBR and CB7 each return the same ordered page list and a page picture at the asked width; a profile over its rating limit gets not found; a non-picture entry is never served; an archive over the limits is refused; the cache evicts past its size.

### WP2 — Dashboard: pass-through for pages
- **What it does:** The Dashboard route that hands page lists and page pictures to the signed-in browser, with a strict allow-list (one id, a page number, a width from the allowed set), credentials resolved per request, private caching.
- **Files likely touched:** `src/MediaEngine.Web/Services/Integration/EngineBookProxyPath.cs`, `src/MediaEngine.Web/Program.cs`, `EngineApiClient.Playback.cs` + `IEngineApiClient.Playback.cs`, `tests/MediaEngine.Web.Tests/`.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** a test fetches page 3 at an allowed width through the Dashboard; any other path or width is not found; an anonymous request is refused.

### WP3 — Page reader screen for comics
- **What it does:** The page reader (D1): shell with the book reader's top line and panel frame, layouts and the spread rules (D5), fit and zoom (D6), direction (D7), turning pages and auto-hide (D8), background (D15), Settings panel, look-ahead loading, all states in section 4. The reader address picks book reader or page reader by file kind.
- **Files likely touched:** new `src/MediaEngine.Web/Components/Pages/PageReader.razor` (+ `.razor.css`), new `src/MediaEngine.Web/wwwroot/js/page-reader.js`, `Components/Pages/EpubReader.razor` (route chooses), `Resources/SharedStrings*.resx`, bUnit and node tests.
- **Depends on:** WP2
- **Size:** Complex
- **Done when:** a generated comic opens at desktop and phone widths; tests cover Auto choosing one or two pages, cover alone, wide page alone, right-to-left swapping sides, and each row of the states table; touch targets pass at 360, 768 and 1440.

### WP4 — PDFs in the page reader
- **What it does:** Draws PDFs with PDF.js inside the same shell (D3) with the same layouts, fit, zoom and turning; Search and Contents panels (D11); scripts off and guarded links (D16); password and damaged messages.
- **Files likely touched:** `wwwroot/js/page-reader.js` (PDF source beside the comic source), `PageReader.razor`, reuse of `wwwroot/vendor/pdfjs/`, node tests with a small generated PDF.
- **Depends on:** WP3
- **Size:** Complex
- **Done when:** a generated PDF with an outline opens, searches ("N places found", marks on the page), jumps from Contents, and a text-less PDF shows the no-text line; only the needed pieces of a large PDF are fetched.

### WP5 — Saved page, progress, Continue
- **What it does:** D12: save and restore the page, percent on tiles and the hero button, finished state, Restart; layout, direction and background preferences (section 5); manga flag from the comic's information.
- **Files likely touched:** reading-progress Engine actions and repository (to be confirmed), `src/MediaEngine.Contracts/Reading/` or `Playback/`, profile reading preferences, `PageReader.razor`, tests.
- **Depends on:** WP3
- **Size:** Standard
- **Done when:** tests show reopening lands on the saved page per profile, Continue shows the comic with the right percent, the last page marks finished, and direction chosen on one issue applies to the next issue of the Series.

### WP6 — Page slider and Pages panel
- **What it does:** D9 and D10, including the preview above the thumb and lazy small pictures (comics from WP1; PDFs drawn small in the browser).
- **Files likely touched:** `PageReader.razor`, `page-reader.js`, shared `AppRangeSlider` use, tests.
- **Depends on:** WP3 (comics), WP4 (PDFs)
- **Size:** Standard
- **Done when:** dragging the slider shows the preview and lands on the page; the Pages panel outlines the current page and loads pictures only as they scroll into view.

### WP7 — Page bookmarks (Global bookmarks stage 3)
- **What it does:** D14: Bookmark this page, Bookmarks panel with Everyone and Mine, marks on the slider behind the switch, page bookmarks in the editor's Bookmarks tab (definitive set else pool, Promote, Find bookmarks compare with the page closeness rule, Export).
- **Files likely touched:** bookmark Engine actions and repository for the page kind (to be confirmed), `Components/Details/` editor Bookmarks tab, `PageReader.razor`, `Contracts` bookmark types + fixtures, tests.
- **Depends on:** WP5, WP6
- **Size:** Complex
- **Done when:** tests show a page bookmark is private until promoted, appears under Everyone after Promote, Find bookmarks labels incoming page bookmarks Same spot, Close, Far or New from `config/bookmarks.json`, and marks appear on the slider only with the switch on.

### WP8 — Next issue
- **What it does:** D13, using the Series order Library already has and owned issues only.
- **Files likely touched:** a small Engine read for "next owned in Series" (to be confirmed whether one exists), `PageReader.razor`, tests.
- **Depends on:** WP5
- **Size:** Standard
- **Done when:** tests show the card for an issue with a next owned issue, none for the last issue, and Read next opens the next issue at page 1 with the Series' direction.

### WP9 — Remote keys, accessibility, reduced motion
- **What it does:** TV remote keys in the Dashboard (section 4), focus order and accessible names for every tool, page announced to screen readers on turn, no page-turn animation under reduced motion.
- **Files likely touched:** `PageReader.razor`, `page-reader.js`, tests.
- **Depends on:** WP6
- **Size:** Standard
- **Done when:** a keyboard-only test walks open, turn, panel, bookmark and close; the accessibility tree names every tool.

### WP10 — Docs and close-out
- **What it does:** Presentation rules (reader section), public guides in `tuvima-docs`, `src/MediaEngine.Web/CLAUDE.md` route table, Global bookmarks spec stage 3 marked built, index rows, Local checks appended to `engineering/validation/batch.md`.
- **Files likely touched:** `docs/product/presentation-rules.md`, `docs/architecture/`, `engineering/specs/global-bookmarks.md` (status line only), `engineering/README.md`, `tuvima-docs/content/products/library/`.
- **Depends on:** WP7, WP8, WP9
- **Size:** Mechanical
- **Done when:** the docs build passes and the index shows this spec Built and Global bookmarks stage 3 built.

Waves: 1 (WP1) · 2 (WP2) · 3 (WP3) · 4 (WP4, WP5) · 5 (WP6, WP8) · 6 (WP7, WP9) · 7 (WP10).

## 7. Local checks

- **L1.** Open a real CBZ, a real CBR and a real CB7 from `Z:\Development\testing\media\real_media`. Each opens on the cover within a few seconds and turns pages without a visible wait. Failure: a blank page, wrong page order, or a long wait on every turn.
- **L2.** Open a large comic (300 MB or more) on a phone on Wi-Fi. Pages stay sharp and the phone does not stall or reload the page.
- **L3.** Open a manga that marks itself right to left: the first turn is a tap on the left. Open the next issue: direction is kept.
- **L4.** Open a real text PDF (a few hundred pages): search finds a known phrase, Contents jumps to a chapter. Open a scanned PDF: the no-text line shows.
- **L5.** Compare each mockup screen with the running reader at 1024, 1280, 1440 and a phone width.
- **L6.** Read a few pages, close, reopen from Continue on another device signed in to the same profile: same page.
- **L7.** Bookmark a page, promote it in the editor's Bookmarks tab, sign in as another profile: it shows under Everyone, and on the slider once the switch is on.
- **L8.** On an iPhone and an Android phone: swipe, edge taps, pinch and double-tap all work; the top line hides and returns.
- **L9.** Finish an issue in a Series: the Next issue card appears and opens the right issue.
- **L10.** A comic in a Read-only folder: nothing new appears in that folder after reading.

## 8. Risks and open questions

- **Risks:**
  - First open of a large CBR or CB7 is slower while it unpacks. We would notice in L1; the fallback is unpacking in the background at import.
  - Very large page scans can exhaust a phone's memory. Sized pictures (D18) are the guard; L2 checks it.
  - PDFs with unusual fonts or very heavy pages can draw slowly. Noticed in L4.
  - Comic archives with odd file names can sort in the wrong order. Natural ordering covers the common cases; L1 on real files is the check.
  - The unpack cache uses disk space on the Engine's machine, bounded by the config limit.
- **Open questions:** none. Q1 to Q5 were answered by Shy on 2026-11-10, all option A: Auto layout by default (D5); right to left set automatically for manga and remembered per Series (D7); PDF Search and Contents without highlights (D11); page bookmarks built with the reader (D14); only Books and Comics PDFs use the reader (D20).

## Plain English summary

Today only EPUB books open in Library. This plan adds a reader for comics (CBZ, CBR, CB7) and PDFs that looks and behaves like the book reader: the same top line and panels, one or two pages or scrolling, zoom, right-to-left for manga, a page slider with a preview, a grid of pages, and search and contents for PDFs. Library remembers your page on every device, shows the title under Continue, and offers the next issue when you finish one. Page bookmarks work like every other bookmark, which finishes the last waiting stage of Global bookmarks. It uses tools Library already ships, so there is nothing new to license. Shy agreed all five open choices on 2026-11-10; nothing is left open.
