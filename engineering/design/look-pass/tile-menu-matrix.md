# Tile "..." menu: matrix by type and by state (draft for Shy)

Status: Draft, not a spec yet. Written 2026-11-04 from Shy's note and the two reference pictures (a plain tile menu: Add to My List, More Info, then Love / Like / Not For Me; and a Continue Watching tile menu: Remove From Continue Watching, Resume, Restart, More Info).

## What changes from today
- The "..." becomes more visible (always shown on the cover, not only on hover; bigger target, clear contrast over any artwork).
- The menu gets icons, larger text and clear wording, grouped with thin dividers.
- The X on Continue tiles is removed. "Remove from Continue Watching" (or Reading, Listening) lives in the menu.
- "More info" becomes "<Type> Details": Movie Details, Show Details, Album Details, Book Details and so on.
- This replaces Home spec D8 ("only Restart, Edit, Delete") and the X from D3. Marked-as-watched stays removed, per Shy's 2 Nov note.

## Menu order (same everywhere, items missing for a type are simply left out)
1. **Primary action**: Resume, or Play / Read / Listen when not started.
2. **Restart**: only when started.
3. **My List**: Add to My List or Remove from My List.
4. **Details**: "<Type> Details".
5. (divider) **Your rating**: Like, Dislike (Watch and Read); Listen uses Add to Favorites.
6. (divider) **Remove from Continue ...**: only on Continue tiles.
7. (divider) **Edit** (desktop only, if allowed) and **Delete** (if allowed, red).

## Matrix by type (tile not in a Continue state)

| Tile | Play | My List | Details | Rating | Edit | Delete |
|---|---|---|---|---|---|---|
| Movie | Play | Add/Remove | Movie Details | Like / Dislike | yes | yes |
| TV show | Play next episode (Play if new) | Add/Remove | Show Details | yes | yes | yes |
| Episode | Play | Add/Remove | Episode Details | yes | yes | yes |
| Season | Play from first unwatched | Add/Remove show | Season Details | no | yes | yes |
| Book | Read | Add/Remove | Book Details | yes | yes | yes |
| Comic | Read | Add/Remove | Comic Details | yes | yes | yes |
| Audiobook | Listen | Add/Remove | Audiobook Details | yes | yes | yes |
| Album | Play album | Add to Favorites | Album Details | no | yes | yes |
| Song | Play | Add to Favorites | Song Details | no | yes | yes |
| Series (books, shows) | Play/Read next | Add/Remove | Series Details | no | yes | no (items are deleted one by one) |
| Universe | none | Add/Remove | Universe Details | no | Edit Universe | no |
| Collection / playlist | Play | Add/Remove | Collection Details | no | admins only | admins only |
| Person | none | none | Person Details | no | yes | no |
| Photo, video (View) | Open | none | Details | no | yes | yes |
| Extra | Play | none | Extra Details | no | yes | yes |
| Not matched file | Play | none | Details | no | "Match this item" | yes |

## Matrix for Continue tiles (started, in progress)

| Tile | Menu in order |
|---|---|
| Movie | Resume, Restart, Add/Remove My List, Movie Details, Remove from Continue Watching, Edit, Delete |
| Episode / show | Resume, Restart episode, Add/Remove My List, Show Details, Remove from Continue Watching, Edit, Delete |
| Book, comic | Resume reading, Restart, My List, Book Details, Remove from Continue Reading, Edit, Delete |
| Audiobook | Resume, Restart, My List, Audiobook Details, Remove from Continue Listening, Edit, Delete |
| Album, playlist | Resume, Start from the top, Add to Favorites, Album Details, Remove from Continue Listening |
| All songs / shuffle collage | Resume shuffle, Remove from Continue Listening |

Rules: "Remove from Continue ..." never deletes progress history you might want; starting the title again brings it back (Shy 2 Nov). The primary item always shows what it will do ("Resume at 42:10").

## Other states
- **New**: no change to the menu; the "New" badge stays one small badge.
- **Not started on a shelf**: no Resume or Restart.
- **Phone**: the same menu opens as a bottom sheet with larger rows. Edit is hidden (desktop only). **TV**: the menu opens on a long press of the select button, with the same order.
- **Kids profiles**: no Edit, no Delete, no rating changes that affect others.

## Open questions for Shy
1. DECIDED: Like / Dislike only (no Love).
2. DECIDED: Add to Favorites for Listen items.
3. DECIDED: also on the detail page "..." menu.

Plain English summary: every cover gets a clear, always-visible "..." menu with the same order everywhere: do the main thing, restart, add to My List, open the details page, rate, then edit and delete for those allowed. Continue tiles lose the X and gain "Remove from Continue ..." in that same menu.

Also decided (2026-11-04): context-menu font sizes across the whole app are too small; fixed in the look pass, not here.
