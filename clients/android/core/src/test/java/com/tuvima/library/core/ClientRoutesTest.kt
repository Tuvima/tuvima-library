package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/** Checks the API v1 paths each new app call sends, without a network or a device. */
class ClientRoutesTest {
    @Test
    fun browseSendsTheDashboardSortAndFilters() {
        val path = ClientRoutes.browse(
            BrowseQuery(
                lane = "watch",
                mediaType = "Movies",
                genres = listOf("Sci-Fi", "Drama"),
                creators = listOf("Denis Villeneuve"),
                years = listOf("2021"),
                status = "in-progress",
                sort = "title",
                offset = 48,
                limit = 48,
            ),
        )
        assertEquals(
            "/api/v1/display/browse?lane=watch&mediaType=Movies&genres=Sci-Fi%2CDrama" +
                "&creator=Denis+Villeneuve&year=2021&status=in-progress&sort=title&offset=48&limit=48&includeCatalog=true",
            path,
        )
    }

    @Test
    fun browseLeavesOutEmptyOptions() {
        val path = ClientRoutes.browse(BrowseQuery(lane = "listen", mediaType = "Music", search = "  "))
        assertEquals("/api/v1/display/browse?lane=listen&mediaType=Music&offset=0&limit=48&includeCatalog=true", path)
    }

    @Test
    fun browseKeepsOffsetAndLimitInRange() {
        val path = ClientRoutes.browse(BrowseQuery(lane = "read", offset = -5, limit = 9_999))
        assertTrue(path.contains("offset=0"))
        assertTrue(path.contains("limit=200"))
    }

    @Test
    fun browseQueryReportsFilters() {
        assertFalse(BrowseQuery(sort = "title").hasFilters)
        assertTrue(BrowseQuery(genres = listOf("Drama")).hasFilters)
        assertTrue(BrowseQuery(status = "favorite").hasFilters)
    }

    @Test
    fun homeAndContinueSkipTheCatalogue() {
        assertEquals("/api/v1/display/home?includeCatalog=false", ClientRoutes.home())
        assertEquals("/api/v1/display/continue?limit=24&includeCatalog=false", ClientRoutes.continueAll())
        assertEquals("/api/v1/display/continue?limit=100&includeCatalog=false", ClientRoutes.continueAll(500))
    }

    @Test
    fun groupAndDetailsPathsAreEncoded() {
        assertEquals("/api/v1/display/groups/abc-123?includeCatalog=false", ClientRoutes.group("abc-123"))
        assertEquals("/api/v1/details/movie/abc-123", ClientRoutes.details("movie", "abc-123"))
    }

    @Test
    fun viewTimelineCarriesCursorAndKeepsLimitInRange() {
        assertEquals("/api/v1/view/timeline?limit=100", ClientRoutes.viewTimeline())
        assertEquals("/api/v1/view/timeline?cursor=Nj_M4-ODg&limit=200", ClientRoutes.viewTimeline("Nj_M4-ODg", 900))
        assertEquals("/api/v1/view/timeline?cursor=a%3D%3D&limit=1", ClientRoutes.viewTimeline("a==", 0))
        assertEquals("/api/v1/view/timeline?limit=100", ClientRoutes.viewTimeline("  "))
    }

    @Test
    fun viewItemRoutesUseTheV1ViewPaths() {
        val id = "aaaaaaaa-0000-4000-8000-000000000001"
        assertEquals("/api/v1/view/galleries", ClientRoutes.VIEW_GALLERIES)
        assertEquals("/api/v1/view/galleries/$id/items?limit=100", ClientRoutes.viewGalleryItems(id))
        assertEquals("/api/v1/view/galleries/$id/items?cursor=c1&limit=50", ClientRoutes.viewGalleryItems(id, "c1", 50))
        assertEquals("/api/v1/view/items/$id", ClientRoutes.viewItem(id))
        assertEquals("/api/v1/view/items/$id/original", ClientRoutes.viewOriginal(id))
    }

