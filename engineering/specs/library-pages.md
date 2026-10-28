# Spec: Library and folder management pages (one Libraries experience)

> **Status:** Built, awaiting your Local checks · **Version:** 14 · **Last updated:** 2026-10-27 · **Related docs:** [Product overview](../product/overview.md) · [Simpler library setup](library-setup-groups.md) (D1, D4, D5, D7, D8, D13, D20, D31, D32, D43, D44, D57) · [Vault sections and Move to Vault](vault-sections-and-move.md) (D26, D28, D29, D33, D34, D35, D36, D39) · [Simpler access](simpler-access.md) (D2, D7, D9, D11) · [Videos](videos.md) (D17, D45, D55) · [Delete groups](delete-groups.md) (D12) · [Batched write-back](batched-writeback.md)

> Written 2026-10-27 from Shy's seven target-state screenshots (Libraries overview, Create library, Choose source folder, validated folder, library Folders tab, File handling tab, Add files) and the bug-log items F4, F6, F7, F8, F9, F10, F23, F24 and F33. It **replaces the front-door cards, the three-step Add folder wizard, the Vault card and the Vault tile** from the library setup spec with one simpler model. Shy's key idea: there is no separate "Vault" section; a library simply has an Access setting, and choosing **Vault-protected** when you create it makes it a Vault library. Version 2 records Shy's answers to the first six questions: a library's Vault setting is chosen when it is created and never flipped later (you move items in instead), each content type has at most a normal library and a Vault library, adult content lives under Watch with a Vault tick, Selected profiles starts hidden for new profiles, and Vault libraries are never listed. This is a Draft: it does not change any Locked spec until Shy locks it (see section 9).

## Plain-English product walkthrough

**What it feels like today.** Settings > Libraries opens on cards, then a type page, then a folder table. Adding a folder is a three-step wizard that asks "what is it?", then "which folder?", then "how should Library treat it?", and afterwards asks whether you want to go to that library. Information bubbles ("i") are everywhere and some appear behind the dialog. The Vault is its own card and its own wizard tile. The first-run setup screen shows an older, different version of the same page.

**What it feels like after this spec.**
1. **One list of libraries.** Settings > Libraries shows four tabs (Watch, Listen, Read, View). Under the chosen tab is one table of your libraries: name, content type, who can see it, how many folders and titles, and a health status (Healthy, Needs review with a count, Empty). Search and a status filter sit above it. Every row has a chevron and highlights on hover, so it is obvious it opens.
2. **One dialog to add a library.** **Add library** asks for four things on one screen: the media area, a name, the content type, and **Access** (Standard, Selected profiles, or Vault-protected; fixed once created), then the first source folder. Inside an existing Movies library, **Add folder** also offers "Add as a Vault folder" for an administrator, which puts the folder in that type's Vault library. It says "Additional folders can be added later." Create, and you land on the new library's page.
3. **One page per library, with four tabs.** *Folders* (the table of source folders, with Scan now, Add folder and Add files), *Access* (who can see it), *File handling* (how Library treats the files) and *Activity* (what happened here). Libraries whose content needs it (YouTube, online lookup for Vault libraries) also get a *Lookup* tab.
4. **Choosing a folder checks it for you.** The folder picker shows a row of clickable folder names and tells you straight away "Folder accessible, 126 video files found", or says plainly why a folder cannot be used.
5. **File handling is two switches and two choices, not three modes.** Preserve original names, Library standard or Custom pattern. Two switches: "Rename or move files automatically" and "Write metadata into media files", both off by default. Duplicates: keep one copy or keep both. Deleted files: use the Recycle bin. Settings apply to the whole library; a folder can still override them from the Folders tab.
6. **There is no Vault card and no Vault wizard.** A Vault library is an ordinary library whose Access is Vault-protected. It shows a lock badge. It is drawn only while the Vault is unlocked for an administrator; otherwise the row, its count and the word "Vault" are absent everywhere (Videos D17, library setup D13). You cannot turn an existing library into a Vault library, because the Vault is a different folder on disk. You **move items** from the normal folder into the Vault folder instead (Move to Vault, from the Vault sections spec), which is easy because both live under the same content type.
7. **First-run setup shows the same page**, with the extras (Access, Vault, Activity, Add files) hidden.

