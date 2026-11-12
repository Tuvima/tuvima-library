# Part 3: search, Vault, View, Universe, Listen and shared parts


Each line: the file, then what moved to which type job (`--tl-type-<job>-size`, and the job weight where the rule already set one). "Left" lists text sizes kept on purpose (icon sizes, initials, map and video-subtitle text, fluid headings that would move by more than 4 px).

- `Search/UniversalSearchResults.razor.css`: changed: 4 to caption, 2 to heading, 2 to label, 2 to overline, 2 to secondary, 1 to subheading. left: clamp(1.35rem, 2.3vw, 2rem)
- `Search/UniversalSearchOverlay.razor.css`: changed: 3 to caption. left: var(--tl-font-size-hero); var(--tl-font-size-lg); var(--tl-icon-size-2xl)
- `Vault/VaultCover.razor.css`: changed: 1 to caption, 1 to secondary. left: var(--tl-font-size-xl); var(--tl-font-size-md)
- `Vault/VaultActivityList.razor.css`: changed: 1 to heading, 1 to secondary
- `Vault/VaultItemList.razor.css`: changed: 1 to caption, 1 to heading, 2 to secondary
- `Vault/VaultIdentifyPanel.razor.css`: changed: 1 to heading, 1 to secondary
- `Vault/VaultUnmatchedList.razor.css`: changed: 2 to secondary
- `Vault/VaultSceneView.razor.css`: changed: 1 to subheading
- `Vault/VaultHiddenReminder.razor.css`: changed: 1 to body
- `Vault/VaultImageViewer.razor.css`: changed: 1 to secondary
- `View/ViewUploadsNotice.razor.css`: changed: 2 to body
- `View/ViewMap.razor.css`: changed: 1 to caption. left: clamp(.65rem,.62rem + .14vw,.78rem)
- `View/EmptyTrashDialog.razor.css`: changed: 1 to subheading
- `View/ViewPlaceStoryPanel.razor.css`: changed: 7 to caption, 1 to heading, 2 to label, 2 to subheading. left: var(--tl-icon-size-xs); .9rem
- `View/ViewPlacesTimeline.razor.css`: changed: 4 to caption, 1 to label, 1 to secondary
- `Universe/AlsoAppearsInPanel.razor.css`: changed: 1 to label, 2 to secondary, 1 to subheading
- `Universe/PathFinderPanel.razor.css`: changed: 2 to caption
- `Universe/FamilyTreeView.razor.css`: changed: 1 to caption
- `Universe/UniverseSeriesShelf.razor.css`: changed: 1 to caption, 1 to control, 2 to overline, 1 to secondary. left: clamp(1.25rem, 1.8vw, 1.7rem)
- `Universe/UniverseThingView.razor.css`: changed: 1 to body, 1 to caption, 3 to control, 1 to heading, 1 to secondary. left: clamp(1.6rem, 3vw, 2.4rem); var(--tl-font-size-hero)
- `Universe/UniverseAdaptationsList.razor.css`: changed: 1 to caption, 1 to secondary
- `Universe/UniverseHero.razor.css`: changed: 1 to body, 1 to control. left: clamp(1.9rem, 4.4vw, 3.4rem)
- `Universe/UniverseConnections.razor.css`: changed: 1 to overline, 1 to secondary, 1 to subheading. left: clamp(1.25rem, 1.8vw, 1.7rem)
- `Universe/AdaptationTreeNode.razor`: already right (only utility classes `tl-u-font-size-*` and icon sizes, defined in app.css, out of scope)
- `Universe/UniverseContents.razor.css`: changed: 1 to control, 1 to label, 2 to secondary. left: clamp(1.25rem, 1.8vw, 1.7rem); var(--tl-font-size-hero)
- `Universe/UniverseCastList.razor.css`: changed: 1 to body, 1 to caption
- `Universe/CastComparison.razor`: already right (only utility classes `tl-u-font-size-*` and icon sizes, defined in app.css, out of scope)
- `Listen/SongTable.razor.css`: changed: 1 to body, 1 to caption, 1 to heading, 2 to overline, 1 to secondary
- `Listen/ListenNavigationSection.razor.css`: changed: 1 to overline
- `Listen/SongsPhoneHeader.razor.css`: changed: 1 to overline
- `Listen/ListenNavigationSection.razor`: already right (only utility classes `tl-u-font-size-*` and icon sizes, defined in app.css, out of scope)
- `Library/StatusPill.razor.css`: changed: 1 to overline
- `Library/MovieAsTvSuggestionCard.razor.css`: changed: 1 to body, 1 to caption
- `Library/LibraryConfigurableTable.razor`: already right (only utility classes `tl-u-font-size-*` and icon sizes, defined in app.css, out of scope)
- `Library/LibraryConfigurableTable.razor.css`: changed: 6 to caption, 2 to control, 3 to label, 11 to overline
- `Artwork/ArtworkJustifiedGallery.razor.css`: changed: 1 to body, 2 to caption, 1 to label. left: var(--tl-icon-size-xs)
- `Artwork/ArtworkAssetPickerDialog.razor.css`: changed: 1 to secondary
- `Artwork/ArtworkAssetBrowser.razor.css`: changed: 1 to caption, 1 to heading, 1 to label, 2 to secondary, 2 to subheading
- `Artwork/ArtworkWorkspace.razor.css`: changed: 6 to caption, 5 to control, 2 to heading, 1 to label, 3 to overline, 3 to secondary, 3 to subheading. left: var(--tl-icon-size-sm)
- `Companion/CompanionScreen.razor.css`: changed: 1 to body, 2 to page-title, 3 to secondary, 1 to subheading
- `Companion/CompanionPositionPicker.razor.css`: changed: 1 to secondary, 1 to subheading
- `Companion/CompanionDeviceList.razor.css`: changed: 1 to secondary, 1 to subheading
- `Companion/CompanionNowPlayingCard.razor.css`: changed: 1 to overline, 1 to secondary
- `Rules/SharedRuleBuilder.razor.css`: changed: 1 to caption, 3 to control, 1 to heading, 1 to label, 1 to overline, 6 to secondary, 2 to subheading. left: var(--tl-icon-size-sm); var(--tl-font-size-lg); var(--tl-icon-size-md); var(--tl-icon-size-md); var(--tl-icon-size-xs)
- `Shared/RecycleBinEntry.razor.css`: changed: 1 to body
- `Shared/AppDiscoveryFilterBar.razor.css`: changed: 1 to label
- `Shared/TuvimaArtworkStack.razor.css`: changed: 1 to overline. left: var(--tl-icon-size-2xl)
- `Shared/AppConfirmDialog.razor.css`: already right
- `Shared/AppListCard.razor.css`: changed: 1 to body, 1 to subheading
- `Shared/AppListPageHeader.razor.css`: changed: 1 to page-title
- `Shared/AppLetterStrip.razor.css`: changed: 1 to overline
- `Shared/AppBrowserItemRow.razor.css`: changed: 1 to overline
- `Shared/AppBrowserSideSection.razor.css`: changed: 1 to overline
- `Shared/BookmarksPanel.razor.css`: already right
- `Shared/PlaybackSeekRail.razor.css`: changed: 1 to body
- `Shared/ContextSidebarShell.razor.css`: changed: 1 to subheading
- `Shared/AppDialogShell.razor.css`: changed: 1 to body, 1 to heading, 1 to overline, 1 to page-title, 1 to secondary. left: var(--tl-icon-size-lg); var(--tl-icon-size-md, 24px); var(--tl-icon-size-md); var(--tl-dialog-title-size); var(--tl-dialog-title-size)
- `Shared/AppSwitchRow.razor.css`: changed: 1 to caption, 2 to control, 1 to secondary
- `Shared/AppArtworkProgress.razor.css`: changed: 1 to secondary
- `Shared/MediaCountLine.razor.css`: changed: 1 to secondary
- `Shared/AppSettingsGroup.razor.css`: changed: 1 to secondary, 1 to subheading
- `Shared/AppChoiceRow.razor.css`: changed: 1 to secondary
- `Shared/AppTimelineGroupingControl.razor.css`: changed: 1 to secondary
- `Shared/Providers/ProviderOnboardingDialog.razor.css`: left: var(--tl-icon-size-lg)
- `Shared/Providers/ProviderCredentialForm.razor.css`: changed: 2 to caption
- `Shared/AppBrowserSideItem.razor.css`: changed: 1 to secondary
- `Shared/AppRowErrorBoundary.razor.css`: changed: 1 to caption, 1 to secondary
- `Shared/AppRangeSlider.razor.css`: changed: 1 to control. left: 0
- `Shared/AppDetailPageFrame.razor.css`: changed: 1 to page-title
- `Shared/Tools/ToolsSummaryLine.razor.css`: changed: 1 to secondary
- `Shared/PlaybackContextRow.razor.css`: changed: 2 to caption, 1 to control, 1 to secondary
- `Shared/AppChoiceTile.razor.css`: changed: 1 to caption, 1 to control
- `Shared/AppDialogFrame.razor.css`: changed: 1 to heading
- `Shared/MediaViewerShell.razor.css`: changed: 1 to caption, 1 to overline, 1 to secondary, 1 to subheading. left: clamp(var(--tl-font-size-xs),1.15vw,var(--tl-font-size-lg)); clamp(var(--tl-font-size-xs),1.15vw,var(--tl-font-size-lg))
- `Shared/AppSavedNote.razor.css`: changed: 1 to secondary
- `Shared/ProfileAvatar.razor`: already right (only utility classes `tl-u-font-size-*` and icon sizes, defined in app.css, out of scope)
- `Shared/AppStateCard.razor.css`: changed: 3 to body, 2 to caption, 1 to subheading
- `Shared/AppSettingRow.razor.css`: changed: 1 to control, 2 to secondary
- `Shared/Shell/SidebarPageHeader.razor.css`: changed: 1 to page-title
- `Shared/Shell/AppPageHeader.razor.css`: changed: 1 to overline, 1 to secondary. left: clamp(1.6rem, 2.2vw, 2.2rem); clamp(1.35rem, 2vw, 1.8rem)
- `Shared/AppTimelineNavigator.razor.css`: changed: 3 to caption
- `Shared/MediaArtworkGroupPreview.razor.css`: changed: 1 to overline. left: clamp(1.4rem, 7cqw, 2.4rem)
- `Shared/AppFormRow.razor.css`: changed: 1 to secondary
- `Shared/AppQuietNote.razor.css`: changed: 1 to secondary
- `Shared/AppCollapsibleRow.razor.css`: changed: 1 to label, 1 to subheading
- `Shared/AppChoiceCard.razor.css`: changed: 1 to body, 1 to caption
- `Shared/AppStatusDot.razor.css`: left: var(--tl-font-size-sm)
- `Shared/AppToggleChip.razor.css`: changed: 1 to control
- `Shared/DeleteFromLibraryDialog.razor.css`: changed: 1 to heading, 1 to subheading
- `Pages/SearchPage.razor.css`: changed: 1 to caption, 1 to overline, 1 to page-title. left: var(--tl-font-size-lg); var(--tl-icon-size-2xl)
- `Pages/ViewArtworkPage.razor.css`: changed: 1 to overline, 1 to secondary
- `Pages/ViewContentPage.razor.css`: changed: 1 to label, 1 to overline, 1 to page-title
- `Pages/ViewContributionsPage.razor.css`: already right
- `Pages/ViewDocumentsPage.razor.css`: changed: 1 to secondary
- `Pages/ViewDropTarget.razor.css`: already right
- `Pages/ViewGalleriesPage.razor.css`: already right
- `Pages/ViewGalleryDetailPage.razor.css`: changed: 1 to caption, 1 to overline, 1 to page-title. left: var(--tl-font-size-xs)
- `Pages/ViewImmersiveViewer.razor.css`: changed: 3 to caption, 1 to control, 2 to label, 1 to secondary, 1 to subheading
- `Pages/ViewOnThisDayStrip.razor.css`: changed: 1 to caption, 1 to control, 1 to heading
- `Pages/ViewPage.razor.css`: changed: 4 to caption, 1 to heading, 2 to label. left: var(--tl-icon-size-xs); var(--tl-icon-size-xl); var(--tl-icon-size-xl)
- `Pages/ViewPeoplePage.razor.css`: already right
- `Pages/ViewPhotoTimeline.razor.css`: changed: 2 to caption, 1 to heading, 1 to subheading. left: var(--tl-font-size-sm)
- `Pages/ViewPlacesPage.razor.css`: changed: 3 to caption, 2 to control, 1 to label. left: 1.2rem
- `Pages/ViewSelectionToolbar.razor.css`: changed: 1 to control. left: var(--tl-icon-size-sm)

