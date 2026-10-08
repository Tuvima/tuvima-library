# Editor header and shelf editing plan

Status: implemented; automated validation recorded below. Signed-in visual acceptance remains pending.

## Intended experience

Use the supplied image as a visual reference for one integrated header. The written request and subsequent clarification govern behavior: keep the parent identity stable, place child selectors beside it, highlight the actual editing target, and remove identity-status badges from the header and selectors. Match information belongs at the top of the editor's Details section. Universe navigation starts from the media detail page, with editing launched on the Universe's own surface; remove the media-editor Universe shortcut entirely. This supersedes the earlier sidebar and in-editor relationship-row proposals. Copy and values in the image are illustrative, not requirements to reproduce.

The editor remains the existing modal on top of the detail page. Selecting a child changes the editable fields and artwork owner, not the identity of the parent shown in the header. Structural shelves, curated collections, and Universe entities retain their separate ownership rules.

## Findings before implementation

- `SharedMediaEditorShell.razor` renders a main header, a separate surface-switch action, and a separate `EditorContextNavigator`. The navigator repeats the root and adds a visible “Edit context” heading.
- `HeaderTitle`, `HeaderSubtitle`, `HeaderKicker`, and header artwork in `SharedMediaEditorShell.razor.cs` follow `ActiveScope`. Child selection therefore replaces the parent identity.
- `GetContextArtworkUrl` uses a node's representative media file, or the first descendant file, through `/stream/{id}/cover`. It does not select the season's `SeasonPoster` or episode's `EpisodeStill` artwork.
- Selector cards and menu entries have independent minimum widths. The shared overflow popover also has a global 16rem maximum width. This conflicts with the larger editor menu content and anchors the menu to the arrow control rather than the full card.
- `BuildEditorScopes` in `MetadataEndpoints.cs` advertises editable artwork for book/comic/audiobook `series` scopes and creates leaf scopes named `book`, `issue`, and `audiobook`. Both `ResolveArtworkSlots` in the Dashboard and `ArtworkScopeService.GetScopedArtworkSlots` omit these scope names, accepting only `item` for those media types. The Dashboard interprets no supported slots as “This owner does not manage artwork.” This is a confirmed code defect, although the reported Harry Potter instance has not been inspected at runtime.
- Movie scope construction currently exposes `item`, whereas the navigator can identify `film_series` and map it to `series`. This needs to be brought into the same capability contract.
- `CollectionEditorShell` already offers an automatic stack preview through `MediaArtworkGroupPreview`, custom artwork upload, and restoration of automatic artwork. Structural shelf editing should reuse this presentation, not become a curated collection editor with membership rules.
- Detail composition and editor launch contain different paths for canonical Works, collections, and system-view groups. Some collection edit eligibility checks enumerate TV and music containers but omit other structural series. Audit all entry points instead of fixing only one shelf route.
- The normal detail launcher already omits `SharedEntityTarget` when no Universe relationship QID is present. Preserve that behavior and verify that the target is resolvable and authorized; do not manufacture a Universe from the media item's own QID.

## Proposed interaction

### One parent header

- Render one parent artwork/title/facts block on the left and only the applicable child selectors on the right. Remove the repeated root card, visible “Edit context” heading, intervening header row, and all retail/canonical match badges from the header and selectors.
- Keep the parent block anchored to the real hierarchy root, including when the editor opens directly on an episode or another child. The header describes identity and navigation; the selected target's source information appears in Details.
- Make the parent identity an accessible action to return to editing the parent. Give it the same purple active treatment when the parent is selected; selecting a child moves that treatment to the corresponding selector.
- Separate parent-header state, selected editor target, and original launch target. Dirty-state prompts must name the actual edited item, even though the header remains the parent.
- A pending child selection does not change the active highlight or fields until Save/Discard/Stay has resolved and the new target loads successfully. Failed loads keep the previous target usable and explain the failure.
- Desktop: one aligned header row. Tablet: preserve parent identity and allow selectors to occupy the next line within the same header. Mobile: stack controls at full width in the same header; do not introduce a duplicate identity row. Reserve explicit space for Close.

### Clean selectors

- Keep the control labels “Season” and “Episode” above their selected values. Remove the repeated type labels/tags inside menu options. An option can read “Season 2 · 1 owned episode” or “E1 · Episode title,” with one thumbnail and a selected checkmark.
- Parent selections remain visible as context, but only the actual edited level receives the active border. Changing season clears any episode from the previous season and edits the newly selected season.
- Display placeholders without fabricated imagery before a child is selected. A missing season poster or episode still gets a neutral placeholder, not a show poster or sibling's image.
- Use managed season posters in portrait format and episode stills in landscape format. Preserve square album and portrait book/comic artwork elsewhere; music tracks remain text-first.
- Match the open menu to the entire selector's measured width, including its arrow segment. Bound it to the viewport and give it internal vertical scrolling. Put sizing behavior in the shared control layer with an opt-in setting so ordinary overflow menus retain their sizing.
- Retain search for long lists, and provide keyboard selection, Escape, focus restoration, selected state, and an empty search result. Do not silently hide items behind the current 50-option rendering limit: searching or paging must make every eligible child reachable.

