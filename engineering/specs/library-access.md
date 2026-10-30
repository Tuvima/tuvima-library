# Spec: Simpler library access, Plex style (ticks on the person, no per-library access)

> **Status:** Built, awaiting your Local checks (Locked by Shy 2026-10-29) · **Version:** 3 · **Last updated:** 2026-10-29 · **Related docs:** [Simpler access](simpler-access.md) (v5, Built) · [Library pages](library-pages.md) (v15, Built) · [Users, profiles and households](users-profiles-households.md) (v6, Built) · [Backup and restore](backup-and-restore.md) (D10, D19, D20) · [Vault sections and Move to Vault](vault-sections-and-move.md) · [Library setup](library-setup-groups.md) (D68)

> Version 4 note (2026-10-30, Shy): library access is on or off per household only. The per-profile library ticks (D1) and the per-profile area switches (Simpler access D1) are removed by the Locked [Libraries redesign](libraries-redesign.md) spec (D16, D19); the household ceiling (D2), Kid's profile and Kid levels (D4, D9, D10) and the Vault's own access (D5, D6) stand. Built by that spec's WP8 with the security review.

> Written from Shy's bug-log items F83 and F82. Version 1 (2026-10-28) proposed dropping per-profile library access altogether. Version 2 (2026-10-29) replaces that after Shy showed how Plex does it: **who can see which library is set on the person, as ticks under each area**, with household limits above it and rating limits beside it. Section 9 lists exactly what changes; the build (LA6) writes those versions.

> Version 3 (2026-10-29) records Shy's decision that **the Vault becomes a fifth top-level area** beside Watch, Listen, Read and View (see the library setup spec, add-folder flow). It replaces v2's Adult content rule (D6) and the "Keep this library in the Vault" creation tick. Everything else in v2 stays. Locked by Shy on 2026-10-29.

## Plain-English product walkthrough

**The model (like Plex).** Access has three layers, each only able to narrow the one above:
1. **Area** (Watch, Listen, Read, View): switched on or off per profile. Under an area that is on, the libraries in it are listed with a tick each. Ticked means the person sees it; unticking takes it away. Example: Sam has Watch, ticked Movies and TV Shows, unticked Home Videos and Other Videos.
2. **Household ceiling.** The server administrator decides which libraries a household can use at all (all ticked by default). A household administrator then ticks libraries for the household's profiles, but only from what the household already has.
3. **Kid's profile and levels** steer what shows on the home page and in lists: a level per area (Watch, Read, Listen) set once on the household. It is a guide for children, not a lock; parents supervise, and a profile PIN stops switching to an adult profile.
The **Vault** is a fifth area, **Vault**, beside Watch, Listen, Read and View. It has its own per-profile access, per-section access and PIN. A profile without Vault access never sees the Vault button, its libraries, counts or search results, so nothing inside it is ticked per person.

**What changes from today.**
1. **One place for library ticks: the person's page.** Not on the library. The library page loses its **Access tab** and the **Create library** dialog loses the "Standard / Selected profiles" choice (library-centric "Selected profiles" goes). Two places for one setting is what made it hard to manage.
2. **The person's page gets the Plex layout**: per area, a switch, then the libraries with ticks, an **All libraries** tick at the top of each list, and one **Save changes / Discard** bar (like Plex's Save Changes) instead of saving each tick on its own. A short line says who it follows: "Limited by Household 2".
3. **New libraries and new profiles start with everything visible** (as today), so nothing goes missing by accident. A new profile of a household gets what the household has.
4. **Households page gets the ceiling list** (server administrator only).
5. **The folder "Suitable for limited profiles" tick and the profile "Allow unrated" switch are replaced** (Shy, answers 2 and 3) by one household switch, "Show unrated items", in the Kid settings.
6. **Kid's profile switch.** One switch on the person's page; what a Kid can see is one household setting, a level each for Watch, Read and Listen (D9), plus one "Show unrated items" switch (D10).
7. **The Vault is its own area, and Adult content exists only there** (Shy, 2026-10-29). Add library in the Vault offers any content type (Movies, TV Shows, Music, Books, Adult content, Other); the normal Add library never offers Adult content or any Vault option. No "Keep in the Vault" tick, no "administrators only until chosen" state.
8. **All export and import of people is removed** (Shy, answer 5, F82). Backup stays.