    @Test
    fun thumbnailAndPreviewSizesSnapToWhatTheEngineAccepts() {
        val id = "item-1"
        assertEquals("/api/v1/view/items/item-1/thumbnail?size=160", ClientRoutes.viewThumbnail(id, 100))
        assertEquals("/api/v1/view/items/item-1/thumbnail?size=320", ClientRoutes.viewThumbnail(id))
        assertEquals("/api/v1/view/items/item-1/thumbnail?size=640", ClientRoutes.viewThumbnail(id, 321))
        assertEquals("/api/v1/view/items/item-1/thumbnail?size=640", ClientRoutes.viewThumbnail(id, 9_999))
        assertEquals("/api/v1/view/items/item-1/preview?size=1280", ClientRoutes.viewPreview(id, 800))
        assertEquals("/api/v1/view/items/item-1/preview?size=2048", ClientRoutes.viewPreview(id))
    }

    @Test
    fun collectionRoutesMatchTheCatalogueAndItemPages() {
        val id = "6f0c2a3e-0000-0000-0000-000000000005"
        assertEquals("/api/v1/collections?limit=24", ClientRoutes.collections())
        assertEquals("/api/v1/collections?cursor=2&limit=250", ClientRoutes.collections("2", 1_000))
        assertEquals("/api/v1/collections/$id", ClientRoutes.collection(id))
        assertEquals("/api/v1/collections/$id/items?cursor=2&limit=24", ClientRoutes.collectionItems(id, "2"))
    }

    @Test
    fun universeIsOpenedOnlyByAQid() {
        assertEquals("/api/v1/universe/Q4242", ClientRoutes.universe("Q4242"))
        assertEquals("/api/v1/universe/Q4242", ClientRoutes.universe("  Q4242 "))
        assertThrows(IllegalArgumentException::class.java) { ClientRoutes.universe("6f0c2a3e-0000-0000-0000-000000000005") }
        assertThrows(IllegalArgumentException::class.java) { ClientRoutes.universe("Q") }
        assertThrows(IllegalArgumentException::class.java) { ClientRoutes.universe("../health") }
        assertTrue(ClientRoutes.isQid("Q1"))
        assertFalse(ClientRoutes.isQid(null))
        assertFalse(ClientRoutes.isQid("q12"))
    }

    @Test
    fun deviceAndDownloadRoutes() {
        assertEquals("/api/v1/devices/current/profiles", ClientRoutes.DEVICE_PROFILES)
        assertEquals("/api/v1/devices/current/switch-profile", ClientRoutes.SWITCH_PROFILE)
        assertEquals("/api/v1/playback/asset-1/encode", ClientRoutes.encodeJob("asset-1"))
        assertEquals(
            "/api/v1/devices/22222222-2222-2222-2222-222222222222",
            ClientRoutes.revokeDevice("22222222-2222-2222-2222-222222222222"),
        )
    }

    @Test
    fun everyRouteStaysInsideApiV1() {
        val id = "id-1"
        val paths = listOf(
            ClientRoutes.home(), ClientRoutes.continueAll(), ClientRoutes.group(id),
            ClientRoutes.browse(BrowseQuery(lane = "watch")), ClientRoutes.details("work", id),
            ClientRoutes.viewTimeline(), ClientRoutes.VIEW_GALLERIES, ClientRoutes.viewGalleryItems(id),
            ClientRoutes.viewItem(id), ClientRoutes.viewThumbnail(id), ClientRoutes.viewPreview(id),
            ClientRoutes.viewOriginal(id), ClientRoutes.collections(), ClientRoutes.collection(id),
            ClientRoutes.collectionItems(id), ClientRoutes.universe("Q1"), ClientRoutes.DEVICE_PROFILES,
            ClientRoutes.SWITCH_PROFILE, ClientRoutes.encodeJob(id), ClientRoutes.revokeDevice(id),
        )
        paths.forEach { assertTrue("$it is outside /api/v1", it.startsWith("/api/v1/")) }
    }
}
