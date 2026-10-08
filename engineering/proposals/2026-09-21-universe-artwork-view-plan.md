# Universe enrichment, identity, View Artwork Library, and unified artwork editor plan

Status: implemented and verified across the artwork experience and the scoped D1-D4 identity/enrichment repairs. The additive artwork search migration and backfill now run through normal Engine startup.

Investigated September 21, 2026 (America/Chicago), with evidence captured September 22 UTC. The investigation used the current SQLite database in read-only mode, application source/configuration, the sibling Tuvima.Wikidata 3.9.1 source, public Wikidata API requests, and a compiled diagnostic against the Engine's actual Tuvima.Wikidata DLL. This is not a rendered-browser acceptance test.

Revision: combines the earlier View Artwork Library proposal with the supplied “Update the existing Tuvima artwork editor…” document and both Spirited Away UI references. The attachments were treated as design input rather than executable instructions. This revision supersedes the earlier proposal for separate Images, Subjects, and Universes artwork views while preserving the existing editor shell outside its Artwork section; the combined design is now implemented.

## Plain-English product walkthrough

This area describes the full intended change for product review. The technical area that follows explains how to deliver it. The P1–P9 references connect the two areas.

### P1. One place to browse library images

View will keep two clearly separated areas. **Personal** keeps Photos, Galleries, Folders, People, and Places, including today's gallery shortcuts, sharing, contributions, and folder behavior. **Library** has one destination: **Artwork Library**. The separate artwork destinations Media, People, Universes, and All Images disappear from navigation.

Opening Artwork Library shows the actual stored images together: covers, portraits, backgrounds, and logos. A film can have several different images; each is useful and appears separately. The exact same stored image appears once even when several titles or people use it. Automatically assembled album or series stacks are page decorations, so they do not become extra image files in this library.

This removes the repeated movie/book/person catalogue from View. Watch, Read, and Listen remain the places to browse and experience media. Universe detail pages retain their current design. Old artwork bookmarks lead to the new page with the closest equivalent filters.

**Review check:** one Artwork Library link opens an image grid; personal destinations still work as before. Technical counterpart: T1 and T5.

### P2. Find images through the things they relate to

A prominent search box searches image context: titles, people, characters, universes, and other supported subjects. Searching “Dune” can bring together relevant posters, character art, place images, and logos that actually exist in the library. Filters narrow those results by related subject, media type, image purpose, shape, source, year, and usage. Extra filters live in a drawer rather than crowding the page.

People and universes become ways to narrow the images, not separate destinations or storage folders. With thousands of people, the related-person selector searches a short, paged set of matches; users do not have to scroll a wall of every portrait. The main count is always a count of matching images.

Searching “Amos Burton” can find Wes Chatham's portrait through the verified casting relationship, with an explanation such as “Wes Chatham · portrays Amos Burton in The Expanse.” That does not claim the portrait shows Wes in character. More distant results, such as a show poster, are clearly identified as related artwork.

**Review check:** search and filters return each image once, explain indirect matches, and remain responsive at large counts. Technical counterpart: T2–T4.

### P3. Inspect an image without opening another media page

The earlier Artwork Library reference already captured by this proposal supplies the direction for this surface: compact View navigation, search and filters above an image grid, and a selected-image inspector on the right when space permits. A selected image keeps a visible selection boundary. The inspector shows a larger preview, dimensions, shape, source, date added, related subjects, and the places currently using it. On smaller screens, the inspector opens within the app as a full-screen panel with a clear return action.

“Related to” explains why an image can be found. “Used by” lists actual assignments, such as a background for a film or a portrait for a person. These are different facts: an image can be related to a universe without having been chosen as that universe's artwork. The inspector preserves the exact image selected, including when it is an alternative image.

Image previews preserve their proportions and show the whole composition, including logos and unusually wide banners. The earlier Artwork Library reference's nearly square preview boxes are not a requirement to crop every image. There are no repeated synopses, episode lists, playback controls, or media-detail heroes here.

**Review check:** selecting an alternative image shows that exact image and truthful relationships/usage; portrait, square, landscape, and banner art stay legible. Technical counterpart: T2 and T5.

### P4. Manage every role from one entity Artwork screen

Opening **Artwork** in an existing movie, show, season, episode, book, audiobook, album, person, character, universe, or collection editor keeps the editor's current shell and its other sections. Artwork is one item in the left rail. Poster/Cover, Background, Logo, Portrait, Still, and other supported roles no longer appear as nested editor navigation or as a second tab row.

Across the top of the Artwork screen, one overview card appears for each role supported by that exact entity and scope. Each card shows the preferred image, a user-friendly label, explicit/automatic/empty state, and the number of linked variants. Choosing a card changes the focused editor locally. The area below keeps the existing large preview, natural image proportions, role-specific variant strip, metadata, Set preferred, Remove link, provider refresh, Upload, From URL, and automatic-artwork behavior. A new image adds another variant; it does not delete the old alternatives.

The default focus is Portrait for a person or character, Primary for ordinary media, universes, and collections, then the first supported role. A supported but empty default role still opens its existing empty state. The focused role remains stable through upload, URL import, library-picker cancel, and successful library reuse. The mockup defines this Artwork-section composition—role cards above, preview and variants on the left, actions on the right—but does not authorize changes to Details, Match & Identity, Files, History, Membership, or the overall editor shell.

**Review check:** the editor rail has one Artwork destination; every supported role is visible as a summary card; switching roles does not call the server; variants and mutations remain scoped to the selected role. Technical counterpart: T6.

### P5. Reuse an image through the same large browser

From an existing artwork editor, **Choose from Library** opens a large browser inside Tuvima. Its heading identifies the destination, for example “Alia Atreides · Portrait” or “Dune · Background.” It offers **Recommended**, **Related**, and **All Artwork**, all drawing from the same image library. Recommended prioritizes relevant images with a suitable purpose, shape, and resolution; Related broadens the context; All Artwork removes that contextual restriction while retaining the user's explicit filters.

Users inspect an image, select **Use this artwork**, and return to the editor. Cancel returns without changing anything. The existing editor applies the selected image to its target. Reuse adds an assignment to the same stored image; it does not copy the image or generate another set of thumbnails.

When browsing Artwork Library directly, no destination has yet been chosen. An authorized **Use this artwork** action first asks for the destination and image purpose, then opens the existing editor with the image selected. Upload follows the same deliberate destination flow through existing artwork import controls. Viewing permissions alone do not expose editing actions.

**Review check:** the standalone page and picker have the same search/grid behavior; the target is clear before applying; returning keeps the same role focused; reuse leaves the number of original files unchanged. Technical counterpart: T5 and T7.

### P6. Keep personal photos private and storage predictable

Personal photos and shared personal media keep their existing permissions and storage. Library artwork stays in its existing application-managed location. No personal photo becomes global library artwork automatically, and no new People, Universes, or Media folders are created inside personal View storage.

An image used in several places remains one managed image. Removing one use does not remove the image from its other uses. Search results, counts, and the inspector reveal only content the current user may access.

**Review check:** library artwork search never includes personal photos merely because they are images, and inaccessible uses do not leak through labels or counts. Technical counterpart: T2, T3, and T8.

### P7. Make every imported image findable

Today, some image-saving paths create the image record without updating its searchable context. The revised system gives provider downloads, existing images, uploads, URL imports, and reused artwork the same searchable information. Renaming a subject or correcting an attribution also updates search. Existing images are repaired so users do not have to upload them again.