**What stays the same:** area switches, hidden-library storage per profile, household inheritance, rating limits and Kids profiles, the Vault's own access, sections and PIN, View's own spaces and per-profile View switches, administrators seeing everything.

## 1. Goal

Make access easy to read and hard to get wrong, the way Plex households already understand it: pick the person, see their areas and libraries, tick what they may use. Remove the second, library-side way of setting it and the folder exception.

## 2. Scope

**In scope**
- Remove "Selected profiles" libraries, the library Access tab and the Access choice in Create library and setup; remove the restricted marker.
- Rebuild the person's library list as grouped ticks with All libraries and one Save bar.
- Add the household ceiling (data, Engine rule, Households page).
- Replace the folder "Suitable for limited profiles" setting and the per-profile "Allow unrated" with one household "Show unrated items" switch, and simplify the content-limit filter.
- Vault as a fifth area and Adult content only there (D6).
- Remove all export and import of people (F82).
- Mark affected Local checks in the batch as superseded.

**Out of scope**
- Rating limit per media type like Plex's Restrictions tab (replaced by a level per area on the household, D9).
- Free-form labels with allow and exclude lists, and single-title blocking (still not built). The library switch in D10 replaces them.
- Vault sections, PIN, Move to Vault; View's own switches; plugin linking (household trust).

## 3. Decisions made

