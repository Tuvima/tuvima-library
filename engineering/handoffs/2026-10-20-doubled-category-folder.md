# Handoff: stop filing media into a doubled category folder

Run this handoff end to end per `.claude/rules/end-to-end-runs.md`.

> Written 2026-10-20 by session nifty-bardeen-de8194, paused by Shy partway through the build. Shy approved the plan (option B below) on 2026-10-20, so finishing and merging this needs no second yes (CLAUDE.md step 9). Work in progress is saved on the parallel work stream `claude/nifty-bardeen-de8194` as one save point marked WIP. Start from that branch, not from main.

## 1. The bug

On the default layout each type has its own managed folder (`{TuvimaData}/library/Books`, `.../Music`, ...). The standard naming templates in `config/core.json` began with the category (`Books/{Author}/...`). Every route that files a file joined the template onto the type's own folder, so media landed in `library\Books\Books\{Author}\...`. This was seen live on 2026-10-20 on a fresh default-layout store. Only the dry run (`IngestionEngine.Simulation`) and the dev integration harness's expected path joined onto the top library folder, so they disagreed with real filing.

| Route | Folder the template is joined onto |
|---|---|
| Staging promotion (`AutoOrganizeService.TryAutoOrganizeAsync`) | the owning library's New media folder (`LibraryFolderEntry.LibraryRoot`) |
| Import folder filing (`AutoOrganizeService.TryFileFromImportAsync`, `ImportFolderFiler`) | `destination.Source.Path` |
| In-place tidy (`AutoOrganizeService.HandleAlreadyOrganizedAsync`) | the owning source folder |
| Folder organise job (`FolderConversionOrganiser`) | `context.SourceRoot` |
| Dry run, dev expected path | was `IngestionOptions.LibraryRoot` |

## 2. Decision (Shy, 2026-10-20): option B

Templates name the path **inside the type's own folder** and never start with the category. Every route joins the template onto the type's folder. A custom per-library template (`organization_policy.custom_template`) is read the same way. Existing doubled folders and custom templates that include the category are pre-release data: no migration (CLAUDE.md rule 6).

## 3. Done so far (WIP save point; it assembles with 0 errors)

- `config/core.json` and `docker/config/core.json`: the category is removed from every template, and the default is `{Title} ({Year})/{Title}{Ext}`. The docker template block was also brought in line with core (it had drifted, and had an unused `Comic` key).
- `IngestionOptions`: built-in defaults and the hard-coded fallback no longer include the category. The `Comic` key is fixed to `Comics`, the `MediaType` name.
- `LibraryOrganizationPath` gains three new members:
  - `UnknownCategory` ("Other")
  - `Category(MediaType?)`, now the single category map; `FileOrganizer`'s `{Category}` token delegates to it
  - `FilingCategory(library, mediaType)`, which returns null for Keep original names
  - `TypeFolder(library, libraryFolder, mediaType)`: the library's New media folder, else `libraryFolder/{Category}`
- `IOrganizationGate.Evaluate`: the last parameter is now `string? filingCategory` (was `resolvedRelativePath`). Gate 3 blocks when it equals "Other", instead of checking whether the path starts with "Other". All callers are updated: AutoOrganizeService (three places), the Pipeline (only when auto-organise is on), and FolderConversionOrganiser. The Simulation re-organise "Other" checks now use `FilingCategory`.
- AutoOrganizeService:
  - Staging promotion joins onto `TypeFolder(...)`.
  - `HandleAlreadyOrganizedAsync` now takes a `typeFolder` (where the template is joined) separately from `libraryRoot` (the folder the new name must stay inside). The fallback branch passes `TypeFolder(owningLibrary, libraryRoot, mediaType)`.
- Dry run (`IngestionEngine.Simulation`) and `IntegrationTestEndpoints.BuildExpectedLibraryPath` now join onto `TypeFolder`. The latter finds the library by media type in `options.LibraryFolders`.
- `CoreConfiguration` and `FileOrganizer` doc comments are updated.

