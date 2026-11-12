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

## Settings, navigation, players, small areas and the global sheet (WP11 part c2)

Settings, navigation, the players' shared parts, the small areas and the two global sheets (`app.css`, `native-fields.css`) moved onto the shared colors. Most moves are exact (a wash or scrim step, a text color, or a fallback that was dropped). This list holds only the moves that are not one to one, by screen, for the visual review.

### Everywhere in this part

- Translucent text is now one of the four solid text colors: 78% and up is primary, 60% to 77% is secondary, 40% to 59% is muted, below 40% is disabled. Slate tints (148, 163, 184) are the muted text color, whatever their strength. Text that was translucent is solid.
- Fallbacks such as `var(--tl-text-primary, #e5edf5)` lost their hand-typed part; the variable already exists, so nothing changes.
- White washes snap to the five named steps (3, 5, 8, 12, 18%); no move is more than 2 points.
- Dark dims snap to the named black scrims; where the old strength was more than 4 points from a step, a mix of the viewer black at the same strength is used, so nothing moves.
- Pure white text and fills on an accent are the on-accent text color (still pure white). Pure white on video is the on-scrim color (still pure white).

### Navigation

Account menu:

- Hover background was a violet at 14%; it is now the purple accent soft (16%). The hover outline is a 22% mix of the lighter purple accent (was a lavender at 22%): a slight change of hue.
- The header hairline was slate at 14%; it is now the default border (16%).
- The badge text was a dark brown; it is now the dark-on-warning text color. The sign-out hover text is the primary text color (was pure white).

Phone section chips: the edge fades use the viewer black; nothing changes.

### Media hub and Listen

- The shelf header link, the empty-shelf text and the page headings are the solid text colors (see above). The lavender link is the lighter purple accent.
- The section header bar was a very dark navy at 88%; it is now a mix of the page background color at 88%.
- The empty shelf panel was a dark navy at 62%; it is now the 60% black scrim.
- The storage bar track was slate at 14%; it is now a 12% white wash. The storage hairline is the subtle border (10%, was 12%).
- The Listen navigation: texts at 42% and 58% are the muted color, 74% is secondary, 96% is primary, and the active row is the 8% wash (was 7%).

### Players' shared parts

Dims over video keep their strength within 4 points (or use a mix at the same strength).

- Playback panel backdrop is the 50% scrim (was 53%).
- Primary button shadows are the 20% scrim (were 22% and 24%).
- Tool sheet shadow is the 50% scrim (was 48%).
- The video touch hint shadow and the host's shadow are the 40% scrim (were 38%).
- The skip-segment border is a 35% mix of the primary text color and the segment marker border is a 48% mix (were pure white at the same strengths): a very slightly cooler white.
- The touch ripple label shadow is the 60% scrim; the ripple is the 18% wash.
- Subtitle Find: its primary button text is the on-accent color; nothing changes.
- The default subtitle text color uses the white swatch from the shared set; nothing changes.

### Setup

- The intro glow was warm yellow-gold and amber; it is now the collection gold accents (hover gold at 24%, gold at 8%): a little less yellow.
- The step ring mixes the purple accent with the primary text color (was pure white).
- The intro cards border fell back to a gray at 25%; it is now the subtle border.

### Universe

- The empty series card: the dashed border is a 32% mix of the primary text color (was white at 32%); its text was a blue-tinted gray at 50% and is now the muted text color. The tile wash ends on a 1% mix of the primary text (was white at 1%).
- Raised panels no longer carry a gray fallback; they use the raised surface.

### Settings

- Dark wells on the access, application webhook and integration key screens were black at 28%; they are the 30% scrim.
- The metadata admin shadow is a mix of the viewer black at 8% (the shared scrims start at 20%).
- Rows and expansions on the provider tester were white at 2% and 3%; both are the 3% wash.
- The advanced network icon tile was white at 6%; it is the 5% wash. The network test panel was white at 3.5%; it is the 3% wash.
- The address QR code keeps a pure white background (it needs the contrast to scan); it now uses the on-accent text color, which is pure white.

### Global sheets (`app.css`, `native-fields.css`)

Surfaces:

- The account menu pop-over (`#0d0f14`) and the browse filter pop-over (`#0c1525`, a navy) are now the page background and the base surface.
- Table header fill and dialog action bar were translucent gray-violet; they are mixes of the hover surface at 72% and 36%.
- The artwork picker button, the media tile label and the cinematic top bar use mixes of the page background at the old strengths; the top bar gradient fades from the 70% scrim to the 30% scrim (was 70% to 34%).
- The browse filter bar button and chip were navy at 56% to 64%; they are the 60% scrim. The row menu button on the artwork view was navy at 72%; it is the 70% scrim. The filter sheet footer lost its fallback.
- Slate hairlines at 28% (the filter pop-over and trigger) are a 28% mix of the secondary text color; at 20% they are the default border (16%); at 24% they are a 24% mix of the muted text color.

Text and accents:

- The filter sheet "Clear" link and the artwork hierarchy labels were lavender; they are the lighter purple accent. The active hierarchy chip was a violet; it is the purple accent with a darker purple fill at 22%: a slight change of hue.
- The profile switcher tag was a violet at 45%; it is the purple accent border (42%).
- Menu items in the error tone had a pale pink hover text; it is a mix of the danger text and primary text colors (60/40).
- The pending-enrichment ring was a yellow-gold (`#e6b93e`); it is the collection gold.
- The playback select highlight fallback was a purple at 18%; it is the purple accent soft (16%).
- The details-page focus ring fallback (`#7765e3`) falls back to the purple accent.
- The native select fallbacks: the border is the strong border, the field fill is a page background mix at 86%, the hover border is the secondary blue accent (was a lighter periwinkle at 78%), and the focus ring is the shared focus ring (was a blue `#6d96ff`). These apply to the select, and to the documents filter and saved-search inputs.
- The video-touch hint, overlay shadows, popover shadows and menu shadows snap to the named scrims; the biggest moves are 52% to 50%, 46% to 50%, 35% kept as a mix.
- The phone details metadata pills: the border was white at 16%, now the 18% wash; the fill was navy at 42%, now the 40% scrim.

Utility classes (`.tl-u-*`; names unchanged because markup uses them):

- Orange tints (`rgba(249,115,22,…)`, `#F97316`) are now the warning color (amber): a slight change of hue.
- The gray text utilities `#444` and `#555` are the disabled text color (a little brighter); `#888` is the muted text color; `#e0e0e0` is the primary text color; the translucent 248 whites are muted (40%), secondary (65%, 70%) and primary (80%).
- White at 10%, 18% and 32% used as an icon color are the primary text color mixed at the same strength (so spinners and placeholder icons look the same).
- `.tl-u-color-hex-1a1208` is the dark-on-warning text color (unused in markup today).

Switch (`native-fields.css`): the thumb shadow is the 40% scrim; the checked thumb is the on-accent text color (pure white).

## Shared parts, artwork, tiles, View and Library (WP11 part c1)

Every hand-typed color in the shared parts (`Components/Shared`, `Artwork`, `MediaTiles`, `View`, `Library`) moved onto the shared set. Most moves are exact. This list holds the ones that are not one to one, by screen, so the visual review can look at them. The map (`View/ViewMap.razor.css`) is a written exception and was not touched.

### Everywhere in these parts