## Rules used where a number had no exact job

- A title of a card or row that was 13 px or smaller went to `label` (13/500); 14 px titles went to `control`; 16 px titles to `subheading`. Titles were not stretched to 16 px in dense lists and tables.
- 14 px with weight 500 or 600 went to `control` (14/500); 13 px with weight 500 or 600 to `label`; 12 px with weight 600 to `overline`; 12 px otherwise to `caption`.
- 14 px plain helper text (muted, note, hint, detail, count, summary, empty) went to `secondary`; other 14 px running text to `body`.
- 16 px settings row labels (AppSettingRow, AppSwitchRow in a group) went to `control` (-2 px) because a row label is a field label.
- Pop-up button triggers and buttons went to `control`; the fixed-width segmented buttons in `ViewPlacesPage` stayed 12 px (`caption`) because their boxes are 4.25rem wide.
- The relative `0.75em` on `.vmt-original-title` (about 10.5 px) went to `caption` (12 px), so it now meets the 12 px floor.

## Left on purpose (exceptions)

- Icon sizes: `--tl-icon-size-*` on icon elements, `.9rem`/`1.2rem` on `svg`, the drag glyph in `SharedRuleBuilder`, the check glyph `AppStatusDot::after`, the icon clamp in `MediaArtworkGroupPreview`.
- Initials: `VaultCover` (both), `UniverseThingView`/`UniverseContents` initials, `ViewGalleryDetailPage` share avatar; `ProfileAvatar.razor` has an SVG `font-size="44"` attribute for initials.
- Map text: `ViewMap` ocean label clamp. Video subtitle text: the two clamps in `MediaViewerShell`. Hidden text: `font-size: 0` in `AppRangeSlider`.
- Already on a dialog token (kept): `--tl-dialog-title-size` in `AppDialogShell`.
- Razor utility classes `tl-u-font-size-*` (AdaptationTreeNode, CastComparison, LibraryConfigurableTable, ListenNavigationSection): defined in `wwwroot/app.css`, which this part does not change.

