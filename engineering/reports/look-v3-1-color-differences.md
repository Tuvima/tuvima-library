# Look v3.1 color sweep: places that may look different

Look v3.1 D7 moves every hand-typed color onto the shared set. Most moves are exact. This list holds the ones that are not one to one, by screen, so the visual review can look at them. Each part of the sweep appends its own section.

## Collections, the collection editor and the rule builder (WP8)

Collection editor window:

- The workspace background was a navy gradient; it is now the neutral dark surfaces (raised to base).
- Panels, section panels and the draft role buttons were translucent navy; they are now the base surface.
- The panel shadow uses the shared small shadow, which has a different shape.
- Wells, the rail and the footer use the named dark scrims (20%, 50% and 70% black) instead of translucent navy.
- The artwork choice and picker gradient ends on the base surface.
- The cover shadow is 40% black (was 38%).
- The header kicker uses the lighter purple accent (was a lavender at 80%).
- The header icon gradient, the history note, the picker tint and the focus ring are mixes of the purple accent (were violet and indigo tints): a slight change of hue.
- Dashed and choice borders use the purple accent border (were lavender at about 47%).
- Slate text tints are now the solid text colors: primary, secondary, muted or disabled. Text that was translucent is solid.
- Role button text is the primary text color (was pure white) and its small line is muted.
- Slate hairlines snap to the subtle or default border.

Collections page:

- The page backdrop's blue glow is the secondary accent at 9%, and its dark wash is the 20% black scrim.
- Filter chip panels were translucent navy and are now the opaque base surface.
- Chip text uses the lighter purple accent (was a lavender).
- Hairlines use the default border; one is a 20% mix of the muted text color.
- Body text is the secondary text color (was a slate at 72%).

"Not in library" tile: unchanged in look (an exact mix of the muted text color).

## The details page (WP11 part a)

Every hand-typed color in the details page styles (`Components/Details`) moved onto the shared set. Most moves are exact. This list holds the ones that are not one to one, by screen area, so the visual review can look at them.

### Everywhere on the page

- Text that was a translucent white or warm white (84% down to 36%) is now one of the four solid text colors: 78% and up is primary, 60% to 77% is secondary, 40% to 59% is muted, below 40% is disabled. Text is solid where it was translucent.
- Pure white and cream text and borders are the primary text color (a very slightly cooler white).
- Warm off-white tints (246, 240, 232) lose their warmth: washes and text are the neutral white washes and text colors.
- White washes and hairlines snap to the five named steps (3, 5, 8, 12, 18%). No move is more than 2 points.
- White hairlines stronger than 18% (24% to 44%) are a mix of the primary text color at the same strength.
- Dark dims snap to the named black scrims (20 to 70%). Eleven moves are 3 to 6 points off: the hero overlay stops at 16, 34, 56 and 66%, and a few shadows at 25, 35, 36, 44, 45 and 55%.
- Very dark navy and near-black fills that were 82% or more opaque are now a mix of the page background color at the same strength (a touch lighter than the old near-black).
- Hand-typed fallbacks inside `var(--tl-detail-primary, ...)` and similar now fall back to a shared color; the artwork color still wins whenever it is set.

### Hero

- Backdrop fade-out masks use the viewer black at the same strengths; they only shape transparency, so nothing changes.
- The bottom overlay on cover-style heroes ends on the page background color (was near-black navy) at the same strength.
- The vinyl and book-page illustrations lose their warm tint: the page edges are the primary, secondary and muted text colors, so the stripe contrast is a little lower. The vinyl discs are the page background color.
- The brand line on the hero is the primary text color (was warm white at 92%).
- Genre overflow glow and the lane-count pill text use the purple accent (the pill text is the lighter purple). Lavender text on the genre pill is now the primary text color.
- The "watch" capability pills are a mix of the page background color at 78% and 82% (were slightly lighter charcoal).

### Page frame, route state and avatars

- The page and route-state backdrops: the purple glow uses the darker purple accent at 26%, and the amber glow is the warning color at 18% (was a muted brown-gold).
- The stage navigation bar gradient ends on the page background color at 90 to 98%.
- The "owned placeholder" art glow (blue-gray) is the secondary accent at 14%, and its base is the page background color.
- Person avatar tints keep their per-person hue; see the exceptions in the coordinator note.

### Overview, Details tab and credits