- Text that was a translucent white or slate (25% to 98%) is now one of the four solid text colors: 78% and up is primary, 60% to 77% is secondary, 40% to 59% is muted, below 40% is disabled. Text is solid where it was translucent.
- Pure white, cream and near-white text and borders are the primary text color (a very slightly cooler white).
- Solid slate text (`#94a3b8`, `#aab6ca`, `#b7c2d4`, `#b8c0d2`, `#b6bbcc`, `#cbd5e1`) is the muted or secondary text color, whichever is closer.
- White washes and hairlines snap to the five named steps (3, 5, 8, 12, 18%). Most moves are 1 to 2 points; 10% hairlines are now 12%.
- White hairlines stronger than 18% (24% to 30%) are a mix of the primary text color at the same strength.
- Dark dims snap to the named black scrims (20 to 70%). Seven moves are 4 points off: 24% to 20%, 34% to 30% (five places), 36% to 40%, and white 22% to 18%.
- Very dark navy and near-black fills are the page background color (or a mix of it at the same strength, 78% to 98.5%). They are a touch lighter and neutral where they were blue-black.
- Dark navy fills at 70% or less that sat over artwork are the named black scrims (28% and 34% became 30%; 68% to 74% became 70%).
- Slate hairlines at 12% to 18% snap to the subtle or default border. Slate hairlines and fills at 20% to 58% are a mix of the muted text color at the same strength.
- Lavender and violet tints (`#c4b5fd`, `#ddd6fe`, `rgba(167,139,250,...)`) are the lighter purple accent. The mid purples (`#8852fc`, `rgba(124,92,255,...)`, `rgba(136,82,252,...)`) are the purple accent. Slight hue changes.
- Hand-typed fallbacks inside `var(--tl-...)` were dropped. Fallbacks inside `var(--playback-...)` and `var(--art-bg-...)` now fall back to a shared color; the playback or artwork color still wins whenever it is set.
- Every `#a78bfa` focus outline and border is the shared focus ring color.

### Shared parts

Viewer window (`MediaViewerShell`):

- The info panel gradient was two near-black navies; it is now the shell background to the page background, both at 98%.
- The top bar, zoom bar and info panel are the page background color at 86% to 98.5% (were navy at the same strengths).
- The stage backdrop is the page background color (was `#02040a`).
- The navigation arrows are the 70% scrim (were navy at 68%).

Artwork stack (`TuvimaArtworkStack`) and group preview (`MediaArtworkGroupPreview`):

- The glow fallbacks are the warning color at 18% and 22% (was an orange at the same strengths) and the purple accent (was a violet). A teal glow is the View area teal at 12%.
- The base fallback (navy at 74%) is the 70% scrim.
- The group preview's dashed placeholder gradient runs from a mix of the hover surface (92%) to the page background (98%).

Discovery filter bar (`AppDiscoveryFilterBar`):

- The search field and the tab fills (navy at 72% to 82%) are the 70% scrim, or the shell background at 82%. The "More" button fill is the raised surface at 76%.
- Tab text at 78% is the primary text color; label text at 72% is the secondary text color.

Smaller shared parts:

- Tag input: the add button is the darker purple accent at 25% (was violet at 25%). Its field is the base surface at 65%.
- Timeline navigator: the empty-year text (34%) is the disabled text color; the month text (68%) is the secondary text color; the compact bubble is the page background color at 90%.
- Range slider: the track fallback is the highest surface (was a blue-gray `#30364a`).
- Tools summary line: the neutral chip is the muted text color at 12% (was gray at 12%).
- Bookmarks panel: the lighten and darken mixes use the primary text color and the viewer black instead of `white` and `black`; same strengths.
- Context sidebar: the surface fallback is the shell background (was `#101821`).

### Artwork

- The workspace background gradient runs from the shell background to the page background (was two navies).
- Panels, previews and wells that were deep navy or near-black (`#05080d`, `#070a11`, `#080d17`, `#0b1019`, `#070c14`) are the page background color. The dark violet upload note (`#171026`) is the base surface.
- Role card and dim fills (navy at 72% and 88%) are the 70% scrim or the page background at 88%.
- Purple selection colors (`#aa64ff`, `#b15dff`, `#ae70ff`, `#a66aff`, `#c084fc`) are the lighter purple accent. Their glows are the purple accent at the old strengths. The old picks were slightly different violets.
- The selected-pill fills (dark violet at 90% and 92%) are the darker purple accent at 90% and 92%. They are lighter than the old deep violet.
- The "upload" gradient (`#792df3` to `#5020bc`) is the purple accent to the darker purple accent.
- The `#9443dc55` glow is the darker purple accent at 33%.
- The gallery check fill (`#111827d9`) is the base surface at 85%.
- Asset browser: blue-gray border (`rgba(145,181,217,.26)`) is a mix of the muted text color at 26%. The viewer fill (teal-navy at 74%) is the raised surface at 74%. The context cards (slate at 46%) are the hover surface at 46%.

