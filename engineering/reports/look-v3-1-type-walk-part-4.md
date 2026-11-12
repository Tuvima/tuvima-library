# Part 4: Libraries, setup, the folder window and Collections

Each line: the file, then what moved to which type job (`--tl-type-<job>-size`, and the job weight where the rule already set one). "Left" lists text sizes kept on purpose (icon glyph holders). All paths are under `src/MediaEngine.Web/Components/`.

- `Settings/Libraries/LibrariesOverview.razor.css`: changed: 1 to control, 2 to secondary. left: `.libs__lock` (lock icon, var(--tl-font-size-md))
- `Settings/Libraries/LibraryFolderStepChips.razor.css`: changed: 1 to control
- `Settings/Libraries/LibraryEditDialog.razor.css`: changed: 1 to control, 2 to secondary
- `Settings/Libraries/LibraryShell.razor.css`: changed: 1 to secondary
- `Settings/Libraries/ViewKindPage.razor.css`: changed: 1 to secondary
- `Settings/Libraries/LibraryStatusLine.razor.css`: changed: 1 to body
- `Settings/Libraries/ViewDuplicatesPanel.razor.css`: changed: 1 to caption, 1 to secondary
- `Settings/Libraries/AddViewFolderDialog.razor.css`: changed: 1 to overline, 2 to secondary
- `Settings/Libraries/LibraryFoldersPopover.razor.css`: changed: 2 to secondary
- `Settings/Libraries/LibraryLookupTab.razor.css`: changed: 1 to subheading
- `Settings/Libraries/LibraryVaultLookupTab.razor.css`: changed: 1 to subheading
- `Settings/Libraries/ViewKindEditDialog.razor.css`: changed: 1 to control, 3 to secondary
- `Settings/Libraries/ViewFileHandlingTab.razor.css`: changed: 1 to body, 2 to caption, 1 to secondary
- `Settings/Libraries/LibraryChoiceRow.razor.css`: changed: 1 to secondary, 1 to subheading
- `Settings/Libraries/AddFilesDialog.razor.css`: changed: 2 to secondary
- `Settings/Libraries/LibrariesSummary.razor.css`: changed: 1 to secondary, 1 to subheading. left: `.lib-summary__icon` (icon token)
- `Settings/Libraries/ViewImportPanel.razor.css`: changed: 2 to caption, 1 to control, 1 to secondary
- `Settings/Libraries/CreateLibraryDialog.razor.css`: changed: 1 to control, 1 to overline, 3 to secondary
- `Settings/Libraries/LibraryServerRulesCard.razor.css`: changed: 1 to secondary
- `Settings/Libraries/ViewAccessTab.razor.css`: changed: 1 to secondary
- `Settings/Libraries/ViewFoldersTab.razor.css`: changed: 3 to caption, 2 to secondary
- `Settings/Libraries/LibraryScanCard.razor.css`: changed: 1 to control, 1 to secondary, 1 to subheading
- `Settings/Libraries/ViewActivityTab.razor.css`: changed: 1 to secondary. left: `.view-activity__icon` (icon token)
- `Settings/Libraries/LibraryHealthIcon.razor.css`: already right (only the status icon holder, var(--tl-font-size-dialog-title), an icon size)
- `Settings/Libraries/LibraryAreaChips.razor.css`, `LibraryFileHandlingTab.razor.css`, `LibraryStatusCell.razor.css`, `MoveToVaultDialog.razor.css`: already right (no font sizes)
- `Settings/LibraryActivity/LibraryActivityProgressCard.razor.css`: changed: 1 to heading, 1 to secondary
- `Settings/LibraryActivity/LibraryActivityTileFace.razor.css`: changed: 1 to overline
- `Settings/LibraryActivity/LibraryActivityRunEntry.razor.css`: changed: 2 to caption, 1 to label, 1 to overline, 1 to secondary
- `Settings/LibraryActivity/LibraryActivityPage.razor.css`: changed: 2 to page-title, 1 to secondary
- `Settings/LibraryActivity/LibraryActivityWorkingNow.razor.css`: changed: 1 to heading
- `Settings/LibraryActivity/LibraryActivityHistory.razor.css`: changed: 1 to heading, 1 to overline, 2 to secondary, 1 to subheading
- `Settings/LibraryActivity/LibraryActivityPicture.razor.css`, `LibraryActivityTile.razor.css`: already right (no font sizes)
- `Setup/SetupVaultOffer.razor.css`: changed: 1 to secondary, 1 to subheading
- `Setup/SetupIntroStage.razor.css`: changed: 1 to body
- `Setup/SetupStepper.razor.css`: changed: 1 to caption, 1 to control
- `Setup/IntroAnimation.razor.css`: changed: 1 to overline (size and weight; was clamp(9px, 1.32vw, 16px) at 500)
- `Setup/SetupLibrariesTable.razor.css`: changed: 1 to secondary
- `Setup/SetupCodeStage.razor.css`, `SetupLibrariesStage.razor.css`, `SetupLocaleStage.razor.css`, `SetupToolsStage.razor.css`, `SetupWelcomeStage.razor.css`: already right (no font sizes; step and welcome titles take their size from the shared heading styles)
- `Collections/CollectionSetChildren.razor.css`: changed: 1 to heading
- `Collections/CollectionPackStep.razor.css`: changed: 1 to body, 3 to secondary
- `Collections/CollectionNotInLibrary.razor.css`: changed: 1 to caption, 1 to heading, 1 to secondary. left: `.collection-missing__plain` (icon holder, var(--tl-font-size-hero))
- `Collections/CollectionEditorShell.razor.css`: changed: 3 to body, 4 to caption, 3 to control, 3 to heading, 2 to overline, 13 to secondary, 2 to subheading. left: five `.tl-icon-root` icon sizes (icon tokens)
- `Collections/CollectionEditorShell.razor`: already right (only the icon utility class `tl-u-font-size-24px` on an icon)
- `Collections/CollectionsSectionLayout.razor.css`: changed: 1 to body, 1 to label, 1 to overline, 1 to page-title
- `Collections/CollectionPersonalMediaSection.razor.css`: changed: 1 to heading, 1 to overline
- `Collections/CollectionRuleValuePicker.razor.css`: changed: 3 to caption, 1 to label, 1 to overline. left: two `.tl-icon-root` icon sizes
- `Collections/CollectionRuleBuilder.razor.css`: changed: 1 to caption, 1 to secondary
- `Collections/CollectionMediaChips.razor.css`, `PeopleCatalogList.razor.css`: already right (no font sizes)
- `Settings/LibraryFolderGrid.razor.css`: changed: 1 to body, 2 to secondary
- `Settings/LibraryFolderTable.razor.css`: changed: 1 to body, 1 to caption, 1 to overline, 2 to secondary
- `Settings/LibraryFoldersPanel.razor.css`: changed: 1 to body, 1 to heading, 1 to overline, 5 to secondary, 1 to subheading
- `Shared/AppBrowserFrame.razor.css`: changed: 1 to body, 1 to secondary
- `Shared/ServerFolderPicker.razor.css`: changed: 1 to field text (`--tl-field-text-size`, was var(--tl-font-size-lg, 1.125rem)), 2 to secondary
- Razor files (`*.razor`) in these folders: already right (no inline `font-size`; the only size is the icon utility class above)

