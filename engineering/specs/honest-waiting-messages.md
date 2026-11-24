# Spec: Honest waiting messages

> **Status:** Locked (Shy, 2026-11-24) · **Version:** 1 · **Last updated:** 2026-11-24 · **Related docs:** [Product overview](../product/overview.md) · [Library area pages](library-area-pages.md) · [Library activity](library-activity.md) · [First-run setup rework](first-run-setup-rework.md) · [Mockups](../design/mockups/profile-and-household.html) (screen 7)

## 1. Goal

When Home, Watch, Read or Listen has nothing to show, the page says why, in one true sentence, and offers the action that fixes it. Today Watch says "Watch is waiting for media" with an "Add a library" button even when a library was added and 19 videos were found and are waiting for a metadata source, and an unfinished first-time setup holds all scanning without saying so anywhere.

## 2. Scope

**In scope**
- The message shown on Home, Watch, Read and Listen when the page (or the chosen tab of it) has no titles to show.
- One quiet line on those pages when some titles are showing but other files are still waiting.
- A clear notice when first-time setup was left unfinished and scanning is on hold.
- Desktop, tablet and phone.

**Out of scope**
- How files are identified, and which sources exist. Nothing about matching changes.
- View (photos and documents), Collections, Search and My List.
- Needs Review, Library activity and Plugins pages themselves. This work only links to them.
- "Folder offline" being shown on healthy folders and the count mismatches in Library activity seen during the review. They are being confirmed on your PC first and are tracked separately.
- The native phone and TV apps.

## 3. Decisions made

- **D1. The empty message always states the real reason.** Library picks the first reason that applies, in this order: setup unfinished, no library of this kind, folder can't be reached, still looking, waiting for a metadata source, waiting in Needs Review, nothing found. Reason: one true sentence beats a generic one, and the order puts the cause before its effects.
- **D2. Counts are shown.** "19 videos found, none identified yet" tells the person their files were seen. Reason: the worst part of today's message is that it suggests nothing was found.
- **D3. At most two actions**, matching what the person can actually do: fix the cause (for example "Connect a metadata source", which opens Settings > Plugins) and look at the files ("See the 19 files", which opens Needs Review filtered to this area).
- **D4. Point at what does work.** If another tab of the same area has titles (for example personal videos under Videos), one line says so with a link.
- **D5. Partly ready pages get one quiet line**, not a banner: "23 more files are waiting for a metadata source", linking to Needs Review. Administrators only. It disappears when nothing is waiting. No toast.
- **D6. Members and Kids profiles see a plain sentence and no administrator actions.** A member sees "Nothing here yet. An administrator is still setting this up." A Kids profile sees "Nothing here yet." Counts, source names and links to Settings are for administrators only. Reason: Privacy, and members cannot act on them.
- **D7. Unfinished setup is said out loud.** While first-time setup is unfinished, Home and the area pages show administrators "Finish setup to start scanning" with a "Continue setup" button, in place of every other empty message. The same line appears at the top of Library activity. Reason: today scanning waits silently and looks broken.
- **D8. The messages update by themselves.** When the cause clears (a source is connected, a scan finishes), the page changes without a reload, the way tiles already appear.
- **D9. The wording names the kind of media**, not a generic "media": videos, films and TV shows, books, audiobooks, comics, music. It uses the app's existing words for each library kind.
- **D10. The message uses the shared empty-state design** already used on these pages (icon, title, one sentence, buttons), dark only, no new look.

## 4. UI behaviour

The table shows Watch as the example. Read and Listen use the same rows with their own words (comics need a source; books and music name theirs).

