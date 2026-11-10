# Spec: View folders behave like every other library row

**Status:** Locked
**Version:** 4
**Last updated:** 2026-11-07 (locked by Shy, 08:28Z)
**Related docs:** [Libraries v3 (fixed names, folder names)](libraries-fixed-names.md) (D17, D18, D20, D21) · [Library setup and add-folder](library-setup-add-folder.md) · [Library pages](library-pages.md) (D24, D25) · [Look pass v3](look-pass-v3.md) (D8, D13, D14, D15, D17) · [Universal editor v5](archive/universal-editor-v5-header-navigation-and-supporting-files.md) (D32, WP8a) · mockups `../design/mockups/view-folders/view-folders-mockups.html` · issue #680

## 1. Goal

A View folder row in the Libraries table (first-run setup, and the same table in Settings) should look and act like a Movies or TV row: named by its kind, "N folders" with a list, one Edit button, and a path that can never push the table wider. Adding a View folder should not ask for a name.

## 2. What is wrong today (findings)

Found in the code; not shown live because adding a View folder writes to a real folder.

1. **The Add folder window requires a name and, in setup, offers no handling choice** (`AddViewFolderDialog`: `ShowFolderName="true"`, and `CanAdd` requires a non-empty name). The picker already fills it from the folder's own name, so a required box is a way to get stuck. The same window forces Read-only (`ManagedAllowed="false"`), while Movies and TV offer Read-only or Managed (Libraries v3 D19). The Folders tab on the View page has the same required "Folder name" box.
2. **Setup lists every View folder as a row of its own**, named after the folder (`SetupLibrariesTable.razor`, the View branch of `Rows`). Movies and the other kinds have one row per kind.
3. **The Folders cell prints the whole path** for those rows (`row.Library` is null, so it falls back to `@row.Path`) instead of the "N folders" button and list.
4. **The action cell has three small icon buttons** (rename, change folder, remove) instead of the one Edit button.
5. **A long path pushes the other columns off the table** (a network path of 80 characters is enough at 1024 px). The same risk exists for any text in a table cell.

Settings already lists View by kind with a count (`LibraryListing`, `IsView`). It shows a chevron where the others have Edit.

## 3. Scope

**In:** setup rows by kind; the folder list for View rows; the Edit button and an Edit window for a View kind; an optional (never required) friendly name and the Read-only or Managed choice in Add; a Folders filter on the View pages; clipped paths in table cells; Settings View rows gain Edit; tests, docs, Local checks.
**Out:** the View page's own controls (timeline switch, tidy, owners, import panel); personal-space folders and Managed View folders beyond what the count already includes; the picker's look (Editor v5 WP8a builds it); any change to other libraries' Add or Edit windows; wire shapes (the Engine still stores a name).

## 4. Decisions made

Shy answered on 2026-11-07: 1A, 2B, 2b yes, 3A, 4A.