### Match information in Details

- Replace the persistent generic source-managed banner with a compact, neutral source summary at the top of the editor's Details content. Show only applicable, independently established matches for the selected target, for example “Sources: TMDB · Matched / Wikidata · Linked,” with one “Review matches” action opening the existing Match & Identity section.
- Remove “Canonical inherited” and “Retail inherited” badges entirely. A child with no independently editable canonical identity does not need a redundant canonical status row. Keep ownership/inheritance information in the underlying capability model and explain the owning parent only where an action requires switching to it.
- Do not present a parent's match as a child's exact match. If an owned episode requires its own retail match, show that actionable condition in its Details summary. Expected inheritance alone is not a warning or an unmatched condition.
- Use actual provider names where available. Expose identifiers, provenance, and match management in Match & Identity, not as permanently visible header content. Normal success states use quiet text; a real problem can use an inline attention state with a relevant action.
- Omit the summary when matching is not applicable, such as a manually maintained collection. Universe/entity Details use their own source facts, without implying they have a retail match. Review mode retains its specific issue banner.

### Media and Universe consistency

| Context | Stable header | Selectors / behavior |
| --- | --- | --- |
| TV | Show | Season, then owned episode |
| Movie series | Structural series | Movie |
| Book or audiobook series | Structural series | Owned title |
| Comic hierarchy | Actual structural series/run | Volume and issue only where those levels really exist |
| Music | Album, or an actual editable parent already modeled by the Engine | Track; inherited artwork opens its album owner |
| Standalone movie/book/audiobook | That title | No artificial hierarchy selectors; audiobook chapter editing stays under Chapters |
| Curated collection | Collection | Existing membership editor remains separate from structural placement |
| Universe workspace | Universe root | Existing category/entity navigation; highlight the selected entity and show its editing context within the workspace |

- Remove “Edit shared Universe” from the media editor entirely: no header, sidebar, Details-row, or overflow replacement. The editor's navigation stays focused on its current media hierarchy. Remove the unused media-to-Universe mode-switch plumbing while retaining standalone Universe/entity editing in `SharedMediaEditorShell`.
- On the media detail page, use the existing relationship presentation to link the named Universe, for example “Universe · Wizarding World.” This opens its consumer Universe/Explore surface. A reader can explore related characters and places without entering editing. Audit existing links before adding a new presentation so the same relationship is not repeated around the page.
- Place an authorized Edit action on the Universe's own surface, and an entity-specific Edit action where a character/place/entity's details are displayed. Each opens the existing shared editor on that owner. Users who already notice a mistake while exploring can correct it there without first opening a media editor.
- Recommended journey: media detail → named Universe → Universe or entity details → Edit → save/close back to the same Universe/entity surface. Preserve normal navigation back to the originating media detail. Closing a media editor first retains its normal unsaved-change guard; do not stack editor dialogs.
- Keep changing which Universe a work belongs to in the appropriate identity/relationship workflow. Editing the Universe's description/artwork/entities and correcting a work's association are separate operations.
- Do not put Universe into the season/episode hierarchy selector: it is a relationship shared across works and media, not a structural parent. Do not bury its only navigation entry in an unlabeled overflow menu.
- Within the Universe editor, retain Save/Discard/Stay protection for entity changes and normal close protection. There is no fabricated “Return to media editor” action. Identify the named Universe and show concise scope copy that its edits affect all linked titles, without adding a routine confirmation for opening it.
- Within Universe mode, retain existing category/entity capabilities, managed entity artwork ownership, and entity-specific fields. Keep the Universe root stable in the header while navigating its entities. Selected entity identity belongs in the workspace selector/content so the user always knows what will be saved.
- The media detail page omits the Universe link if no accessible Universe exists. Read access permits exploration; edit access separately permits Edit on the Universe/entity surface. Distinguish transient load failure from absence, and recheck authorization when opening or saving. If the model supplies multiple applicable Universes, show the actual named relationships rather than selecting the first silently.

## Shelf and series editing

### Correct owner and capabilities

Resolve the selected shelf to its authoritative structural Work or collection owner on the Engine. Never silently route shelf edits to a representative child. System-view IDs must resolve through the system-view mapping; local/provider-backed shelves must remain editable without a Wikidata QID.

