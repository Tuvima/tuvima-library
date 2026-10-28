---
title: "Inline Media Editing"
description: "How Tuvima lets users correct media from the surface where they find the issue."
audience: "developer"
category: "architecture"
product_area: "editing"
tags:
  - "dashboard"
  - "editing"
  - "review"
status: current
---

# Inline Media Editing

## In this page

How Tuvima lets users correct media from the surface where they find the issue.

## Where this lives in the code

- `src/MediaEngine.Web/Services/Editing/MediaEditorLauncherService.cs`
- `src/MediaEngine.Web/Services/Editing/EditorDeviceRule.cs`
- `src/MediaEngine.Web/Services/Editing/EditingModeContext.cs`
- `src/MediaEngine.Web/Components/Details/EditingBanner.razor`
- `src/MediaEngine.Web/Components/Details/EditingSectionSlot.razor`
- `src/MediaEngine.Web/Components/Shared/AppConfirmDialog.razor`
- `src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor`
- `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs`

Tuvima no longer uses a separate media management workbench. Users browse through Home, Read, Watch, Listen, Search, and detail pages. When a media item needs correction, the edit action appears on that same surface: on a single item's detail page it turns on Editing Mode, elsewhere it opens the shared media editor in a dialog.

## Product model

- Normal single-item correction happens in **Editing Mode** on the detail page: choosing Edit in the page's "..." menu shows a banner and per-field pencils, and Manage Artwork, Match and Identity and History open as one section directly below the hero (`EditingSectionSlot`), which hosts the same `SharedMediaEditorShell` sections in line. There is no drawer. See [Editing Mode](#editing-mode).
- Review, browse-row and Recently Added correction, and the people, series-shelf and curated-collection editors, open the same editor shell in a dialog through `MediaEditorLauncherService.OpenAsync` because they originate outside a single item's detail page.
- Review Queue is only for blocked, uncertain, or low-confidence items that need human confirmation.
- Settings/Admin is for folders, providers, profiles, ingestion status, system health, logs, and configuration.
- No current feature should route users to a separate media library workspace.

## Shared editor

**Desktop-only rule.** `EditorDeviceRule.CanEdit` is the one device rule: the form factor is desktop AND the primary pointer is fine and can hover (`DeviceContextService.CanEdit`, fed by the same four signals the browser snapshot reports). `MediaEditorLauncherService`, `CollectionEditorLauncherService` and `GalleryEditorLauncherService` check it before anything else (before the administrator unlock). Where it is false `TryOpenAsync` returns `EditorLaunchResult.NotAvailableOnThisDevice`, shows the toast "Edit on a computer" and opens no dialog; `OpenAsync` keeps its boolean contract (true only when a change was applied). A missing device context also refuses. Entry points are hidden off the desktop too, so the refusal is a safety net for deep links and query-string launches. Phone, tablet, TV, a native shell, an unknown form factor and an undetectable pointer cannot edit in the Dashboard. This is a Dashboard rule: the Engine never receives the device, its permission check (the Edit action it offers and the authorization on each save) still decides who may edit, and the Engine does not block coarse-pointer or touch devices.

All media editing renders `SharedMediaEditorShell`: through `MediaEditorLauncherService.OpenAsync` as a dialog for Review, Batch and the callers listed above, and in line in the Editing Mode section slot on a single item's detail page (and on a Universe page). Review supplies its review context and Batch is limited to a real selection. `MediaEditorSurface` supplies the common shell without duplicating editor state or persistence logic.

The launch request should include the current context:

- `EntityIds`
- `LaunchEntityId`
- `LaunchEntityKind`
- `ActiveProfileId` for profile-owned notes and tags
- `Mode`
- `InitialScope` or `InitialTab`
- `MediaType`
- `HeaderTitle`
- `HeaderSubtitle`
- `CoverUrl`
- `PreviewItems`
- `ReviewItemId` plus the review-specific trigger/context when opened from Review Queue