## 4. What is left

1. **Tests (in progress).** `dotnet test tests/MediaEngine.Ingestion.Tests --no-build --filter "Category!=LiveProvider"` gave about 60 failures. These are expected: the tests pin the old doubled path. The affected files are:
   - `CaseOnlyRenameSafetyTests`
   - `FolderConversionTests` (`Standard()` helper, line 34)
   - `FolderConversionLinkTests`
   - `FolderMergeTests` (`Standard()` helper, line 40)
   - `ManagedFolderTidyInPlaceTests`
   - `HydrationDataSafetyTests`
   - `ImportFolderFilingTests`
   - `ImportFolderPipelineTests`

   They build expected paths as `Path.Combine(<typeDir>, "Movies", "Dune (2021)", ...)` or write source files at `"Movies/Dune (2021)/..."`. Remove the leading category segment from **expected destinations, and from files written to be already at their standard name**. Leave Import-folder source paths such as `WriteImportFile("Movies/Dune.mkv")` alone: those are the user's own Import subfolders. About 131 lines match. For each failure, confirm it's this cause and not a real regression before changing it.

   Then run the other affected test projects and fix what they pin:
   - Api.Tests (`FolderJobServiceTests`, Settings preview, integration endpoints)
   - Web.Tests (`LibrarySettingsFlowTests` line 183 uses old-style templates in a fake; update for consistency)
   - Storage.Tests, if any config fixture carries templates
   - `FileOrganizerTests` uses `{Category}` templates on purpose to test the token. Leave those.
2. **New tests.** Add one per route proving a managed Books/Music/Movies/TV file lands at `{typeFolder}\{Author or Title}\...` with no doubled segment:
   - staging promotion
   - Import filing
   - in-place tidy
   - folder organise
   - dry run

   Also add a gate test: an unknown type is held (filing category "Other"), and a Keep-original-names library is not held by it.
3. **Settings naming examples.** `src/MediaEngine.Web/Components/Settings/LibraryAdvancedSettings.razor` `FallbackExamples` start with the category (`"Books/Frank Herbert/..."`); drop it. Consider a short "inside the Books folder" hint by the example. The Settings preview action (`SettingsEndpoints` ~599-716) renders the template with sample tokens, so it follows the new templates automatically. A Dashboard change needs the visual checks in `src/MediaEngine.Web/CLAUDE.md`.
4. **Docs.**
   - `docs/reference/configuration.md` lines 60-61: templates are inside the type's folder, and `{Category}` is still available but unused by default.
   - Any naming or folder-layout explanation in `docs/` (grep `organization_template`, `Books/{Author}`, `library/Books/`).
   - `AGENTS.md`, if it shows the old templates.
   - Per `.claude/rules/docs.md`.
5. **Check and finish.**
   - Run the CI-strength build (`dotnet build MediaEngine.slnx --no-restore --warnaserror --no-incremental`) and the CI test filter.
   - Then do one short isolated Engine **check**: a small generated library of a few Books, Music, Movies and TV files on the default layout. Claim the shared slot in the main checkout's `.tmp/engine-slot.md` (`.claude/rules/one-engine-at-a-time.md`), confirm each file lands at `library\{Type}\{Author or Title}\...`, then release.
   - Run the `reviewer` agent, open the proposed change with Auto-fix on, and merge per CLAUDE.md step 9.
   - Replace the WIP save point message (squash or amend) before opening it.

## 5. Things to watch

- `LibraryReconciliationService.CleanStaleRootSidecars` (~line 550) cleans `library.xml` directly inside category folders, from an old shallow-template bug. With the new templates, `{Author}` is the first level inside the type folder, so this is unaffected, but read it once.
- `IngestionEngine.Pipeline` `isAtTargetLocation` is a suffix match on the relative path. It still works, and is now stricter.
- Gate behaviour change: an unknown-type file in a library with Keep original names is still never held by gate 3 (`FilingCategory` returns null). That matches the old behaviour.