Return explicit supported artwork slots and artwork presentation mode with the editor capability data. The Dashboard should render those capabilities rather than maintain another incomplete media/scope switch. Validate the same capabilities at write time. Include `series`, `book`, `audiobook`, `issue`, movie-series, TV, and music inheritance cases in regression coverage.

### Artwork

- Show the actual automatic representative artwork preview using `MediaArtworkGroupPreview`, with at most four owned images and their natural aspect ratios. Ordered shelves retain canonical sequence order; curated collections retain their established representative composition.
- Offer “Automatic artwork” and a container-owned custom cover override, with upload/replace and “Restore automatic artwork.” Changes affect the shelf, never its children's covers. A small preview of representative items can offer explicit navigation to edit a member's own artwork when that is the intended correction.
- Reuse existing managed asset storage, validation, preferred-variant selection, and history. Offer background/logo editing only where the destination surface actually consumes those slots. Preserve the neutral standard-collection hero policy.
- Apply the same artwork precedence to shelf cards, editor previews, and detail headers: explicit supported override, otherwise automatic composition. Verify existing custom-cover support before adding it to a surface; a saved override must not be ignored by its renderer.
- Empty artwork is still editable when the owner supports uploads. Permission-denied and inherited artwork are separate states with useful explanations.

### Details and descriptions

- Expose display title, description, applicable sorting, and local tags for the actual container. Preserve provider-sourced facts and structural identity/order separately.
- Use local presentation overrides for sourced shelves, with a clear “Customize” action and “Restore source value.” Use the collection's own editable description for a curated collection. A container without a sourced description can still receive a local description.
- Trace persistence and reading together: after saving, the same container's detail hero/Overview, editor, and applicable browse surfaces must read the override. Reopening the editor must show it. Editing a shelf description must leave member descriptions unchanged.
- Keep detail-page corrections in the shared modal launched from Edit; the underlying page stays mounted and refreshes after a successful save. Do not add a second independent description form on the detail page.

## Delivery sequence

1. **Owner and capability contract:** correct edit targets and permission projections, align valid scopes, declare artwork slots/presentation, and add managed artwork previews to navigator nodes. Resolve the description override owner and read path. Cover these with focused API/contract/persistence tests.
2. **Header and navigation:** introduce stable parent state; integrate child controls into the header; remove duplicate labels and status badges; implement full-card menu sizing and correct image ratios; add the editor Details source summary. Remove the media-editor Universe shortcut and mode-switch path. Audit media-detail Universe links and direct Universe/entity Edit entry points on Explore. Preserve target-switch guards and originating page state.
3. **Container editing:** reuse the automatic-artwork preview and controls in the shared media editor, enable supported overrides, and wire title/description saves through detail and browse composition. Keep structural membership and sequence semantics unchanged.
4. **Acceptance:** test representative TV, books, audiobooks, comics, movie series, music, curated collections, and Universe navigation; update product guidance to describe the implemented behavior.

## Completion criteria

- TV shows one parent identity when editing show, season, or episode. Exactly one editing target is highlighted; direct episode entry still shows the show header. Save/Discard/Stay works for media target switches and for entity switches within the Universe editor.
- Header, selector cards, and selector menus contain no match badges or inherited-status labels. Details shows only relevant target-scoped source information. Inherited canonical identity creates neither a redundant status nor an unmatched warning; missing episode-level retail matches remain actionable.
- Season options use their own posters and episode options their own stills. Missing art has a placeholder. The open menu and selector have equal width unless viewport bounds require clamping.
- Harry Potter is a representative acceptance example, not a special case: its shelf opens on the shelf owner, displays the automatic stack, accepts title/description/artwork changes, and refreshes its detail page. Another unrelated series passes the same test.
- Add/replace/remove artwork and description overrides persist across reopening; resetting restores automatic/source behavior. Child media remain unchanged. Local-only shelves work without canonical identities.
- No Universe navigation/edit shortcut remains inside the media editor. The media detail page links only accessible, existing Universes. Authorized Universe/entity Edit opens the requested owner and closes back to its originating Explore surface. Read-only exploration remains available without edit permission. Editing a Universe and changing a media item's Universe association remain distinct actions.
- Permission checks cover the container and the membership/artwork projected into previews. Unknown or stale owner IDs fail explicitly rather than editing a child or bypassing access restrictions.
- Capture before/after rendered desktop states at 1920×1080 and verify tablet, mobile, and lower-height layouts. Check Close, selectors, left-rail utilities, field lock/customize controls, focus outlines, and popover placement.
- Run focused ownership, artwork-scope, override persistence, navigation, and component tests; contract snapshots if changed; then the required solution restore, build, and tests. If the implementation changes artwork ingestion, validate with a fresh disposable ingest under the repository's source-protection rules.