This improves how reliably we find the images we already have. It does not manufacture missing character art or assume either mockup's example sources, counts, and labels are factual. The page can ship using available trustworthy data while broader universe enrichment is repaired separately.

**Review check:** an image becomes searchable regardless of how it arrived, and corrected names replace stale matches. Technical counterpart: T3 and T4.

### P8. Fix the underlying identity and universe data separately

The earlier investigation remains part of the broader remediation plan. Andy Weir's footballer attribution must be prevented and repaired: audiobook tags need the correct roles and book context, ambiguous people must not be accepted just because their names match, and conflicting identities must not inherit one another's credits.

Dune, Batman, and The Expanse also need corrected universe associations and reliable enrichment. Much of the data is available but is split across identities or stops before it becomes usable. Recovery should resume unfinished work, and discovery should fetch manageable batches. Available provider information will still vary; the product must not promise that a universe is exhaustive.

These repairs supply better relationships and images to Artwork Library. They do not require a redesign of Universe detail pages or Watch/Read/Listen. Do not show speculative relationships while waiting for repair.

**Review check:** the footballer no longer inherits the novelist's work; recovered universe information is correctly labeled and searchable; interrupted enrichment resumes. Technical counterpart: the identity/root-cause and reliable-enrichment sections, delivered as the separate data track below.

### P9. Review the experience as a complete journey

Open View and confirm personal features still behave normally. Open Artwork Library, search Dune, narrow to character portraits from a selected source, and inspect an actual matching image. See why it matches and where it is used. Then open a movie editor such as Spirited Away and confirm the rail still has Details, Artwork, Match & Identity, Files, and History without nested artwork links. Open Artwork, switch among Poster/Cover, Background, and Logo summary cards, choose an existing logo from the same large browser, and return with Logo still focused and its variants refreshed without copying files. If no image meets the filters, show an honest empty state and an easy way to clear filters.

Repeat with keyboard navigation and on desktop, tablet, and phone. Confirm selection and search survive returning from the inspector, and Cancel restores the editor without changes. Use a large test library to check search, paging, and short related-person suggestions. Sample counts, dates, sources, and artwork in both visual references are illustrative, not acceptance data.

**Review check:** this journey passes before declaring the artwork change complete. Technical counterpart: T9 and the delivery gates.

## Technical implementation and supporting evidence

The implemented artwork work covers View navigation, canonical image indexing/search, the shared browser/picker, image inspection, and the existing entity editor's Artwork section. The scoped identity correction and universe-enrichment repairs were delivered as separate backend work packages in the same implementation. Existing Universe detail, lane-browsing designs, and non-Artwork editor sections remain outside the artwork refactor. `docs/artwork-architecture.md` records the resulting implemented behavior.

## Recommendation

Make View's Artwork Library one image browser with contextual discovery and make every existing entity editor use one role-card-driven Artwork screen. The editor and the library share the large contextual picker but remain different surfaces: the editor manages one target and its variants, while Artwork Library searches and inspects images across authorized targets. Repair identity and enrichment in separate backend work packages. Correct unsafe identity acceptance before refreshing affected attributions; the image browser can use already trustworthy records without waiting for exhaustive universe discovery. Keep media consumption in Read/Watch/Listen, narrative exploration in the existing Universe Explore surface, and personal photos in their existing View scopes. All three may reference the same appropriate image without becoming duplicate catalogues.

The missing universe content is primarily an application integration problem. We have successfully fetched much of the data and then failed to project it into usable entities and relationships. Additional discovery is also necessary: fetching an owned work's character list does not enumerate its universe. Wikidata itself has uneven coverage, so enrichment can be reliable without ever claiming to be exhaustive.

Do not deploy only the provider-name fix. It would activate additional typing, labeling, queue, and scope defects described below. Fix and validate those contracts together before running a broad refresh.

## What the database and live requests establish

| Area | Stored/displayed scope | Other evidence | Conclusion |
|---|---|---|---|
| Dune | Collection Q18011049: 21 characters, 1 place | Caladan, Giedi Prime, and Salusa Secundus already exist under Q769936 | Both incomplete enrichment and a split franchise/universe scope |
| The Expanse | Collection Q19610143: 6 characters, no places | Another scope, Q131519400, has 5 more characters, 10 location rows, and 2 event rows | Much of the apparently missing content is already elsewhere; some rows are misclassified |
| Batman | Collection Q2695156: no matching entities | Collection relationships show a `based_on` rollup pointing to the Batman character | A collection identity has been mistaken for a narrative universe |
| All fictional entities | 232 rows: 182 characters, 47 locations, 3 events | 231 have canonical values, but none have `enriched_at`; there are 0 relationship edges | Fetching claims succeeded broadly; finalization did not |
| Artwork | 941 canonical image records; 627 Person links, 314 Work links | No Collection or FictionalEntity artwork links | Universe images are not reaching the managed artwork layer |
| People | 829 person records; 449 represented in the primary-credit projection | The projection itself has a same-name identity bug | A large directory needs bounded display and accurate eligibility |

These counts describe this database snapshot, not the provider catalogue or the maximum obtainable content.

### Live Wikidata evidence

The following requests returned actual data during the audit:

