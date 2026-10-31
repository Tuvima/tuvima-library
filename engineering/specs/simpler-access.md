# Spec: Simpler access (areas, ratings, untick a library, folder checkbox)

> **Status:** Built, awaiting your Local checks · **Version:** 5 · **Last updated:** 2026-10-29 · **Related docs:** [Product overview](../product/overview.md) · [Simpler library setup](library-setup-groups.md) (D51, D60) · [Vault sections and Move to Vault](vault-sections-and-move.md) (D28, D32, D39) · [Videos](videos.md) (D55) · [Library access v3](library-access.md) · [Security](../../docs/architecture/security.md)
> Version 6 note (2026-10-30): the per-profile area switches (D1) are removed by the [Libraries redesign](libraries-redesign.md) (D16, D19-Q3, Built with WP8); library access is on or off per household only.


> Version 6 note (2026-10-30): the per-profile area switches (D1) are removed by the Locked [Libraries redesign](libraries-redesign.md) spec (D16, D19); hiding is done with Kid levels, the household ceiling and the Vault.

> Written 2026-10-23 after Shy asked for a review of the permissions strategy and a comparison with Plex, Jellyfin, Netflix and Disney+. Shy approved the recommended model the same day ("Good with all these suggestions and the new access model"), which locks this spec. Because it is an access change, every packet gets the security review before merge. Version 2 (same day) records two more answers: saved videos follow "allow unrated" like every other unrated item (D14), and removing settings from the libraries file is a fresh start, not a conversion (D15). Version 3 (same day): built (WP1 #446, WP2 #447, WP5 #448, WP3 and WP4 #449, WP6 docs). *Amendment note: the [Users, profiles and households](users-profiles-households.md) spec (D19, merged in #451) moved every access right from the account to the profile. Read "account" as "profile" in D2, D4, D5 and D13: hidden libraries are stored per profile (`profile_library_hidden`), a household administrator can hide more for the household's profiles but never show a library hidden from their own profile, and the move notice names profiles.*

> Version 4 (2026-10-27) adds Selected profiles libraries for the [Library pages spec](library-pages.md) (D16). Approved by Shy on 2026-10-27.

> Version 5 (2026-10-29) records what the Locked [library-access v3](library-access.md) spec (Shy, 2026-10-29) changes here: who can see which library is now set on the person as grouped ticks with one Save bar, a household has an explicit ceiling, the folder "Suitable for limited profiles" tick and the per-profile "Allow unrated" are replaced by household Kid levels and one "Show unrated items" switch, and "Selected profiles" libraries are removed. Decisions are marked *Superseded* where they changed; D1, D2, D3, D6 (area switches, Kids profiles), D8, D9, D10, D12 and D15 stay. As built, the visibility rule is: not hidden for the household (`household_library_hidden`) and not hidden for the profile (`profile_library_hidden`).

## 1. Goal

Make "who sees what" simple and hard to get wrong. A person gets whole areas (Watch, Listen, Read, View). Their rating limit decides what they see inside them. An administrator can hide the odd library from one person by unticking it. New libraries appear for everyone who has the area, so nothing goes missing by accident.

## 2. Scope

**In scope**
- Library access flips from "tick to share" to "everything visible, untick to hide", per person.
- The person's page gains an untick list per area.
- "Suitable for limited profiles" moves from the library to the folder and covers every kind, not only videos.
- The unused library sharing fields are removed.
- One Engine read tells a move confirmation who will stop seeing the files.

**Out of scope**
- Area access stays as it is (D1).
- The rating limit, "allow unrated" and Kids profiles stay as they are (D6).
- The Vault and its sections are built by the [Vault sections spec](vault-sections-and-move.md) (D9).
- View's own spaces and its per-profile Photos, Personal videos and Documents switches stay (D10).
- Blocking single titles, and labels or tags, are not built (D12).

## 3. Decisions made

- **D1. Areas are the main door** (Shy, 2026-10-23). Each account is given Watch, Listen, Read and View separately, as today (`account_feature_grants`). Reason: Ease of use. A household needs "can this person use Watch", not a list of every library.
- **D2. Every library in a person's areas is visible unless an administrator unticks it** (Shy, 2026-10-23, answer 2A). Unticking is stored as a hidden-library row per account. A new library is visible straight away to everyone who has its area. Administrators always see everything, as today. Reason: Reliability. Today a new library is hidden from everyone until someone remembers to tick it, which is how files "vanish" after a folder moves or a library is created. It also covers the friend case: share Watch with a friend but untick "Lectures".
- **D3. The old ticks are not carried over.** Pre-release rule (CLAUDE.md "Never" 6): the new data structure starts empty, so after the update everyone sees every library in the areas they have. Shy re-checks the person pages once (Local checks). Reason: Maintenance. There is no conversion code to carry.
- **D4. Household members inherit the primary account's hidden libraries** (as `grants_inherit_from_account_id` does today). A household administrator can hide more for their members, but never show a library the primary account has hidden. Reason: Privacy. *Superseded by [library-access v3](library-access.md) D2 (v5):* household members no longer inherit the primary profile's hidden libraries; the server administrator sets an explicit household list, and a household administrator can untick more for the household's profiles but never show what the household lacks.
- **D5. The untick list lives on the person's page.** Under each area the person has, a "Libraries" list shows one tick per library in that area, all ticked by default. It appears only when the area has more than one library. Unticking saves straight away and says "Hidden from {name}". The list never names a Vault library. Reason: Ease of use. It is the place an administrator already goes to set a person's areas. *Superseded by [library-access v3](library-access.md) D1 (v5):* the list is grouped ticks per area with an "All libraries" tick and one Save changes / Discard bar; ticks no longer save one by one.
- **D6. The rating limit stays the dial.** Each profile's rating limit, "allow unrated" and Kids profiles work as today. Reason: it matches Netflix and Disney+, which households already understand. *Partly superseded by [library-access v3](library-access.md) D4, D9, D10 (v5):* the area switches and Kids profiles stay, but the per-profile rating dropdown and "allow unrated" are replaced by a "Kid's profile" switch with household levels for Watch, Read and Listen and one household "Show unrated items" switch.
- **D7. "Suitable for limited profiles" is a folder setting and covers every kind** (Shy, 2026-10-23). *Superseded by [library-access v3](library-access.md) D10 (v5):* the folder "Suitable for limited profiles" setting is removed (it no longer exists in the libraries file, version 10.0); unrated items follow the household "Show unrated items" switch.
  - A folder with it ticked shows its **unrated** items to profiles with a rating limit, whatever their "allow unrated" setting says.
  - **Rated** items in that folder still follow the person's limit, so an R-rated film slipped into a kids' folder stays hidden from a PG profile.
  - It is off by default, and set on the folder's row on its type page.
  - The library-level setting goes away.
  - Reason: Ease of use and Privacy. It closes the gap where a child sees none of their own unrated audiobooks or saved videos, without opening every unrated file in the library. Jellyfin has this gap.
- **D8. The unused library sharing fields are removed.** A library's `visibility`, `authorized_profile_ids` and `owner_profile_id`, and the personal policy's `default_visibility`, are stored but never enforced or shown. They are removed from config, the validator and the wire. Reason: Maintenance. Fields that look like access rules but do nothing are a trap.
- **D9. Vault access is per section, nothing ticked by default** (Shy, 2026-10-23, answer 2a A). This confirms Vault sections D28 and D39. A Vault library is never in the hidden-library list or the untick list (Videos D55b). Built by the Vault sections spec.
- **D10. View keeps its own rules** (Shy, 2026-10-23, answer 2b A): own space, household shared library, the contribute right, and the per-profile Photos, Personal videos and Documents switches.
- **D11. One rule in the Engine.** Every place that asks "may this account see this library" asks one question, "is this library hidden from this account?". That covers browse, search, detail pages, players, artwork, streams, events, counts, downloads, View and the native apps. A guardrail fails the build if a read path checks the old grant table or a second copy of the rule. The Vault sections spec's central access service (its WP0) later absorbs this rule rather than duplicating it. Reason: Reliability and Privacy. *Changed by [library-access v3](library-access.md) (v5):* the one question is now "is this library hidden for this household or for this profile?".
- **D12. Not built: single-title blocking, labels and tags.** Ratings, the folder checkbox and unticking cover household needs, and either can be added later. Reason: Maintenance.
- **D13. A folder move tells you who will stop seeing the files** (Shy, 2026-10-23, answers library setup Q2). A moved folder's files follow the library they move into. If anyone has that library hidden, the confirmation names them first: "Sam won't see these 40 files." Otherwise nothing extra is said. The Engine answers "who would lose sight" in one read (WP5), which "Change what this folder is" (library setup WP7b-3) and Merge into use. *Changed by [library-access v3](library-access.md) (v5):* the notice names households or profiles who lose sight.
- **D14. Saved videos follow "allow unrated" like every other unrated item** (Shy, 2026-10-23, answer 1A). Under D7 a profile with "allow unrated" on sees unrated items from every folder, saved videos (YouTube downloads, Other videos) included; with it off, only those from folders marked suitable. This replaces today's rule that a limited profile sees Videos only from a library marked OK. Reason: Ease of use; one rule, and the safe choice (off) is the default. *Superseded by [library-access v3](library-access.md) D10 (v5):* saved videos follow the household "Show unrated items" switch like every other unrated item.
- **D15. Removing settings from the libraries file is a fresh start** (Shy, 2026-10-23, answer 2: "do whatever is the cleanest method"). WP3 and WP4 remove library-level settings and add the folder one, together, under one new libraries file format version. An older file is refused with the existing plain message (move it aside, Library starts fresh, add the folders again); nothing is converted. Reason: Maintenance; pre-release, a clean format beats compatibility code.
- **D16. Selected profiles** (Shy, 2026-10-27, Library pages answer 2). A library can be limited to chosen profiles. A profile added later starts **without** access to such a library until an administrator ticks it. It is stored as the existing hidden-library rows plus a per-library "restricted" marker, so a new profile is hidden by default. D2 stays for Standard libraries. Administrators always see everything. A Vault library is never listed here (D9). D11's single question becomes "is this library hidden from this profile, or restricted without them?", and the guardrail test covers both. *Superseded by [library-access v3](library-access.md) (v5):* Selected profiles libraries, the restricted marker and "new profiles start hidden" are removed; a new profile gets what its household has.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Administrator opens a person's page | Under each area the person has, "Libraries" with a tick per library, all ticked unless hidden. Areas with one library show no list. |
| Administrator unticks "Lectures" for Sam | It saves straight away with "Hidden from Sam". Sam no longer sees Lectures anywhere: lanes, search, Continue, detail links, phone app. Sam keeps everything else in Watch. |
| Administrator adds a new library | Everyone with its area sees it straight away. Nothing needs ticking. |
| Household member's page, seen by a household administrator | The same list. Libraries the primary account hides are shown greyed with "Hidden by {primary}", and can't be ticked. |
| Kids profile, folder "Kids audiobooks" ticked as suitable | The child sees the unrated audiobooks from that folder. Unrated audiobooks from other folders stay hidden (allow unrated is off). |
| Folder row on a type page | A "Suitable for limited profiles" switch, with the tooltip "Unrated items from this folder are shown to profiles with a rating limit. Rated items still follow each profile's limit." |
| Folder move or kind change that someone can't follow | The confirmation adds one line, "Sam won't see these 40 files", before the usual Confirm. |
| Engine unreachable on the person's page | The usual page error state. No list, nothing saved. |
| Phone | Read-only, as today: the list shows ticks but can't change them. |

## 5. Data / model changes

- **New:** `account_library_hidden` (account id, library id, created at, created by). One row per library hidden from that account. Vault library ids are refused.
- **Removed:** `account_library_grants`. Pre-release, so the new table starts empty (D3).
- **Config:** a folder (`sources[]` in `config/libraries.json`) gains `ok_for_limited_profiles` (default false). The library-level `ok_for_limited_profiles`, `visibility`, `authorized_profile_ids`, `owner_profile_id` and the personal policy's `default_visibility` are removed.
- **Wire:**
  - The access contracts replace `library_grants` and `library_ids` with `hidden_library_ids`.
  - The folder DTO gains `ok_for_limited_profiles`.
  - The library DTO loses the removed fields.
  - The fixtures are refreshed.
- **Backup:** the new table takes the classification the grant table had.

## 6. Work packets

Wave 1: WP1 and WP4 (different files). Wave 2: WP2 and WP3. Wave 3: WP5 and WP6. Every packet except WP4 and WP6 gets the security review.

### WP1 — Hidden libraries in the Engine
- **What it does:**
  - D2, D3, D4, D11.
  - An upgrade step at the next free number drops `account_library_grants` and creates `account_library_hidden`.
  - The repository gets `GetHiddenLibrariesAsync` and `IsLibraryHiddenAsync`, following household inheritance, and a `ReplaceHiddenLibraries` that refuses Vault ids.
  - Every enforcement site moves from "granted" to "not hidden":
    - `AuthorizedDisplayProjectionReadService` (`LoadLibraryAccessAsync`, `AllowsLibraryAndFeature`);
    - `RequestAuthorityServices.EvaluateLibraryAsync`;
    - `CatalogueResourceAuthorization`;
    - `ViewEndpoints`;
    - `IntercomAudience`;
    - the outbox and telemetry readers that take library access.
  - Access actions: `PUT /access/accounts/{id}/access` and `POST /access/accounts` take `hidden_library_ids`. Invitations start with none hidden.
  - The `GET /access/libraries` picker returns each library with its area, and for household administrators whether the primary account hides it.
  - Contracts, fixtures, the export and backup classification guardrails, and a new guardrail per D11.
  - Docs: `docs/architecture/security.md` and `docs/reference/database-schema.md`.
- **Tests:**
  - A new library is visible to an account with its area.
  - A hidden library is absent from browse, search, detail, player, artwork, events and counts.
  - A household member inherits the primary account's hidden libraries.
  - A household administrator can't unhide what the primary account hides.
  - A Vault id is refused.
  - Administrators see everything.
- **Depends on:** none
- **Size:** Complex (security)
- **Done when:** the tests pass, the guardrail fails on a seeded read of the old table, and the CI-strength build is clean.

### WP2 — The untick list on the person's page *(Reworked as LA3 in [library-access v3](library-access.md), v5.)*
- **What it does:**
  - D5 on `ProfileDetailPage` and `ManagedAccessUserPermissions` (and `ProfileAccessSheet` if it shows libraries): a Libraries list per granted area, as described in section 4.
  - Removes the old library tick list.
  - bUnit tests for: default all ticked; untick saves; the household "Hidden by" state; no list for a one-library area; read-only on phones.
- **Depends on:** WP1
- **Size:** Standard (security)
- **Done when:** the tests pass, plus the visual check at desktop and phone.

### WP3 — Suitable for limited profiles on the folder *(Removed by [library-access v3](library-access.md) LA2, v5.)*
- **What it does:**
  - D7. `ContentLimitFilter` takes the set of ticked folder paths instead of `videoLibrariesOk`.
  - For a limited profile, an unrated item counts as allowed when its file sits inside a ticked folder. It uses the same normalised path-containment rule as the folder first check, both in memory and in the SQL paths that count and page.
  - Rated items are unchanged.
  - Removes `AllowsVideo` and the library setting.
  - Adds the switch on the folder row (`LibraryFoldersPanel`), with the config schema, validator and `docs/reference/configuration.md`.
- **Tests:**
  - A PG profile with allow unrated off sees an unrated audiobook from a ticked folder, and not one from an unticked folder.
  - An R film in a ticked folder stays hidden.
  - Counts match the lists.
- **Depends on:** WP1 (both touch `AuthorizedDisplayProjectionReadService`); ships with WP4 under one libraries file format bump (D15)
- **Size:** Complex (security)
- **Done when:** the tests pass and the CI-strength build is clean.

### WP4 — Remove the unused sharing fields
- **What it does:** D8. Removes the fields from `LibrariesConfiguration`, `config/libraries.json`, `docker/config/libraries.json`, the validator, `CoreSettingsContracts`, `SettingsContractMapper`, `LibraryFolderAddService` and the docs. Refreshes the fixtures.
- **Depends on:** none
- **Size:** Mechanical
- **Done when:** the build is clean, Contracts and Storage tests pass, and a grep finds no `authorized_profile_ids` or `owner_profile_id` for libraries.

### WP5 — Who loses sight
- **What it does:** D13. An Engine read: given files (or a folder) and a target library, it returns the profiles that can see them now but would not after the move, with counts, for administrators only. The Dashboard client method and Contracts come with it. Library setup WP7b-3 and Merge into show the line.
- **Depends on:** WP1
- **Size:** Standard (security)
- **Done when:** a test where Sam hides the target library returns Sam with the right count, and one where nobody does returns an empty list.

### WP7 (v4) — Selected profiles *(Removed by [library-access v3](library-access.md) LA1, v5.)*
- **What it does:** The restricted marker, the new-profile default, and a guardrail case (the Access tab itself is Library pages WP6).
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** a profile added after a Selected-profiles library was made cannot see it until ticked, in lanes, search, counts and the apps.

### WP6 — Docs
- **What it does:** `docs/architecture/security.md`, `docs/guides/library-settings.md`, the user and profile guides, `docs/product/presentation-rules.md` and `.claude/rules/security.md` ("Accounts own feature and actual library grants" becomes "feature grants and hidden libraries").
- **Depends on:** WP1 to WP5
- **Size:** Mechanical
- **Done when:** a docs grep finds no "library grant" wording describing current behaviour.

## 7. Local checks

1. On SHY-DESKTOP after the update, open each person's page and untick anything they shouldn't see (D3).
2. Add a new folder as a new library, and check that a second account with that area sees it at once in the Dashboard and the phone app.
3. Untick it for that account, then check that it is gone from lanes, search, Continue and a direct detail link on the phone app.
4. A Kids profile sees unrated audiobooks from a folder ticked as suitable, and not from others.

## 8. Risks

- **Wider access on the day of the update (D3).** Until Shy re-checks the person pages, everyone sees every library in their areas. This is acceptable pre-release, and listed as Local check 1.
- **Path containment** must match the folder first check exactly, or a file could fall outside its folder's checkbox. WP3 reuses that rule rather than writing a second one.

## Plain English summary

People get whole areas and a rating limit, like Netflix. For the odd library a friend or child shouldn't see, you untick it on their page, like Plex. New libraries just appear for everyone who has the area, so nothing goes missing. A kids' folder can be marked suitable so its unrated audiobooks and videos show up for children, while anything rated still follows their limit. The Vault keeps its own per-section ticks.