## Product-owner summary

The editor shows one steady parent header and clearly mark the season, episode, or title being edited. Selectors use the right images and have room to read. Match information appears quietly in the editor's Details section, without repeated inherited badges. Users navigate from a media detail page to its Universe and edit the Universe or its entities there; the media editor stays focused on media. Shelves expose their own descriptions and artwork controls, so users can customize a series without accidentally changing one of its books or films.

## Implementation notes

Implemented the parent header, clean owner-artwork selectors, target-specific Details source summary, and direct Universe/entity edit launch from Explore. Engine artwork capabilities now cover structural series and actual leaf scope names. Shelf overrides are consumed in detail and browse, and system-view membership can resolve by root Work ID so a display-title edit cannot empty or rename the route. Audiobook series also receive their own editor target.

Regression coverage includes season posters versus episode stills, menus with more than 50 items and search selection, artwork capability aliases, stable shelf routes, custom shelf previews/restoration, and audiobook shelf ownership. Final validation results will be recorded below.

## Validation — September 20, 2026

- Solution restore and build succeeded; build reported zero warnings and errors.
- Latest complete solution test run: 3,806 passed, six failed, and 34 provider tests skipped. All 1,091 Dashboard tests passed. Editor-related API/component regressions passed, including shelf rename/membership, custom-artwork restoration, owned season/episode imagery, and audiobook owner selection.
- Remaining failures concern existing collection contract fixture drift (three snapshot tests and one legacy field-list test), the storage schema fixture missing collection audience columns, and the music integration-harness provider expectation. Only this change's editor contract additions were accepted in the wire/shape fixtures; unrelated fixture drift was left intact.
- An intermittent SQLite disposed-connection failure in a collection test passed on the subsequent full run.
- Responsive layout has explicit desktop/tablet/mobile flex placement and regression checks for Close space, selector sizing, and shared-menu width overrides. Rendered 1920×1080, tablet/mobile, and signed-in Harry Potter/TV/Universe workflows have **not** been verified: the local Dashboard browser is at its sign-in page, and no credentials were supplied. No authentication bypass or library data repair was performed. Explore retains its existing desktop/tablet availability.
- No ingestion or migration behavior changed, so no fresh ingest was required.

After final cleanup, the solution build passed again, followed by 230 focused Dashboard/guardrail tests and 60 focused API tests. `git diff --check` passed. A documentation-site build was unavailable because the local Python runtime does not have MkDocs installed.

### Header hover/alignment follow-up

Reproduced both reported regressions in a browser fixture using the rendered AppButton and EditorContextNavigator with production MudBlazor, app, token, and scoped styles. The global text-button hover background painted over the parent identity; the selector label inherited centered flex alignment. The parent hit target now stays transparent in hover/focus/active states, and selector contents align left inside equal-height cards. Stacked mobile selectors share a minimum height.

Before/after rendering was checked at 1920×1080, with responsive checks at 1024×768 and 390×844. At desktop/tablet, both selectors share the same top and height; artwork starts approximately 12px inside both cards. Parent artwork and title remain visible while the hit target is hovered. Mobile controls retain aligned edges without horizontal overflow. This validates the header component layout; authenticated editing and persistence acceptance remains separate.

Follow-up verification: restore/build passed; all 1,091 Dashboard tests passed. The full solution run retained the same six pre-existing failures listed above. The shorter desktop layout was also inspected at 1440×720.


### Target, matching, and logo follow-up

Details and Matching now name the active editor target, while the main parent header stays fixed. Compact Retail and Canonical checks replace source-status prose. Current match cards retain provider/identifier evidence, and search explains that applying a result overrides the current identity. TV series/season targets no longer expose Matching; episode matching retains its canonical parent context without an edit-parent shortcut. Current retail cards use the selected target's artwork.

The logo regression was reproduced with production styles: a 320px-wide frame forced an 80px height while its image needed 260px. The primary logo frame now grows with its contained image and reserves space for artwork actions. Browser verification uses an isolated component/style preview with sample content; it does not establish authenticated end-to-end library acceptance.


Follow-up validation: solution restore/build passed with no warnings. All 1,098 Dashboard tests pass. The full suite retained the six unrelated fixture/provider failures noted above; two timing-sensitive ingestion tests also failed under the full parallel run and passed when rerun together. The isolated preview was inspected at 1920×1080, 1024×768, 390×844, and 1440×720, including a live episode-to-season target-summary change and a complete tall-logo preview.
