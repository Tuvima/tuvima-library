# Spec: Universe entry points (make Universes easy to find)

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-11-04 · **Related docs:** [Universe spec](universe.md) (parent; its D1 "no Universe link on item pages beyond one quiet line" is changed by this draft, see Decisions) · [Universal editor v3](universal-editor-v3.md) · Local check items 57, 59, 60 (`engineering/validation/local-check-2026-11-03.md`)

## 0. Why Shy has never seen a Universe (verified by reading the code; the real data lives on SHY-DESKTOP and was not looked at)

The Universe page exists and works (`/universe/{qid}`). Three things hide it:

1. **A Universe only exists as a page once the Library holds two or more Series that name it.** The Universe "shelf" is created by `ParentCollectionResolver` only when `UniverseIdentityRule.ShouldPresentUniverseShelf` says so: at least two Series (and not just the audio and print formats of one book Series). The page itself (`UniverseClientReadService.FindOpenUniverseAsync`) refuses to open without that shelf. So a title can have rich Universe data in the Engine (the log shows John Wick being built from The Continental) and still have no page, no tile and no link, because the Library holds only one Series of it. The Expanse shows as a normal Series shelf for the same reason.
2. **The only link inside an item is one small line of text** ("Part of the X Universe") under the hero, and it follows the same two-Series rule (`ShowsPartOfLine`: 2+ Series or 2+ formats). On a one-Series Universe it never appears.
3. **On Collections the Universe tiles sit behind a "Relationship" filter** (All, Universes, Series rollups, Related works), so even a real Universe is not in front of you.

Best guess for Shy's Library: for John Wick and The Expanse the two-Series rule is not met, so nothing formed. This is a guess until someone looks at the data on SHY-DESKTOP (Local check, packet 1).

## 1. Goal
If a title has Universe data, you can see that at a glance and get to the Universe in one click, from the title itself, without hunting through Collections.

## 2. Scope
**In:** a Universe icon on titles; a rule change so a Universe page can open when the Library holds one Series (or one title) of it; Universe visible on the Collections page; the editor hook; Fandom Lore status shown on the Universe page.
**Out:** the Universe page contents (parent spec), companion, helper-app screens (they follow in a later packet once the Engine rule is settled), the editor overhaul itself (separate thread "Overhaul the editor" owns the editor; this spec only names the one row it needs).

## 3. Decisions made
Shy, 2026-11-04 (agreed to all recommendations): **Q1A** a Universe page opens with one Series (Collections still lists Universes of 2+ Series only); **Q2A** icon on item pages only, plus "Open Universe" in the tile "..." menu; **Q3A** the icon replaces the "Part of the X Universe" text line; **Q4A** a Universe row at the top of the default Collections view. **Icon:** the edge-on galaxy with four stars, own drawing (the uploaded image has no known licence), large (about 84 px desktop, 64 px phone). **Hover:** the galaxy spins (two bands flow round the core in opposite directions) and the stars twinkle; on desktop a small card shows the Universe name, Series count, Works owned and what the page offers (Cast, Adaptations). Shy said "looks good, lock it" after seeing this; the card was recommended and not explicitly refused. Motion stops with reduced-motion. Mockups: icon-galaxy-spin.html, icon-galaxy-animated.html (kept in the project files). Carried in from Shy's notes (2026-11-04): a small "Universe" icon floats above the cover art or at the top right of the landscape background; it is also reachable from the editor.
The rest below follows these answers.

## 4. UI behaviour (proposed)

