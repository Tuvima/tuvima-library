package com.tuvima.library.core

// Rules the phone's Find another bottom sheet adds on top of the shared window (spec "Find another on TV", WP8; Find another v1
// D12). Pure and screen-free so they are unit tested; the sheet itself only draws.

object FindAnotherSheet {
    /**
     * Whether the "Find subtitles…" row is drawn in the Audio & subtitles view. It needs the sources check to say Ready, and a
     * downloaded copy on the phone has none of the Engine's subtitle files to swap, so it never offers the row.
     */
    fun entryVisible(sources: TextTrackSources, playingDownloadedCopy: Boolean): Boolean =
        sources.isReady && !playingDownloadedCopy

    /**
     * What a row of the single-column sheet shows under its checks, or null for nothing. A row with words shows them (in full
     * for the tapped row, the short preview for the others); "Reading…" and "Could not read this one" show on their own row;
     * the prompt to read shows only on the selected row, so ten unread rows do not repeat it.
     */
    fun rowPane(item: TextTrackAlternative, selected: Boolean, fullWords: Boolean, reading: Boolean, failed: Boolean): FindAnotherPane? {
        val pane = FindAnotherRows.pane(item, reading = reading, failed = failed, full = selected && fullWords)
        return if (pane is FindAnotherPane.Prompt && !selected) null else pane
    }
}
