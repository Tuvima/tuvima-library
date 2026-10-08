# Editor matching by the identity being edited

Status: TV and music picker changes and the episode placement correction are implemented in this checkout; live visual review and remaining provider catalog work are scoped below.

## Product walkthrough

1. **Start with the file the person owns.** An episode file, music track, or comic issue is the item being corrected. The show, season, album, and run organize those files and supply shared artwork. Selecting a different parent must move only the selected owned item; it must never rematch or move sibling files. Parent matches and artwork remain separately editable. Maps to packages A, B, and F.
2. **Pick the identity level.** An episode editor can match the show, its season, or that episode. Each choice searches and compares the name at that level. Matching the show lists shows; matching the season lists seasons of the matched show; matching the episode lists episodes in its current season. No result changes an owned file until it is applied. Maps to packages A and B.
3. **Correct a wrongly placed episode.** From the episode picker, choose **Move to another season or show**. In Library placement, select a local or TheTVDB show, browse that show's seasons, and preview the destination before confirming. The editor then returns to episode results in the new season so the exact episode can be chosen. For example, a Solo Leveling file in Season 1 can move to Season 2 or a different show without disturbing the other Season 1 episodes. Maps to package F.
4. **Choose directly from a short list.** TV uses TheTVDB's default order without an order selector. The ordinary episode list uses the current season number, including Season 0 for Specials. Each row has a thumbnail, title, number, and date when available. The picker omits repeated “Matched show” context already visible above it. Maps to packages A and C.
5. **Correct music at the right level.** An album result can show its provider track list. Selecting a track matches that owned song to the exact release track after the Engine verifies album membership. A song can move to a different album; the last song leaving an album does not silently delete the old album or its artwork. Maps to packages B and E.
6. **Keep other media honest.** Comics can match a run and an issue separately; issue results must belong to the confirmed run. Books and audiobooks keep their separate series and work scopes, using direct work search because their current providers do not expose a dependable series-to-work picker. The editor does not invent child choices. Maps to package D.
7. **Show a consistent artwork position.** Retail, local parent, and TV season/episode result rows have compact previews. When no image is available, a fixed placeholder keeps results aligned. Canonical Wikidata identity results use a placeholder because that search currently supplies no candidate art. Maps to packages C and F.

The familiar Match & Identity fields, comparison, and explicit Apply step remain. A season with no TheTVDB match can still have an individually matched episode; matching the season separately corrects its poster and metadata. Moving an episode and selecting its exact provider episode are explicit steps so the destination can be reviewed before the file's match changes. Empty former parents are retained for a separate cleanup process.

**Observable acceptance:** Solo Leveling Season 2 appears in the season list when TheTVDB supplies it. An episode owned under Season 2 opens Season 2 candidates, not Specials or Season 1, and can be selected before the parent season is matched. A misplaced episode can be moved to another season or show, then matched there, while its file and sibling episodes stay intact. A result from a different show or season is rejected on apply. TV rows are aligned and inset within the picker. Every result has an artwork slot and no result tile fetches a provider original when a smaller rendition is available. A song can be picked from a selected album's track list and a forged track/album pair is rejected.

## Root cause and API mechanics

- The Dashboard disabled episode candidates unless the parent season had `tvdb_season_id`; the Engine repeated that restriction. The Engine also fell back to the first provider season when the owned season number was absent, which commonly meant Season 0. Both layers had to change.
- TV series search uses a series ID. The scoped season endpoint enumerates seasons attached to that series in its default order; the episode endpoint fetches the series' paged default-order episodes and limits them to the owned season. Apply re-fetches the selected record and verifies series, order, season, and identity revision. A confirmed season ID must agree with the owned season before an episode can be applied.
- TheTVDB's alternate order endpoints remain as compatibility code, but the normal editor no longer exposes or sends alternate order choices. Supporting an alternate order later would require an explicit product use case and an ingestion-wide identity migration, not an incidental selector in Match & Identity.
- Generic retail matching already has scope-specific target policies and hierarchy preview/apply. MusicBrainz release membership and Apple album-track membership must be verified by the Engine; the track list is only the way to make an exact selection.
- The provider candidate preview path accepts allow-listed image hosts, chooses a compact provider rendition where one exists, bounds the decoded image to 180 pixels, and returns a preview. TV season/episode images use the scoped 320-pixel preview route. Missing provider art is shown as unavailable rather than borrowing local art from a potentially different match.
- The separate membership service already moves the owned leaf and retains its edition and file. The correction flow exposes it beside episode matching, permits a destination show lookup, and requests destination seasons read-only. A newly created TheTVDB show writes its provider ID as both a bridge and canonical value so subsequent season and episode queries resolve the correct show.