- Credit group headings that were gold (`#efc77a`) are the lighter gold accent.
- Credit text and borders follow the common text and wash rules above. One glass panel is the 60% scrim (was charcoal at 62%).

### Series panel, entries and cards

- All blues move to the secondary accent (a sky blue). Series eyebrow, heading, links and the owned-count were periwinkle blues (`#87a9ff`, `#75a8ff`, `#6f9bff`, `#b7caff`): they are now one sky blue, and hover and focus no longer differ in lightness.
- Node borders, the timeline glow and the connector line are the secondary accent at the old strengths (72%, 85%, 42%, 22%, 34%). The "current" node gradient is secondary accent to purple accent (was bright blue to indigo).
- The focus ring on a clickable item falls back to the lighter purple accent (was an indigo).
- Dark navy panels and carousel arrows (70 to 98%) are the page background color or the 60 and 70% scrims; they are neutral where they were blue-black.
- Blue-gray hairlines (32% slate) are a mix of the muted text color at 32%. The compact donut ring is the secondary accent at 52%.
- Blue-navy fills (18%, 30%, 40% navy) are the secondary accent at 14%.
- Completed badge gradient ends on the darker purple accent.

### Music and audiobooks

- The album series rail base is the page background color; its lavender text is the lighter purple accent and its near-white is the primary text color.
- The embedded audio panel gradient: the dark-purple end is the base surface at 78% (was a purple-tinted navy), the other end is the page background color at 88%.

### Extras

- The amber "missing" icon is the warning color (was a softer amber). The play icon and focus outline are the primary text color (were warm cream).
- The extra viewer lightbox base is the page background color at 94%.

## Pages, browse, the hero carousel and search (WP11 part b)

Look v3.1 D7 moves every hand-typed color onto the shared set. This part covers the pages, the browse lists, the hero carousel and search. Most moves are exact or within two points. This list holds the ones that are not one to one, by screen, so the visual review can look at them.

General moves that apply to every screen below:

- Text that was white, off-white or light slate at some strength is now one of the solid text colors (primary at 85% and above, secondary at 65% to 84%, muted at 45% to 64%, disabled below that). Text that was translucent is now solid.
- Navy and near-black panels, bars and gradient ends are now the neutral dark surfaces (root, base or raised), kept translucent at the same strength where they sit over artwork, a map or a photo. The slight blue tint is gone.
- Slate and navy hairlines snap to the subtle or default border; slate lines stronger than 20% are a mix of the muted text color at the same strength.
- Violet, indigo, lavender and deep purple tints (fills, borders, glows, focus rings) are mixes of the purple accent (or the lighter purple accent for lavender) at the same strength: a slight change of hue.
- Black and navy shadows use the named dark scrims, or a mix of the 70% scrim when no step is within about 4 points.

### Home and Listen

Listen page (the largest file):

- The side rail, cards and panels were translucent navy; they are now neutral dark surfaces at the same strength.
- The two panel gradients' faint indigo glow is the purple accent at 7%.
- The indigo-to-deep-violet button gradient is the purple accent (72%) to the darker purple accent (82%).
- Its glow shadow is the shared purple glow (28%).
- White text at 42% and 52% to 62% moved to the disabled and muted text colors; 72% to 78% to the secondary text color.
- Slate borders at 25% to 28% stay as muted-text mixes; at 18% to 20% they are the default border.
- Row and chip fills at 14% to 18% slate are the default border color.

### Watch, Read and Now Playing

- Now Playing: the poster shadow is the 50% dark scrim (was 50% black, same).
- Extra viewer: the off-white caption (66%) is the secondary text color; its 20% hairline is the 18% white wash.
- Library browse page: off-white text at 88% is primary, at 56% is muted; its 12% hairline is the 12% white wash.
- Setup page: the heading icon text is the primary text color (was pure white); the stage shadow is the 20% dark scrim.
- Who is watching: the backdrop is the 60% dark scrim (was 62%); the error text drops its red fallback and uses the status red.

### View: photos, galleries, places

Photo timeline and artwork:

- Selected tile highlight: the deep violet wash (12%) and lavender border (82%) are the purple accent at 12% and the lighter purple accent at 82%.
- Tile badges, notes and the select control sit on the neutral root surface at 82%, 88% and 42% (were navy).
- The select box border was white at 86%; it is now the solid primary text color.
- Artwork page: its violet borders, fills and focus ring (62%, 20%, 75%, 85%, 14%) are the purple accent at the same strength.
- Artwork page: white fills at 3.5% and 2.5% are the 3% wash; the 10% line is the 12% wash.
- Artwork page: the dark panel (55%) is the root surface.
- Artwork page: the card shadow is the 30% dark scrim (was 28%).