## Size moves above 4 px, and other notable moves

- `Settings/LibraryActivity/LibraryActivityPage.razor.css` `.lact-title-main`: 32 px to page-title (24 px), 8 px smaller; the phone rule (was 24 px) now names page-title too, so the two are the same size.
- `Collections/CollectionsSectionLayout.razor.css` results `h1`: clamp(1.6rem, 1.7vw + 1rem, 2.65rem), 25.6 to 42.4 px, to page-title (24 px); up to 18 px smaller on wide screens.
- `Setup/IntroAnimation.razor.css` `.intro-anim__tag`: the fluid 9 to 16 px tagline (uppercase, letter-spaced) to overline (12 px, weight 500 to 600). Within 4 px but the weight rises.
- Exactly 4 px: `Collections/CollectionEditorShell.razor.css` window title `.sme-title`, the membership summary number and the preview count (24 px to heading 20 px); `Shared/ServerFolderPicker.razor.css` typed folder name field (18 px to field text 14 px, 16 px on a phone).
- 2 px with a weight change: `Collections/CollectionEditorShell.razor.css` rail items (16 px at 600 to control 14 px at 500); `Setup/SetupIntroStage.razor.css` lead line (16 px to body 14 px).

## Left on purpose (icon holders)

- `Settings/Libraries/LibrariesOverview.razor.css:17` `.libs__lock` (var(--tl-font-size-md))
- `Settings/Libraries/LibraryHealthIcon.razor.css:5` `.lib-health` (var(--tl-font-size-dialog-title))
- `Collections/CollectionNotInLibrary.razor.css:46` `.collection-missing__plain` (var(--tl-font-size-hero))

## Unclear cases

None left unchanged. Judgement calls to check on the visual review: `Setup/IntroAnimation.razor.css:7` (overline vs label), `Collections/CollectionEditorShell.razor.css:107` rail items (control vs menu), `Settings/Libraries/AddViewFolderDialog.razor.css:21` and `CreateLibraryDialog.razor.css:22` letter-spaced captions (overline vs label).

## After the My List build merged

The My List page, the hero action row's progress label and the reconnect window title now name their type jobs (body, secondary and heading). Every move keeps the same size. The hero action button label stays at its own 20 px hero size (the hero is an allowed exception in D8). The top navigation and the dock have no sizes of their own left to move.
