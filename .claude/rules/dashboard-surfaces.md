---
paths:
  - "src/MediaEngine.Web/**"
  - "src/MediaEngine.Api/Services/Details/**"
  - "tests/MediaEngine.Web.Tests/**"
---

# Dashboard product-surface rules

Current per-surface rules for the Dashboard, moved verbatim from AGENTS.md on 2026-10-17 (dated headings replaced by subject headings). Public per-surface behaviour lives in `docs/product/presentation-rules.md`; where the two disagree, stop and ask.

## Collections and linked identity

- Collections uses one viewport-bounded, padded content scroller beside its anchored rail; never allow a second document scrollbar.
- Automatic rollups prioritize trusted fictional-universe, franchise, and based-on/adaptation QIDs over lane-local parent/series structure. A cross-media identity such as Batman can therefore combine qualifying comic and film groups.
- People presence counts distinct top-level works: tracks collapse to albums and episodes collapse to shows.
- Person names use the exact shared media-detail title family, scale, weight, responsive density, and lower-left identity anchor through the rendered person copy hook. Person dates, locations, and owned-title facts reuse the standard detail metadata row/item typography used by year, runtime, and genre. The portrait may remain centered independently; it must not vertically center the identity column.
- Musical groups resolve all canonical Wikidata `has parts` identities into `person_group_members` and queue incomplete people for full hydration. Collective pen names continue to use aliases; band members must never be rendered as pseudonym aliases.

## TV episode context and personal status

TV detail uses one profile-aware continuation policy: unstarted/reset shows retain series artwork, active shows use the current or next owned episode still, and explicit episode details stay episode-scoped. Completed owned runs offer rewatch with series artwork. Episodes use short episode synopsis text, never Wikipedia extracts. Missing catalogue entries have no synopsis or artwork and retain the Not in library placeholder. The episode rail scrolls without a four-card cap.

TMDB episode credits are stored separately from show aggregates; full credits support episode, season, and role filtering. Season coverage currently reflects owned episode evidence. More exposes media-appropriate personal completion/reset, history, Undo, Continue visibility, and applicable queue/playlist/collection utilities independently of metadata-edit permission. Revision checks prevent stale progress writes from reversing a reset; bookmarks, genuine consumption history, and music play counts are preserved.

These changes require fresh pre-beta ingestion. Runtime and responsive visual acceptance are pending; see the TV episode consistency proposal for validation status and remaining scope.

## Libraries settings

Libraries is one master/detail screen (`LibraryFoldersPanel`, shared by first-run setup and Settings > Libraries); there is no Library settings page and no detail tabs. The six media types plus the Import folder are always listed. A folder is Managed or As-is (`management_mode`), each type has one New media goes here folder, and there is no priority. Advanced holds per-type naming and tags plus the global duplicate and artwork rules. Header and list rows share one full-width column grid.

There is no Add Library wizard: a type's library is created when its first folder is added through the folder picker (full-width paste-a-path bar, single-click selects and double-click opens, copyable selected path, media locations listed before drives, paths as the Engine sees them, folders only, Tuvima's own folders never listed). Personal Media configures the existing shared View root and reserves profile identities through View services; it must never add user-facing personal library rows. Changing a populated View root is rejected; no relocation is implied. Convert to Managed, Merge into and the Import folder run as durable organising jobs (`folder_jobs`, ledger `folder_organise_log`); see `docs/architecture/library-model-and-intake.md`. Setup has six steps (Name your server, Your account, Libraries, Language, Enhance your library, All set); the server check runs silently unless a Required tool is missing, when it lists "A few things to install" with Check again and Continue (never blocks). Tuvima's own library folder is created under `TUVIMA_DATA_DIR` (shipped config uses the `{TuvimaData}` placeholder).

Existing-file protection applies to managed originals through the shared mutation gate. Incoming staging files and destination use remain eligible; existing-library sources remain read-only. Naming and duplicate policies must be enforced by ingestion, with unique-name filename collisions preserving existing files.

### Readable View storage