Use `SharedMediaEditorMode.Normal` from media surfaces, `SharedMediaEditorMode.Review` from Review Queue, and `SharedMediaEditorMode.Batch` only when a real list/table selection exists.

For a single item, Details is intentionally small: Appearance contains display title, description, provider-native tagline, sort title, and durable genre overrides; My Library contains profile-owned notes and local tags. Genre and tag entry both suggest existing library values, preserve spaces while typing, and commit only on Enter or explicit selection. The editor header owns the sole primary artwork preview, while custom artwork is managed on Artwork. Provider-managed people, dates, language, runtime, and ratings remain visible as source facts and change through Matching or ingestion rather than becoming a large editable form. Batch editing may retain a separate Options step for bulk-only controls. Files is limited to physical-file state, while identity, metadata, artwork, and ingestion events belong exclusively in History.

### Universe and fictional-entity editing

Universe and fictional-entity administration is rendered as content inside `SharedMediaEditorShell`, not as a separate route, dialog, or media workbench. A media detail with a Universe QID passes both its original media target and a typed `SharedEntityEditorTargetDto`; the editor can switch from that media to the Universe, navigate category/entity selectors in place, return from an entity to the Universe root, and return to the original media. Every surface switch uses the same save/discard/stay guard as other editor retargets. Consumer Explore remains a separate read-only experience.

The Universe root presents Engine-ordered Characters, Locations/Places, Organizations/Groups, Events, and Objects. Category and sibling controls open anchored, viewport-bounded, searchable selector popovers; the category rail stays a single horizontally scrollable row with keyboard navigation, visible overflow controls, and touch/wheel support. The shell composes tabs only from the target's readable capabilities, so graph targets never gain the physical-media Files section. Organization `members` and Event `participants` are friendly, filtered projections of the qualified relationship data; the full Relationships section remains separate. Details and managed EntityAsset Artwork use the authorized shared-editor endpoints. Entity artwork is owned by the Universe/entity target, not inherited from a media work or parent container. Appearances carry work-scoped role, context, anchor, temporal, and spoiler qualifiers. Graph facts are read-only; authored-container canonical titles/order and media cross-navigation paths are not changed by graph editing.

The editor's media Context Rail replaces the former generic Contents editor tab. It shows the real hierarchy and retargets the same media editor; it does not turn series, seasons, albums, or collections into knowledge-graph entities. Audiobook chapter-title overrides remain available under the explicitly named Chapters section. Retail Match handles provider identity and structural placement together: a candidate that changes the hierarchy previews Previous Path → Target Path before Apply, and Apply is the only confirmation. The normal editor and Review Queue do not offer a Change Type action; media type correction remains outside this editor surface.

Keep the data models distinct: structural placement groups media works, authored containers retain their user-owned canonical title/order, and the knowledge graph links a Universe to fictional entities and to works through qualified appearances/relationships. Real-world release/claim dates, in-universe narrative time, and editor History are different facts and must not be substituted for one another. Stage 2 establishes canonical identity; bounded Stage 3 enriches already grounded narrative entities and relationships without silently rewriting authored containers or structural placement.

Description resolution follows the selected item scope. A comic issue reads `issue_description` or `issue_overview`, including asset-owned canonical values, and never substitutes the parent series description. Comic Vine issue copy retains its issue URL, provider terms, and retrieval attribution on the detail surface. Series copy appears only on a series/container surface.

TV shows, seasons, music albums, and other structural containers use the media Context Rail and their existing consumer browse/detail arrays. Episodes and tracks expose a deliberate Parent & position workflow inside Matching; structural moves are previewed and confirmed separately from ordinary metadata saves. Audiobook chapter-title overrides use a dedicated Chapters section while file-derived chapter timing and boundaries remain read-only. Collection membership is edited only in the collection editor, not repeated on the collection detail page.

Canonical presentation overrides are limited to `title`, `description`, `tagline`, `sort_title`, and `genre`. The Engine atomically saves those durable work-level overrides together with profile-owned notes and tags using an optimistic revision. A stale revision returns `409 Conflict`; the editor stays open, preserves the user's input, and offers an explicit reload action.

