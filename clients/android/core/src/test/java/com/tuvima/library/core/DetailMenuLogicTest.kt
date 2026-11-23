package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DetailMenuLogicTest {
    private fun items(
        canSave: Boolean = true,
        isSaved: Boolean = false,
        canDownload: Boolean = true,
        downloadLabel: String = "Download",
        downloadEnabled: Boolean = true,
        canDownloadNextEpisodes: Boolean = false,
        canDownloadAll: Boolean = false,
    ) = DetailMenuLogic.items(canSave, isSaved, canDownload, downloadLabel, downloadEnabled, canDownloadNextEpisodes, canDownloadAll)

    @Test
    fun aMovieMenuHoldsMyListThenDownloadAndNeverEditOrAddToCollection() {
        val rows = items()

        assertEquals(listOf("Add to My List", "Download"), rows.map { it.label })
        assertFalse(rows.any { it.label.contains("Edit") || it.label.contains("collection", ignoreCase = true) })
    }

    @Test
    fun aSavedTitleOffersToRemoveItFromMyList() {
        assertEquals("Remove from My List", items(isSaved = true).first().label)
    }

    @Test
    fun aShowAlsoOffersTheNextEpisodes() {
        assertEquals(
            listOf(DetailMenuAction.ToggleMyList, DetailMenuAction.Download, DetailMenuAction.DownloadNextEpisodes),
            items(canDownloadNextEpisodes = true).map { it.action },
        )
    }

    @Test
    fun aDownloadAlreadyOnItsWayStaysInPlaceButDimmed() {
        val row = items(downloadLabel = "Downloading", downloadEnabled = false).first { it.action == DetailMenuAction.Download }

        assertEquals("Downloading", row.label)
        assertFalse(row.enabled)
    }

    @Test
    fun aBookWithNothingToDownloadKeepsOnlyMyList() {
        assertEquals(listOf(DetailMenuAction.ToggleMyList), items(canDownload = false, canDownloadNextEpisodes = true).map { it.action })
    }

    @Test
    fun aPageThatListsSeveralFilesOffersDownloadAllLastEvenWithNoSingleFileToDownload() {
        assertEquals(
            listOf(DetailMenuAction.ToggleMyList, DetailMenuAction.Download, DetailMenuAction.DownloadAll),
            items(canDownloadAll = true).map { it.action },
        )
        assertEquals(
            listOf(DetailMenuAction.ToggleMyList, DetailMenuAction.DownloadAll),
            items(canDownload = false, canDownloadAll = true).map { it.action },
        )
    }

    @Test
    fun noButtonIsDrawnWhenNoRowApplies() {
        assertFalse(DetailMenuLogic.showsButton(items(canSave = false, canDownload = false)))
        assertTrue(DetailMenuLogic.showsButton(items()))
    }
}