| Situation | What an administrator sees |
|---|---|
| Normal (titles to show) | The page as today. |
| Setup unfinished | "Finish setup to start scanning. Library has not looked at your folders yet." Button: Continue setup. |
| No library of this kind | "Nothing to watch yet. Add a library for films, TV shows or videos and they appear here." Button: Add a library. (Today's behaviour, reworded.) |
| Folder can't be reached | "Library can't reach the Movies folder. Check that the drive is connected." Button: Open Libraries. |
| Loading (scan still running, nothing ready) | "Looking through 54 files. Titles appear here as they are identified." Link: See progress (Library activity). |
| Waiting for a metadata source | "19 videos found, none identified yet. Films and TV shows need a metadata source before Library can name them and fetch posters. Connect one and they appear here by themselves." Buttons: Connect a metadata source, See the 19 files. |
| Waiting in Needs Review | "19 videos found. Library isn't sure what they are." Button: Open Needs Review. |
| Nothing found | "The Movies folder has no video files Library can use." Button: Open Libraries. |
| Partly ready | The page as today, plus one quiet line under the tabs: "23 more files are waiting for a metadata source." |
| Error (Engine unreachable) | The app's normal "Can't reach Library" state with Try again. Never the "add a library" message. |
| Something else ready in this area | One extra line: "Your 4 personal videos need no source and are in Videos." |

| Who and where | Difference |
|---|---|
| Member | "Nothing here yet. An administrator is still setting this up." No counts, no buttons. No quiet line when partly ready. |
| Kids profile | "Nothing here yet." |
| Home | Same reasons, counted across all kinds: "54 files found, none identified yet." |
| Phone and tablet | Same sentences; buttons stack full width, 44 px tall. |
| TV | The sentence only, no buttons. |

## 5. Data / model changes

Nothing new is stored. The Engine gains one read-only summary per area that the Dashboard asks for when a page is empty: whether setup is finished, how many libraries of this kind exist and whether their folders can be reached, how many files were found, how many are still being worked on, how many wait for a source (and which kind of source), and how many are in Needs Review. All of these are counted from information the Engine already keeps. No reset is needed.

## 6. Work packets

### WP1 — Area readiness summary in the Engine
- **What it does:** Adds a read-only Engine action that returns the facts in section 5 for Home, Watch, Read and Listen, limited to what the asking profile may see (a member gets only "empty or not"; D6). Adds the wire type to Contracts.
- **Files likely touched:** `src/MediaEngine.Contracts/Library/` (new contract beside `LibraryActivityContracts.cs`), `src/MediaEngine.Api/Endpoints/` (beside the library activity endpoints), a query in `src/MediaEngine.Storage/` with the `Repository` suffix, the setup gate used by `src/MediaEngine.Ingestion/IngestionEngine.cs`, Contracts fixtures via `tools/Update-ContractFixtures.ps1`, `tests/MediaEngine.Api.Tests/`, `tests/MediaEngine.Storage.Tests/`, `tests/MediaEngine.Contracts.Tests/`
- **Depends on:** none
- **Size:** Complex
- **Done when:** endpoint tests return the right reason for each row of the section 4 table on a temp data store, and a member's answer carries no counts.

### WP2 — Choosing the message
- **What it does:** One small Dashboard rule that turns the summary into a reason (order in D1), title, sentence and up to two actions, with wording keys for every media kind (D2, D3, D4, D9).
- **Files likely touched:** new presentation class under `src/MediaEngine.Web/Components/MediaHub/`, `src/MediaEngine.Web/Resources/SharedStrings.resx`, Dashboard Engine client for the new action, `tests/MediaEngine.Web.Tests/` (new presentation tests)
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** a test per reason and per user kind passes, including that "Add a library" is offered only when no library of that kind exists.

### WP3 — Area pages and Home
- **What it does:** Watch, Read, Listen and Home use the rule from WP2 in the shared empty state, show the quiet partly-ready line (D5), and refresh when ingestion events arrive (D8).
- **Files likely touched:** `src/MediaEngine.Web/Components/MediaHub/MediaHubPage.razor`, `EmptyShelfState.razor` and its `.css`, `src/MediaEngine.Web/Components/Pages/LibraryBrowsePage.razor`, `tests/MediaEngine.Web.Tests/EmptyShelfStateTests.cs`
- **Depends on:** WP2
- **Size:** Standard
- **Done when:** component tests show each message on Watch, and no sideways scroll or clipped button at 1024, 1280, 1440 and phone width on a small test library.

### WP4 — Unfinished setup notice
- **What it does:** Shows "Finish setup to start scanning" with Continue setup on Home, the area pages and the top of Library activity while setup is unfinished (D7).
- **Files likely touched:** `src/MediaEngine.Web/Components/Pages/LibraryBrowsePage.razor`, the Library activity page component, `src/MediaEngine.Web/Resources/SharedStrings.resx`, `tests/MediaEngine.Web.Tests/ActivityPageTests.cs`
- **Depends on:** WP2
- **Size:** Standard
- **Done when:** with setup unfinished on a test library the notice shows in all three places, and it is gone after setup completes without a reload.

### WP5 — Help pages
- **What it does:** Updates the public pages that describe empty areas and first-time setup.
- **Files likely touched:** in `tuvima-docs`: `product/presentation-rules.md`, `reference/api-endpoints.md`, the first-time setup guide
- **Depends on:** WP3, WP4
- **Size:** Mechanical
- **Done when:** the docs build passes and the pages describe the new messages.

## 7. Local checks

- **L1.** Fresh start with a film folder and no film source connected. Open Watch. It says how many videos were found and that they need a metadata source, with the two buttons. Failure: "Add a library" or "waiting for media".
- **L2.** Connect the source and wait. Films appear on Watch without reloading and the message goes away.
- **L3.** Leave first-time setup at the last step and open Home. "Finish setup to start scanning" shows with Continue setup. Finish it. Scanning starts and the notice goes.
- **L4.** Unplug or rename a library folder. Watch says the folder can't be reached. Plug it back. Confirm healthy folders never show this.
- **L5.** Switch to a member, then a Kids profile, on an empty area. No counts, no buttons.
- **L6.** With some films matched and some waiting, the quiet line shows the right number and opens Needs Review on those files.
- **L7.** Repeat L1 on a real phone.

## 8. Risks and open questions

- **Risks:** The counts must agree with Needs Review and Library activity, or the message becomes one more number that does not match; the build takes them from the same source those pages use and tests that they agree. Asking for the summary on every empty page must stay fast on a large library (Performance); it is asked only when a page has nothing to show or on Home, and counted in one query.
- **Open questions:** none.
