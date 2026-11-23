package com.tuvima.library.core

// The "..." menu on a title's Details page. Version 1 of the phone has no Edit and no Add to collection (editing opens the Dashboard in
// the phone's browser in a later packet, spec native-apps D7), so the menu holds what the phone can do itself. Pure rules.

enum class DetailMenuAction { ToggleMyList, Download, DownloadNextEpisodes }

/** One menu row. A row that cannot act now stays in place, dimmed ([enabled] false), never hidden. */
data class DetailMenuItem(val action: DetailMenuAction, val label: String, val enabled: Boolean = true)

object DetailMenuLogic {
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
    ): List<DetailMenuItem> = buildList {
        if (canSave) add(DetailMenuItem(DetailMenuAction.ToggleMyList, if (isSaved) MyListLogic.REMOVE else MyListLogic.ADD))
        if (canDownload) add(DetailMenuItem(DetailMenuAction.Download, downloadLabel, downloadEnabled))
        if (canDownload && canDownloadNextEpisodes) add(DetailMenuItem(DetailMenuAction.DownloadNextEpisodes, "Download next episodes"))
    }

    /** The "..." button has nothing to open when no row applies, so it is not drawn. */
    fun showsButton(items: List<DetailMenuItem>): Boolean = items.isNotEmpty()
}
