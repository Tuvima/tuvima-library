package com.tuvima.library.core

import kotlin.random.Random
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class DetailPlayTargetTest {
    private val none: (String?) -> String? = { it }

    private fun album() = DetailChildren.parse(WireFixtures.json("details-album-media-groups.json"), none)

    private fun audiobook() = DetailChildren.parse(WireFixtures.json("details-audiobook-media-groups.json"), none)

    @Test
    fun anAlbumOpenedWithoutAFileStillPlaysItsOwnedSongsInOrder() {
        val target = DetailPlay.resolve(assetId = null, children = album(), title = "Fixture Album", artist = "The Fixture Band", artworkUrl = "/art")

        assertTrue(target is DetailPlayTarget.Songs)
        target as DetailPlayTarget.Songs
        assertEquals(2, target.tracks.size)
        assertEquals("Fixture Album", target.sourceName)
        assertEquals("Fixture Album", target.tracks.first().album)
        assertTrue(DetailPlay.offersShuffle(target))
    }

    @Test
    fun playOnAnAlbumPlaysTheAlbumEvenWhenTheCardCarriedOneSongsFile() {
        val target = DetailPlay.resolve("one-song", album(), "Fixture Album", null, null)

        assertTrue(target is DetailPlayTarget.Songs)
    }

    @Test
    fun anAudiobookFoundBySearchPlaysItsFirstFile() {
        val target = DetailPlay.resolve(null, audiobook(), "The Way Beyond", null, null)

        assertEquals(DetailPlayTarget.OneFile("6f0c2a3e-0000-0000-0000-000000000053"), target)
        assertFalse(DetailPlay.offersShuffle(target))
    }

    @Test
    fun aPageThatCameWithAFileAndHasNoSongsPlaysThatFile() {
        assertEquals(DetailPlayTarget.OneFile("a1"), DetailPlay.resolve("a1", emptyList(), "Movie", null, null))
    }

    @Test
    fun aPageWithNothingToPlayOffersNoPlay() {
        assertEquals(DetailPlayTarget.Nothing, DetailPlay.resolve(null, emptyList(), "A book", null, null))
        assertEquals(DetailPlayTarget.Nothing, DetailPlay.resolve("  ", emptyList(), "A book", null, null))
    }

    @Test
    fun theQueueRequestIsPlainForPlayAndShuffledForShuffle() {
        val target = DetailPlay.resolve(null, album(), "Fixture Album", null, null)

        val play = DetailPlay.request(target, shuffle = false, random = Random(5))!!
        val shuffle = DetailPlay.request(target, shuffle = true, random = Random(5))!!

        assertFalse(play.shuffle)
        assertEquals(0, play.startIndex)
        assertTrue(shuffle.shuffle)
        assertEquals(play.tracks, shuffle.tracks)
        assertNull(DetailPlay.request(DetailPlayTarget.OneFile("x"), false, Random(1)))
    }
}