- **D1 (Shy, 1A). Setup lists View by kind**, one row per kind that has folders: Photos, Personal videos, Documents. Same as Settings and as Movies.
- **D2 (Shy, 2B). Add folder shows the friendly name as an optional box, pre-filled from the folder's own name, the same as every other library** (Libraries v3 D18). It never blocks Add folder: an empty box saves the folder's own name, and a name already used in the same kind gets " (2)". The name is shown in lists only when the kind has two or more folders. The Folders tab on the View page uses the same optional box; a Managed folder created there keeps it because the name creates the folder.
- **D3 (Shy, 3A). Settings View rows get the same Edit button**; the row still opens the View page.
- **D4a (Shy, 4A). Read-only or Managed is chosen when a View folder is added, in setup and in Settings.** Read-only is the default; Managed is offered and recommended for a new photo folder (Library pages D25). Setup's "View folders are always read-only" rule is removed. Nothing moves during setup because nothing is scanned there; handling takes effect when scanning starts. Edit shows each folder's tag and offers Switch to Managed as for other libraries (Libraries v3 D27).
- **D4b (Shy, 2b yes). A Folders filter on the Photos, Personal videos and Documents pages** once the kind has two or more folders: All folders or one folder by friendly name, with its Read-only or Managed tag, remembered per profile in the browser (Libraries v3 D18, which was not built). Reason: Ease of use; it answers "what is on which drive".
- **D4c (Shy, 2026-11-07 08:13Z). Nothing can be uploaded to View until a Managed folder is added for that kind.** The rule is the same for all three kinds: Photos, Personal videos and Documents (Shy, 08:13Z, "or documents as well"). Uploads, phone backup and Add files are filed into a Managed folder (Library pages D25); a Read-only folder is never written to. So while a kind has no Managed folder: (a) Add files and the upload drop zone on that kind's page are disabled, with a tooltip that says why ("Uploads need a Managed folder") and a line on the page with an Add a Managed folder button; (b) Add folder shows a plain line when Read-only is picked and the kind has no Managed folder yet ("You can't upload photos until you add a Managed folder" (the noun follows the kind: photos and videos, personal videos, documents)); (c) the Edit window shows the same line above the folder blocks. The line disappears once a Managed folder exists. Reason: Ease of use and Reliability; the person learns why upload is off before trying it.
- **D4. Edit for a View kind** is a Window M (Look v3 D14) in the same anatomy as the library Edit window: one block per folder (clipped path, online dot, Read-only or Managed tag, skipped count, friendly name when 2+ folders, icon-only red-outlined Remove), Add folder, and Save changes showing Review first. Removing a folder never deletes files. It reuses `LibraryEditDialog` with a View mode if that fits; if it does not, a small `ViewKindEditDialog` shares the folder block.
- **D5. The folder list part** (`LibraryFoldersPopover`) takes a small neutral item (path, online state, label) so it serves both library folders and View folders.
- **D6. Cut-off text in a table cell is clipped and the full text shows in a tooltip** (Look v3 D8c, D15). Table columns are fixed; a cell never sets the table's width. A guardrail test renders a 90-character path at 1024 px and asserts the table does not grow.
- **D7. The three-icon cell and its setup helpers go** (rename, change folder, remove for a View folder in `LibraryFolderAddService`). No redirect or compatibility path is kept (Look v3 D17).
- **D8. Wording and rules.** New strings go in `SharedStrings.resx`; no new rule or word list; the name length limit stays `FolderHandlingWords.MaxFolderNameLength`.
- **D9. Ownership.** Look v3 owns the window frame, the tooltip rule and the alignment rules; Editor v5 WP8a owns the picker frame. This spec consumes them and sets no colours, fonts or sizes of its own.

## 5. UI behaviour

See `view-folders-mockups.html` (screens A to F, numbered). Empty: no View folders means no View row; the View chip shows 0 and Add library > View is the way in. Loading: unchanged. Error: Add shows the picker's check message (Already used blocks); Save in Edit shows the Engine's message above the footer. Partly ready: an offline folder shows an amber dot in the list and Edit. Phone: setup stays read-only (the existing note); Settings shows the card with the same count and an Edit that opens a Sheet. Tablet and desktop: the table. TV: not applicable.

## 6. Data and model changes

None. `CreateViewSourceRequest.Name` stays required on the wire; the Dashboard now fills it. No data conversion (pre-release).

## 7. Work packets

### WP1: View rows by kind
- **What it does:** D1, D3, D5. Setup's table groups View folders by kind; the folder cell uses the list part; Settings View rows show Edit.
- **Files likely touched (to be confirmed):** `Components/Setup/SetupLibrariesTable.razor`, `Components/Settings/Libraries/LibrariesOverview.razor`, `LibraryFoldersPopover.razor`, `LibraryListing.cs`.
- **Depends on:** none (consumes Look v3 wave 1 for styling).
- **Size:** Standard.
- **Done when:** a setup with two Photos folders and one Documents folder shows two rows, "2 folders" and "1 folder", each opening a list with no path in the cell.

### WP2: Edit window for a View kind
- **What it does:** D4, D7. Folder blocks, friendly name (2+ folders), remove, add, Review. Removes the three icon buttons and their helpers.
- **Files likely touched:** `LibraryEditDialog.razor` (or new `ViewKindEditDialog.razor`), `LibraryFolderAddService.cs`, `SetupLibrariesTable.razor`.
- **Depends on:** WP1.
- **Size:** Standard.
- **Done when:** Edit on Photos can rename, remove and add a folder in setup and in Settings; Review lists the change; no icon-button cell remains.