Libraries now includes All/Read/Watch/Listen/View filters, a single View summary row, and a dedicated View settings page. Scope changes filter the mounted list without repeated library loads or filesystem probes. Add folder belongs inside Folders and saves immediately; detachment asks for confirmation and keeps files. Names and custom templates use focused Apply dialogs, while other library settings save individually with conflict detection. There is no page-wide Save. Breadcrumbs remain shell-owned and retain the selected scope.

New Personal Spaces use `View/Profiles/<stable-readable-label>/Timeline` and named managed folders beneath `Folders`. Persisted label reservations survive profile deletion so another profile cannot silently inherit the former folder. Display-name changes do not rename storage. `View/Shared` is the server-owned Shared Library beside Profiles. Existing obsolete View source state fails closed rather than moving originals or using compatibility paths. View source additions queue reconciliation in its dedicated hosted worker.

## Setup, Operations and View storage

Setup offers an optional 4–12 digit profile PIN alongside the administrator password, live password validation and reveal controls, and readable recovery codes. Media locations can be explicitly deferred and configured later through Libraries. Reloading setup must render fetched stage data after asynchronous initialization.

Operations and the navbar consume the same Engine batch progress definition, including outstanding identity and enrichment work. Live media selection includes identity-job updates, ranks active/recent groups before limiting, and preserves server order on refresh. Comic covers remain issue-scoped. TV and comic ingestion counts never display a provider denominator. Active batches stay above server-filtered historical batches; every historical action is Open batch. Review counts remain visible throughout Settings. Processing details is removed.

View uses a common library-detail header and Folders, Organization, File Handling, Sharing & Access, and Advanced Settings sections. The configurable View library root contains Shared and Profiles; Shared Library is an authorization scope distinct from the physical base. Logical personal-space/source registration never creates directories. Uploads and accepted transfers create destination folders only at actual write time. Merely visiting View or adding a profile must leave unused storage empty.

The install banner records dismissal in browser/site local storage. Subsequent install events and navigation respect it. Settings > Overview > Apps offers an independent manual installation action ("Install on this device").

_Note 2026-10-19: Operations pages now redirect; review lives in Settings → Server → Library and Ingestion → Recently Added (Needs Review), opened from "View all"; Recently Added is no longer a menu row._

## UI action alignment completion rule

Before finishing any UI work, verify action alignment at desktop, tablet, and mobile breakpoints. Buttons beside labeled form fields must align with the input control rather than the label or helper text. Buttons in list or detail rows must remain in the row's intended action column and must not auto-flow onto a separate row that creates avoidable empty space. A separate full-width action row is acceptable only when it is an intentional mobile layout. Do not report UI work complete until these alignments have been checked in the rendered layout or enforced with explicit grid/flex placement and regression coverage.

## Editor relationship and identity model

- Keep three models distinct: structural media placement (Series/parent/position), authored-container canonical title and order, and the knowledge graph (Universe, fictional entities, and sourced/qualified links). Stage 2 establishes canonical Work identity; bounded Stage 3 enriches grounded graph facts and artwork without silently rewriting authored containers or structural placement.
- The normal media editor uses one stable parent header with adjacent child selectors and a single highlighted editing target. Keep audiobook chapter-title overrides under Chapters. Match information belongs at the top of Details, never in header/selector badges; inherited canonical identity needs no status label. Retail Match owns provider identity plus proposed structural placement; preview previous/target paths before Apply. Do not expose Change Type in the normal editor or Review Queue.
- Universe/entity editing remains content within `SharedMediaEditorShell`, opened from the authorized Edit actions on the Universe Explore surface or its selected entity. Media detail pages link actual accessible Universe relationships; media editors contain no Universe shortcut or mode switch. Keep the Universe header stable and preserve the existing dirty-state guard for category/entity navigation.
- Render graph sections from server readable capabilities, including friendly Organization Members and Event Participants projections; keep full Relationships separate. Category/sibling selectors are anchored, viewport-bounded, searchable popovers with keyboard/focus behavior, while the category rail remains one horizontally scrollable row with explicit overflow controls.
- Managed EntityAsset artwork belongs to its Universe/entity owner and must not be inferred from a work or parent container. Appearance and relationship facts remain work-scoped and preserve role, context, anchor, temporal, provenance, and spoiler qualifiers. Real-world dates, in-universe narrative time, structural sequence order, and editor History are different data and presentation concepts.