## Unclear, left for a decision (size would move by more than 4 px or the job is open)

- `Search/UniversalSearchResults.razor.css:94` result top title `clamp(1.35rem, 2.3vw, 2rem)` (21.6-32 px): page-title (24) or hero (32).
- `Search/UniversalSearchOverlay.razor.css:53` and `Pages/SearchPage.razor.css` overlay h1 `--tl-font-size-hero` (32/500): page-title (24/700, -8) or hero. The Search page h1 itself moved to page-title.
- `Search/UniversalSearchOverlay.razor.css:89` and `Pages/SearchPage.razor.css:38` search box text `--tl-font-size-lg` (20): control (14, -6) or heading (20).
- `Universe/UniverseSeriesShelf.razor.css:26`, `UniverseConnections.razor.css:19`, `UniverseContents.razor.css:26` section titles `clamp(1.25rem, 1.8vw, 1.7rem)` (20-27 px, 600): heading (20) or page-title (24).
- `Universe/UniverseThingView.razor.css:88` page title `clamp(1.6rem, 3vw, 2.4rem)` (25.6-38.4, 600) and `UniverseHero.razor.css:25` `clamp(1.9rem, 4.4vw, 3.4rem)` (30-54, 700): page-title (24) or hero (32).
- `Shared/Shell/AppPageHeader.razor.css:4` title `clamp(1.6rem, 2.2vw, 2.2rem)` (25.6-35.2) and `:9` compact `clamp(1.35rem, 2vw, 1.8rem)` (21.6-28.8): page-title (24).
- `Vault/VaultCover.razor.css` initials are left (initials exception).

