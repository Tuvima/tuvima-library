package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Checks the logic around the new app calls: typed results, photo consent, routing and photo grid layout. */
class AppModelsTest {
    private fun item(id: String, kind: String = "image") = ViewItem(
        id = id,
        mediaKind = kind,
        title = null,
        fileName = "$id.jpg",
        mimeType = null,
        capturedAt = null,
        width = null,
        height = null,
        durationSeconds = null,
        byteSize = null,
        favorite = false,
        locationName = null,
        cameraName = null,
        description = null,
    )

    private fun card(assetId: String?, actionAssetId: String?, workId: String? = "work") = DisplayCard(
        id = "card",
        workId = workId,
        assetId = assetId,
        collectionId = null,
        mediaType = "Movie",
        title = "Alpha One",
        subtitle = null,
        facts = emptyList(),
        description = null,
        artworkUrl = null,
        actions = listOf(DisplayAction("playAsset", "Play", "work", actionAssetId, null, null)),
        progressPercent = null,
    )

    // ---- profile switch results ----

    @Test
    fun switchFailuresBecomeTypedResultsByStatus() {
        assertTrue(classifySwitchFailure(428, "pin_required", "PIN needed") is SwitchProfileResult.PinRequired)
        assertTrue(classifySwitchFailure(403, "profile_not_allowed", null) is SwitchProfileResult.NotAllowed)
        assertTrue(classifySwitchFailure(409, "already_active_profile", null) is SwitchProfileResult.AlreadyActive)
        assertTrue(classifySwitchFailure(429, "pin_locked", null) is SwitchProfileResult.Locked)
    }

    @Test
    fun otherSwitchFailuresKeepTheStatusAndCode() {
        val failed = classifySwitchFailure(401, "invalid_grant", "Sign in again") as SwitchProfileResult.Failed
        assertEquals(401, failed.statusCode)
        assertEquals("invalid_grant", failed.code)
        assertEquals("Sign in again", failed.message)
        val unknown = classifySwitchFailure(500, null, null) as SwitchProfileResult.Failed
        assertEquals("request_failed", unknown.code)
    }

    @Test
    fun pinRequiredCarriesTheEnginesMessage() {
        val result = classifySwitchFailure(428, "pin_required", "That PIN is not right.")
        assertEquals("That PIN is not right.", (result as SwitchProfileResult.PinRequired).message)
    }

    // ---- photo consent ----

    @Test
    fun photoScopesAreRequestedOnlyWhenThePersonOptsIn() {
        val base = "library.read artwork.read downloads.write"
        assertEquals(base, PhotoScopes.requested(base, includePhotos = false))
        assertEquals(
            "$base view.personal.read view.originals.read view.galleries.read",
            PhotoScopes.requested(base, includePhotos = true),
        )
    }

    @Test
    fun photoScopesAreNotRepeated() {
        val base = "library.read view.personal.read"
        assertEquals(
            "library.read view.personal.read view.originals.read view.galleries.read",
            PhotoScopes.requested(base, includePhotos = true),
        )
    }

    @Test
    fun photoAccessIsReadFromTheGrantedScope() {
        assertTrue(PhotoScopes.grantedIn("library.read view.personal.read"))
        assertFalse(PhotoScopes.grantedIn("library.read artwork.read"))
        assertFalse(PhotoScopes.grantedIn(""))
    }

    @Test
    fun photoDeniedMessageIsTheAgreedText() {
        assertEquals(
            "Photo access was not allowed for this device. Re-pair and tick photos and videos to enable it",
            PHOTO_ACCESS_DENIED_MESSAGE,
        )
        assertEquals(PHOTO_ACCESS_DENIED_MESSAGE, PhotoAccessDeniedException().message)
    }

    // ---- routing from server answers ----

    @Test
    fun detailRoutesBecomeEntityTypeAndId() {
        assertEquals(
            "work" to "6f0c2a3e-0000-0000-0000-000000000002",
            detailTargetFromRoute("/details/work/6f0c2a3e-0000-0000-0000-000000000002?context=watch"),
        )
        assertEquals("tvShow" to "abc", detailTargetFromRoute("/api/v1/details/tvShow/abc"))
        assertNull(detailTargetFromRoute("/watch"))
        assertNull(detailTargetFromRoute(null))
    }

    @Test
    fun anEpisodeRouteKeepsTheShowIdWithoutItsQueryString() {
        val route = "/details/tvshow/6f0c2a3e-0000-0000-0000-000000000002?context=watch&episode=11111111-0000-0000-0000-000000000009"
        assertEquals("tvshow" to "6f0c2a3e-0000-0000-0000-000000000002", detailTargetFromRoute(route))
        assertEquals("tvshow" to "abc", detailTargetFromRoute("/details/tvshow/abc#top"))
        assertEquals("tvshow" to "abc", detailTargetFromRoute("https://server/details/tvshow/abc?context=watch"))
    }

    @Test
    fun theShowIdForPlayingComesFromTheShowRouteOrTheEpisodeAnswer() {
        // A show page (or an episode card that opens its show): the id itself, query removed.
        assertEquals("show-1", playShowId("tvshow", "show-1", null))
        assertEquals("show-1", playShowId("tvShow", "show-1?context=watch&episode=e1", null))
        // An episode page: the show its own answer named; nothing is guessed.
        assertEquals("show-9", playShowId("tvEpisode", "ep-1", "show-9"))
        assertEquals("show-9", playShowId("tvEpisode", "ep-1", " show-9 "))
        assertNull(playShowId("tvEpisode", "ep-1", null))
        assertNull(playShowId("tvEpisode", "ep-1", "  "))
    }