### Media tiles

- The video collection tile gradient was a deep violet to two near-blacks; it is now a 35% mix of the darker purple accent into the page background, then the base surface, then the shell background. A little less violet at the top.
- Its border and rule are the purple accent at 45% and 50%; the creator line is the lighter purple accent at 80%.
- The shelf scroller arrows are the page background color at 82% and the shell background at 94% (were two different navies). The arrow border snaps from 22% white to the 18% wash.
- Stand-in and shadow fills (navy at 8%, 28%, 70%) are the viewer black at 8%, the 30% scrim and the 70% scrim.
- The collection wash fallback ends on the 70% scrim.
- The tile shelf muted line (slate at 88%) is the muted text color (a little dimmer).

### Library

- Library table dialog background (`rgb(32,32,32)`) is the raised surface (bluer by a few points). The table row background (`#14181d`) is the shell background.
- The gold flag (`rgba(201,146,46,...)`) is the collection accent at 12% and 25% (a slightly lighter gold).
- The favorite heart (`#ff4f76`) is the shared pink-red (`--tl-status-unmatched`); a bit softer.
- The error text, wash and border (`#ef4444`, `rgba(239,68,68,...)`, `rgba(226,75,74,.7)`) are the danger color at the same strengths.
- Blue link text (`#38bdf8`) is the secondary accent (same color).
- The stage dot ring (`#0d0f12`) is the page background color.

### View (places)

- The story panel gradient (`#070d1a` to `#040812`) is the shell background to the page background. The scrollbar thumb (`#4c1d95`) is the darker purple accent (lighter).
- Fact card and thumbnail fills (slate navy at 58% and solid) are the base surface at 58% and solid.
- Chips and facts: slate hairlines follow the border rules above; the fact border is the purple accent at 20%.
- Timeline bar: the border (`#334155`) is the default border; the tooltip border (`#475569`) is the strong border; the tooltip fill (`#182033`) is the raised surface; the bar fill is the page background color at 94%.
- Callout (`#382064`, light text `#ede9fe`) is a 35% mix of the darker purple accent into the page background, with primary text.
- Selected and outside histogram bars (`#a66bff`) are the lighter purple accent. The hover bar (`#c4b5fd` and `#e9d5ff`) is the lighter purple accent, mixed 60% into the primary text color for the brightest state.
- The active range button fill (`#5b21b6`) is the darker purple accent (lighter).

### Exceptions left alone

- `Components/View/ViewMap.razor.css` (15 colors): the map draws its own base, labels and hotspots over map tiles.
- No other exception was needed. The histogram bars are the app's purple, so they moved to tokens.

## The app frame and the structure sheet (WP11, last pieces)

- The My List button in the top bar, while its page is open, is the lighter purple accent (was a yellow). Selected things are purple everywhere.
- Scroll bar thumbs are the secondary text color, and the muted text color on hover (were two plain greys): a slightly cooler grey.
- The top bar over a hero fades from the 70% black scrim to the 20% scrim (was 68% and 34% of a near-black navy).
- The phone bottom bar's shadow is the 20% black scrim (was 28%); the side drawer's shadow is the 40% scrim, and its backdrop the 70% scrim (was 72%).
- Table row hover and list row hover are the 5% white wash (were 4%); striped rows the 3% wash (2.5%); the alert close button's hover the 8% wash.

## After the final review

- The favorite heart in the library table, when on, is the lighter purple accent (the sweep had first used a status color; it was a pink-red before). The Like state is purple everywhere.
- The Listen rail's section labels are the muted text color (white at 42% before).
- The readers' menus, side panel and thumbnails use the shared colors: the side panel's dim is the 40% black scrim (was 35%), panel shadows the 20% scrim (were 30 to 35%), the thumbnail strip's shadow the 70% scrim (was 65%), picture placeholders the 5% white wash (were 6%), and the selection menu's background is 94% of the page background (was a dark navy). Page themes (paper, night, sepia), highlight colors and the bookmark mark are unchanged.
- The step indicator's active number and the bulk selection bar lost their typed fallbacks; they look the same.
- Recovery codes are 14 px (were 15 px).
