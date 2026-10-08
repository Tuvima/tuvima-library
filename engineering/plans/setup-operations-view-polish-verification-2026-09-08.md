# Setup, Operations, and View verification

Implemented against the [implementation plan](./setup-operations-view-polish-2026-09-08.md).

## Delivered behavior

- Setup: reveal controls, immediate password errors, optional confirmed profile PIN, all ten recovery codes visible, and durable media-folder deferral. Asynchronous setup data now triggers a render after loading, including on resume.
- Operations: shared navbar/page progress; duplicate files count as settled; identity processing drives active-item selection and ready counts; live cards and the complete current browser refresh; first-click navigation works; TV and comic counts omit denominators; every historical action opens the batch; active work is excluded before historical paging; review attention is visible across Settings; Processing details is removed.
- Artwork: representative owned asset/work covers take precedence over parent covers, respect preferred/user-selected artwork, and can come from another group member. Artwork completion requires an available cover.
- History: identity readiness counts as an addition even when intake never wrote a presentation timestamp. A freshly completed import no longer reports that it added nothing.
- Libraries: View and catalogued libraries share their detail header. View explains the storage base, Shared Library, private profile spaces, organization, file handling, and access. Reserving a personal-space identity or source does not create directories; actual file writes do.
- Installation: browser/site dismissal persists across reloads and later install events. System Overview retains a separate compact manual action.

## Root causes confirmed during implementation

1. Live preview merging retained old order, while activity ranking omitted identity jobs. Intake timestamps could remain unchanged throughout real enrichment.
2. The page measured intake completion, while the navbar measured terminal pipeline work. Duplicate intake outcomes were also missing from the latter's settled count.
3. History relied on `media_assets.presented_at`, which the current identity path did not populate. Ready identity jobs and completed batches could therefore show zero additions.
4. Artwork selection allowed a newer parent image to win over an issue's image. The inspected Saga fixture also contains a 640×960 placeholder as its sole archived page; that unusual original is not a damaged browser rendition.
5. MudBlazor validation cleared externally supplied errors after typing unless given the validation function. Shared single-line input CSS also overrode textarea height and line spacing.
6. Setup rendered before asynchronous stage data loaded without rendering again afterward. This affected the initial media Continue action and provider-stage resume.
7. View storage created physical folders during logical registration, including read/configuration paths. Install dismissal existed only in the component's current memory.

## Automated validation

- `dotnet restore MediaEngine.slnx`: passed.
- `dotnet build MediaEngine.slnx --no-restore`: passed, zero warnings/errors.
- `dotnet test MediaEngine.slnx --no-build`: 3,272 passed; 37 existing provider integration tests skipped.
- `node --test tests/pwa-install-persistence.test.cjs`: 2 passed, covering reload, later install events, observer independence, and dismissed browser installation.
- Strict MkDocs build and `git diff --check`: passed.

Regression coverage includes actual input events, PIN leading zeroes and pre-write validation, setup deferral persistence, lazy/concurrent View registration, active identity ranking, issue artwork selection, historical filtering before pagination, readiness-based additions, and shared progress with duplicate outcomes.

## Browser and fresh-ingestion validation

Rendered states were inspected at 1920×1080, 1280×720, and 390×844 using the local Dashboard. Temporary isolated setup/databases and a copied fixture protected the existing account, library, and media originals.

- Typed an invalid seven-character password, revealed it, corrected it, and confirmed immediate feedback. Completed setup both with a leading-zero PIN and without a PIN.
- Confirmed all ten recovery codes fit without an inner scrollbar on desktop and phone. Verified media deferral survives reload and permits setup completion.
- Freshly ingested the copied Saga issue. Its preferred managed cover was 1440×2193, with a separate medium rendition. The full issue cover appeared while processing continued, and the completed batch showed one added item with the same artwork. Open batch opened its browser on the first click.
- In the existing library, navbar and Operations both displayed 63% / 81 of 129 settled files. The preview reordered automatically as Imagine and then Nevermind became active. View all opened the complete paged browser on the first click, and its Back to Ingestion link returned without a reload.
- Confirmed TV cards show “1 episode added” or “0 episodes added,” with no total. Active work appeared above an empty historical section rather than consuming historical rows. The green active icon animated.
- Inspected View and Music library details for consistent header, section, folder, and organization treatments. Long View paths wrapped on mobile without horizontal page overflow. Visiting an empty personal space left its physical View directory absent in the isolated library.
- Confirmed the Operations review count remained visible on System Overview and Libraries. Inspected the compact install action and its browser guidance.

The embedded verification browser did not expose a native install prompt. Banner persistence was therefore validated through the executable JavaScript tests; the manual Overview action was checked visually. No native app installation was performed.

## Product-owner summary

Setup gives clear feedback and lets people defer folders. Operations now follows the work actually happening and reports completed additions accurately. View explains where private and shared files belong, while unused profiles leave no empty folders behind. Dismissing installation stays dismissed, with a quiet option available later.
