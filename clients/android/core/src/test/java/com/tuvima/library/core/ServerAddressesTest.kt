package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The two addresses of one server, the one in use, and how URLs built for one are moved onto the other. */
class ServerAddressesTest {
    private val home = "http://192.168.1.20:5016"
    private val outside = "https://tuvima.example.com"

    private fun token(profile: String) = ClientToken(
        accessToken = "access-$profile",
        refreshToken = "refresh-$profile",
        expiresAtEpochSeconds = 4_000_000_000L,
        scope = "library.read",
        deviceId = "device-1",
        profileId = profile,
    )

    @Test
    fun startsOnTheHomeAddress() {
        val addresses = ServerAddresses(home, outside)

        assertEquals(home, addresses.current)
        assertEquals(outside, addresses.outside)
    }

    @Test
    fun pairingFromOutsideLeavesOnlyOneKnownAddress() {
        val addresses = ServerAddresses(outside)

        assertNull(addresses.outside)
        assertFalse(addresses.select("https://elsewhere.example.com"))
        assertEquals(outside, addresses.current)
    }

    @Test
    fun movesToTheOutsideAddressAndBack() {
        val addresses = ServerAddresses(home, outside)

        assertTrue(addresses.select(outside))
        assertEquals(outside, addresses.current)
        assertTrue(addresses.select(home))
        assertEquals(home, addresses.current)
        assertFalse(addresses.select(home))
    }

    @Test
    fun willNotMoveToAnAddressItWasNeverTold() {
        val addresses = ServerAddresses(home, outside)

        assertFalse(addresses.select("https://other.example.com"))
        assertEquals(home, addresses.current)
    }

    @Test
    fun signInSurvivesSwitchingBetweenAddresses() {
        val addresses = ServerAddresses(home, outside)
        val session = SessionHolder(token("a"))
        val saved = mutableListOf<String>()
        session.accept(token("a")) { saved += addresses.home }

        addresses.select(outside)
        session.accept(token("a")) { saved += addresses.home }
        addresses.select(home)

        // Tokens are keyed to the home address, which never changes, so nobody is signed out by a switch.
        assertEquals(listOf(home, home), saved)
        assertEquals("a", session.current()?.profileId)
        assertEquals(ProfileBinding("a", 0), session.binding())
    }

    @Test
    fun aDiscoveryAnswerReplacesTheOutsideAddress() {
        val addresses = ServerAddresses(home, outside)

        assertTrue(addresses.updateOutside("https://new.tailnet.ts.net/", answeredBy = home))
        assertEquals("https://new.tailnet.ts.net", addresses.outside)
        assertFalse(addresses.updateOutside("https://new.tailnet.ts.net", answeredBy = home))
    }

    @Test
    fun aDiscoveryAnswerFromHomeThatAnnouncesNoneRemovesTheOutsideAddress() {
        val addresses = ServerAddresses(home, outside)

        assertTrue(addresses.updateOutside(null, answeredBy = home))
        assertNull(addresses.outside)
        assertEquals(home, addresses.current)
    }

    @Test
    fun anAnswerReceivedOverTheOutsideAddressNeverMakesThePhoneForgetIt() {
        val addresses = ServerAddresses(home, outside)
        addresses.select(outside)

        assertFalse(addresses.updateOutside(null, answeredBy = outside))
        assertEquals(outside, addresses.outside)
        assertEquals(outside, addresses.current)
    }

    @Test
    fun whenTheOutsideAddressInUseIsReplacedTheNewOneIsUsed_andWhenItIsRemovedHomeIs() {
        val addresses = ServerAddresses(home, outside)
        addresses.select(outside)

        addresses.updateOutside("https://new.tailnet.ts.net", answeredBy = outside)
        assertEquals("https://new.tailnet.ts.net", addresses.current)

        addresses.updateOutside(null, answeredBy = home)
        assertEquals(home, addresses.current)
    }

    @Test
    fun anAnnouncedAddressEqualToHomeOrUnusableIsIgnored() {
        val addresses = ServerAddresses(home)

        assertFalse(addresses.updateOutside(home, answeredBy = home))
        assertFalse(addresses.updateOutside("http://public.example.com", answeredBy = home))
        assertFalse(addresses.updateOutside("   ", answeredBy = home))
        assertNull(addresses.outside)
    }

    @Test
    fun rebasesAUrlBuiltForTheOtherAddressOntoTheOneInUse() {
        val addresses = ServerAddresses(home, outside)
        addresses.select(outside)

        assertEquals("$outside/api/v1/stream/1?x=2", addresses.rebase("$home/api/v1/stream/1?x=2"))
        assertEquals("$outside/api/v1/stream/1", addresses.rebase("$outside/api/v1/stream/1"))

        addresses.select(home)
        assertEquals("$home/api/v1/artwork/9", addresses.rebase("$outside/api/v1/artwork/9"))
    }

    @Test
    fun leavesOtherHostsAndLookAlikesAlone() {
        val addresses = ServerAddresses(home, outside)

        assertEquals("https://cdn.example.org/a.jpg", addresses.rebase("https://cdn.example.org/a.jpg"))
        assertEquals("$outside.evil.test/x", addresses.rebase("$outside.evil.test/x"))
        assertEquals("$home" + "0/x", addresses.rebase("$home" + "0/x"))
    }

    @Test
    fun resolvesServerPathsAgainstTheAddressAskedFor() {
        val addresses = ServerAddresses(home, outside)

        assertEquals("$home/api/v1/display/home", addresses.resolve("/api/v1/display/home"))
        addresses.select(outside)
        assertEquals("$outside/api/v1/display/home", addresses.resolve("/api/v1/display/home"))
        // A request reads the address once and keeps it, whatever happens to the one in use meanwhile.
        assertEquals("$home/api/v1/display/home", addresses.resolve("/api/v1/display/home", origin = home))
        assertEquals("$outside/api/v1/stream/1", addresses.resolve("$home/api/v1/stream/1"))
        assertNull(addresses.resolve(null))
        assertNull(addresses.resolve("  "))
    }

    @Test
    fun originsKeepOnlySchemeHostAndPort() {
        assertEquals("https://tuvima.example.com", ServerOrigins.normalize("tuvima.example.com"))
        assertEquals("https://tuvima.example.com:8443", ServerOrigins.normalize("https://tuvima.example.com:8443/library?x=1"))
        assertEquals("http://192.168.1.20:5016", ServerOrigins.normalize("http://192.168.1.20:5016/"))
    }

    @Test
    fun plainHttpIsAcceptedOnlyOnTheLocalNetwork() {
        assertNull(ServerOrigins.normalizeOrNull("http://public.example.com"))
        assertNull(ServerOrigins.normalizeOrNull(null))
        assertNull(ServerOrigins.normalizeOrNull(""))
        assertEquals("https://host.tailnet.ts.net", ServerOrigins.normalizeOrNull("https://host.tailnet.ts.net/"))
    }
}