## Shelf editing and owner artwork

Structural shelves edit their own parent Work through the shared editor. The Engine declares supported artwork slots and automatic-group presentation; do not duplicate media/scope capability switches in the Dashboard. Shelves preview up to four ordered owned covers, accept a shelf-owned custom cover, and can restore the automatic stack. Title and description overrides remain local presentation choices and must agree across editor, detail, and browse. Group routes and membership resolve by structural owner identity rather than a customized display title. Season selectors use managed SeasonPoster artwork and episodes use EpisodeStill; absent owner artwork remains a placeholder.

Editor Details names the active editing target independently of the stable parent header and shows compact Retail/Canonical match checks. TV matching is exposed only on episode targets, never series or seasons; the inherited canonical record remains visible for context without an editor jump to the series. Match searches retain the current identity until the user explicitly applies or overrides a result. Logo previews preserve the complete image at its natural ratio.

## Home, primary actions, playback rails and lyrics

Home previews reserve resting shelf geometry before expansion and use a bounded cinematic layout for movies/TV, with artwork and logo only; Home Continue retains episode identity and truthful progress. Home Watch discovery cards expand in-row to a landscape background at the resting cover height, shifting neighboring cards horizontally; Watch pages and episode lists highlight only; non-Watch cards remain outline-only. Shelf arrows have dedicated rails and minimum 44px targets. Continue groups have natural whole-card widths, 32px dividers/gaps and stack below 1280px. Shared book foreground height, perspective, page/spine geometry and proportional shadow live in `HeroBackdrop`; detail stages retain 95svh. Known small Home sources use backdrop framing; unknown dimensions use the conservative fallback.

The shared primary action uses the larger default requested by the product owner for Play, Read, Listen, Resume and Continue: 22rem wide and 4.6rem high, capped to the available phone width. Plain actions use larger text/icons; continued actions show a second percentage line and embedded progress. Adjacent My List, Rate and More are 56px circles; Shuffle remains a circle beside album Play. Restart belongs in More and preserves existing query/fragment state while starting at position zero. Progress includes a visible watched/read/listened percentage and a slim strip inside the primary action. Rate choices are Likes/Dislikes; only a song's Love/heart adds it to Favorites. My List is saved-item state, not a rating. Song menus operate on the selected song and use guarded active-profile/snapshot identity.

Home refreshes are coalesced and profile-bounded: progress is limited to once per minute, library-state changes to once per 15 seconds, new media to a two-second debounce, pause to five seconds, and dismissal/stop to immediate refresh. Transport ticks do not rebuild shelves or Recently Added. Background Recently Added refresh keeps the mounted cards visible and deduplicates paging.

Audio, phone/popout and Watch/View video use `PlaybackSeekRail`: total duration sits at the right end and toggles to remaining time; elapsed time stays visible at the left end and also appears in the focused/hovered/dragged seek bubble. Chapter name/time shares that bubble. Speed uses the bounded 0.5–3.0 slider in 0.05 steps with reset to 1×; stored rate precision is retained until changed. Sleep retains its existing select and authoritative deadline.

Watch/View video use shared bottom chrome, a Back affordance, control hover/focus holds, and one subtitle-positioning owner in `playback-chrome.js`. Active cues sit 12px above the seek rail while controls show and restore authored settings when hidden or detached. Up Next uses owned eligibility and separates Up Next/Episodes/Chapters; no provider-only episode is promoted. Native cue animation is browser-dependent and is not promised.

Lyrics content omits the former version/provider toolbar. Enhanced LRC strips inline timestamps into independently timed words, uses native audio time, and keeps client highlighting separate from server render updates. Only explicit instrumental markers, explicit word-end gaps, known intro/outro boundaries produce three-dot sections; ordinary long line spacing never invents a musical break. Three configurable durations live in `config/ui/playback-client.json`. Unknown duration prefers timed lyrics. Authorized empty state opens Manage lyrics in the existing editor Details tab. LRCLIB attribution appears for LRCLIB tracks.