## Technical work packages

### A. TV scope and default-order picker — implemented

- Resolve the episode's season from the parent season canonical value, then episode value or owned scope label. Never default to the first provider season.
- Return all default-order season candidates for Season scope and only the owned season's episodes for Episode scope. Keep Season 0 explicit when owned.
- Allow episode apply without a confirmed season match; verify its provider season against the owned season and reject a conflicting confirmed parent ID. Keep season and episode writes separate.
- Remove normal editor order controls, manual season-number selection, disabled-candidate gate, repeated parent context, and verbose selected-result copy.

### B. Music child selection and membership — implemented

- Use release-specific provider track IDs and positions from the selected album detail. Reuse hierarchy preview/apply for the exact child.
- Verify MusicBrainz recording membership in the selected release and Apple track membership in the selected collection during apply.
- Preserve empty source albums and artwork for the separate cleanup process.

### C. Result artwork and layout — implemented, visual review pending

- Generate bounded previews for configured TVDB, TMDB, Apple, MusicBrainz, and Comic Vine retail sources and render only those preview URLs in compact result cards.
- Use fixed art boxes and placeholders for missing retail, TV scoped, and canonical identity images. Show one album cover for a nested track catalog rather than repeating it on every child row.
- Review desktop and mobile geometry with configured providers and real missing-art responses before calling the visual acceptance complete.

### D. Further provider catalogs — future provider work

- Comic Vine's current integration searches issues and validates their volume, but does not expose a run-to-issue catalog endpoint. Add one only if its API can supply stable issue IDs, ordering, artwork, and pagination without weakening volume checks.
- Current book and audiobook providers do not supply a dependable series-to-work child catalog. Preserve direct title/identifier matching; add a child picker only with provider-backed membership evidence.

### E. Empty-parent cleanup — separate reviewed process

- Preview albums or other parents left empty after a move, including references from profiles, collections, preferences, and user-selected artwork. Offer an explicit cleanup action only after those dependencies are checked. Matching itself preserves the parent.

### F. Correct an owned episode's placement — implemented, live review pending

- Add a clear route from episode matching to Library placement. Search local or TheTVDB shows, browse the selected show's default-order seasons, and preview the move before confirmation.
- Move the selected episode work only. Retain its edition, file, and asset; leave sibling episodes and the former season/show in place. Reject occupied destination episode numbers.
- Record TheTVDB identity on a newly selected show and return to the episode picker in the destination season after confirmation. Match the exact episode there; keep parent matching and parent artwork editable at their own scopes.
- Show compact local/provider artwork or a stable placeholder on destination suggestions.

## Verification

- Focused Engine tests cover default Season 2 selection, season membership, and compact/allow-listed retail image previews.
- Focused Dashboard tests cover TV match target selection and selectable episode results. Engine and Dashboard test projects build with these changes.
- A focused Engine test covers moving one owned episode to a new TheTVDB show and season while retaining its asset and recording a usable show ID.
- Live inspection against a configured Solo Leveling library and desktop/mobile screenshot review remain to be done; provider artwork depends on what TheTVDB and other providers actually return.

## Plain-English completion summary

The owned file is the center of correction. An episode can be moved to the right show and season, then matched to the exact episode there; matching a parent remains available to correct shared artwork and information. Music tracks follow the same ownership rule. Result lists now have consistent artwork spaces, and unsupported provider child catalogs remain explicit future work.