    @Test
    fun theParentShowPrefersAnExplicitIdThenTheEpisodeContext() {
        assertEquals("a", parentShowId("a", "b"))
        assertEquals("b", parentShowId(null, "b"))
        assertEquals("b", parentShowId("  ", "b"))
        assertNull(parentShowId(null, null))
        assertNull(parentShowId("", ""))
    }

    @Test
    fun collectionItemFallsBackToItsWorkWhenTheRouteIsMissing() {
        val withRoute = CollectionItem("1", "w1", "Alpha One", null, "Movies", null, "/details/movie/w1?context=watch")
        assertEquals("movie" to "w1", withRoute.detailTarget())
        val withoutRoute = CollectionItem("1", "w1", "Alpha One", null, "Movies", null, null)
        assertEquals("work" to "w1", withoutRoute.detailTarget())
    }

    @Test
    fun groupTypesReadAsTheirUserFacingKind() {
        assertEquals("Show", groupTypeLabel("show"))
        assertEquals("Series", groupTypeLabel("series"))
        assertEquals("Collection", groupTypeLabel("collection"))
        assertEquals("List", groupTypeLabel("LIST"))
        assertEquals("Universe", groupTypeLabel("universe"))
        assertEquals("Collection", groupTypeLabel("something-new"))
    }

    @Test
    fun aUniverseOpensOnlyWhenTheEntryCarriesAQid() {
        fun entry(systemKey: String?) = CollectionEntry("1", "Dune", null, "collection", "Universe", systemKey, 4, emptyList())
        assertEquals("Q4242", entry("Q4242").universeQid())
        assertNull(entry("universe:dune").universeQid())
        assertNull(entry(null).universeQid())
        // The Engine names a Universe by `universe_qid` (its `system_key` is null), as in collections.json.
        val universe = CollectionEntry("2", "Test Universe", null, "universe", "Universe", null, 4, emptyList(), wireUniverseQid = "Q4242")
        assertEquals("Q4242", universe.universeQid())
        assertNull(universe.copy(wireUniverseQid = "not-a-qid").universeQid())
    }

    @Test
    fun playTargetPrefersThePlayActionsAsset() {
        assertEquals("action-asset", card("card-asset", "action-asset").playableAssetId())
        assertEquals("card-asset", card("card-asset", null).playableAssetId())
        assertNull(card(null, null).playableAssetId())
    }

    @Test
    fun collectionCardsMapEachWorkToItsFile() {
        val cards = listOf(
            card("asset-1", null, workId = "work-1"),
            card(null, "asset-2", workId = "work-2"),
            card(null, null, workId = "work-3"),
            card("asset-4", null, workId = null),
        )
        assertEquals(mapOf("work-1" to "asset-1", "work-2" to "asset-2"), mapPlayableAssets(cards))
        assertTrue(mapPlayableAssets(emptyList()).isEmpty())
    }

    // ---- detail facts ----

    @Test
    fun detailResumeShowsOnlyForStartedWork() {
        fun detail(percent: Double?) = DetailModel(
            "Dune", null, null, null, "2021", "2h 35m", "PG-13", emptyList(), null, emptyList(), emptyList(), percent,
        )
        assertFalse(detail(null).isInProgress)
        assertFalse(detail(0.0).isInProgress)
        assertTrue(detail(42.5).isInProgress)
        assertFalse(detail(100.0).isInProgress)
        assertEquals("2021 · PG-13 · 2h 35m", detail(null).factLine())
    }

    // ---- photo grid ----

    @Test
    fun monthsAreMergedAcrossPages() {
        val first = listOf(ViewTimelineMonth(2026, 3, listOf(item("a"), item("b"))))
        val second = listOf(
            ViewTimelineMonth(2026, 3, listOf(item("b"), item("c"))),
            ViewTimelineMonth(2026, 2, listOf(item("d"))),
        )
        val merged = mergeTimelineMonths(first, second)
        assertEquals(2, merged.size)
        assertEquals(listOf("a", "b", "c"), merged[0].items.map { it.id })
        assertEquals(2, merged[1].month)
    }

    @Test
    fun timelineRowsHaveAHeaderPerMonthAndFullLines() {
        val months = listOf(
            ViewTimelineMonth(2026, 3, listOf(item("a"), item("b"), item("c"), item("d"), item("e"))),
            ViewTimelineMonth(2026, 2, emptyList()),
            ViewTimelineMonth(2026, 1, listOf(item("f"))),
        )
        val rows = buildTimelineRows(months, columns = 3)
        assertEquals(5, rows.size)
        assertEquals("March 2026", (rows[0] as TimelineRow.Header).label)
        assertEquals(3, (rows[1] as TimelineRow.Cells).items.size)
        assertEquals(2, (rows[2] as TimelineRow.Cells).items.size)
        assertEquals("January 2026", (rows[3] as TimelineRow.Header).label)
        assertEquals(1, (rows[4] as TimelineRow.Cells).items.size)
    }

    @Test
    fun flatRowsHaveNoHeadings() {
        val rows = buildFlatRows(listOf(item("a"), item("b"), item("c")), columns = 2)
        assertEquals(2, rows.size)
        assertTrue(rows.all { it is TimelineRow.Cells })
        assertTrue(buildFlatRows(emptyList(), 3).isEmpty())
    }

    @Test
    fun monthLabelsReadAsNames() {
        assertEquals("December 2025", monthLabel(2025, 12))
        assertEquals("2025", monthLabel(2025, 0))
    }

    @Test
    fun aVideoIsRecognisedByItsKind() {
        assertTrue(item("v", kind = "video").isVideo)
        assertFalse(item("i").isVideo)
        assertEquals("a.jpg", item("a").displayTitle())
    }
}
