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
- `src/MediaEngine.Web/Components/Details/EditingDrawer.razor`
- `src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor`
- `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs`

Tuvima no longer uses a separate media management workbench. Users browse through Home, Read, Watch, Listen, Search, and detail pages. When a media item needs correction, the edit action appears on that same surface: on a single item's detail page it turns on Editing Mode, elsewhere it opens the shared media editor in a dialog.

## Product model

- Normal single-item correction happens in **Editing Mode** on the detail page: the hero Edit / Done toggle adds per-field pencils and a right-hand drawer (`EditingDrawer`) that hosts the same `SharedMediaEditorShell` sections beside the unchanged detail page and URL. See [Editing Mode](#editing-mode).
- Review, browse-row and Recently Added correction, and the people, series-shelf and curated-collection editors, open the same editor shell in a dialog through `MediaEditorLauncherService.OpenAsync` because they originate outside a single item's detail page.
- Review Queue is only for blocked, uncertain, or low-confidence items that need human confirmation.
- Settings/Admin is for folders, providers, profiles, ingestion status, system health, logs, and configuration.
- No current feature should route users to a separate media library workspace.

## Shared editor

**Desktop-only rule.** `EditorDeviceRule.CanEdit` is the one device rule: the form factor is desktop AND the primary pointer is fine and can hover (`DeviceContextService.CanEdit`, fed by the same four signals the browser snapshot reports). `MediaEditorLauncherService`, `CollectionEditorLauncherService` and `GalleryEditorLauncherService` check it before anything else (before the administrator unlock). Where it is false `TryOpenAsync` returns `EditorLaunchResult.NotAvailableOnThisDevice`, shows the toast "Edit on a computer" and opens no dialog; `OpenAsync` keeps its boolean contract (true only when a change was applied). A missing device context also refuses. Entry points are hidden off the desktop too, so the refusal is a safety net for deep links and query-string launches. Phone, tablet, TV, a native shell, an unknown form factor and an undetectable pointer cannot edit in the Dashboard. This is a Dashboard rule: the Engine never receives the device, its permission check (the Edit action it offers and the authorization on each save) still decides who may edit, and the Engine does not block coarse-pointer or touch devices.

All media editing renders `SharedMediaEditorShell`: through `MediaEditorLauncherService.OpenAsync` as a dialog for Review, Batch and the callers listed above, and inside the Editing Mode drawer on a single item's detail page. Review supplies its review context and Batch is limited to a real selection. `MediaEditorSurface` supplies the common shell without duplicating editor state or persistence logic.

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

- **Toggle.** `EditingModeToggle` (inside `EditingModeBar` on the hero) shows **Edit** when off and **Done** when on. It renders only when `EditingModeContext.CanEdit` is true: the Engine offered the Edit action (edit permission, so a Kids or other non-edit profile never sees it) and `EditorDeviceRule.CanEdit` is true. The state lives in `DetailPage` and ends on Done or when the page is left; a deep link never starts in Editing Mode. If the Edit action is disabled while ingestion runs, the button is disabled with the reason.
- **Pencils.** `EditableFieldPencil` renders nothing unless the mode is on. It resolves its field from `ItemFieldEditSession`, which loads the same editor context the Details section uses and builds the same `MediaEditorDetailsPresenter` presentation, so "editable", "provider fact" and "inherited" mean what they mean in the editor. An editable field shows one pencil; a provider fact that cannot be changed here shows a lock with the reason; an inherited field is locked with the parent named. Pressing a pencil opens `EditorDetailsInlineField` (or `MediaEditorTagEditor` for genres and tags) in place. Enter or the check saves one field through `PUT /library/items/{id}/display-overrides` (or the profile preference layer when the value is already a profile override), Esc cancels, success shows "Saved" inline, and a failed save keeps the typed value with the error beside it. The hero title pencil only appears when the editor field shows the same text as the hero, so it never edits something else.
- **Hero art.** `EditingHeroArtworkButtons` offers Edit Background, Edit Logo and Edit Cover for the slots the active scope can hold; each opens the drawer on Artwork for that slot.
- **Top buttons.** Manage Artwork (drawer on Artwork), Match and Identity (drawer on the existing matching and rematch flow, with its preview and review gating) and More (the existing shared action list). The details More entry that used to open the modal now toggles Editing Mode.
- **Drawer.** `EditingDrawer` is a right-hand panel of about 380px beside the unchanged page. It hosts `SharedMediaEditorShell` with `Drawer="true"`: the shell hides its own header and section rail and History, and shows a Details / Artwork / Match and Identity tab strip over the existing sections. It uses the shell's own save, discard and navigation-guard behaviour (`Closed`, `DirtyChanged` and `Committed` tell the page what happened). The drawer closes with X, Esc or Done; focus returns to the control that opened it; Esc closes an open field first and the drawer second.
- **Dirty guard.** `EditingModeBar` registers a `NavigationLock` with a `MediaEditorNavigationGuard` for a field typed into on the page; the shell inside the drawer keeps its own lock. Leaving the page, Done or closing the drawer with unsaved input asks first (Keep editing, Discard, or Save and finish/leave). Opening the drawer is refused while a field is mid-edit, and fields refuse to open while the drawer has unsaved input, so only one source of unsaved input exists at a time.
- **No undo.** There is no change journal and no undo: single-field edits save straight away and can be edited again, and History is read-only.
- **Scope.** Series shelves, curated collections and people keep their own editor dialogs; every other single item uses Editing Mode, the same drawer for every media type.
- **Device rule.** Everything above is guarded by `EditorDeviceRule.CanEdit` in the Dashboard's handlers as well as in the markup (the Engine does not see the device; its permission check still decides who may edit): `EditingModeContext.EnterAsync`, `OpenDrawer`, `ItemFieldEditSession.Begin` and `SaveAsync` each refuse when it is false, and the mode ends by itself when the device stops qualifying.

## After save

After an edit is applied, the detail surface refreshes after the dialog closes with a successful result (in Editing Mode it refreshes as soon as each field or drawer change is saved). The modal editor scrolls internally; Cancel returns to the unchanged detail. Save failures and structural confirmations keep the editor visible. Dirty target and route changes are guarded, focus enters the editor on open and returns to the Edit action on close, and editing never changes the detail URL or navigates to a management workspace.

Applying a different retail match is a two-speed operation. The selected provider identity and its primary artwork are committed synchronously: stale provider-managed artwork is removed, the replacement cover is downloaded into managed storage, and the detail hero refreshes while the editor remains open. User-uploaded artwork remains available. Wikidata alignment and deeper enrichment are then queued and may finish in the background. The manual update and queued identity job must both be visible in application logs and item History.

In Review mode, resolving a row is explicit. Saving field changes, applying a provider/canonical match, or approving the current metadata must call the Engine review API for the concrete review item. If that call fails, the item stays in the Review Queue.

## Artwork editing

The shared editor owns artwork correction for normal and review flows. Each artwork type opens a focused gallery where one click makes a variant active, the magnifier opens a full-size preview, and the delete action removes that variant from the item with inline confirmation.

Deleting provider artwork removes the item association but keeps shared provider/image cache data when another work, edition, collection, or descendant still references it. Deleting user-uploaded artwork can also clean up the owned local file. Artwork type behavior is documented for users in [Artwork Types](../reference/artwork-types.md).