Places (the map page):

- Toolbar, search, types and compass were navy at 88% to 96%; they are now the root surface at the same strength.
- The map background is the root surface (was a near-black navy).
- The active filter button was deep violet at 90%; it is the purple accent at 90%.
- The type switch's active gradient is the purple accent to the darker purple accent; its glow is the purple accent at 38%.
- The unmapped count badge is the darker purple accent with the on-accent text color; its border was pale lavender at 45% and is now the primary text color at 45%.
- Shadows are the dark scrims, or a mix of the 70% scrim where no step fits (36% on the toolbar).
- Map labels moved to text colors: pale blue icon and white input text are primary, grey placeholder and small text are muted, the sheet labels are secondary.

Galleries and people:

- Gallery card: the gradient runs from the purple accent (20%) to the base surface (92%) (was violet to deep navy). The editor window is the root surface and its shadow is the 70% dark scrim.
- Gallery detail code block: base surface at 90% (was navy at 90%).
- Immersive viewer panel: the description card border is the purple accent at 48%, its fill at 9% (was deep violet at 9%); input fields are the base surface at 78%.
- Selection toolbar: was nearly solid navy (98%); it is now the solid root surface. Its shadow is the 50% dark scrim (was 52%) and the glow is the purple accent at 16%.
- People page: the portrait ring is the lighter purple accent at 28% (was lavender).

View home page:

- Tiles' navy panels (74% to 96%) are base, raised or root surfaces at the same strength.
- The white-on-photo selection border (70%) is the primary text color at 70%.

### Browse lists (movies, shows, books, music and the rest)

- Filter bar and panels: navy at 44% to 78% is the root or base surface at the same strength; the faint 1.5% to 2% inner lines are the 3% wash.
- Hairlines at 12% to 20% are the subtle or default border; the 22% to 26% ones are muted-text mixes.
- Selected display button: the violet-to-deep-violet gradient (76% to 70%) is the purple accent to the darker purple accent.
- Its inset ring and glow are the lighter purple accent at 48% and the purple accent at 22%.
- Quick filter toggle: the track was slate-600 at 50%; it is now a mix of the muted text color at 50% (a lighter grey). The knob shadow is the 30% dark scrim (was 34%).
- Active filter pill: the fill is the shared purple soft color (16%, was deep violet at 18%) and its text is the primary text color (was pale lavender at 96%).
- Timeline results: card gradient is the base surface (74%) to the root surface (46%); borders at about 19% are the default border.
- Continue strip: navy cards (50% to 72%) are root or raised surfaces at the same strength.

### Hero carousel and phone hero

- Pills and controls: dark overlays (46% and 58%) are the root surface at the same strength; the 13% and 16% white lines are the 12% and 18% washes.
- Dots: pale slate at 30% and 52% are the primary text color at 30% and 52%.
- The collage shadow and the phone hero card shadow were 45% black; they are mixes of the 70% scrim at 64%.
- The phone hero gradients end on the root surface at 90% and 92% (was near-black navy).
- The surface navigation bar fade (34% to 58%) is the root surface at the same strength.
- The warm off-white title text (94%) is the primary text color.
- Colors drawn from the cover artwork (the `--hero-*` variables) are unchanged.

### Search

- The search window background is the root surface (was `#060b17`) with a soft raised glow at 36% (was a dark violet glow).
- The window shadow is the 60% dark scrim (was 62%) and the faint ring is the purple accent at 6%.
- Section titles and match highlights in lavenders (`#c4b5fd`, `#bba4ff`, `#b79cff`) are all the lighter purple accent: three slightly different tints become one.
- The no-cover placeholder was a navy gradient; it is the raised to root surfaces, with its lavender icon at 70%.
- Result cards: the violet-tinted gradient is raised to base surfaces at 78% to 80%; the card border is the purple accent at 52%.
- The result panel gradient is base to root surfaces at 94% to 96%.
- Slate borders at 22% to 32% are muted-text mixes at the same strength; those at 10% to 16% are the subtle or default border.
- The faint slate row fills (8% and 14%) are the subtle and default borders.
- Input hint text is the muted text color (was slate at 62%); the leading search glyph is the lighter purple accent at 42%.