### WP3: Add folder: optional name and handling choice
- **What it does:** D2, D4a. Make the name optional in `AddViewFolderDialog` (fill from the folder, (2) rule), offer Read-only or Managed there (remove `ManagedAllowed="false"`), and make the name optional in the `ViewFoldersTab` form for a linked folder.
- **Files likely touched:** `AddViewFolderDialog.razor`, `ViewFoldersTab.razor`, `FolderHandlingWords`, strings.
- **Depends on:** none.
- **Size:** Mechanical to Standard.
- **Done when:** Add folder is enabled the moment a usable folder is chosen, with or without a name; Read-only and Managed both work in setup and Settings; two folders with the same name save as "Pictures" and "Pictures (2)".

### WP3b: Folders filter on View pages
- **What it does:** D4b. The Folders filter in the filter row of the three View kind pages, shown at two or more folders, remembered per profile; the Engine list actions take an optional folder.
- **Files likely touched (to be confirmed):** the View kind pages and their list queries, Engine View list endpoints, Contracts and wire fixtures, tests.
- **Depends on:** WP3.
- **Size:** Standard.
- **Done when:** with a Read-only and a Managed folder in Photos, choosing one shows only its pictures, and the filter is absent with one folder.

### WP3c: Upload needs a Managed folder
- **What it does:** D4c. A shared "has a Managed folder" check per View kind; the disabled Add files and drop zone with reason tooltip, the page line with its button, and the notes in Add folder and Edit. Wording in `SharedStrings.resx`.
- **Files likely touched (to be confirmed):** the View kind pages, `LibraryUploadZone.razor`, `AddFilesDialog.razor`, `AddViewFolderDialog.razor`, the Edit window, tests.
- **Depends on:** WP3.
- **Size:** Standard.
- **Done when:** with only a Read-only Photos folder, Add files and the drop zone are off with the reason shown and nothing uploads; adding a Managed folder turns them on and removes the line.

### WP4: Clipped cells guardrail
- **What it does:** D6. Fixed table columns, ellipsis and tooltip for cut-off text in the Libraries tables; a bUnit and a style guardrail test.
- **Files likely touched:** `SetupLibrariesTable.razor.css`, `LibrariesOverview.razor.css`, tests.
- **Depends on:** Look v3 WP0 and WP4.
- **Size:** Standard.
- **Done when:** a 90-character path leaves the table width unchanged at 1024, 1280 and 1440.

### WP5: Docs and checks
- **What it does:** updates the public setup and Libraries guides in `tuvima-docs` (View rows are by kind, no View folder name), `presentation-rules.md`, `engineering/validation/batch.md`; links #680 ("Fixes #680").
- **Depends on:** WP1 to WP4.
- **Size:** Mechanical.

Order: WP1 and WP3 together, then WP2, WP3b and WP3c, then WP4, then WP5. One cloud thread, after Look v3 wave 1; the picker look arrives with Editor v5 WP8a.

## 8. Local checks

On a small test library, never the real one: (1) first-run setup, add a Photos folder with a very long network path, add a second, check rows, "N folders", the list, Edit, remove; (2) Settings > Libraries same rows, Edit on View kinds; (3b) with only a Read-only folder in each of Photos, Personal videos and Documents, try Add files and drag a picture onto the page: both are off with the reason; add a Managed folder and they work; (3) Add folder enabled without typing a name, and Read-only and Managed both offered in setup; add one of each to Photos and use the Folders filter; (4) 1024, 1280, 1440 and phone, Mock | Live; (5) an offline folder (unplug a drive).

## 9. Risks and open questions

- Which folders the Settings count includes (Shared Library only, or personal-space folders too) must be confirmed by the build's first read; Edit lists the same set.
- The Folders filter changes Engine list actions and the wire, so it needs Contracts tests and fixtures (WP3b).
- Switching a View folder to Managed reuses the Libraries v3 D27 preview; confirm it applies to View folders in the build's first read.

## Plain English summary

View folders will look and work like Movies and TV: one row per kind (Photos, Personal videos, Documents) with "2 folders", a list, and one Edit button. Adding a View folder offers Read-only or Managed like other libraries, and the name is optional and pre-filled from the folder. Once a kind has two folders, a Folders filter lets you view one at a time. Uploading photos needs a Managed folder, and the app says so up front. A long folder path can no longer stretch the table; it is cut off with the full path on hover. Nothing about how your files are read or stored changes.
