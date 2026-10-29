# Spec: Vault sections and Move to Vault

> **Status:** Built, awaiting your Local checks · **Version:** 5 · **Last updated:** 2026-10-27 · **Related docs:** [Product overview](../product/overview.md) · [Videos](videos.md) (D17, D18, D21, D25, D27, D28, D33, D45, D52, D54, D55, D56) · [Simpler library setup](library-setup-groups.md) · [Delete a show, season, album or Series](delete-groups.md) · [Safer deleting in View](safer-deleting-in-view.md)

> Split out of the library setup spec on 2026-10-21. Version 4 (2026-10-22) folds in the independent security review against the Vault as merged (PR #374 and follow-ups #414, #415) and Shy's answers to its five decisions (D34 to D38). Locked by Shy on 2026-10-22, which approves the changes it makes to Videos D21, D27, D28, D54 and to CLAUDE.md "Never" rule 7. Because it is a security, storage and access change, the build gets the extra security review before any merge.

> Version 5 (2026-10-27) follows the [Library pages spec](library-pages.md): the Vault is no longer set up from its own card, wizard tile or root folder. A Vault library is created as one (Access: Vault-protected) from the ordinary Add library and Add folder dialogs, and items move in with Move to Vault. Vault access is per profile and a profile can hide the Vault from the navigation bar. Approved by Shy on 2026-10-27. Build order unchanged: the central access service (WP0) first, then the extra security review before any merge.

## 1. Goal

Let an administrator keep ordinary kinds of media (Movies, TV, Music, Photos and so on) in the Vault, privately, with their own search and permissions, and move items from the normal library into the Vault and back, without any title, count or name leaking to a profile that cannot see the Vault.

## 2. Scope

**In scope:** Vault kinds that are full libraries; separate search and permissions; per-section access; people pages that show Vault titles only in Vault mode; Move to Vault and Move out of Vault; closing every leak listed in section 4; documentation of why the Vault is separate on disk.

**Out of scope:** the setup screens and Add folder wizard (library setup spec); the adult kinds' own lookup rules (Videos); community plugins (parked).

## 3. Decisions made

- **D26. A Vault kind is a full library of its own, with separate search and permissions** (Shy, 2026-10-21). Its titles live in a separate index and never appear in normal search, shelves, counts, events or notifications. Reason: Privacy and Reliability.
- **D27. People pages show Vault titles only in Vault mode, and say so** (Shy, 2026-10-21). One canonical page per person; with Vault mode off it lists only titles outside the Vault and counts only those; with it on it lists everything, Vault ones marked with a lock and "In the Vault". Amends Videos D28.
- **D28. Access is per Vault section, set by an administrator** (Shy, 2026-10-21). A profile can be allowed into some sections only. Only administrators add Vault folders, change access and move items. Sections are stored in their own profile-level table, never as account library grants (Videos D55b stays: a Vault library can never be an account grant). *Amendment note (Shy, 2026-10-23): nothing is ticked by default; a profile opens no section until an administrator ticks it. Account library grants are replaced by hidden libraries in the [Simpler access](simpler-access.md) spec; a Vault library is never in that list either.*
- **D29. Vault and non-Vault files are never mixed in one folder** (Shy, 2026-10-21). Mostly built already as Videos D56 (a folder holding ordinary files is refused). Remaining work: also refuse a folder that is inside, contains, or is the same as a normal library folder, and one that holds known photos; check for junctions and symlinks in the Vault folder path.
- **D26a (v5, Library pages D1, D10).** A Vault kind is a library whose Access is Vault-protected. Each content type has at most one normal and one Vault library. The Vault has no separate setup screens, and the root-folder option of library setup D32 is dropped. A Vault library is never listed in Selected-profiles pickers or untick lists.
- **D29a (v5, Library pages D5).** The folder picker applies the D29 checks inline and refuses, with a plain message, a Vault folder inside, containing or equal to a normal library folder, or one holding ordinary files.
- **D30. Moving a file out of a Read-only folder into the Vault is allowed as one explicit, separate action** (Shy, 2026-10-21). It is its own server-side action, not a switch on the general move or delete path, so it can never unlock delete or rename. It needs the administrator PIN checked on the Engine, a plain-language confirmation naming the folder that will change, and a saved record before any file is touched. It extends CLAUDE.md "Never" rule 7 by this one named exception; **only Shy can approve that wording change**, and approving this spec for locking is that approval only if Shy says so explicitly.
  - *v5 note (Shy, 2026-10-27):* creating a Vault library or folder needs only the signed-in server administrator and no extra PIN; a Vault PIN, if set, still guards viewing and this override.
- **D32. One shared people list, one central access service** (Shy, 2026-10-21). Every read goes through one service that applies, in order: Vault and its sections, Shared Library, content limit, documents-off, personal spaces. WP0 **extends the existing `ContentLimitFilter`** rather than starting again. A guardrail test fails the build if a read path with a viewer skips it. Reads with no viewer (outbox, background jobs) still rely on the Vault SQL predicates, and the guardrail covers both.
- **D33. Move to Vault needs a Managed Vault folder of the right kind** (Shy, 2026-10-21). Today every Vault folder is added Read-only, so this spec adds **Managed Vault folders**: an administrator-only choice at Add folder, still refused if the folder mixes with a normal library (D29). Read-only Vault folders can hold things but are never a move destination.
  - *v5 addition (Library pages D11):* the Move to Vault preview checks the destination first, so a move never fails for lack of one. No Vault-protected library of that content type: the preview says so and an administrator gets **Add a Vault library**, which opens Create library preselected and continues the move when it is created. Only a Read-only Vault folder: it says Library needs a folder it manages and offers the explicit override or adding one. Several Vault folders: the person picks, import destination preselected. Offline or full destination: named, nothing moves.
- **D31. Move to Vault: design** (Shy, 2026-10-21; refined by D36 to D38). "Move to Vault" is in the "..." menu in Editing Mode (desktop only, administrators only, Vault mode on, administrator PIN), works on several items, has the same preview and re-check as Delete from library, and is crash-safe (see D37).
- **D34. Private Movies may look up TMDB and Wikidata only with an explicit per-section opt-in** (Shy, 2026-10-22, answer 1A). Today the Vault is local-only for ordinary kinds (Videos D21/D26, `ProviderAudienceGate`, `LocalOnly` policy, a schema check limiting lookups to none or Stash-box). This spec reverses that only where the administrator turns on "Look up details online" for that section. The setting shows a privacy note: "Titles from this section will be sent to online services." Default is off. Needs a revised Videos D21 and the lookup guardrail.
- **D35. Vault films never join shared Series and Universes; only the shared people list is shared** (Shy, 2026-10-22, answer 2A). A Vault Movie never gets a Series or Universe, so the "a Vault work never joins a Universe" rule in event routing stays true. People links carry a Vault flag (D27, D32).
- **D36. A move keeps nothing by identity; it re-adds and copies across** (Shy, 2026-10-22, answer 3A). The schema triggers that forbid a file changing library stay. Move to Vault removes the item and adds it to the Vault library, copying progress, ratings and bookmarks to the new item. Move out does the reverse. History kept while an item is in the Vault stays hidden from the normal library (Videos `personal_activity` rule).
- **D37. Moves are durable and honest about failure** (security review). A move is a durable job row saved before any file is touched, with a hash check after copying and recovery after a crash. If a copy to another drive succeeds but the original cannot be deleted (read-only share, NAS), the item is shown as "Moved, original still in place" and appears only in the Vault; the leftover original is reported to the administrator and never shown to other profiles. New file names go through the organiser's cleaning. Artwork and thumbnails left in shared stores are removed, the search entry is dropped immediately, and no event naming the title is sent to anyone.
- **D38. Move out goes only to a Managed folder, never into a Read-only one** (Shy, 2026-10-22, answer 4A).
- **D39. One unlock opens every section the profile is allowed; profiles with a content limit never get a section** (Shy, 2026-10-22, answer 5A). Keeps one PIN and one unlock per device session (Videos D18, D52). Per-section grants say which sections an unlocked session can open.
- **D40. Moves are recorded without the title** (security review). The activity entry for a move says "An item was moved to the Vault" with no title, id or path in anything a non-Vault profile can read; the full record lives only in the Vault activity list (new kinds: moved in, moved out).
- **D41. Vault access is per profile, and the navigation entry is optional** (Shy, 2026-10-27). A profile without Vault access sees no Vault entry, library, count or result anywhere. A profile with access has a privacy switch "Show Vault in the navigation bar" (Settings, the user's own section; proposed default off); with it off the Vault is reached only from that Settings section, then the PIN unlock. Viewing keeps the PIN unlock as a second step (Videos D17, D18, D52).
- **D42. Adult content is a Watch kind with a Vault tick on by default, holding videos and pictures** (Library pages D12, D21). Unticked, the same library appears in Watch with its videos and pictures; its Access then starts as Selected profiles (administrators only) and it always counts as adult-rated.
- **D43. Details lookup extends D34.** Local-only plugins work on Vault items by default. Plugins that send data out follow the section's "Look up details online" setting. Stash-box is the Vault's own source.

## 4. Leaks the build must close (from the security review)

1. **Lookups blocked today:** schema check (`0030`), `ProviderAudienceGate`, `LocalOnly` policy: change per D34 only.
2. **People counts:** `/persons/presence` and the people read services return unfiltered counts; must go through the access service (D27, D32).
3. **Events:** `IntercomAudience` treats person-enriched events as Vault-free; people seen only in Vault films must not broadcast their names.
4. **Ingestion:** `PersonRepository.LinkToMediaAssetAsync` and `CollectionAssignmentService` have no Vault guard; Vault ingestion now runs the full chain, so they need one (D35).
5. **Local AI and recommendations:** batch runners already read Vault metadata; they join the access service. Also check vector stores, provider caches, and backup person preferences for Vault-only people (UNVERIFIED).
6. **Move activity entry** includes the title today; fixed by D40.
7. **Photos folders** are not checked by `HoldsOrdinaryFilesAsync`; extend it (D29).
8. **Junctions and symlinks** in a Vault folder could put files under a normal library; check on Add folder and on every move.
9. **`AccountAccessMutationService.ValidateLibraries`** still accepts a Vault library id; refuse it (Videos D55b).

## 5. Data / model changes

A Vault flag on shared people links, a profile-level per-section grant table, a per-section "Look up details online" flag, Managed Vault folders, a durable move-job table, new Vault activity kinds, and changes to the `vault_only` and lookup triggers named in the review. Pre-release, so a reset is acceptable (project rule).

## 6. Work packets

### WP0 — Central access service
- **What it does:** Inventories every read path, extends `ContentLimitFilter` into the single access service of D32, moves Shared Library, View, people, events and Vault checks behind it, and adds the guardrail test (covers viewer reads and no-viewer reads).
- **Files likely touched:** `src/MediaEngine.Api/Security/*`, `ContentLimitPolicy.cs`, `IntercomAudience.cs`, people read services, search, `tests/`
- **Depends on:** none (ships first; also protects PG limits)
- **Size:** Complex
- **Done when:** the guardrail fails if a read path skips the service; non-Vault viewers see identical results to today; people counts and events leak nothing (leaks 2 and 3).

### WP1 — Revise Videos D21, D27, D28, D54 and add grants
- **What it does:** Revises those decisions and the Vault lookup guardrail; adds the per-section grant table (D28, D39) and the per-section online-lookup flag (D34).
- **Depends on:** WP0, Shy's approval of the Videos and CLAUDE.md changes
- **Size:** Complex
- **Done when:** a profile granted only Movies cannot reach any other section by any route; online lookup is off unless turned on per section and sends nothing otherwise.

### WP2 — Sections: separate index, search, people and Managed folders
- **What it does:** Vault kinds as full libraries (D26), Vault-mode people pages (D27), Vault flag on people links, no Series or Universe for Vault films (D35), Managed Vault folders (D33; v5: the Vault-protected option in Add library and Add folder, no wizard tile), folder checks (D29, leaks 4, 7, 8, 9).
- **Depends on:** WP0, WP1, library setup WP3
- **Size:** Complex
- **Done when:** a Vault movie never appears in normal search, shelves, counts, events or another profile's view; a person's page counts and lists Vault titles only with Vault mode on.

### WP3 — Move to Vault and Move out
- **What it does:** D30, D31, D36, D37, D38, D40: separate override action with PIN, durable job, hash check, re-add and copy, leftover handling, titleless activity.
- **Files likely touched:** move services, `SourceMutationPolicyGate` (security review), editing menu, Vault activity, tests
- **Depends on:** WP2
- **Size:** Complex
- **Done when:** with no Vault-protected Movies library the preview says so and offers Add a Vault library; a Managed-folder movie and a Read-only-folder movie both move in and out with progress kept; a failure at each step (crash, failed delete, failed copy) leaves all records consistent; no title appears in any non-Vault log, event or search.

### WP5 (v5) — Per-profile Vault navigation switch
- **What it does:** D41: the "Show Vault in the navigation bar" profile switch, the navigation entry only for profiles with access and the switch on, and the Settings entry point (same as Library pages WP9b).
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** a profile with the switch off has no Vault link on any screen size but can open the Vault from Settings.

### WP4 — Docs
- **What it does:** Public Vault page with the rationale for separation on disk and the privacy note on online lookups.
- **Depends on:** WP1 to WP3
- **Size:** Mechanical

## 7. Local checks

- **L1.** A profile allowed only into Vault Movies sees them and nothing else; search never returns them to anyone else.
- **L2.** Move a movie from a Managed folder, then one from a Read-only folder (with the override message), into Vault Movies; both vanish from Watch and appear in the Vault with progress intact; Move out puts them back in a Managed folder only.
- **L3.** An actor in one normal film and one Vault film shows one film with Vault mode off and two (one marked "In the Vault") with it on.
- **L4.** Turn on online lookup for Vault Movies and see the privacy note; with it off nothing leaves the machine.
- **L6 (v5).** As an administrator create Private Movies as Vault-protected from Add library with no PIN prompt.
- **L7 (v5).** Move a film when there is no Vault Movies library and follow Add a Vault library; the move continues.
- **L8 (v5).** A profile with the navigation switch off has no Vault link anywhere but can open it from Settings.
- **L5.** Pull the plug mid-move and restart: nothing is lost or shown twice.

## 8. Risks

- A leak between the two worlds is a privacy failure; every path needs a test.
- Copy-then-delete across drives can fail halfway (D37).
- Unchecked: Local AI vector stores, provider caches, backup person preferences naming Vault-only people.

## Plain English summary

One central permission service will decide what every profile can see, so Vault sections, PG limits and the Shared Library follow the same rules. You can keep ordinary media such as films in the Vault with their own search and permissions and move things in and out. Private films can look up details online only if you switch that on for that section, and the app tells you that titles will be sent out. A move copies the item into the Vault, keeps its progress and ratings, and never writes the title anywhere other profiles can see. Moving something out of a protected folder is one separate, PIN-protected action, and moving out goes only to a folder the app is allowed to write to.
