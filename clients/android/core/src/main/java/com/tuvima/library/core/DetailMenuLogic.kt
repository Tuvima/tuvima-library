package com.tuvima.library.core

// The "..." menu on a title's Details page. Version 1 of the phone has no editor of its own and no Add to collection: Edit opens the Dashboard in
// the phone's browser on the title's own page (spec native-apps D7, `EditInBrowser`), so the menu holds what the phone can do itself plus that
// one row. Pure rules.

enum class DetailMenuAction { ToggleMyList, Download, DownloadNextEpisodes, DownloadAll, EditInBrowser }

/** One menu row. A row that cannot act now stays in place, dimmed ([enabled] false), never hidden. */
data class DetailMenuItem(val action: DetailMenuAction, val label: String, val enabled: Boolean = true)

object DetailMenuLogic {
    /** Opens the choice of what to keep: everything on the page, or one season or group. */
    const val DOWNLOAD_ALL_LABEL = "Download all…"

    /**
     * The rows, in order: My List (only for a title My List can hold), Download (only when a file is known and the kind can be kept
     * offline), and, for a show, Download next episodes. [downloadLabel] is the state the Download button already shows
     * ("Download", "Downloading", "Downloaded", "Retry download"); [downloadEnabled] is false while a copy is on its way or done.
     */
    fun items(
        canSave: Boolean,
        isSaved: Boolean,
        canDownload: Boolean,
        downloadLabel: String,
        downloadEnabled: Boolean,
        canDownloadNextEpisodes: Boolean,
        /** True when the page lists more than one file of its own (a season or show, an album, a playlist, an audiobook of several parts). */
        canDownloadAll: Boolean = false,
        /** True when Edit is offered: not a Kids profile, and the title has a Dashboard page to open (see [EditInBrowser]). */
        canEdit: Boolean = false,
    ): List<DetailMenuItem> = buildList {
        if (canSave) add(DetailMenuItem(DetailMenuAction.ToggleMyList, if (isSaved) MyListLogic.REMOVE else MyListLogic.ADD))
        if (canDownload) add(DetailMenuItem(DetailMenuAction.Download, downloadLabel, downloadEnabled))
        if (canDownload && canDownloadNextEpisodes) add(DetailMenuItem(DetailMenuAction.DownloadNextEpisodes, "Download next episodes"))
        if (canDownloadAll) add(DetailMenuItem(DetailMenuAction.DownloadAll, DOWNLOAD_ALL_LABEL))
        if (canEdit) add(DetailMenuItem(DetailMenuAction.EditInBrowser, EditInBrowser.LABEL))
    }

    /** The "..." button has nothing to open when no row applies, so it is not drawn. */
    fun showsButton(items: List<DetailMenuItem>): Boolean = items.isNotEmpty()
}
