package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class EditInBrowserTest {
    private val movie = "6f0c2a3e-0000-0000-0000-000000000011"
    private val show = "6f0c2a3e-0000-0000-0000-000000000022"
    private val server = "http://192.168.1.20:5016"

    @Test
    fun aKidsProfileIsNeverOfferedEditAndAnAdultIs() {
        assertFalse(EditInBrowser.isOffered(treatAsKids = true))
        assertTrue(EditInBrowser.isOffered(treatAsKids = false))
        // The shell fails closed: a profile list that has not been read yet is treated as Kids, so Edit does not flash.
        assertFalse(EditInBrowser.isOffered(PhoneShellLogic.treatAsKids(null)))
    }

    @Test
    fun editOpensTheTitlesOwnPageOnTheServerAddressTheAppUses() {
        assertEquals("$server/details/movie/$movie", EditInBrowser.pageUrl(server, "movie", movie))
        assertEquals("$server/details/movie/$movie", EditInBrowser.pageUrl("$server/", "movie", movie))
        assertEquals("https://library.example.com/details/audiobook/$movie", EditInBrowser.pageUrl("https://library.example.com", "audiobook", movie))
    }

    @Test
    fun theLinkIsOnlyThePageAndNeverOpensTheEditorOrCarriesASignIn() {
        val url = EditInBrowser.pageUrl(server, "tvShow", show)!!

        assertEquals("$server/details/tvshow/$show", url)
        assertFalse(url.contains("edit", ignoreCase = true))
        assertFalse(url.contains("token", ignoreCase = true))
        assertFalse(url.contains("?"))
    }

    @Test
    fun routeStyleTypeNamesAreReadTheWayTheDashboardReadsThem() {
        assertEquals("$server/details/musicalbum/$movie", EditInBrowser.pageUrl(server, "music_album", movie))
        assertEquals("$server/details/comicissue/$movie", EditInBrowser.pageUrl(server, "comic-issue", movie))
        assertEquals("$server/details/tvseason/$movie", EditInBrowser.pageUrl(server, "tvSeason", movie))
    }

    @Test
    fun anEpisodeOpensItsShowOnThatEpisodeBecauseTheDashboardHasNoEpisodePage() {
        assertEquals("$server/details/tvshow/$show?episode=$movie", EditInBrowser.pageUrl(server, "tvEpisode", movie, showId = show))
        assertNull(EditInBrowser.pageUrl(server, "tvEpisode", movie, showId = null))
        assertNull(EditInBrowser.pageUrl(server, "tvEpisode", movie, showId = "not-an-id"))
    }

    @Test
    fun aPageTheDashboardCannotOpenGivesNoLinkSoEditIsNotOffered() {
        assertNull(EditInBrowser.pageUrl(server, "movie", "not-a-library-id"))
        assertNull(EditInBrowser.pageUrl(server, "podcast", movie))
        assertNull(EditInBrowser.pageUrl(server, "", movie))
        assertNull(EditInBrowser.pageUrl(null, "movie", movie))
        assertNull(EditInBrowser.pageUrl("   ", "movie", movie))
        assertNull(EditInBrowser.pageUrl("ftp://server", "movie", movie))
        assertNull(EditInBrowser.pageUrl("http://", "movie", movie))
    }

    @Test
    fun aRouteQueryOrFragmentOnTheIdIsNotPartOfTheLink() {
        assertEquals("$server/details/movie/$movie", EditInBrowser.pageUrl(server, "movie", "$movie?context=watch#top"))
    }
}
