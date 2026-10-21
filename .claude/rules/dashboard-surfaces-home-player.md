---
paths:
  - "src/MediaEngine.Web/Components/Pages/**"
  - "src/MediaEngine.Web/Components/Cinematic/**"
  - "src/MediaEngine.Web/Components/MediaHub/**"
  - "src/MediaEngine.Web/Components/MediaTiles/**"
  - "src/MediaEngine.Web/Components/Watch/**"
  - "src/MediaEngine.Web/Components/Listen/**"
  - "src/MediaEngine.Web/Components/Navigation/**"
  - "src/MediaEngine.Web/Components/Layout/**"
  - "src/MediaEngine.Web/Components/View/**"
  - "src/MediaEngine.Web/Services/MediaTiles/**"
  - "src/MediaEngine.Web/Services/Playback/**"
  - "src/MediaEngine.Web/wwwroot/**"
---

# Dashboard rules: Home, Continue cards and Player

Current per-surface rules for the Dashboard, moved verbatim from AGENTS.md on 2026-10-17 (dated headings replaced by subject headings) and split by area on 2026-10-20 so only the relevant file loads. Public per-surface behaviour lives in `docs/product/presentation-rules.md`; where the two disagree, stop and ask.

## Home, primary actions, playback rails and lyrics

Home previews reserve resting shelf geometry before expansion and use a bounded cinematic layout for movies/TV, with artwork and logo only; Home Continue retains episode identity and truthful progress. Home Watch discovery cards expand in-row to a landscape background at the resting cover height, shifting neighboring cards horizontally (only when the item has a landscape background; otherwise the cover stays and only highlights); Watch pages and episode lists highlight only; non-Watch cards remain outline-only. Shelf arrows have dedicated rails and minimum 44px targets. Continue groups have natural whole-card widths, 32px dividers/gaps and stack below 1280px. Shared book foreground height, perspective, page/spine geometry and proportional shadow live in `HeroBackdrop`; detail stages retain 95svh. Known small Home sources use backdrop framing; unknown dimensions use the conservative fallback.

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

Watch and View video remove Back and expose exactly one Close action at the top right. Watch Close opens the movie detail or the show detail scoped to the current owned episode, including when Close is pressed while episode metadata is loading. View Close invokes its hosting listing callback and preserves that listing's scroll position. View Info is a visibly selected toggle with aria-pressed and Show/Hide information tooltips; its panel has no separate Close button. Click-pinned desktop Lyrics and Queue & History cards persist through outside clicks, focus loss and page navigation; their own trigger, Escape from inside the card, or player shutdown closes them. Hover previews still dismiss on leave and opening another dock card replaces the previous one. Ordinary menus retain ordinary dismissal. My List uses a heart everywhere, outlined until saved and filled once saved (see `docs/product/presentation-rules.md`); song Favorites keep their own heart. Owned audiobook totals count distinct works across chapter files and editions; music remains file-based and physical ingestion totals are labelled Files. Vertical wheel scrolling remains native over cards; horizontal shelf motion is guarded only during active desktop Home expansion.

Continue Watching on Home and Watch uses episode stills, a main S{n} E{n} · Episode title line and a show-name caption. Percentage text is removed while accessible progress remains. Hover/focus increases the artwork-edge progress strip by 2px with the shared brighter accent, without changing card geometry. TV episode rails start at the first owned card with an 8px highlight gutter and a disabled/hidden previous arrow; Season and Jump to share one controls row, the redundant watched-status line is removed, and Show missing belongs beside the owned summary. Detail hero utility circles use the same 24-unit outline playback glyph family and 56px targets. Native switches retain the pre-migration track/thumb geometry, thumb elevation and state colours through the shared controls layer.