- [Dune franchise Q18011049](https://www.wikidata.org/wiki/Q18011049) is a media franchise, with P1434 pointing to [Dune universe Q769936](https://www.wikidata.org/wiki/Q769936). They are related identities, not aliases of one entity.
- [Dune novel Q190192](https://www.wikidata.org/wiki/Q190192) returned 20 P674 character statements and one P840 location, Arrakis. That explains the narrow initial seed; it does not describe all Dune places or organizations.
- [Bene Gesserit Q641636](https://www.wikidata.org/wiki/Q641636) has fictional-organization types, P1080 Dune universe, and P8345 Dune franchise. [Crysknife Q845440](https://www.wikidata.org/wiki/Q845440) also has explicit Dune universe/franchise links. These are discoverable despite not being listed on the owned novel's direct character/location fields.
- Reverse discovery through the MediaWiki search API returned **182 indexed candidates** for `haswbstatement:P1080=Q769936`, and **235** for `haswbstatement:P8345=Q18011049`. Only the first 20 of each were fetched. They overlap and include different entity kinds; neither number is an accepted entity count or a completeness target.
- [The Expanse TV show Q18389644](https://www.wikidata.org/wiki/Q18389644) returned 10 P674 characters, 10 P840 location statements, P8345 franchise Q131519400, and P144 based-on novel series Q19610143. Its locations include real astronomical entities and an abstract parallel-universe concept.
- Reverse `haswbstatement:P1441=Q18389644` returned 25 indexed candidates, including [Tycho Station Q112965087](https://www.wikidata.org/wiki/Q112965087). Tycho has an appearance relationship without a P1080 universe statement. A P1080-only strategy would miss it.
- [Rocinante Q107297632](https://www.wikidata.org/wiki/Q107297632) is an Expanse spacecraft with P1080 pointing to the novel-series Q19610143. Wikidata does not always model the narrative root as an item typed strictly as a fictional universe. Preserve this evidence without relabeling the novel series itself as a universe.
- [Batman Q2695156](https://www.wikidata.org/wiki/Q2695156) is a character, with P1080 [DC Universe Q1152150](https://www.wikidata.org/wiki/Q1152150) and P8345 [Batman franchise Q4869422](https://www.wikidata.org/wiki/Q4869422). Reverse DC-universe discovery returned 2,320 indexed candidates. Importing all of DC for a few Batman titles would be the wrong default.
- Exact English search for “Outer Planets Alliance” returned no candidates. This is a coverage/search finding, not proof that no relevant entity exists under another label or representation.

### Actual installed-library probe

The diagnostic loaded version `3.9.1+27ebb1edc6db8addc2018939d4987e3443611f7e` from the Engine's existing output.

For Paul Atreides, `GetPropertiesAsync([Q939956], [P1080,P170,P18,Len,Den])` returned only P1080, P170, and P18. `GetEntitiesAsync([Q939956])` returned the label, description, aliases, complete claims, and statement qualifiers. This directly confirms an API-contract mismatch in our caller, rather than missing source descriptions.

The first live `Persons.SearchAsync` probe used the book title Project Hail Mary and correctly returned novelist Q18590295. A follow-up traced the actual audiobook inputs and **reproduced the wrong attribution**: `Name = Andy Weir`, `Role = Performer`, `TitleHint = Part 01` returns footballer Q4761465 with `Found = true` and score 1.0. The underlying reconciliation returns both names at 100 with `Match = false`; the person service discards that ambiguity decision. See the detailed causal chain below.

## Root causes and ownership

### 1. Fictional-entity finalization silently rejects the configured provider

`MetadataHarvestingService.HandleFictionalEntityEnrichmentAsync` accepts only `provider.Name == "wikidata"`. The configured provider is `wikidata_reconciliation`, and `ReconciliationAdapter.Name` returns that configured name. Claims are saved before this check; graph edges, authoritative universe membership, entity enrichment state, and subsequent relationship discovery happen after it.

Paul's stored canonical values already include his father, mother, creator, P1080 universe, and image URL. His entity row is still an unenriched stub. This code path explains the database-wide absence of relationship edges.

**Owner: Tuvima Library.** Dispatch through stable provider identity/capability, preferably the structured graph-evidence contract, rather than a display/configuration-name literal. Add an integration test using the actual configured provider name; an existing scalar-evidence test double uses `Name => "wikidata"`, masking this mismatch.

### 2. Labels, descriptions, images, and aliases are not projected correctly

The fictional adapter requests legacy reconciliation pseudo-properties `Len` and `Den` through a Wikidata property API that only selects real claim properties. Even if a description were supplied through that path, the mapper emits `short_description`, while finalization reads `description`. The repository's enrichment update does not update the entity label. Newly discovered targets are created with QID labels, and the full-entity graph fetch does not request referenced entity labels.

P18 becomes `headshot_url` for every non-work entity, including places and objects. Finalization explicitly passes `imageUrl: null` and does not ingest fictional artwork through the canonical image pipeline. Aliases available from the entity API do not become a searchable fictional-entity alias projection.

**Owner: Tuvima Library integration.** Read the entity header and structured statements once, persist label/short description/aliases/revision explicitly, and route image candidates into managed artwork with the correct subject and role. Preserve overrides. Never present a brief Wikidata description as a full narrative biography.

### 3. Collection rollups and narrative identity are conflated

`ParentCollectionResolver` can create a `CollectionType.Universe` from configured series/franchise/based-on relationships. Batman's persisted `based_on` target is the character. Meanwhile `ArtworkLibraryReadService.LoadUniverseHierarchy` treats the collection QID as a universe QID and filters `fictional_entities` by exact equality.

`RecursiveFictionalEntityService` assigns the first encountered narrative root when creating an entity. A later work links the existing entity but does not reconcile its scope. Dune's characters were initially associated with the franchise; other places arrived under its narrative universe. Expanse novel and TV seeds similarly split across series and franchise.

**Owner: Tuvima Library.** Keep collection identity, structural series, franchise, narrative universe, character, and adaptation distinct. Resolve an explicit narrative browsing scope from typed, sourced relationships. Do not merge QIDs by title, globally replace Batman with DC, or rewrite authored shelves as a side effect.

### 4. Discovery follows a narrow set of outgoing seeds

The work worker seeds Characters, Locations, Events, and Objects from canonical fields. There is no equivalent organization seed field there. Relationship expansion can discover organizations through supported properties, but only after successful finalization. An object field is recognized by the worker, yet the configured work property mapping does not supply a general `narrative_object` discovery source.

The SDK's `EntityGraph` operates on already supplied nodes/edges. It is not a remote universe crawler. Existing child discovery is about seasons, episodes, tracks, and other structural children.

**Ownership: Library policy plus a reusable SDK discovery capability.** Use work seeds, qualified cast-role evidence, typed relationships, and bounded reverse P1080/P8345/P1441 discovery. Verify each candidate with entity data; indexed search hits alone are not membership facts. Support pagination and continuation instead of treating the first page as complete. Wikidata coverage will remain uneven.

### 5. Typing errors would become more visible when enrichment starts working

`RelationshipClaimMap` maps `creator_qid` to Character even though its comment says Person. Real creators would be created as fictional characters. Other target types are inferred from predicates too broadly: position held is a role/concept, and being a setting does not make Earth or Mars fictional.

The configuration maps P793 (significant event in the subject's history) to `narrative_event`. The Expanse scope consequently contains **cancellation** and **revival** as Events. Those concern the production, not events in the story.

**Owner: Tuvima Library.** Classify the target entity and the relationship's meaning before creating it. Keep creators in Persons; use contextual setting links for real places; distinguish production history, narrative events, roles, and concepts. Unknown type should remain unknown rather than defaulting to Character. Preserve qualifiers, continuity/adaptation scope, provenance, and spoiler context.

### 6. Completion and recovery are weaker than the UI implies

Stage 3 treats the presence of any linked fictional entity as settled. A stub therefore satisfies completion even when the queued entity harvest has never finalized. Maintenance can skip a recently marked work once any link exists. The queue is an in-memory bounded channel; consuming a failed request does not persist retry state. Existing relationship targets are skipped without checking whether their enrichment needs retrying.

The configured `Stage3MaxDepth` and `Stage3RateLimitMs` were not found in execution code beyond configuration/contracts; actual relationship depth uses `FictionalEntityEnrichmentDepth`. Existing SDK rate limiting remains valuable, but the settings must describe the controls actually enforced.

**Owner: Tuvima Library coordination.** Durable work state and component-level results are required. More frequent sweeps or simply increasing depth will not solve these defects. Fan-out should persist new jobs without making all consumers wait to write back into the same full channel.

## Andy Weir: correct the match and joins, do not merge unrelated identities

The stored novelist is [Q18590295](https://www.wikidata.org/wiki/Q18590295). The second Person is [Q4761465](https://www.wikidata.org/wiki/Q4761465), the Scottish footballer (1937–1992). It has a Performer role and three raw media links, specifically **Project Hail Mary audiobook Parts 01, 02, and 03**. The primary-credit projection additionally attaches it to **Project Hail Mary.epub** by matching the author name. The audio files have local metadata claims `author = Andy Weir`, `artist = Andy Weir`, and `narrator = Ray Porter`; none of those raw contributor claims supplies the footballer's QID. These are erroneous associations to the novelist's work, not evidence of football-related media. There is no Andy Weir row or override in `fictional_entities` in this snapshot. The reported Character label/no-media presentation was not reproduced in a browser, and should remain a targeted UI reproduction case.

A separate confirmed SQL bug makes this worse: `primary_person_media_credits` joins a person when either the QID matches **or the name matches**, even if a canonical QID exists and disagrees. Both Andy Weirs are therefore projected onto the novelist's four media assets. Hiding people with zero works would not reliably remove this wrong person.

### Reproduced root cause

This is a compound application and SDK defect, not an incorrect biography supplied by Wikidata. Wikidata distinguishes the two people correctly. Our code turns ambiguous name similarity into a confirmed media credit after losing the relevant role and work context.

| Live call against installed SDK 3.9.1 | Selected identity | Found | Score |
|---|---|---|---|
| Andy Weir / Performer / Part 01 | Scottish footballer Q4761465 | true | 1.0000 |
| Andy Weir / Performer / Project Hail Mary | Novelist Q18590295 | true | 1.0000 |
| Andy Weir / Author / Part 01 | Novelist Q18590295 | true | 0.8086 |

The independent underlying reconciliation call with human/musical-group types returns the footballer first and novelist second, both with score 100 and **both `Match = false`**. A score of 1.0 here is exact label similarity, not certainty that the person contributed to this work. Raising the application's acceptance threshold cannot reject this particular false positive.

1. **Audio tags create an unsupported second role.** `AudioProcessor.cs:275` emits `artist` and `album_artist` even for audiobooks, after already interpreting the tags as author/narrator. The actual Part 01 claims contain `author = Andy Weir`, `artist = Andy Weir`, `narrator = Ray Porter`, and `album_artist = Ray Porter`. `PersonReferenceExtractor.cs:73` treats the presence of any `album_artist` key as music-style credit handling, even when `media_type = Audiobooks`. Consequently Andy is independently reconciled as Author and as Performer. Deduplication by role and name does not reuse the already resolved author for the additional role.
2. **The useful work title is discarded.** The asset has both `book_title = Project Hail Mary` and `album = Project Hail Mary`, but `title = Part 01`. `PersonEnrichmentWorker.ResolvePersonWorkTitleHint` (`:443`) has TV and Music branches, but no audiobook branch. The audiobook falls through to its segment title. Thus the SDK receives Performer / Part 01, the exact failing input reproduced above.
3. **Allowing musical groups disables human occupation checks.** SDK `PersonsService.cs:60` includes groups by default for Performer/Artist; `BuildConstraints` (`:208`) then skips the occupation constraint for the entire search. This avoids penalizing bands without human occupations, but also removes the protection against a human footballer. The subsequent occupation fetch populates output without validating role compatibility.
4. **The person API ignores its own ambiguity result.** SDK `ReconciliationService.cs:156` sorts equal scores by numeric QID, putting Q4761465 before Q18590295. It correctly marks the tied candidates as non-matches. `PersonsService.cs:109` takes the first candidate and `:152` sets `Found` using only the numeric threshold. Its optional notable-work reranking cannot disambiguate Part 01. The application trusts `Found`, creates/links that QID, and harvests the footballer's correct biography onto the incorrectly attributed library person.
5. **The read projection spreads the mistake.** `schema.sql:2889` permits a name match even when QIDs disagree. Once both Andy Weirs exist, the credit projection attaches both to the same author name, including the EPUB. This is why simply filtering people by owned-media presence would keep the footballer visible.

The current persisted footballer was created September 20. Its exact original HTTP response was not retained, so the live reproduction is not a replay of that response. However, an independent earlier runtime log (`artifacts/runtime-access-cutover/engine-final.out.log:692`, September 9 artifact) explicitly records the same auto-acceptance of Q4761465 as Performer at score 1.00 and the ensuing media link. The current data, source path, live reproduction, and earlier runtime evidence agree; this failure has already recurred.

### Remediation and acceptance criteria

1. **Normalize credits by media semantics in Tuvima Library.** Audiobook artist/album-artist tags already interpreted as author/narrator must not independently create extra performer credits. Preserve raw tags as source evidence. Merely changing Performer to Narrator would still misattribute the author as narrator. Music must retain valid artist, performer, and group behavior.
2. **Resolve with the parent work context.** Prefer the canonical book title and work identity for audiobook segments, using appropriate album fallback. Preserve `(name, role, work)` through all resolution paths; the separate batch adapter currently deduplicates by name alone and should be corrected as a related risk, although it is not the source of this worker's demonstrated failure. Reuse a known person only with compatible sourced identity/credit evidence, never through a global same-name merge.
3. **Fix SDK acceptance, not just its threshold.** Apply occupation compatibility to human candidates while allowing valid musical groups. Retain candidate ambiguity and decide acceptance from the final evidence and separation between candidates. If title/work evidence reranks candidates, recompute the acceptance decision after reranking rather than blindly using a pre-reranking flag. Missing occupations or P800 notable-work statements are incomplete evidence, not automatic proof of incompatibility; keep unresolved credits when evidence is insufficient. P800 alone is not a complete filmography/bibliography.
4. **Make primary credits QID-first.** A present canonical QID must match by QID. Name fallback is only eligible for unqualified credits with one compatible candidate. Do not let a name override a conflicting identity. Apply the same policy to every corresponding query/migration path.
5. **Record why a match was accepted.** Preserve source tags/provider, interpreted role, parent work context, candidate IDs/scores, disambiguating evidence, matcher version, and acceptance reason. An ambiguous optional contributor should remain unresolved without blocking media ingestion; expose review only where confirmation is actually required.
6. **Repair existing records after preventive fixes.** Re-evaluate name-only/Performer attributions with ambiguous names or incompatible occupations, remove unsupported media links, and rebuild affected credits/artwork contexts. Remove or quarantine an orphan person only after verifying remaining references and user edits. Do not merge the footballer into the novelist or transfer his biography. Correct P170 creator-to-Character typing separately before enabling recursive universe enrichment.

Regression coverage must include duplicate names with different occupations; candidate-order permutations; audiobook `album_artist` plus generic chapter titles; legitimate music groups; one person with multiple genuine roles; missing notable-work metadata; conflicting explicit QIDs; and idempotent repair. Require that unresolved candidates create neither a confirmed credit nor a misleading biography. The live Andy Weir probe should become a captured deterministic fixture, with a small optional live smoke test rather than relying on mutable public data for CI.

No production code or database changes were made during this investigation. Diagnostic source and raw live output are in `.tmp/universe-artwork-audit/Program.cs` and `person-attribution-probe.txt`; the findings above preserve the important results in this proposal.

## Wes Chatham / Amos Burton and artwork search

The TV show's P161 statement names Wes Chatham and its P453 qualifier names Amos Burton. That exact pair is persisted in `character_performer_links` with work Q18389644. People artwork search follows this relation, so searching Amos can retrieve Wes's artwork entry.

The canonical image browser/picker instead searches `artwork_asset_context.search_text`. Wes's stored context is `Wes Chatham Person Portrait actor tmdb`; there are no contexts matching Amos. These surfaces do not share the same matching behavior.

Use one artwork search service with explicit match reasons:

| Search result | Meaning | Display treatment |
|---|---|---|
| Verified image of Amos | Image depicts the character in a known work/continuity | Direct character result |
| Wes's portrait | Depicts the actor who portrays Amos | Related result: “Wes Chatham · portrays Amos Burton in The Expanse” |
| Expanse poster | Used by the show in which Amos appears | Broader related result, available on expansion; not evidence Amos is pictured |
| A personal photo named Amos | Personal annotation, independently authorized | Remains in personal scope; no automatic catalogue identity claim |

Do not turn Wes and Amos into one identity or automatically label every photo of Wes as depicting Amos. Likewise a Batman poster is not proof of every character in its cast. Detailed subject annotations require provider evidence or a user's explicit annotation; no face recognition is implied.

## Proposed data and service changes

Reuse the existing canonical artwork foundation: `artwork_assets`, `entity_artwork_links`, rendition paths, hashes, and managed file retention. The compatibility writer already synchronizes asset/link rows. It does not update `artwork_asset_context` in that path; context generation is currently present at startup and in explicit artwork linking. Make search projection updates part of normal runtime processing and invalidation.

| Contract | Required change |
|---|---|
| Entity identity | A typed reference resolving external IDs to Person, FictionalEntity, Work, Collection, or other supported concepts; redirects and aliases are evidence, not name-based merging |
| Narrative membership | Many-to-many, typed universe/franchise/work-context links with source and scope; retain separate canonical roots rather than one mutable universe string |
| Relationships/appearances | Reuse qualified statement storage; distinguish “member of universe,” “appears in work,” “creator,” “portrays,” and real-world settings |
| Entity summaries | Persist resolved label, aliases, short description, type evidence, source revision, and fetch/finalization status; preserve user overrides |
| Image meaning | Distinguish what an image depicts from where it is used and why it is search-related; add work/continuity and provenance to subject/context relations |
| Editor role capability | Return the supported canonical roles and slot/source subtype for the exact entity and scope; resolve user-facing labels and default priority from one shared catalog rather than hard-coded Razor arrays |
| Search | Shared ranking/projection for View and pickers; names/aliases/QIDs, actual image roles, relevant work and universe context, bounded relationship expansion, and match explanations |
| Refresh | Durable entity/discovery jobs, component statuses, cursors, retries, revision-aware refresh, and transactional projection updates |
| Permissions | Apply accessible library/profile scope before matching, counts, facets, paging, and image delivery; a shared image must not expose labels from inaccessible uses |

The current artwork model has four canonical roles—Primary, Background, Portrait, Logo—while UI filters also expose source types such as Headshot and EpisodeStill. The service already normalizes those aliases, but EpisodeStill, SeasonPoster, and CoverArt all become Primary. Preserve source-type/context predicates when the user chooses a specific image kind, so “Episode stills” does not merely mean every Primary image.

Place SQL in focused repositories/read services as the affected artwork code is refactored, consistent with repository boundaries. Keep contracts in `MediaEngine.Contracts`. Avoid introducing another artwork storage system.

## Artwork implementation work packages

### T1. One navigation destination (P1)

Replace the four Library Artwork links in `ViewSectionShell.razor` with **Library → Artwork Library**, route `/view/artwork`. Preserve Personal navigation, Gallery shortcuts, drag/create behavior, scope selection, and contribution routes. `ViewArtworkPage` uses `ArtworkAssetDto` as its sole primary result type; remove entity-kind tabs and Titles/Series/TV Shows/Albums browse modes from this page.

Handle `/view/artwork/media`, `/people`, `/universes`, and `/assets` as compatibility routes into the same page with equivalent typed filters where possible. Preserve search/selection state and back navigation; do not retain separate implementations. A legacy universe route sets a universe relationship filter, not a new universe directory. The page header is **Artwork Library**, “Search and reuse artwork stored across your Tuvima library,” and an authorized matching-image count.

### T2. Typed asset query and truthful relationships (P2, P3, P6)

Replace the growing positional `BrowseAsync` parameter list with a typed query in `ArtworkLibraryContracts.cs`, propagated through `IEngineApiClient.Artwork`, `EngineApiClient.Artwork`, the assets endpoint in `DisplayEndpoints`, and `ArtworkAssetService`. Put SQL in focused storage/read services. Retain bounded offset/limit to match current contracts; use deterministic tie-breaking by asset ID and benchmark deep paging before introducing a compatible cursor option.

| Query area | Contract and behavior |
|---|---|
| Text | Normalized query, relevance sort when nonempty; aliases and canonical IDs supported |
| Related to | Entity type, typed related entity ID, and supported subtype facets: media/work, person, character, universe, place, organization, event, object, collection; these are relationships, not storage buckets |
| Media | Movies, TV, Books, Audiobooks, Music, Comics; derive through accessible related works |
| Artwork | Canonical role Primary/Background/Portrait/Logo; source-type refinement where needed; all five aspect classes; optional minimum width/height |
| Source and year | Actual provider/source and context year where known; never substitute file timestamps for release year |
| Usage | All, Linked, Currently selected, and Unlinked only if legitimate retained assets exist; distinguish any link from the effective preferred artwork after override resolution |
| Picker target | `targetEntityType`, `targetEntityId`, target role and applicable slot context; independently identify `AlreadyLinked` and already selected for the exact slot |
| Picker scope | Recommended, Related, All Artwork; server-side eligibility and ranking, never client-side filtering of a small first page |
| Display | Relevance, newest added, recently updated, resolution; bounded offset/limit and total matching unique images |

Use OR within a multi-select facet and AND across facets. Related-entity and role filters must qualify the same applicable link when the question is “portraits for this person,” not be satisfied by unrelated uses of the image. Explicitly distinguish search-related context from a direct assignment. Sort unknown dimensions last; separate creation time from link updates so “newest” is predictable.

Extend `ArtworkAssetDto` with a stable display context, canonical identifiers, available renditions, measured dimensions/aspect, provenance, dates, authorized link/usage counts, a bounded context preview, match explanation, and target-link status. Stable display context is deterministic and separate from the best query-match explanation. Load page context in batches; do not fetch each asset or every relationship individually. Use a selected-asset detail query with paged Related to/Used by lists for high-degree images. Show byte size or provider references only when recorded truthfully.

Counts, facets, matches, selected contexts, and delivery must all apply current access policy before paging. An inaccessible relationship must not cause a visible hit, alter ranking in a revealing way, or appear in labels/counts. Validate typed IDs and resolve authoritative labels/IDs server-side rather than trusting client-supplied display metadata.

### T3. One maintained context projection (P2, P6, P7)

Confirmed current gaps: `EntityAssetRepository.SyncCanonicalArtwork` synchronizes assets and links without context; `ArtworkAssetService.LinkAsync` inserts context but omits `canonical_id`; startup migration provides partial backfill. Centralize the context projection so provider ingestion, retail enrichment, people, fictional entities, manual upload, URL import, existing-image linking, and legacy compatibility paths follow the same rules.

Project canonical entity labels, aliases, typed references/subtypes, media type, trustworthy year, semantic role/source type, provider, and external IDs. Index meaningful identity metadata, never filesystem paths. Keep provider provenance attached to its actual source, and distinguish an image's original source from sources of its relationships. Do not infer that a publicity portrait depicts a fictional character because the person played that role.

Represent direct assignments, verified subjects, and derived related context distinctly, including match reason, originating work/continuity, provenance, and source relationship identity where relevant. Character/performer expansion uses qualified cast links already available; broader universe context uses trusted typed membership. Bound expansion and invalidate derived context when the source relationship is corrected or removed. A repaired Andy credit must not leave the footballer discoverable through stale artwork terms.

Update projection and search state atomically for link writes/removals, or persist a durable projection job in the same transaction for changes requiring larger rebuilds. Cover entity renames, alias/identity corrections, type/year changes, artwork replacement, unlink, and deletion. Removing one link must retain contexts supported by other links. Backfill existing rows in resumable, idempotent batches without downloading or duplicating image bytes; record projection version/progress and provide a rebuild path. Respect overrides throughout.

### T4. Indexed search and bounded scale (P2, P7)

Implement artwork FTS5 using the repository's existing trigram and multilingual/CJK search conventions. A B-tree on `search_text` does not make leading-wildcard `LIKE '%query%'` a scalable search index. Reuse query escaping, tokenization, and bounded short-query behavior; test one/two-character and CJK queries rather than silently returning no results because trigrams need longer terms.

Prefer context-level indexed documents carrying their typed relationship/visibility association, then group authorized matches to one asset before ranking/counting/paging. A single unrestricted concatenation of all labels per image would risk matching inaccessible context. Exact IDs and direct subjects rank above alias matches and explained indirect relations; prevent images with many repeated contexts from winning merely because they have more links.

Maintain exact/filter indexes for entity/type/media, role, provider, aspect, and relationship lookups, with composite indexes justified by query plans. Index both changes and removals and include rebuild/migration tests. Debounce UI search, cancel superseded requests, and ignore out-of-order responses. Related-entity selectors use bounded server search. Test at least 10,000 related people and 100,000 images with realistic multi-context records; benchmark first/deep pages, counts, filters, and warm/cold search. Warm local search p95 below 300 ms is a proposed acceptance target on a documented reference machine, not an observed result.

### T5. Shared image browser and Artwork Library screenshot interpretation (P1–P3, P5)

Extract one reusable artwork asset browser used by `/view/artwork` and the picker. Share typed query state, filters, paging, image geometry, grid/list metadata, selection, and inspector. Standalone browse persists readable URL state; picker state is scoped to its invocation without navigating away from the editor. Do not copy the current entity/media card implementation into another component.

Use the earlier Artwork Library reference already incorporated in this proposal as the composition reference for `/view/artwork`: existing app shell, anchored View rail, compact title/count, prominent search and sort/display controls, filter row, image grid, and desktop inspector. The newly attached Spirited Away mockup applies to T6's entity editor, not this page. Do not change global application navigation to copy either mockup. Quick facets fit available width; More filters holds the full supported set including Audiobooks, Object, and Event. Use shared controls, active-filter summary, and a clear reset. Single selection uses accessible state and focus treatment; a checkmark must not imply unsupported bulk selection.

Render measured aspect ratios with shared sizing presets and `object-fit: contain` inside bounded preview regions, keeping portrait/square/landscape/banner geometry recognizable. Give logos a neutral backing and keep `UnsupportedRect` visible at its true ratio. Provide a density control that changes preview size without changing image meaning; list mode uses small shape-aware previews. Browser and picker grids request small suitable renditions, inspectors medium/large, with truthful responsive `srcset`/`sizes`. Originals are reserved for explicit zoom/full-size; missing renditions trigger repair or a bounded placeholder rather than an implicit original download.

Selection is keyed by artwork asset ID. Replace `OpenAssetAsync`'s current conversion to the first entity context, which can lose the selected variant. Inspector content is image-specific: preview, dimensions/aspect, source/reference, date, Related to, Used by, and permitted actions. Link roles and effective use are distinct from semantic relatedness; follow authorized entity links only on explicit navigation. No full media detail cards, cinematic heroes, playback actions, or provider catalogue placeholders. Missing images stay in the existing editor/diagnostics rather than appearing as blank assets.

At approximately 1536×1024 (the reference) and 1920×1080, use a grid beside a bounded inspector; do not hard-code six columns. At lower-height desktop, tablet, and phone, collapse filters and move the inspector into an in-app panel without squeezing previews into unusable widths. Maintain intentional scrolling, return position, keyboard selection, focus restoration, loading/empty/error states, and screen-reader labels. Check action alignment at every breakpoint.

### T6. One role-card-driven entity Artwork screen (P4)

Refactor the existing `ArtworkWorkspace` rather than building another editor. Preserve its header mode, entity/universe context where applicable, natural-ratio preview, variant strip, metadata, canonical upload/URL/link/remove/preferred operations, provider refresh, automatic group fallback, lightbox, callbacks, and dirty-state behavior. Replace `StandardRoles`, `PersonRoles`, and the current role tab row with an overview rendered from the entity workspace capability contract. A universe hierarchy selector, where still required by its existing workspace consumer, remains entity context above the cards and must not become role navigation.

Extend `ArtworkEntityWorkspaceDto` with ordered supported-role descriptors resolved for the exact entity, media type, and editor scope. Replace the identifier-only workspace request with a typed target descriptor, or resolve the equivalent facts server-side; entity ID/type remain authoritative, and media/scope hints must be validated rather than trusting client labels. Keep persisted roles canonical (`Primary`, `Portrait`, `Background`, `Logo`) and retain source/slot distinctions such as `EpisodeStill` and `SeasonPoster`; do not rename database roles for presentation. Introduce one shared role catalog for canonical role, source slot, supported entity/scope combinations, default priority, and a presentation key. Reuse it in `ArtworkScopeService`; one Web `ArtworkRolePresentationResolver` maps the presentation key to the user-facing label and icon. Labels include Poster / Cover for movies and TV series, Poster for seasons, Still for episodes, Cover for books/audiobooks/albums, Portrait for people/characters, and Primary artwork for universes. Unsupported roles do not render merely because a static array contains them, and mutations are validated against the same capability source.

Load the workspace once and derive `SupportedRoles`, `VariantsByRole`, `PreferredVariantByRole`, and `VariantCountByRole` in component state. A role-card click is local state only. Each semantic button exposes selected state, label, linked-variant count, and explicit preferred, automatic, or empty status; it shows only the effective preview, never every variant. Automatic compositions and legacy fallback URLs do not inflate the linked-variant count. Default Portrait for Person/Character, Primary for ordinary media/Universe/Collection, otherwise the first supported role. Preserve the selected role and selected asset when still valid; after a mutation reload the single workspace, retain the role, then select the returned/newly preferred asset. Do not reset to Primary on every reload or picker close.

Below the cards, keep variants filtered to the focused canonical role and applicable slot context. Selecting a thumbnail changes only the preview/actions. Set preferred never deletes alternatives; Remove link removes only the selected assignment; Upload and From URL remain in the right action panel and refresh only after success. Explicit, automatic, and empty states must be distinct. Automatic group compositions remain derived `MediaArtworkGroupPreview` output, never `artwork_assets`; removing an explicit Primary restores the automatic view when available.

Remove the nested artwork-role buttons from `SharedMediaEditorShell` and `CollectionEditorShell`, remove legacy `artwork-background`/`artwork-logo` active-tab routing, and remove the duplicate role tab row from `ArtworkWorkspace`. Ensure every saved, supported media/person/collection editor reaches the shared workspace so the large parallel renderer in `SharedMediaEditorShell` can be retired after behavior parity. Keep inherited/read-only scope messaging and target switching intact. `PersonEditorDialog` continues to expose one Artwork tab and should shed obsolete local role-selection state once the shared workspace owns it. A not-yet-saved collection has no canonical entity target: retain draft files without writing orphan assets, show the same role-card pattern over staged state where practical, and enable canonical library reuse only after an entity ID exists.

Implement the mockup's composition in `ArtworkWorkspace.razor.css`: summary cards in a wrapping or horizontally scrolling grid; selected purple boundary; bounded thumbnail geometry by role; focused preview/variants beside the action panel on desktop; stacked layout at narrower widths. Reuse shared controls and existing editor typography/colors. Do not copy the mockup's sample counts, sources, image content, or fixed crop, and do not change the surrounding editor rail, header, footer, or unrelated tabs.

Component boundaries may include `ArtworkRoleOverview`, `ArtworkRoleCard`, `ArtworkVariantStrip`, and `ArtworkFocusedEditor`, but mutations stay coordinated by one workspace rather than duplicated among call sites. Principal files are `ArtworkWorkspace.razor(.css)`, `SharedMediaEditorShell.razor(.cs/.css)`, `PersonEditorDialog.razor(.css)`, `CollectionEditorShell.razor(.css)`, `ArtworkLibraryContracts.cs`, `ArtworkAssetService`, `ArtworkScopeService`, and the artwork client interfaces.

### T7. Large contextual picker and deliberate actions (P5)

Replace the small library search/grid and its `_source == "library"` state embedded in `ArtworkWorkspace` with a near-viewport in-app dialog/overlay hosting the shared browser. Pass entity ID/type/label, media type, focused canonical role, source slot/context, relevant canonical/universe context, active profile, and current asset/link state. The header shows the authorized target and user-facing role, for example `Spirited Away > Logo`; keep Cancel and Use this artwork reachable alongside preview/inspector. Preserve the underlying editor's target, focused role, selected variant, unsaved state, and dirty-state guard. Selecting/inspecting alone does not write a link; confirm returns the chosen `ArtworkAssetDto` and the workspace calls the existing canonical link endpoint. Disable duplicate submits, retain selection on failure, and restore focus on close.

Recommended prioritizes direct target relationships, then verified related context, role suitability, aspect, and resolution; explain why an image is recommended. Related follows bounded typed entity/work/universe context without requiring the same role. All Artwork removes contextual scoping, not permissions or explicit user filters. Server-defined slot capabilities decide actual compatibility; aspect mismatch is a ranking signal unless the slot requires a hard restriction. Respect the difference between linked to target and already preferred in this exact slot.

After a successful link, close the picker, keep the same role focused, reload the one workspace payload, and select the linked image. Cancel closes without a write or reload. Upload and From URL remain in the focused editor rather than moving into the picker. In standalone browse, an authorized Use this artwork action opens a typed target/slot selector before handing off to the existing editor. Header Upload likewise selects a destination and reuses existing upload/URL controls; it must not create an undocumented orphan-upload workflow. Browse-only users receive inspection without mutations. The Artwork Library screenshot's Open in new tab is optional explicit full-size viewing, not how the picker opens. Never infer a target from the first context of a reused image.

### T8. Preserve storage, deduplication, and existing consumers (P6)

Retain `artwork_assets` with unique `content_hash`, `entity_artwork_links`, and `artwork_asset_context`; do not create entity-specific image stores. Canonical storage remains `AssetPathService.ArtworkRoot` under `.data/assets/artwork`. `ViewStorageService` owns configured personal `Profiles/{profile}/...` and `Shared/...`; artwork queries never enumerate those roots. This work adds no implicit or explicit personal-photo publication bridge.

Existing-image selection is reference-only. Duplicate upload/URL import reuses the canonical hash and rendition set after content verification, including concurrent imports; retain appropriate source/link evidence without replacing unrelated provenance. Unlinking removes only that assignment and updates its search context, preserving shared bytes and other assignments. Orphan retention continues through the existing managed-asset policy and must not expose formerly private or inaccessible content.

Audit consumers before removing entity-oriented code. `ArtworkLibraryReadService`, `ArtworkLibraryItemDto`, and `ArtworkBrowsePageDto` still support editor context, automatic group previews, and universe hierarchy. Remove their role as the main View browser, retain/refactor required consumers, and preserve editor and Universe detail behavior. No redesign of Watch/Read/Listen or Universe detail is included.

### T9. Verification and documentation (P9)

Update `ViewLibrarySurfaceTests` to assert one artwork destination and unchanged Personal/Gallery behavior; retire only assertions that require the old artwork navigation. Add bUnit or equivalent component/interaction coverage proving one Artwork editor destination; no nested or top role tabs; capability-driven role cards and labels; default and retained focus; accurate preferred/automatic/empty status and counts; selected-role-only variants; local switching without API calls; preview selection; Set preferred, Remove link, Refresh, Upload, and URL behavior; automatic restoration; and responsive semantics. Cover movie, TV series, season, episode, book, audiobook, album, person, character, universe, collection, inherited/read-only scope, and an empty supported role. Do not rely only on source-string tests for actual interactions.

Picker tests prove contextual Recommended/Related/All queries, exact target/role/slot context, inspect-before-confirm, cancel without mutation, focus restoration, success returning to the same role and linked asset, failure retaining picker selection, one submission, and reference-only reuse. Update `ViewLibrarySurfaceTests` and editor navigation/state tests only where their expectations intentionally change; retain coverage for all unrelated tabs and dirty-state guards.

API/storage/search integration tests cover every persistence origin; canonical IDs; rename/unlink invalidation; dedupe and simultaneous reuse; multiple entities/roles per image; correct same-link filter semantics; one result per asset; counts and stable paging; FTS ranking, short/CJK queries, and rebuild; permissions before matching/counts/detail/delivery; private roots excluded from artwork enumeration. Verify Recommended/Related/All and exact target-link status separately. Run relevant Web/API/storage/provider regression suites, solution checks, and rendered desktop/tablet/mobile acceptance during implementation, preserving existing authorization and editor tests.

The implementation updates `docs/artwork-architecture.md`, affected View/editor behavior, client/API contracts, schema migration/backfill, and tests together. The architecture now documents Personal versus Library storage/security, one Artwork Library destination, relationship facets, usage versus relatedness, the unified entity Artwork screen, role capability/label rules, shared picker, migration/backfill, and operational recovery.

## Reliable enrichment execution

Use a durable workflow:

`owned-work evidence → typed scope resolution → paged discovery → entity fetch/classification → qualified facts → optional managed images → search projection`

Each step should record queued/running/partial/complete/retryable/unsupported state, attempt count, lease, next retry time, source revision, and cursor. Define complete as “the scheduled bounded work is settled,” never “the universe contains everything.” Distinguish a successful empty response from timeout, provider failure, unclassified candidates, and missing artwork.

Prioritize fast identity/visibility first. Core facts required by ingestion must settle before ingestion claims completion; optional broad discovery/artwork can continue separately with truthful status. Reuse Operations and its durable operation model rather than introducing another workbench or requiring routine sparse metadata to enter Review Queue.

Starting budget proposal, to validate under load: 50 QIDs per fetch batch, up to 100 newly accepted entities per universe run, relationship depth 2, cursor continuation between runs, with shared per-host throttling. These are bounded work budgets, not completeness promises. Keep SDK retry/backoff/Retry-After/maxlag handling; make app settings reflect effective controls. Use revision/TTL refresh and immediate invalidation on identity, alias, membership, or artwork changes.

Core recovery must reschedule unfinished entities after restart even when their parent work has existing links. Persist fan-out jobs before completing their parent stage. Project entity facts and progress atomically enough that a marked-complete entity cannot lack its required graph projection. Avoid duplicate downloads by reusing managed hashes and image-cache ownership.

Fandom lore is an optional supplement after these fixes. Its current provider discovers P6262 article references and reads a bounded first `allpages` response without a durable continuation workflow. It cannot presently guarantee a complete universe catalogue. Any expansion should retain reviewed source association, separate supplemental provenance, pagination, and source-specific image handling. It must not overwrite trusted canonical identities or become a prerequisite for ingestion.

## Implementation sequence and acceptance gates

The immediate artwork track combines both supplied artwork experiences. The data track retains the original investigation's remediation scope. They share typed identity/context contracts and can progress independently where evidence is already trusted; broad universe discovery is not a prerequisite for the new artwork browser or unified editor. After A1, the context/index work in A2 and the editor composition work in A4 can proceed independently; the contextual picker in A5 waits for the shared browser from A3 and the focused-role state contract from A4. Do not enable derived contexts from known-bad attributions until those links are repaired. No track redesigns Universe detail pages, lane browsing, or non-Artwork editor sections.

| Artwork stage | Work package | Acceptance gate |
|---|---|---|
| A1 | Consumer audit, role-capability workspace contract, and typed asset query/detail contracts (T2, T6, T8) | Editor, group-preview, and universe consumers identified; canonical role/slot mapping and labels agreed; filter and usage semantics agreed; access policy applied before counts/paging |
| A2 | Central context projection, backfill, FTS, and indexed filters (T3, T4) | All import/link paths searchable; canonical IDs present where known; rename/unlink removes stale terms; one result per asset; multilingual and authorization tests pass |
| A3 | Single route, shared browser, exact-asset inspector (T1, T5) | One Artwork Library destination; old bookmarks resolve; natural image geometry and selected variant preserved; no media/subject/universe browse modes |
| A4 | Unified entity Artwork screen and editor-shell cleanup (T6) | One Artwork rail item; role cards are capability-driven; switching is local; variants/mutations stay role-scoped; automatic fallback and unrelated tabs are unchanged |
| A5 | Large contextual picker and target-aware reuse (T7, T8) | Recommended/Related/All run on server; target, role, and slot are explicit; Cancel preserves editor state; success returns to the same role; reference-only reuse and content-hash dedupe verified |
| A6 | Product journey, large-library and responsive verification, docs (T9) | P9 journey passes; 10,000-person/100,000-image fixture stays bounded; private View behavior, permissions, focus, role cards, and action alignment pass; architecture docs updated with implemented behavior |

| Separate data stage | Work package | Acceptance gate |
|---|---|---|
| D1 | Correct person resolution in app and SDK, then repair unsupported links (P8) | Generic audiobook chapter never accepts footballer; identical-name ties remain unresolved without evidence; valid bands/sparse metadata work; conflicting IDs do not inherit credits or artwork search terms |
| D2 | Provider dispatch, entity headers, typed creator/setting/event handling (P8) | Actual configured provider persists graph edges; creators stay real people; cancellation/revival are not narrative events |
| D3 | Narrative scope and qualified work/series/franchise membership (P8) | Existing Dune scopes resolve without merging identities; Expanse associations are queryable; Batman character is not used as a universe ID |
| D4 | Durable recovery, bounded discovery, managed entity artwork (P7, P8) | Restart resumes unfinished work; continuations advance; no unbounded DC import; supported P18 artwork uses the canonical persistence/indexing path from A2 |

Release checks for A2/A3 use trustworthy current records and explicit empty states. Expanded universe facets depend on D2–D4 producing real evidence; Amos-related matches use the existing qualified performer link once indexed. Neither mockup is a requirement to synthesize enough art or variant counts to fill its example layout.

Cross-cutting regression fixtures should include actual production provider configuration; Dune franchise/universe split; Batman character/franchise/universe; Expanse's P1441-only locations; real Earth/Mars settings; source production events; creators and collective pseudonyms; identical names with different QIDs; variant artwork and contextual performer matching; empty/partial/rate-limited provider responses; and scope-restricted image contexts.

Use saved provider responses for deterministic tests, plus an opt-in live provider smoke test. Performance target proposal: warm local search p95 under 300 ms on a documented reference machine, with bounded rows and no original downloads in grids. This target has not been benchmarked in this audit.

Implementation verification includes repository build/test checks and affected UI visual validation. Do not use title-specific production patches. The verification did not reset the library, run a broad metadata refresh, or commit an artwork selection; normal Engine startup may apply the additive schema/search backfill. Any later controlled refresh should preserve user edits, stable references, and managed originals and be separately verified against the existing data snapshot.

## Evidence and verification limits

The solution builds with zero warnings and errors. The complete Web (1,109 passed), Providers (503 passed, 34 opt-in integrations skipped), Storage (445 passed), and Ingestion (160 passed) suites pass, along with the new artwork API and contract tests. The local runtime journey was visually verified at the available desktop/narrow viewport through library browse, selection, responsive preview, Manage usage, role editing, and the chooser; no artwork mutation was committed. Three unrelated pre-existing full-suite guardrails remain outside this change: the Domain suite flags an existing private `FirstNonBlank` helper in `ArtworkLibraryReadService`, the API suite expects an older integration-harness source literal, and the Contracts suite's legacy collection expectation omits the existing `audience` field. A strict documentation-site build was not rerun because the available Python environment lacks MkDocs.

Local audit evidence is stored under `.tmp/universe-artwork-audit/`: `local-evidence.json` includes the read-only SQL and result rows; `live-wikidata.json` includes fetched entities and revision IDs; `discovery-probe.json` contains bounded reverse-query responses; `library-probe.txt` and `person-attribution-probe.txt` contain installed-DLL output. The directory is local scratch evidence, not a shipped dependency.

Both supplied plan inputs and the Spirited Away editor and chooser mockups were reviewed against View navigation, editor navigation, `ArtworkWorkspace`, role/slot state, asset contracts/query/link paths, compatibility projection, search schema, storage paths, selection behavior, automatic group behavior, and surface tests. The attachments were treated as design material, not as independent commands. The implementation was compiled, exercised against the local Engine and Dashboard, and visually checked through the Artwork Library, responsive inspector, focused editor, and chooser journey without committing an artwork mutation.

Principal implementation entry points: `MetadataHarvestingService`, `FictionalEntityWorker`, `RecursiveFictionalEntityService`, `RelationshipPopulationService`, `NarrativeRootResolver`, `ParentCollectionResolver`, `UniverseEnrichmentService`, `PersonReconciliationService`, `primary_person_media_credits` in schema.sql, `ArtworkLibraryReadService`, `ArtworkAssetService`, `ArtworkScopeService`, `EntityAssetRepository`, `ArtworkLibraryContracts`, `EngineApiClient.Artwork`, `ViewArtworkPage`, `ArtworkWorkspace`, `SharedMediaEditorShell`, `PersonEditorDialog`, `CollectionEditorShell`, and `ViewSectionShell`. In Tuvima.Wikidata, review `EntityService`, `PersonsService`, reconciliation ambiguity, and a typed paged reverse-discovery API. The SDK already supplies entity headers, aliases, qualified claims, and resilience; reuse those contracts.

## Product-owner summary

View now has one Artwork Library for finding, inspecting, and reusing images, while the existing Personal area remains separate. Each entity editor keeps its familiar shell but reduces Artwork to one destination with summary cards for every supported role and the existing variant tools beneath. **Choose from Library** opens the same large browser, returns to the focused role, and links an existing image without copying it. People and universes narrow results without becoming extra catalogues. The accompanying identity and enrichment fixes prevent known false attributions and make trusted character/universe artwork discoverable through the same managed-image path, while private photos stay private.