## Tests updated for the new strings

- `PlaybackPrimitiveTests` (PlaybackContextRow), `IconLadderTests` (AppChoiceTile label), `TokenResolutionGuardrailTests` (SidebarPageHeader, SearchPage and ViewContentPage now expect `--tl-type-page-title-size`; SettingsPageFrame, which another part changes, keeps its old-token check).

## Titles decided after the walk

These were left open by the walk because the job moves the size by more than 4 px. Each now names its job (D8); all are listed for the visual review.

- Search overlay title: Page title (was 32 px at weight 500).
- Search results "top result" title: Heading (was 19 to 28 px with the screen).
- The large search box on the Search page and in the overlay: Heading size, 20 px, unchanged (the page's main field).
- Universe section titles (Series, Connections, Contents): Heading (was 17.5 to 24 px with the screen).
- A Universe thing's title (character, place, organisation): Page title (was 22 to 34 px).
- Universe hero title: Hero, 32 px (was 27 to 48 px with the screen).
- The shared page header title: Page title; its compact form: Heading.

## Titles decided after the walk

These were left open by the walk because the job moves the size by more than 4 px. Each now names its job (D8); all are listed for the visual review.

- Search overlay title: Page title (was 32 px at weight 500).
- Search results "top result" title: Heading (was 19 to 28 px with the screen).
- The large search box on the Search page and in the overlay: Heading size, 20 px, unchanged (the page's main field).
- Universe section titles (Series, Connections, Contents): Heading (was 17.5 to 24 px with the screen).
- A Universe thing's title (character, place, organisation): Page title (was 22 to 34 px).
- Universe hero title: Hero, 32 px (was 27 to 48 px with the screen).
- The shared page header title: Page title; its compact form: Heading.