**What stays the same:** the Engine's safety checks, Read-only protection of original files, "New media goes here" (now called the import destination), the Recycle bin and its 90 days, phones being read-only for libraries, desktop-only editing of folders, Settings being configuration and never a correction workspace.

## 1. Goal

Make setting up and managing libraries obvious for a person who has never seen the screen: one table, one add dialog, one page per library, the same in first-run setup and Settings, with Vault protection as a simple Access choice instead of a separate area.

## 2. Scope

**In scope**
- The Libraries overview, Create library dialog, library page (Folders, Access, File handling, Activity, Lookup tabs), the folder picker with inline validation, and Add files.
- Using the same screens in first-run setup (F6).
- Access: Standard, Selected profiles, Vault-protected, including turning a library into a Vault library and back.
- The "i" bubble fix and fewer bubbles (F7, F9); fewer steps after adding (F10); a clear "clickable" cue (F33); YouTube key help (F8); where adult videos live (F23).
- Removing the old front-door cards, the three-step wizard, the Vault card and the Vault tile.

**Out of scope**
- The Vault's inner workings (separate index, central access service, Move to Vault): the Vault sections spec. This spec only gives them their door.
- The shared dialog style for save/discard and other popups (F35): own item; this spec uses it when it exists.
- View's own rules (own space, shared library, per-profile switches): unchanged (Simpler access D10).
- The item Edit panel (separate plan).
- Community plugins (parked).

## 3. Decisions made

Proposed by this draft from Shy's screenshots; each becomes final only when Shy locks the spec. Items marked **Q** are open questions in section 8.

- **D1. A library is the unit people manage; several folders of one type simply add together.** (Shy, 2026-10-27, answers 1 and 7). A Movies library can have as many normal folders as you like; their films are combined into one Movies library. **Only one folder per library can be the import destination**, and only if you want Library to do imports. A content type also has at most one Vault library beside the normal one (for example Private Movies), with its own folders and its own single import destination. The same film in both would be two copies, so it is refused as an overlap (D5). Moving a film into the Vault is then simple, because source and destination are two folders of the same content type. Reason: Ease of use and Privacy.
- **D2. Four area tabs filter the list; they are not toggles.** Watch, Listen, Read, View behave like tabs (one selected, clear selected state). The list shows only that area's libraries. The Vault is not an area here. Reason: Ease of use; answers F4.
- **D3. One "Add library" dialog on one screen** (media area, name, content type, access, first folder). The name pre-fills from the content type. Reason: fewer steps (F10, F24). The old three-step wizard is removed.
- **D4. After Create library or Add folder, no "go to..." question.** Creating a library opens that library's Folders tab with a scan started; adding a folder returns to the table with the new row highlighted and a one-line confirmation. Reason: F10.
- **D5. The folder picker validates as you choose** (Engine, read-only): accessible or not, number of supported files found (capped and cancellable; "10,000+" for very large folders), and plain refusals: already used by another library, inside or containing another library's folder, not readable, and for Vault libraries the on-disk separation rules (Vault spec D29). The "only this folder and its subfolders are scanned" note is a quiet (i) line, not an amber warning; amber is only for problems. The path field stays editable and the row of folder names is clickable (library setup D43).
- **D6. File handling replaces the three folder modes with plain choices** at library level, with per-folder overrides on the Folders tab.
  - *File organization:* Preserve original names (default), Library standard, Custom pattern. A short example shows the result.
  - *Rename or move files automatically* (off by default) is today's **Managed**; off is today's **Read-only**. *Write metadata into media files* (off by default) is the write-back choice that library setup D31 already separates from handling.
  - Turning the first switch on for a library that already has files shows how many files will change and runs the existing visible background job (hours, no automatic undo). It is never applied silently.
  - *Duplicates:* Keep one copy (compares contents, so renamed copies count) or Keep both. *Deleted files:* Use Recycle bin (Delete groups D12 stays).
  - Changes are saved with a sticky **Save changes / Discard** bar and a "You have unsaved changes" prompt when leaving.
  Reason: Ease of use and Maintenance; one model for what Library may do to files.