## Editing Mode

Editing Mode is the detail page's in-page editor. It is a state of the page, not a new management area, and it does not add a route.

- **Entering and the banner.** Edit is an entry in the detail page's "..." menu (there is no standing Edit button). It turns the mode on only when `EditingModeContext.CanEdit` is true: the Engine offered the Edit action (edit permission, so a Kids or other non-edit profile never sees it) and `EditorDeviceRule.CanEdit` is true. `EditingBanner` then shows one full-width bar at the top of the page, sticky inside the overlay's scroller: a purple dot, "Editing", "Changes stay in your library", the Manage Artwork, Match and Identity and History buttons (`aria-pressed` follows the open one; Match and Identity only where `MediaEditorMatchRule` says the editor can match the target; all three are disabled while a field is open), a "..." menu that holds only Delete from library (shown only to an administrator on a desktop for a single title, last and red) and one **Done** button, which is disabled while a field or the open section's own Save is running (`EditingModeContext.SectionSaving`, reported by the shell's `SavingChanged`), so Done can never start a second save. Phones and tablets never see it. The state lives in `DetailPage` and ends on Done or when the page is left; a deep link never starts in Editing Mode. While editing, the page's own "..." menu hides Mark as watched and its reset.
- **Pencils.** `EditableFieldPencil` renders nothing unless the mode is on. It resolves its field from `ItemFieldEditSession`, which loads the same editor context the Details section uses and builds the same `MediaEditorDetailsPresenter` presentation, so "editable", "provider fact" and "inherited" mean what they mean in the editor. An editable field shows one pencil; a provider fact that cannot be changed here shows a lock with the reason; an inherited field is locked with the parent named. Pressing a pencil opens `EditorDetailsInlineField` (or `MediaEditorTagEditor` for genres and tags) in place. Enter or the check saves one field through `PUT /library/items/{id}/display-overrides` (or the profile preference layer when the value is already a profile override), Esc cancels, success shows "Saved" inline, and a failed save keeps the typed value with the error beside it. The hero title pencil only appears when the editor field shows the same text as the hero, so it never edits something else.
- **Hero art.** `EditingHeroArtworkButtons` offers Edit Background, Edit Logo and Edit Cover for the slots the active scope can hold; each opens the Artwork section on that slot.
- **Section slot.** `EditingSectionSlot` sits directly below the hero at full content width and shows one section at a time (Artwork, Match and Identity, History; the Universe editor uses Details). It hosts `SharedMediaEditorShell` with `Inline` and `Section="artwork|links|history|details"`: the shell shows no header and no section rail, only the requested section, and History is a read-only timeline. Each section keeps its own save behaviour (`Closed`, `DirtyChanged` and `Committed` tell the page what happened). The slot is capped at about 70% of the overlay's height and scrolls inside, so the page below stays reachable. Pressing the open section's banner button again, its close button, Esc or Done closes it, and focus goes back to the control that opened it (or, if that is gone, the hero's "..." button).
- **Save or discard.** Leaving Editing Mode goes through one question, `AppConfirmDialog` titled "Save or discard?" with Save and Discard (closing the dialog is Cancel and keeps the person editing with the input intact). Done, closing a section, Esc from outside a field, the backdrop and Back all ask it when a field or the open section holds unsaved input; Save saves (a failed save stays in the mode with the field open and its error showing) and Discard drops the input. A second Done, close or Esc while the question or a save is in flight is refused (`EditingModeContext.IsSettling`).
- **Esc, in order.** Esc pressed inside an open field cancels that field (the field's own handler). From anywhere else in the overlay: an open field closes (asking "Save or discard?" first when it holds typed input), otherwise an open section closes (asking first when it holds unsaved input), otherwise the overlay dismisses, leaving Editing Mode first through the same question. The overlay offers Esc to `EditingModeContext.HandleEscapeAsync` through `DetailsOverlayState.RegisterEscapeHandler`; while the mode is on the context is also the overlay's `IDetailsOverlayDismissGuard`, so Back, the close button and links to another title ask the same question. A page outside the overlay (a Universe) has no guard: its banner asks when the person navigates away, and its slot listens for Esc itself (`editing-section-escape.js`) with the same rules as the overlay: an open popup or a control that uses Esc keeps the key. A `NavigationLock` in the banner adds the browser's own prompt for a reload or tab close with unsaved input.
- **Delete from library.** For an effective administrator on a desktop, on a single title only, the page's own "..." menu lists **Delete from library…** last (red, below a divider) whether or not Editing Mode is on; the banner's "..." lists the same entry while the mode is on. Both open the same `DeleteFromLibraryDialog` flow.
- **Removed while open.** When another person deletes the title while the overlay is open, `DetailPage` keeps the overlay and swaps the page for `RemovedItemNotice`, and Editing Mode ends without asking (`EditingModeContext.Leave`). Text typed into a pencil field is kept on the notice with a Copy button; an open section's own unsaved input cannot be read from the page, so the notice only says it was lost. A "Save or discard?" question that was already open can no longer save or dismiss the overlay once the mode has ended: `DoneAsync`, closing a section and Esc all check the mode again after the question returns. Outside the overlay the page shows a toast and goes back.
- **Refresh metadata.** At the top of Match and Identity, administrators get a **Refresh metadata** button for a single item. It queues an enrichment refresh for the item (`QueueEnrichmentRefreshNowAsync`), is disabled while one is already waiting or running, and re-checks the administrator rule when pressed. A failure shows one plain line ("Refresh could not start. Try again later.") and the Engine's own text goes to the log.
- **Artwork confirmations.** Remove artwork (showing the image that will go, with Remove and Keep) and Restore automatic artwork both ask through the shared `AppConfirmDialog` (`ArtworkRemoveConfirmation`), in the Editing Mode section and in Needs Review. Picking, uploading or pasting a URL applies at once. **Retry failed uploads** runs one retry at a time: the button is disabled while it works.
- **Moving a title: Match first, then move.** In Match and Identity a song, an episode or an audiobook part shows **Move to another album / season / audiobook** (`ItemMoveArea`; a title added anyway shows **Match** instead, and a numbered one **Change number**). The person picks a place from the list the Engine offers (`GET /library/items/{id}/move-destinations`: only places that accept this kind), then the Match step shows the place's own list and which entry this is. The Engine pre-selects its best guess (title, then length) and says why; a taken entry offers **Add as another version**; **Add anyway** joins the title with no number and a quiet "Not matched" tag (`works.placement_unmatched`, shown through `IsUnmatched`). Every move confirms in the shared `AppConfirmDialog` with a preview the Engine worked out for the choices made (`POST /library/items/move-preview` with `choices`), including what happens to the file, and the confirm sends the preview's token: a changed plan answers 409 with a fresh preview and nothing moves (`LibraryMoveService`). The Library change is saved first, and a durable file-queue job renames and files the file in Managed folders; Read-only and protected files are never touched.
- **Series are not moved into by hand (D20).** A book, comic or movie is placed in a Series by its match: re-matching it in Match and Identity sets its parent, and the Series placement rule builds the Series. Its Move entry therefore reads **Add to a Series without a number** and only forces the title into a Series (Add anyway, tagged "Not matched"); the Match step shows the Engine's plain line instead of a list ("Series are built from each title's match. Use Match and Identity to re-match it, or add it here without a number.") and the confirm offers Add anyway or Cancel. Hand-picked collections keep free-order moves. The batch bar's Move for these kinds reads the same and does the same.
- **Editing several rows together.** Rows can be ticked on a season or album page in Editing Mode, on the songs list and on library tables (desktop only, where `EditorDeviceRule.CanEdit` allows it). A selection bar (`BulkSelectionBar`) offers **Edit**, **Match** (songs of one album or episodes of one season), **Move to album/season** and Clear, with Delete from library in its "..." menu. Edit opens the same right-hand panel as the Details tool, titled "Edit 4 songs" (`BulkEditPanel`): a field the items disagree on says "Mixed, leave unchanged", and only a field that is changed is staged, one entry per item and field, into the same pending list the editing bar saves (a list with no detail page, such as the songs list, keeps its own pending list in `BulkEditHost` with the same check-then-save). Songs also get **Renumber sequentially** (track numbers 1 to N in the order the list shows) and **Change album**. Filters, sort and scroll are never touched while editing or after a save.
- **No undo.** There is no change journal and no undo: single-field edits save straight away and can be edited again, and History is read-only.
- **Scope.** Series shelves, curated collections and people keep their own editor dialogs; every other single item uses Editing Mode, the same banner and section slot for every media type. The Universe page uses the same banner (Done only, since its editor has its own sections) and slot with the Universe editor.
- **Device rule.** Everything above is guarded by `EditorDeviceRule.CanEdit` in the Dashboard's handlers as well as in the markup (the Engine does not see the device; its permission check still decides who may edit): `EditingModeContext.EnterAsync`, `OpenSection`, `ItemFieldEditSession.Begin` and `SaveAsync` each refuse when it is false, and the mode ends by itself when the device stops qualifying.

## After save

After an edit is applied, the detail surface refreshes after the dialog closes with a successful result (in Editing Mode it refreshes as soon as each field or section change is saved). The modal editor scrolls internally; Cancel returns to the unchanged detail. Save failures and structural confirmations keep the editor visible. Dirty target and route changes are guarded, focus enters the editor on open and returns to the Edit action on close, and editing never changes the detail URL or navigates to a management workspace.

Applying a different retail match is a two-speed operation. The selected provider identity and its primary artwork are committed synchronously: stale provider-managed artwork is removed, the replacement cover is downloaded into managed storage, and the detail hero refreshes while the editor remains open. User-uploaded artwork remains available. Wikidata alignment and deeper enrichment are then queued and may finish in the background. The manual update and queued identity job must both be visible in application logs and item History.

In Review mode, resolving a row is explicit. Saving field changes, applying a provider/canonical match, or approving the current metadata must call the Engine review API for the concrete review item. If that call fails, the item stays in the Review Queue.

## Artwork editing

The shared editor owns artwork correction for normal and review flows. Each artwork type opens a focused gallery where one click makes a variant active, the magnifier opens a full-size preview, and the delete action removes that variant from the item with inline confirmation.

Deleting provider artwork removes the item association but keeps shared provider/image cache data when another work, edition, collection, or descendant still references it. Deleting user-uploaded artwork can also clean up the owned local file. Artwork type behavior is documented for users in [Artwork Types](../reference/artwork-types.md).



## People, Series, curated collections and Universes

These edit in place with the same bar, right-hand panel, floating artwork picker and Review changes as a title (universal editor spec D17, D17a); the old person pop-up and the Series and curated-collection pop-ups for an existing item are gone. The Engine describes each one through `GET /library/editor-subjects/{id}` (and `/library/editor-subjects/universes/{qid}` for a Universe), and `POST /library/edit-session` saves it with the existing Details and Artwork change kinds: the handlers find the subject by its id.

- **Person:** Details (name, biography, sort name, tags), the provider facts read-only, and the photo (Headshot). Match is not offered until a provider record can be re-matched.
- **Series:** title and description as local overrides over the provider's values; cover and background.
- **Curated collection:** its own name and description (a name is required, and a curated collection must keep at least one item); cover and background. Creating a collection, a playlist or a gallery, and managing items and rules, still use `CollectionEditorShell`.
- **Universe:** name and description as local overrides stored in `collections.display_overrides_json` (the Wikidata values are never touched and come back when the override is cleared), a cover and a background shown on the Universe hero, and a read-only list of its Series. No Match tool.

Every save is refused for a profile that may not edit (`EditSessionAccessService`), and every Dashboard entry point checks `EditorDeviceRule.CanEdit`.
