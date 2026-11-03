# Spec: Documents in View, one place for personal and shared

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-11-03 · **Related docs:** [Library setup groups](library-setup-groups.md) (D36, D47, D48, D49, D52) · [Extras](archive/extras.md) · [Libraries redesign](libraries-redesign.md)

## 1. Goal
Documents work like photos: each person has their own, the household has one shared set, and documents that belong to a show, film, album, comic or book sit on that page as Extras. Most of this is already built. This spec closes three gaps so it reads that way.

## 2. Scope
**In scope**
- Shared documents become plain shared documents: no Shared Inbox.
- A loose PDF in a Read folder stays a book; Documents come only from Documents folders.
- Remaining wording and page alignment so Documents matches Photos.

**Out of scope**
- Personal Inbox, filing rules, saved views, bulk edit, Trash (unchanged).
- Extras (built).
- New file types, new reading or search features.

## 3. Decisions made
- **D1. Shared documents have no Inbox** (Shy, 2026-11-03: "should just be shared documents"). A document added to Shared appears straight away, with the adder's name. Nothing waits for an administrator. Your own Inbox stays.
- **D2. Anyone who may use Documents and add to Shared can tag and edit a shared document's details** (recommended by Claude, accepted by Shy as "yes to all", 2026-11-03). Child profiles follow their Documents switch. Deleting from Shared stays an administrator action, as today.
- **D3. A loose PDF in a books folder is a book** (Shy, 2026-11-03). Documents are only what sits in a Documents folder. "Other reading" now reads "Loose books and reading material with no match".
- **D4. Documents beside media stay Extras** (Shy, 2026-11-03; already built).
- **D5a. Your own Inbox stays in Mine** (Shy, 2026-11-03: "I like A"). Only the Shared Inbox goes.
- **D5. View keeps its side list (Photos, Galleries, Documents, People, Places)**; nothing to merge. The earlier idea of a new tab strip is dropped because the list already does that job.

## 4. UI behaviour
| Situation | What the person sees |
|---|---|
| Mine | Documents with the Inbox card list as today. |
| Shared | The household's documents, "Added by" chips when two or more people added items, no Inbox, no "Filed by a household administrator" note. |
| Child with Documents off | Unavailable page, as today. |
| Empty Shared | "Nothing shared yet" with Send to Shared hint. |
| TV | Browse and open only. |

## 5. Data / model changes
None. The "can file Shared Inbox" permission is removed from the scopes answer. Documents already in Shared simply show as ordinary shared documents. Pre-release, so no migration.

## 6. Work packets
### WP1 — Remove the Shared Inbox
- **What it does:** Shared scope shows no Inbox; shared documents are editable by anyone with Documents who may add to Shared (D2); the admin-only filing rule and note go.
- **Files likely touched:** `ViewDocumentsPage.razor`, `ViewDocumentInboxCard.razor`, `ViewEndpoints.cs`, `LocalAssetContracts.cs` (can_file_shared_inbox), `ViewSharedInboxAccess`, Contracts tests and fixtures, tests (to be confirmed)
- **Depends on:** none
- **Size:** Standard
- **Done when:** a test shows a non-administrator adult can tag a shared document, the Shared view returns no Inbox section, and the contract fixture no longer has the field.

### WP2 — Wording
- **What it does:** Updates "Other reading" text and the public pages (Documents in View, Use your Personal Space) to match D1 and D3.
- **Files likely touched:** `config/library_kinds.json`, tuvima-docs `documents-in-view.md`, `view-personal-space.md`
- **Depends on:** WP1
- **Size:** Mechanical
- **Done when:** no page says "Shared Inbox" or "documents" under Other reading.

## 7. Local checks
- **L1.** Sign in as a second adult, add a PDF to Shared, and tag it. Expected: it appears at once with your name and no Inbox. Failure: it waits to be filed.
- **L2.** Put a PDF in a books folder. Expected: it shows as a book, not under Documents.

## 8. Risks and open questions
- **Risk:** the one-off D2 rule lets any adult retag shared documents. Accepted for household trust (see the earlier profile-trust decision).
- **Open questions:** none.