- **D7. Folders tab columns:** Folder (short name with the full path under it), Titles, Access (Accessible, Folder offline, Needs access), Scan (on or off), Import destination, and a "..." menu (override file handling for this folder, pause scanning, change what this folder is, remove). One folder per library is the import destination (the old "New media goes here"); new uploads and added files go there; every folder is still scanned. The explanation sits in an (i) tooltip, not a large box. Reason: Uniformity; Shy's UI style.
- **D8. Add files copies; it never moves or changes the originals.** Choosing server files in the picker copies them into the import destination (a free-space check first; the screenshot's "2 videos selected, 7.8 GB" is shown). Unsupported files are greyed with an (i). Needs an import destination. Phones do not show Add files. Reason: Privacy and Reliability (original source media stay protected).
- **D9. Access is a property of the library, in three choices:** *Standard* (visible to everyone who has the area, subject to profile limits), *Selected profiles* (only chosen profiles), *Vault-protected* (Vault rules). The Access tab shows the choice and, for Selected profiles, the list of profiles to tick. **Access is chosen when the library is created and Vault-protected can never be switched on or off later** (D11). A profile added later starts **without** access to a Selected-profiles library until an administrator ticks it (Shy, 2026-10-27, answer 2); this needs a small new version of Simpler access (D2 stays for Standard libraries). Reason: Privacy.
- **D10. Vault-protected is an option, not a mode** (Shy, 2026-10-27). It means exactly a Vault library from the Vault sections spec: its titles live in the Vault's separate index with its own search and per-section permissions (Vault D26, D28, D39), and nothing about it appears for anyone who cannot open the Vault. Any administrator sees the Vault tick when adding a library or folder, and does not have to switch any mode on first; creating a Vault library needs no extra PIN: being signed in as a server administrator on this page is enough (Shy, 2026-10-27). A Vault PIN, if one is set, still guards viewing and the explicit Read-only override (Vault D30), not creation. Non-administrators never see the option. Vault libraries are **never** listed in Selected-profiles pickers or untick lists (Shy, 2026-10-27, answer 6). Reason: Privacy. **Who can open Vault libraries is set per profile** (Shy, 2026-10-27; Vault D28, D39): a profile without Vault access sees no Vault entry in the top navigation, no Vault libraries, counts or search results. The Vault entry appears in the top navigation only for profiles that have access. A profile with access still enters the Vault PIN unlock to view (Shy, 2026-10-27, answer 8; Videos D17, D18, D52 stay).
- **D11. A library cannot be turned into a Vault library or back** (Shy, 2026-10-27, answer 3). The Vault is a different folder, so the Access choice is fixed at creation and the tab shows it read-only for Vault libraries. The way in is **Move to Vault** (Vault sections spec WP3): the item is re-added in the Vault library and its file is moved from a normal folder into the Vault folder (Managed folder needed, or the explicit override for a Read-only one, with the administrator PIN). **No destination yet:** the Move to Vault preview checks the destination before anything is touched, so a move never fails halfway for this reason. If there is no Vault-protected library of the item's content type, the preview says so plainly ("There is no Vault-protected Movies library yet") and an administrator gets **Add a Vault library**, which opens Create library with the area, content type and Vault-protected already chosen; when it is created the move continues from the preview. If the only Vault folder is Read-only it says that Move to Vault needs a folder Library manages, and offers the explicit override or adding a managed folder (Vault D30, D33). With several Vault folders the person picks one (the import destination is preselected). A destination that is offline or full is named, and nothing moves. This spec only adds the doors: a "Move to Vault" shortcut on the Folders tab menu that opens the same flow. Reason: Reliability and Privacy; no giant conversion job.
- **D12. Adult content is always offered under Watch, with a Vault tick on by default** (Shy, 2026-10-27). Choosing it ticks **Vault-protected** for you. Unticking it creates an ordinary Watch library, so adult videos can live in Watch if the owner prefers, with Stash-box lookup as in Videos. Nobody is forced into the Vault. This answers F23 and replaces my earlier idea that it be hidden unless Vault mode is on.
- **D13. One component for Settings and first-run setup** (F6). Setup passes a "simple" flag: Standard access only, no Vault, no Activity, no Add files, defaults for file handling. No second copy of the page exists. Reason: Maintenance.
- **D14. Fewer (i) bubbles, and they always sit on top** (F7, F9, F24). Explanations are one (i) per section heading, not per option; tooltips render in a top layer so a dialog can never cover them. Reason: Ease of use.
- **D15. A Lookup tab appears only when the library's content type has lookup settings.** YouTube: the optional personal key field with a "How do I get a key?" expander of four plain steps and a link (F8), plus the privacy line (only video IDs are sent). Vault libraries of ordinary kinds: the per-section "Look up details online" switch with its privacy note (Vault D34). Reason: Privacy; one place for lookup.
- **D16. Activity is a filtered view of events that already exist** (scans, imports, conversions, moves, errors) for this library. No new storage. For a Vault library it follows Vault D40: no titles outside the Vault activity list.
- **D17. Status words:** Healthy, Needs review (count, links to Needs Review filtered to this library), Empty, Folder offline, Scanning. A group's or library's icon is the worst state of its folders (library setup D14).
- **D18. Phones show the overview and each library read-only** (counts, status) with the usual note; TV keeps "unavailable". No folder editing on phones (library setup D15).
- **D19. Style rules from Shy apply:** icons with colour, cover art where a list has titles, few pill-boxes (the content type is plain text with an icon; only the Vault lock badge keeps a chip), centred Settings page, sentence-case headings.
- **D20. A profile can hide the Vault from the navigation bar** (Shy, 2026-10-27). A per-profile privacy switch "Show Vault in the navigation bar" (Settings, the user's own section, only for profiles with Vault access). When off, there is no Vault entry in the top navigation; the person reaches it only from that same Settings section, then the usual PIN unlock. Proposed default: off, in line with privacy first (change it if you prefer on). Stored on the profile, so it follows the person across devices. Reason: Privacy.
- **D21. "Adult content" is one kind that holds videos and pictures together, and works the same with or without the Vault** (Shy, 2026-10-27, answer 9A, revised the same day). It is offered under Watch. Videos are looked up on a Stash-box server (Videos D21); pictures are organised locally as galleries with no lookup (Videos D44), pending the Stash terms check. **The Vault tick only protects it.** Ticked (the default), the kind lives in the Vault. Unticked, the same library appears in Watch with its videos and its pictures (pictures as galleries); nothing is dropped. There is no separate adult photos library in View. Safety default (proposed, not asked): when the tick is turned off, the library's Access starts as Selected profiles (administrators only) and the dialog says so, so unticking never opens adult content to every profile by accident; it can be changed. Adult content always counts as adult-rated for profile limits. Reason: Privacy and Uniformity (one behaviour, the Vault is only protection).
- **D22. Several named libraries for the catch-all kinds, and a Videos page in Watch with chips** (Shy, 2026-10-27, answer 10A). Other videos, Other audio and Other reading can each have several named libraries (for example Home Videos, TikTok Edits, Other Videos), one row each in Settings, each with its own folders, access, file handling and import destination. Structured kinds (Movies, TV Shows, Music, Books and so on) keep one normal library each, with folders merging. Watch gets one **Videos** entry gathering the Other videos and YouTube libraries, with filter chips (All plus one per library); a chip is hidden from a profile that cannot see that library. The chips filter on the library each title already belongs to, so no new "label" storage is needed. This lifts the "one normal library per kind" limit of D1 for those kinds only. Reason: Ease of use and Maintenance.
- **D23. Plain hints about subfolders** (Shy, 2026-10-27). In Watch, subfolders give structure: a subfolder of several videos is a series, subfolders inside it are sections, a one-video subfolder stays standalone (Videos D4). In View, subfolders are scanned when "Include subfolders" is on and photos are shown by date. The folder step carries one short (i) line saying which applies.
- **D24. The View "Folders" page is removed** (Shy, 2026-10-27: not offered by Immich or Google Photos, so deprecate). Photos, Places, the timeline and galleries stay. What it uniquely did was let a folder stay out of the Photos timeline; that moves to a simple **Show in Photos timeline** switch on each source folder of the View Folders tab (set per source folder, no longer per sub-branch). Pinned folders and the real-folder-tree browser are dropped. Pre-release rule: removed outright, with no compatibility shim and no data conversion. Reason: Maintenance and Uniformity (one way to organise, fewer View destinations).
- **D25. View folders digest everything inside them, and View libraries are Managed by default** (Shy, 2026-10-27). Adding a View folder indexes everything beneath it, subfolders included; the "Include subfolders" switch is removed for View and there is no excluded-subfolders list (per-source only, the earlier option A). A View library's recommended folder is a **Managed** one: Library owns the structure, names, tagging and write-back, and creates an **import subfolder** inside it as the staging area. Photos dropped in, uploaded, backed up from a phone or added with Add files from another location land in staging, are renamed and filed into the right person's or the Shared Library's managed folders, and the originals elsewhere are copied, never moved (D8). A linked (As-is) folder is still allowed for an existing collection; it is read-only, so it is indexed but never renamed, and its "Show in Photos timeline" switch is the only way to keep it out of the timeline. Reason: Ease of use and Reliability (several people adding photos need one consistent structure).
- **D26. Importing into a managed library: whose, duplicates, naming, backups and rejects** (Shy, 2026-10-27).
  - **Whose photo:** it depends on where it came from. A phone backup goes to that person's own space. A photo dropped or added while browsing a space (own or Shared) goes to the space being viewed. Add files from a library page uses the scope picked at the top of that page.
  - **Duplicates:** checked by file contents, natively, as each file reaches staging and before it is filed (the existing "Keep one copy / Keep both" setting). A duplicate is skipped with "Already in your library" in the import summary; there is no later clean-up job. Only exact copies are caught; "similar" photos (resized or edited) are not part of this spec.
  - **Companion files:** RAW plus JPEG pairs, Live Photos and sidecar files move and are renamed together.
  - **Default layout and names:** by capture date, year then date folders, original file name kept, a short number added on a name clash; capture date from the photo's own metadata, then the file date. It is a pattern in the File handling tab and can be changed.
  - **Write-back** follows the Batched write-back spec; the File handling tab links to it.
  - **Backups:** a quiet (i) note on the Folders tab of every managed View library and on the Create library step says that Library's backups never include media, so the folder needs the owner's own backup. The Settings > System > Backup page repeats it in one line.
  - **Rejects:** a file that cannot be read is refused at the door with a plain message in the import summary ("2 files could not be read and were not imported"); nothing is moved. In a read-only linked folder an unreadable file is left untouched and listed in Activity.
  - **Space:** before any copy or move (drag and drop, Add files, phone backup, conversion) Library adds up the sizes and checks free space on the destination with a safety margin; if it will not fit, nothing starts and the message says how much is needed and how much is free. Long jobs check again and stop cleanly if space runs out. Reason: Reliability.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Overview, normal | Four area tabs; table of that area's libraries (Name, Content type, Access, Folders & titles, Status, chevron); search; status filter; **Add library** |
| Overview, area empty | "No Watch libraries yet" with one prominent **Add library** |
| Overview, Vault locked | No Vault-protected rows, counts or filters; the Vault tick in Add library is still there for an administrator |
| Overview, Vault unlocked (administrator) | Vault-protected libraries appear in their area with a lock and "Vault-protected" |
| Create library | One dialog; Create is disabled until name, type, access and a valid folder are set; refusals appear under the folder field |
| Folder picker | Clickable folder names, search, back/forward/up/refresh; below it a result card (accessible and file count, or why not) |
| Library page | Breadcrumb Libraries > area > name, title, content type, counts and status, **Scan now**, **Add folder**, **Add files**; tabs below |
| Folders tab | Table as in D7; offline folder shows an amber warning row; scan in progress shows the small progress dot |
| File handling tab | Four cards (Organization, File changes, Duplicates, Deleted files) with an Example line; sticky Save/Discard |
| Access tab | Three choices; Vault-protected only when allowed; shown read-only once created (D11) |
| Loading / Error | Existing loading state; "Libraries could not be loaded" with Try again |
| Phone / tablet / desktop / TV | Read-only list on phone; full on tablet and desktop (dialogs centred); "unavailable" on TV |
| First-run setup | Same page, simple mode (D13) |

## 5. Data / model changes

- Libraries already have a name, sources, a primary destination folder and an organisation policy in the libraries file. This spec adds: several libraries per content type (lift the per-kind limit where safe), a library-level access value (Standard, Selected profiles, Vault-protected), a library-level file handling block (organisation, write metadata, duplicates, recycle bin) with optional per-folder override, and a per-folder scan on/off.
- Vault-protected libraries continue to be stored in the Vault's own tables (not as account grants; Videos D55b). The overview reads both through the central access service.
- Pre-release: the libraries file gets a new format version; an older file is refused with the existing plain message and Library starts fresh (project rule: no migrations).

## 6. Work packets

Built in the cloud; waves one after another. Wave A needs nothing from the Vault. The Vault work waits for the Vault sections spec's central access service (its WP0) and an approved new version of that spec (section 9).

### WP1 — Library model: several per type, access value, file handling block
- **What it does:** Raises the one-library-per-kind limit, adds the library-level access value and file handling block (D6, D9), per-folder override and scan on/off, new libraries file version.
- **Files likely touched:** `config/libraries.json`, `config/library_kinds.json`, `src/MediaEngine.Contracts/Settings/*`, the Engine settings services, tests (to be confirmed)
- **Depends on:** none
- **Size:** Complex
- **Done when:** two Movies libraries can exist with different folders; overlapping folders are refused; an old file is refused with the plain message; wire fixtures updated.

### WP2 — Overview and library page shell
- **What it does:** The four tabs, the table, search and status filter, health words, the library page with breadcrumb, header, actions and tabs (D2, D7, D17, D19); hover and chevron cues (F33); phone read-only.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/Libraries/*`, `LibrariesTab.razor` (to be confirmed)
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** a bUnit test shows the right rows, counts and status per area; the phone render has no edit controls.

### WP3 — Create library dialog and folder picker with checks
- **What it does:** D3, D4, D5, D14: one-screen dialog, inline folder validation read from the Engine (capped count, refusals), picker polish, top-layer tooltips.
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** picking a folder inside another library's folder is refused with a plain message; a 126-file folder shows "126 video files found"; a tooltip over a dialog is visible (F9 test).

### WP4 — Folders tab, Add folder, Add files
- **What it does:** D7, D8: the table and "..." menu, import destination, Scan now, Add folder (picker then done), Add files (server file picker, copy with free-space check).
- **Depends on:** WP2, WP3
- **Size:** Standard
- **Done when:** adding a folder shows one confirmation and no follow-up question; Add files copies and leaves originals untouched.

### WP5 — File handling tab
- **What it does:** D6, including the visible conversion job when the first switch is turned on, Save/Discard bar and unsaved prompt.
- **Depends on:** WP1, WP2
- **Size:** Standard
- **Done when:** switching on automatic rename for a library with files shows the file count and starts the existing job only after Confirm; Discard restores saved values.

### WP6 — Access tab (Standard and Selected profiles)
- **What it does:** D9 for the two non-Vault choices on top of the Simpler access rules (D9).
- **Depends on:** WP1, WP2
- **Size:** Standard
- **Done when:** a profile not selected never sees the library in lanes, search, counts or the apps.

### WP7 — Activity and Lookup tabs
- **What it does:** D15, D16: filtered activity; YouTube key help; online lookup switch for Vault libraries (the switch itself waits for WP9).
- **Depends on:** WP2
- **Size:** Standard
- **Done when:** the YouTube tab shows the four steps; activity lists this library's events only.

### WP8 — Same page in first-run setup, and old-screen removal
- **What it does:** D13: setup uses the shared component in simple mode; removes the three-step wizard, group cards, Vault card and tile, and the older setup libraries step.
- **Depends on:** WP2 to WP5
- **Size:** Standard
- **Done when:** setup and Settings render from one component; a grep finds no old wizard or Vault card.

### WP9 — Vault-protected libraries and conversion
- **What it does:** D10, D11, D12: the Vault-protected option and "Add as a Vault folder" (administrators only), creation of the Vault sibling library, the read-only Access display, the Move to Vault shortcut, Adult content with its default Vault tick, the same behaviour in Watch when unticked, and Watch showing its pictures as galleries (D21). Gets the extra security review.
- **Depends on:** WP3, WP6, Vault spec WP0 to WP3, an approved new version of the Vault sections spec
- **Size:** Complex
- **Done when:** for a non-administrator, a render of every page contains no Vault text; a Vault library never appears in normal search, counts, events or another profile's lists, and a film moved from Movies to Private Movies keeps its progress.

### WP9b — Vault in the navigation: per-profile switch
- **What it does:** D20: the profile switch, the navigation entry shown only when the profile has Vault access and the switch is on, and the Settings entry point.
- **Files likely touched:** profile preferences contract and storage, the navigation component, Settings user section (to be confirmed)
- **Depends on:** Vault sections spec WP1
- **Size:** Standard
- **Done when:** a profile with the switch off has no Vault link in the navigation on any screen size but can open the Vault from Settings; a profile without access sees nothing at all.

### WP11 — View on the same screens
- **What it does:** Shy's answer 5A: View's three kinds use the overview table, the Folders tab and the File handling tab (galleries for photos), keep View's own access controls in place of the Access tab, and have no Add library dialog.
- **Depends on:** WP2 to WP5
- **Size:** Standard
- **Done when:** Photos, Personal videos and Documents open the same page shape as Movies, with View's per-profile switches and shared-library controls on the access tab.

### WP12 — Several named libraries and the Watch Videos page
- **What it does:** D22 and D23: lifts the one-library limit for the catch-all kinds, adds the Videos entry in Watch with library chips (access-aware), and the subfolder hints.
- **Depends on:** WP1, WP2
- **Size:** Standard
- **Done when:** three Other videos libraries show as three rows and three chips; a profile that cannot see one library sees neither the row nor the chip; two Movies libraries are still refused.

### WP13 — Remove the View Folders page
- **What it does:** D24: deletes the View Folders page and its navigation entry, the Engine folder browse service, endpoint and contracts, pinned-folder and sub-branch timeline policy storage; adds the per-source "Show in Photos timeline" switch to the View Folders tab; updates wire fixtures, tests and docs (View personal space guide, presentation rules).
- **Files likely touched:** `ViewFoldersPage.razor`, `ViewSectionShell.razor`, `ViewFolderService.cs`, `ViewEndpoints.cs`, `ViewFolderContracts.cs`, `EngineApiClient.View.cs`, Contracts tests and fixtures, docs (to be confirmed)
- **Depends on:** WP11
- **Size:** Standard
- **Done when:** no route, menu entry, endpoint or wire type for View folders remains; a source folder with the switch off keeps its photos out of the timeline; a search of the docs finds no description of the Folders page.

### WP10 — Docs and batch
- **What it does:** Public Libraries and Vault pages, presentation rules, library setup spec note; appends Local checks to the batch list.
- **Depends on:** WP2 to WP9
- **Size:** Mechanical

## 7. Local checks

- **L1.** On a small test library, open Settings > Libraries: four tabs, a table, clear hover and chevron; clicking a row opens it.
- **L2.** Add library for a real movie folder on `Z:\Development\testing\media\real_media`: the picker shows the file count within a few seconds, and Create lands on the new page with a scan running.
- **L3.** File handling: with both switches off, no file on disk changes after a scan; turning on automatic rename shows the count and asks first.
- **L4.** Add files: pick two big files; originals stay, copies appear in the import destination.
- **L5.** A non-administrator sees no Vault word anywhere; as an administrator, create a Vault-protected library, then move a film from Movies into it; check search, counts and a second profile.
- **L6.** First-run setup on a fresh data folder looks like the Settings page, in simple mode.
- **L7.** Tooltips on every dialog appear on top.

## 8. Risks and open questions

- **Risks:** several libraries of one type may show the same title twice (two copies in two libraries); converting to Vault is the most sensitive action in the app, so every leak path needs a test; folder file counts on slow network drives must never freeze the dialog (capped, cancellable); removing the old wizard before the new dialog is solid would leave no way to add folders, so WP8 goes last.

**Answered by Shy (2026-10-27):** Vault viewing keeps the PIN unlock as a second step (8, D10, D20); libraries per type (answer 1, D1), Selected profiles starts hidden (2), no conversion (3), adult content under Watch (4), Vault libraries never listed (6).

**Also answered:** adult pictures stay with adult videos in one Adult content kind (9A, D21); View gets the same overview, Folders and File handling screens later, keeping its own access controls and no Add library dialog (5A, new WP11); cross-drive Move to Vault is allowed as copy then delete (7A).

**Open questions:** none. Shy answered every question on 2026-10-27 (Q9: answer A, see D21).

## 9. Gaps and drift

- **Vault sections spec (Locked, v4):** compatible. It needs a new version (v5) that: drops D32's root-folder option and the Vault tile, states that a Vault library is created or converted from this Access setting, and says a Vault library is created as one (no conversion); Move to Vault (WP3) is the way in. D28 and D39 stay.
- **Library setup spec (Built, v32):** D1, D4 (Vault in an area), D5, D7, D8, D13, D20 (Adult content kind in Vault area), D32 and D57 are superseded by this spec once locked; a version 33 note points here.
- **Simpler access (Built):** D2 and D11 stay for Standard libraries; a new version adds Selected profiles starting hidden for new profiles (D9).
- **Videos (Built):** D17 and D45 (Vault invisible to anyone who cannot open it) stay; the unlock stays as a second step for viewing (D10).

## Plain English summary

Libraries become one simple list: pick Watch, Listen, Read or View, and see your libraries with their health. One dialog adds a library; one page per library has Folders, Access, File handling and Activity. The folder picker checks the folder for you. Nothing is renamed or changed unless you switch that on. A Vault library is just a library whose Access is "Vault-protected", so there is no separate Vault section, and nobody without access can tell it exists. You do not convert a library; you move films into the Vault. First-run setup uses the same screens. No decisions are left open.

## Follow-ups

Added 2026-10-28 when the docs packet (WP10) was written. Nothing here changes the Locked body above; each item is work still to plan or build.

- **Managed import folder for View** (D25, D26): the staging folder, filing by capture date, duplicates and the reject messages need Engine work. Planned in [Photo and video filing](photo-video-filing.md) (Locked 2026-10-28), which keeps staging in the global Import area outside the library folder instead of the import subfolder inside it (D25).
- **View pages lack Add files and Scan now**: Photos, Personal videos and Documents have Add folder only.
- **Vault work still to build** (Vault sections WP1 to WP3): the durable Move to Vault job (the dialog shows a preview only today), Managed Vault folders, and the lookup gates that make the "Look up details online" switch send anything.
- **Needs Review `?library=` filter**: the "Needs review" status link opens Needs Review without filtering to the library.
- **Galleries for Adult content pictures** (D21).
- **Settings > Backup page note** (D26).