- **D1. Access is set on the person, as ticks under each area** (Shy, 2026-10-29, Plex model; revises Simpler access D5 into a single Save bar and an All libraries tick). It is the only place a person's libraries are set. Reason: Ease of use and Uniformity.
- **D2. A household has a ceiling** (Shy, 2026-10-29): "if a household is added, the only thing they can restrict their household from is what they already have." The server administrator sets which libraries a household may use; a household administrator can untick more for its profiles but never tick what the household lacks. Replaces Simpler access D4 and Users D19's "primary profile's hidden libraries" inheritance with an explicit household list. Reason: Privacy and Reliability.
- **D3. Nothing is hidden by default** (Simpler access D2 stays): a new library appears for every household and profile that has its area; a new profile gets what its household has. The library-side "Selected profiles" and its "new profiles start hidden" rule (D16) are removed.
- **D4. Children: Kid's profile levels and one "Show unrated items" switch only** (Shy, 2026-10-28, revised 2026-10-29). No per-library, per-folder or per-title choices. The levels steer home page and defaults; parents supervise; a PIN guards an adult profile.
- **D5. Anything private goes in the Vault** (Shy, 2026-10-28), which keeps its own access. The Vault is a top-level area (D6), never listed in person ticks or household ceilings.
- **D6. The Vault is a fifth top-level area, and Adult content exists only there** (Shy, 2026-10-29; replaces v2's D6 and reverses the earlier choice that Adult content could be an ordinary Watch library, and the earlier choice that the Vault is only an option on a library). Add library inside the Vault offers Movies, TV Shows, Music, Books, Adult content and Other; each content type still has at most one Vault library (Library pages D1). The normal Add library screens say nothing about the Vault. Who may open the Vault is set per profile as today (Vault sections D28, D39; the PIN unlock stays as a second step). A Kid's profile can never be given Vault access (Shy, 2026-10-29; the build checks it matches today's rule). Adult content always counts as adult-rated. Existing Vault libraries and Move to Vault are unchanged. Trade-off accepted: Adult content can no longer sit in the ordinary Watch list. Reason: Privacy and Ease of use.
- **D7. All export and import of people is removed; Backup is the only way to move data** (Shy, 2026-10-28, F82: "nobody is going to move between libraries"). Covers "Export this household", "Export my data", per-person Export and "Import a user". A full backup restore still rebuilds every household.
- **D9. A profile is either a Kid's profile or not, and what a Kid's profile can see is one editable household setting** (Shy, 2026-10-29: a Kid's profile is just a preset of ratings; the definition of a Kid is the changeable part). On the person's page: a single switch, **Kid's profile**, off by default; off sees everything in its libraries. Kid settings live on the household (Settings > Profiles, administrators only): **a maximum level for each area, Watch, Read and Listen** (Shy, 2026-10-29, answer 2B: books and music can carry language you care about less than a film), each a plain choice from the existing G / PG / PG-13 / R ladder (`ProfileContentLimits`) described by what it shows. Defaults: PG for all three, unrated shown. Changing it updates every Kid profile at once. View's own switches are unaffected. Replaces the per-profile rating dropdown and the Kids tick; anything the Kids flag does beyond the limit is checked before building and tied to the switch. Reason: Ease of use.
- **D10. Unrated items: Kids either see them or don't, one household setting** (Shy, 2026-10-29, replaces the proposed Kid-friendly label). In the Kid settings, one switch **Show unrated items**, **on by default** (Shy, 2026-10-29). It applies to every area and every item with no native rating (home videos, most books and music). Rated items always follow the level. There is no label, no per-library, per-folder or per-title choice. Replaces Simpler access D7 and the per-profile "Allow unrated". Reason: Ease of use and Maintenance.
- **D8. Pre-release fresh start** (project rule): the restricted marker, library access value and folder flag are dropped, not converted. An old libraries file is refused with the existing plain message.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Administrator opens a person's page | Under **Access**: each area as a switch; under an area that is on, its libraries with a tick each and an **All libraries** tick; unticked ones are Plex-style "not shared". One **Save changes / Discard** bar. Areas with one library show no list. |
| Who may change access | Only administrators. A Kid's profile, and any ordinary member, can never change their own or anyone else's access, levels or Kid settings; they see only what they have. The server administrator can change anyone and the household ceilings. A household administrator can change profiles in their own household, only within the household's ceiling and never above what their own profile holds (as Users, profiles and households D19 and section 10 already say). Switching to an administrator's profile needs that profile's PIN if one is set. Not changed by this spec. |
| Library limited by the household | Shown greyed with "Not available to {household}"; cannot be ticked. |
| Household administrator | Same page for their household's profiles; only the household's libraries appear. |
| Server administrator, Households page | Per household, "Libraries this household can use", all ticked by default; Save changes / Discard. The Vault is never listed (its access is per profile, D6). |
| Library page | Tabs: Folders, File handling, Activity (Lookup where it applies). No Access tab. No "who can see this" line (Shy, answer 1). |
| Libraries overview | Columns: Name, Content type, Folders and titles, Status. No "who can see it". Vault libraries show only inside the Vault area. |
| Create library | Area (Watch, Listen, Read, View), name, content type, first folder. No Access choice, no Vault option; none of this in first-run setup. Add library inside the Vault area (administrators with Vault access) offers every content type, Adult content included. |
| Folder row | No "Suitable for limited profiles" switch. |
| Person's page | A "Kid's profile" switch. No rating dropdown, no Allow unrated. |
| Settings > Profiles, household | "What Kids can see": a level each for Watch, Read and Listen, and one "Show unrated items" switch. |
| Folder move or kind change | The "{name} won't see these 40 files" line stays, now naming households or profiles who lose it. |
| People and Security pages | No Export or Import for people. |
| Phone / TV | Read-only, as today. |
| Error / empty | As today. |

## 5. Data / model changes

- **Kept:** `profile_library_hidden`.
- **Removed:** the per-library "restricted" marker, the library `access` value (Standard / Selected profiles), folder `ok_for_limited_profiles`, the profile "allow unrated" setting and the single per-profile limit.
- **Added:** a profile "is Kid's profile" flag (the Kids flag today, to be confirmed); household "Kid levels" (one per area) and "show unrated items" (default false).
- **Added:** `household_library_hidden` (household id, library id, created at, created by); Vault ids refused; backup-classified as configuration.
- **Rule:** a library is visible to a profile when it is not hidden for its household and not hidden for the profile (administrators see everything).
- **Libraries file:** new format version; the library keeps its area, which may be Vault.
- **Wire:** access contracts and the household contracts gain the household list; library, folder and user-transfer contracts lose the removed fields and actions; fixtures refreshed.
- **Data step:** next free number, to be confirmed.

## 6. Work packets

Security review on LA1, LA2 and LA4 before merge.

### LA1 — Household ceiling and one rule in the Engine
- **What it does:** Adds `household_library_hidden`; effective visibility = not hidden for the household and not for the profile; removes the restricted marker and its new-profile rule; updates `ViewerAccessService`, `LibraryVisibilityRule`, `AuthorizedDisplayProjectionReadService`, `CatalogueResourceAuthorization`, `ConfigurationLibraryAccessCatalog`, `ViewEndpoints`, `LibraryAccessEndpoints`, `LibraryAccessChangeService`, `ProfileAccessRepository`, `BrowseLibraryListService`, backup classification and the single-rule guardrail (to be confirmed). A household administrator cannot show what the household lacks.
- **Depends on:** none · **Size:** Complex (security)
- **Done when:** a library hidden for Household 2 is absent for all its profiles in browse, search, detail, player, artwork, events and counts and present elsewhere; a profile hidden from one library keeps the rest; a household administrator cannot tick a household-hidden library; Vault ids refused; administrators see everything; the guardrail fails on a seeded second rule.

### LA2 — Remove folder "Suitable for limited profiles"
- **What it does:** Removes `ok_for_limited_profiles` from folder config, validator, wire and the folder switch (`LibraryFoldersPanel`, `LibraryFolderTable`, `LibraryFolderGrid`); adds the household Kid levels and "show unrated items"; `ContentLimitFilter` and `ContentLimitPolicy` apply the household's level for the item's area to Kid's profiles, show unrated items to Kid's profiles only when the switch is on, and let rated items follow the level; Adult content starting state removed.
- **Depends on:** LA1 · **Size:** Complex (security)
- **Done when:** a Kid's profile at PG sees every unrated item with the switch on (the default) and none with it off; a rated R film stays hidden either way; Watch, Read and Listen follow their own levels; non-Kid profiles are unaffected; counts match lists; unticked Adult content is visible to a non-Kid profile.

### LA3 — Dashboard: person-side ticks, no library-side access
- **What it does:** Removes `LibraryAccessTab`, `LibraryAccessOptions`, the Access column and the Access choice in `CreateLibraryDialog` (no Vault tick; the Vault is its own area in Add library), and setup copy; adds the Kid's profile switch and, on Settings > Profiles, the household's Kid levels (D9), in place of the rating dropdown and Kids tick, and rebuilds the Libraries list on `ProfileDetailPage` and `ManagedAccessUserPermissions` as grouped ticks with All libraries and one Save bar, household-limited state shown greyed; View keeps its own switches.
- **Depends on:** LA1 · **Size:** Standard
- **Done when:** the profile type choice sets the same stored limit the dropdown did, and a new Kid's profile uses the household's Kid levels, starting on PG for each area with unrated items shown; library pages show no Access tab; Create library has no Access choice; a person's page shows area switches with libraries beneath, saves once, and greys household-limited libraries; bUnit tests updated; visual checks at desktop and phone.

### LA4 — Households page: the ceiling list
- **What it does:** "Libraries this household can use" with Save changes / Discard on the Households page (server administrator only); Engine action; "who loses sight" names households and profiles.
- **Depends on:** LA1 · **Size:** Standard (security)
- **Done when:** unticking a library for Household 2 hides it from its profiles only; new libraries default ticked.

### LA5 — Remove export and import of people (F82, D7)
- **What it does:** Removes the buttons and handlers in `ProfilesTab`, Security and Users and Sign-in; user-transfer actions, services, `UserImportRepository`, `UserExportScope`, contracts and tests (`UserTransferEndpoints`, `UserTransferService`, `IUserTransferService`, `DashboardIdentityClient`); Backup spec D19, D20 and WP6 become "removed". Backup and restore stay (to be confirmed which shared code backup still uses).
- **Depends on:** none · **Size:** Standard
- **Done when:** no export or import route, button or wire type for people remains; backup and restore tests pass; a full restore rebuilds every household.

### LA6 — Docs, specs and batch
- **What it does:** New versions of Simpler access (v5), Library pages (v15), Users, profiles and households (v6) per section 9; `docs/architecture/security.md`, `docs/guides/library-settings.md`, `docs/reference/database-schema.md`, `docs/reference/api-endpoints.md`, `docs/architecture/backup-and-restore.md`, `.claude/rules/security.md`; marks superseded rows in `engineering/validation/batch.md`; appends Local checks; docs PR in `tuvima-docs` if a mirrored page changes.
- **Depends on:** LA1 to LA5 · **Size:** Mechanical
- **Done when:** a docs grep finds no "Selected profiles", "suitable for limited profiles" or personal export describing current behaviour.

Waves: LA1 with LA5; then LA2, LA3 and LA4; then LA6.

## 7. Local checks

- **L1.** Open a person's page: areas with libraries beneath; untick Home Videos, press Save changes; that person no longer sees it in lanes, search, Continue or the phone app; All libraries restores it.
- **L2.** Untick a library on the Households page for Household 2: its administrator's profile list greys it and cannot tick it; Household 1 is unaffected.
- **L3.** Add a library and a profile: both visible straight away, nothing to tick.
- **L4.** A Kid's profile (PG) sees unrated videos, books and music by default and no rated R film; turn off "Show unrated items" and the unrated ones disappear.
- **L4b.** Turn on Kid's profile for a test profile: lists narrow to the Kid level across movies, TV, books and music. Change the household's Kid level: every Kid profile follows at once. Turn off "Show unrated items": Kids lose unrated items everywhere; a rated R film stays hidden either way. Set Listen to G and Watch to PG-13: music narrows more than movies.
- **L5.** As an administrator with Vault access, the Vault button appears beside Watch, Listen, Read and View; add Adult content there. A profile without Vault access sees no Vault button, libraries, counts or search results; Adult content is not offered in the normal Add library.
- **L6.** No Export or Import for people anywhere; a full backup restore brings back every household.

## 8. Risks and open questions

- **Behaviour change on update** (fresh start, D8): library choices set before are lost; re-check person pages once.
- **Unrated items for children (D10):** "Show unrated items" is on by default, so Kid's profiles see every unrated item in every library, including unrated books and music with strong language, until a parent turns it off. Accepted: parents supervise; the Vault holds anything private.
- **No way to move a person between Libraries (D7).** Accepted.
- **Open questions:** none; Shy answered 1A, 2B and 3 (library-level, unrated only) on 2026-10-29.

## 9. Gaps and drift: exactly what changes in the existing specs

**Simpler access (v4 → v5).**
- D4 (inherit the primary account's hidden libraries): replaced by the household ceiling (D2 here).
- D5 (untick list that saves on each tick): replaced by grouped ticks with All libraries and one Save bar.
- D7 (folder "Suitable for limited profiles") and the "allow unrated" part of D6: replaced by the household "Show unrated items" switch (D10 here) and a household level per area. D14 follows D10.
- D11 (one rule): the question becomes "hidden for this household or this profile?".
- D13 (who loses sight): names households or profiles.
- D16 (Selected profiles, new profiles start hidden): removed; WP7 removed; WP3 removed; WP2 reworked as LA3.
- D1, D2, D3, D6, D8, D9, D10, D12, D15 stay.

**Library pages (v14 → v15).**
- Walkthrough items 2 and 3, D9 (three Access choices), the Access tab, the overview "Access" column and WP6: removed.
- D10 and D11: the Vault is a fifth area, not a creation tick on a library; libraries are created in it, never converted. D12 and D21: Adult content exists only in the Vault. D1: one normal and one Vault library per content type stays.
- D21: the "unticking starts as Selected profiles (administrators only)" default goes.
- D13 first-run "Standard access only" line goes.

**Users, profiles and households (v5 → v6).**
- D19: hidden libraries stay on the profile; inheritance from the primary profile is replaced by the household list; a household administrator cannot show what the household lacks.
- D9 and D16 (restore and import under a chosen user; export by the server administrator): removed (D7 here).

**Backup and restore (Built):** D10 (export is the same as backup), D19, D20 and WP6 removed.

**Others, notes only:** Library setup D68 points here; Vault sections D26a and D42 are rewritten for the fifth area (D42: Adult content only in the Vault) and lose their Selected-profiles wording; the library setup add-folder spec depends on this v3; Videos D55 unaffected.

## Plain English summary

Access works like Plex: open a person, see Watch, Listen and Read, tick the libraries they may use, and press Save. A household can only be given, and can only hand out, what the server administrator allows it. Children are steered by their rating limit and watched by their parents; anything private goes in the Vault. The Access tab on libraries, the "Selected profiles" choice and the kids folder tick go, so there is one place to manage access. People can no longer be exported to other Libraries; Backup still covers everything.