### 4.1 The Universe icon
- A square icon button, large enough to notice (about 84 px on desktop, 64 px on phone; the galaxy itself about 64 px) (square, as decided for pills; icon with colour; tooltip "Open the {name} Universe") sits **top right of the landscape background** on movie, show, season, book, audiobook, comic and album pages. On phones, where the hero is a cover, it floats at the top right of the cover.
- It shows whenever the title has Universe data (a Wikidata Universe the Engine has recorded), not only when the Library holds two Series.
- On hover the edge-on galaxy spins (its two bands flow round the core, outer slowly, inner faster) with twinkling stars and a soft glow (no motion with reduced-motion; none on touch). Icon art: own redrawn galaxy recommended (Shy's uploaded image has no known licence); pending Shy.
- Click opens the Universe page. It replaces the plain "Part of the X Universe" line, which stays as the accessible text next to it on wide screens only if Shy wants both (Q3).
- On list tiles the icon is **not** shown (too busy); the tile menu "..." gets "Open Universe" (Q2).

| Situation | What the person sees |
|---|---|
| Title has Universe data and the profile may open it | Icon, tooltip with the Universe name |
| Universe data is still being built | Icon hidden until the Universe is ready, never a dead link |
| Title has no Universe data | Nothing, no empty placeholder |
| Profile cannot see the Universe (Kids, private) | Nothing |
| Phone / tablet / desktop / TV | Phone: on the cover. Tablet and desktop: top right of the background. TV app: a "Universe" button in the action row (no floating icon on TV) |

### 4.2 A Universe with only one Series
The Universe page opens with one Series and shows Connections (Cast, Adaptations). Collections shows a Universe tile only when two or more Series are held (today's rule, unchanged), so Collections does not fill up with a "Universe" for every single book series. The icon is the way in for the small ones.

### 4.3 Collections page
The Universe group is shown as its own row at the top of the default view when any exist, instead of only behind the Relationship filter. Empty state: no row.

### 4.4 Editor hook (coordinate with "Overhaul the editor")
The Match tool of the pop-over editor shows a read-only "Universe: {name}" row with "Open Universe". Opening it closes the editor (Cancel rules apply) and goes to the page. Editing the grouping itself stays where the editor thread puts it.

### 4.5 Fandom Lore (off by default)
On the Universe page, admins see one line when the lore plugin is off: "Add Fandom details" linking to the plugin screen (already in the parent spec, section 4.2). Nothing changes for non-admins. The plugin stays off by default.

## 5. Data / model changes
- Engine: a Universe page can be backed by a Universe record without a shelf, or the shelf is created for one Series when the user opens it (decision Q1, recommendation below).
- Detail page contract: `UniverseLink` is set from the Universe record, no longer gated by `ShowsPartOfLine`.
- No data-store migration (pre-release; reset acceptable).

## 6. Work packets
1. **Find out what is really on SHY-DESKTOP** (Mechanical, local). Read-only look at which Universe records and shelves exist for John Wick and The Expanse. Done when: it is known whether none formed or the link is missing. Depends on: none.
2. **Let a Universe open with one Series** (Standard). `UniverseIdentityRule`, `ParentCollectionResolver`, `UniverseClientReadService`, tests. Done when: a title in a one-Series Universe has a working page.
3. **Universe icon on detail pages** (Standard). `DetailHero.razor`, detail contract, `UniverseLinkViewModel` rule. Done when: icon at top right on desktop, on the cover on phone, hidden when unavailable; screenshots at 1440 and phone width.
4. **Collections Universe row** (Standard). `CollectionsPage.razor`, default view. Done when: a real Universe is visible without touching a filter.
5. **Editor "Open Universe" row** (Mechanical, after the editor thread lands). Depends on: Overhaul the editor.
6. **Docs** (Mechanical). Public docs explain why a Universe appears and why the icon is there, including the one-Series case.

## 7. Local checks (also added to `engineering/validation/batch.md` by the build)
Open John Wick (any film) and The Expanse (book or show): the icon is there and opens the Universe. Open Collections: the Universe row is visible. Phone width and TV button.

## 8. Risks and open questions
Q1 to Q4 are in the message to Shy. Risk: a Universe page with one Series looks thin; mitigated by Connections and "Still learning about this Universe". Performance: the icon is read with the detail page, no extra request (the link cache already exists).

## Plain English summary
Universes exist behind the scenes but only become a page when you own two Series from them, and the only link is a small line of text. This plan adds a small Universe icon at the top right of any title that has Universe data, lets a Universe open even with one Series, shows Universes at the top of Collections, and adds an "Open Universe" row in the editor.
