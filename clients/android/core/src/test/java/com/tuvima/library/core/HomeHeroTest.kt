package com.tuvima.library.core

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class HomeHeroTest {
    private fun slides() = HomeHeroLogic.parse(WireFixtures.json("display-home.json"), WireFixtures.absolute)

    @Test
    fun theHomeFixtureGivesOneSlidePerSpotlightInOrder() {
        val parsed = slides()

        assertEquals(listOf("Alpha One", "Alpha Rising"), parsed.map { it.card.title })
        assertEquals("Continue", parsed.first().eyebrow)
        assertEquals("Recently added", parsed.last().eyebrow)
    }

    @Test
    fun theMetaLineIsTheFirstFactsInTheEnginesOrder() {
        assertEquals("2024 · PG · 1h 52m", slides().first().metaLine)
        assertEquals("2019 · 320 pages", slides().last().metaLine)
    }

    @Test
    fun aSlideWithNoFactsFallsBackToItsSubtitle() {
        val card = slides().last().card.copy(facts = emptyList())

        assertEquals("Ben Author", HomeHeroLogic.metaLine(card))
        assertEquals("", HomeHeroLogic.metaLine(card.copy(subtitle = null)))
    }

    @Test
    fun aPartWayThroughTitleOffersResumeAndPlaysItsFile() {
        val primary = HomeHeroLogic.primary(slides().first())

        assertEquals("Resume", primary.label)
        assertEquals("6f0c2a3e-0000-4000-8000-000000000510", primary.assetId)
        assertTrue(HomeHeroLogic.showsProgress(slides().first().card))
    }

    @Test
    fun aTitleNotStartedOffersPlayWhenItHasAFileToPlay() {
        val slide = slides().first().let { it.copy(card = it.card.copy(progressPercent = null, actions = listOf(playAction("Play")))) }

        assertEquals("Play", HomeHeroLogic.primary(slide).label)
        assertFalse(HomeHeroLogic.showsProgress(slide.card))
    }

    @Test
    fun aFinishedTitleDoesNotShowProgressOrOfferResume() {
        val slide = slides().first().let { it.copy(card = it.card.copy(progressPercent = 100.0, actions = listOf(playAction("Play")))) }

        assertEquals("Play", HomeHeroLogic.primary(slide).label)
        assertFalse(HomeHeroLogic.showsProgress(slide.card))
    }

    @Test
    fun aBookOpensItsDetailsBecauseThePhoneHasNoReaderYet() {
        val primary = HomeHeroLogic.primary(slides().last())

        assertEquals("Details", primary.label)
        assertNull(primary.assetId)
    }

    @Test
    fun anAlbumHeroPlaysItsFirstTrackAndAPlayActionWithNoFileOpensDetails() {
        val album = slides().first().let {
            it.copy(card = it.card.copy(progressPercent = null, actions = listOf(DisplayAction("playAlbum", "Play Album", "w", "a1", null, null))))
        }
        val noFile = album.copy(card = album.card.copy(actions = listOf(DisplayAction("playAsset", "Play", "w", null, null, null))))

        assertEquals("a1", HomeHeroLogic.primary(album).assetId)
        assertEquals("Details", HomeHeroLogic.primary(noFile).label)
    }

    @Test
    fun withNoSpotlightsTheSingleHeroIsTheOnlySlide() {
        val json = WireFixtures.json("display-home.json").also { it.remove("spotlights") }

        val parsed = HomeHeroLogic.parse(json, WireFixtures.absolute)

        assertEquals(listOf("Alpha One"), parsed.map { it.card.title })
    }

    @Test
    fun aHomeAnswerWithNoHeroHasNoSlides() {
        assertTrue(HomeHeroLogic.parse(JSONObject("""{"key":"home","shelves":[]}"""), WireFixtures.absolute).isEmpty())
    }

    @Test
    fun aRepeatedOrUntitledSlideIsSkippedAndTheSlidesAreCapped() {
        val one = """{"id":"%d","title":"T%d","mediaType":"Movie","actions":[]}"""
        val many = (1..8).joinToString(",") { one.format(it, it) }
        val repeated = """{"id":"1","title":"Again","actions":[]},{"id":"9","title":"","actions":[]}"""
        val json = JSONObject("""{"spotlights":[$repeated,$many]}""")

        val parsed = HomeHeroLogic.parse(json, WireFixtures.absolute)

        // "1" is taken by the first (Again); the empty title is skipped; the cap is five.
        assertEquals(5, parsed.size)
        assertEquals("Again", parsed.first().card.title)
        assertEquals(listOf("1", "2", "3", "4", "5"), parsed.map { it.card.id })
    }

    @Test
    fun aSlideWithoutAnIdTakesItsWorkId() {
        val json = JSONObject("""{"hero":{"workId":"w9","title":"By work","mediaType":"Movie","actions":[]}}""")

        assertEquals("w9", HomeHeroLogic.parse(json, WireFixtures.absolute).single().card.id)
    }

    @Test
    fun theDotsSayWhichFeaturedItemTheyShow() {
        assertEquals("Show featured item 2 of 5: Alpha Rising", HomeHeroLogic.dotLabel(1, 5, "Alpha Rising"))
    }

    private fun playAction(label: String) = DisplayAction("playAsset", label, "w", "a", null, null)

    @Test
    fun theRecentlyAddedMusicSlideIsACollageThatOpensMusicNotOneAlbum() {
        val json = JSONObject(
            """
            {"spotlights":[{"id":"alb-9","workId":"alb-9","title":"Recently added music","mediaType":"Music",
              "collageUrls":["/stream/a/cover","/stream/b/cover"],
              "actions":[{"type":"openRoute","label":"Open","webUrl":"/listen/music/playlists/system/recently-added"}]}]}
            """,
        )

        val slide = HomeHeroLogic.parse(json, WireFixtures.absolute).single()
        val primary = HomeHeroLogic.primary(slide)

        assertTrue(slide.isCollage)
        assertEquals(listOf("http://engine.test/stream/a/cover", "http://engine.test/stream/b/cover"), slide.collageUrls)
        assertEquals("", slide.metaLine)
        assertEquals("Open music", primary.label)
        assertTrue(primary.opensMusic)
        assertNull(primary.assetId)
    }

    @Test
    fun anOrdinarySlideIsNotACollage() {
        val slide = slides().first()

        assertFalse(slide.isCollage)
        assertFalse(HomeHeroLogic.primary(slide).opensMusic)
    }
}