Editor artwork previews are padded and contained. Open full size opens the authenticated original image in a new tab and removes rendition-size query parameters. Canonical entity artwork lookup avoids an expensive full-detail composition when a cover claim exists. No ten-minute cache was added because invalidation ownership is not yet established.

## Continue cards, hero actions and player colours

The latest product-owner reference supersedes earlier hover and smaller-button guidance. All Continue Watching movies and episodes keep their still and exact resting dimensions; hover/focus reveals a compact in-place identity overlay without JavaScript expansion. Books, comics, music, audiobooks, and other non-Watch media use only a subtle outline and register no preview mouse events. Only Home enables changing previews through the explicit IsHomeSurface parameter. Its movie/show discovery cards widen within the row toward 16:9, shifting neighbors horizontally while retaining the artwork height and showing only the full landscape artwork and logo with a slight highlight. Never mount a floating preview or use a viewport-based 900–1280px expansion. Watch pages, browse grids, detail episode lists, and every other surface only highlight cover art and episode stills. Home and Watch Continue Watching keep their landscape episode still and exact dimensions, with S{n} E{n} · Episode title above the show name. They show no percentage text; the accessible progress label remains, and hover/focus makes the artwork-edge strip subtly thicker and brighter. Continue cover art for books and audio only highlights. Episode overlays show season/episode number and the canonical episode name, with the show title as a persistent caption underneath. Continue projection prefers asset/work episode_title over a generic show title. Media/group cards still have one detail link; group artwork composition remains fixed.

All hero Play/Read/Listen/Continue actions use the same larger 22rem by 4.6rem button, bounded to the available width on phones. A continued action includes the applicable percent watched/read/listened; a plain action centers larger text and icon. Song menus use intrinsic content width, the same shared item-height/typography/padding tokens as detail menus, and the shared circular song action row in full Now Playing. Obsolete overflow-menu geometry overrides are removed. The audio seek rail and end time sit inside the dock, centered together above the controls; desktop reserves 104px and phone 88px plus safe area. Playback ownership and guarded personal-action identities remain unchanged.

Home discovery hover is artwork-and-logo only. Preserve the complete landscape image without synopsis or fact overlays. Freeze artwork and shelf height through opening and closing so only neighbors in the same row shift horizontally. Home Continue scrollers reserve 8px on each side for the first and last card highlight; packed group widths include these gutters.

Continue groups show at least two whole cards when two are available on desktop and tablet. Desktop groups wrap rather than shrinking below two; narrower non-mobile layouts give each group a full row. Mobile may show one card. Preserve natural artwork dimensions and the 8px highlight gutters when allocating group widths.

Movie/TV compact card captions omit the generic subtitle (including series position such as Movie 1 in a series). Retain the title and canonical year; other media retain author/artist subtitles and Continue retains truthful progress context.

Continue Watching, Reading, and Listening share one artwork height (80% of the Home shelf artwork height). Landscape Watch cards retain 16:9 and gain horizontal width. On narrow non-mobile full-width rows, cap the shared height to fit two landscape cards plus rails, gap, and highlight gutters; all media scale together so height parity and the two-card minimum both hold.

The app primary purple is #8852FC, with #A46FFF hover and matching RGB soft/glow tokens. Player surfaces use #0F131E background, #121623 surface, #30364A tracks, #F7F7FA text and #9EA4BC secondary text. Dock timeline shows elapsed time on the left and toggleable total/remaining time on the right. Music dock exposes the existing song rating control beside Favorite/More; Likes remain distinct from Favorites. Lyrics uses a quotation speech-bubble glyph. An open rating control and its hero action row rise above sibling controls.

Sign-in rejects stale, missing, mismatched, or unreadable antiforgery tokens with HTTP 400 and a freshly tokenized sign-in form. The expired-form notice requests a new submission; rejected credentials never reach the Engine. A local return destination is preserved, while credential fields are cleared. Other security actions retain their existing validation.

Playback song rating, Favorite and More use the same outline 22px glyph sizing and circular 44px target. Song overflow omits Like/Dislike when the direct Rate control is rendered; surfaces without a direct Rate control retain those menu actions. Detail rating circles keep their existing appearance.

Rate hover, focus, and selected-choice highlights are circular and contained within their 44px targets and padded choice pill. Playback Rate keeps the shared circular border at rest; its open/hover highlight is circular, matching the choice buttons.

## Player panels and shared controls

The phone full player and the popout share `PlaybackFullPlayer` as one screen with no bottom sheets. The default view is artwork, followed by progress, transport, mode buttons, and volume.

- **Modes.** Lyrics and Queue (music), or Chapters, Bookmarks, and History (audiobooks), replace the artwork with a `PlaybackPanelCard`. Selecting the active mode returns to artwork.
- **Controls.** Lyrics and Queue triggers are icon-only, with tooltips and accessible names. Volume is shown on every surface, including phones. Audiobook Speed and Sleep live in More.
- **Dock cards.** The Lyrics card and the combined Queue & History card preview on hover and stay open when pinned. Only one dock card is open at a time.
- **Queue.** Upcoming occurrences reorder by drag, keyboard, or row menu through the guarded `reorder-upcoming` command, using the expected queue revision and saved occurrence IDs. The current item never moves.
- **History.** `clear-history` clears only the player's recent music list, after confirmation.
- **Output.** The main audio owner applies output selection through `setSinkId`. The control appears only where named output devices are already permitted, and it never requests microphone access.
- **Quality and Media Session.** A Lossless or Hi-Res Lossless badge requires known lossless direct delivery. Media Session metadata and actions bind to the main owner.
- **Identity links.** Player identity links use `detailOrigin.fresh`, so the destination opens at the top, including the detail shell's `.context-sidebar-shell__main` scroller. Ordinary Back restoration is preserved.
- **Shared controls.** Dashboard controls use first-party `Components/Shared/App*` primitives, including `AppSelect`/`AppTypedSelect`, `AppTooltip`, `AppRangeSlider`, `AppProgressBar`, and `AppSpinner`. Their native HTML, SVG, CSS and JavaScript own sizing, appearance, focus and expanded/selected semantics. Pages use the shared components rather than reimplementing their controls.
- **Intrinsic selectors.** Selectors such as the series selector size to the selected label within the available width, then ellipsize with a full-label tooltip.
- **Follow-up.** Shared controls and Release CSS minification now use first-party ownership. Bundle acceptance still requires measured Release assets and paired visual evidence; broader per-render interop optimization remains separate.

## Watch/View video, dock cards and Continue Watching

Watch and View video remove Back and expose exactly one Close action at the top right. Watch Close opens the movie detail or the show detail scoped to the current owned episode, including when Close is pressed while episode metadata is loading. View Close invokes its hosting listing callback and preserves that listing's scroll position. View Info is a visibly selected toggle with aria-pressed and Show/Hide information tooltips; its panel has no separate Close button. Click-pinned desktop Lyrics and Queue & History cards persist through outside clicks, focus loss and page navigation; their own trigger, Escape from inside the card, or player shutdown closes them. Hover previews still dismiss on leave and opening another dock card replaces the previous one. Ordinary menus retain ordinary dismissal. The top-bar My List action uses the ringed-planet glyph. Owned audiobook totals count distinct works across chapter files and editions; music remains file-based and physical ingestion totals are labelled Files. Vertical wheel scrolling remains native over cards; horizontal shelf motion is guarded only during active desktop Home expansion.

Continue Watching on Home and Watch uses episode stills, a main S{n} E{n} · Episode title line and a show-name caption. Percentage text is removed while accessible progress remains. Hover/focus increases the artwork-edge progress strip by 2px with the shared brighter accent, without changing card geometry. TV episode rails start at the first owned card with an 8px highlight gutter and a disabled/hidden previous arrow; Season and Jump to share one controls row, the redundant watched-status line is removed, and Show missing belongs beside the owned summary. Detail hero utility circles use the same 24-unit outline playback glyph family and 56px targets. Native switches retain the pre-migration track/thumb geometry, thumb elevation and state colours through the shared controls layer.
