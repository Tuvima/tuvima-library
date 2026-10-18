package com.tuvima.library.core

import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** Which address to use, with a scripted network: home first, outside as the fallback, and no flapping when nothing answers. */
class ServerAddressSelectorTest {
    private val home = "http://192.168.1.20:5016"
    private val outside = "https://tuvima.example.com"

    private class Network(private val reachable: Set<String>) {
        val asked = mutableListOf<String>()
        val probe: suspend (String) -> Boolean = { origin ->
            asked += origin
            origin in reachable
        }
    }

    private fun choose(network: Network, outside: String?, current: String) =
        runBlocking { ServerAddressSelector.choose(home, outside, current, network.probe) }

    @Test
    fun usesTheHomeAddressWhenItAnswers_andNeverAsksTheOutsideOne() {
        val network = Network(setOf(home, outside))

        assertEquals(home, choose(network, outside, current = home))
        assertEquals(listOf(home), network.asked)
    }

    @Test
    fun fallsBackToTheOutsideAddressWhenHomeDoesNotAnswer() {
        val network = Network(setOf(outside))

        assertEquals(outside, choose(network, outside, current = home))
        assertEquals(listOf(home, outside), network.asked)
    }

    @Test
    fun switchesBackHomeWhenHomeAnswersAgain() {
        val network = Network(setOf(home, outside))

        assertEquals(home, choose(network, outside, current = outside))
    }

    @Test
    fun staysWhereItIsWhenNeitherAddressAnswers_soAnOfflinePhoneDoesNotFlap() {
        val network = Network(emptySet())

        assertEquals(home, choose(network, outside, current = home))
        assertEquals(outside, choose(network, outside, current = outside))
    }

    @Test
    fun staysOnHomeWithoutAskingAnythingWhenThereIsNoOutsideAddress() {
        val network = Network(emptySet())

        assertEquals(home, choose(network, outside = null, current = home))
        assertTrue(network.asked.isEmpty())
    }

    @Test
    fun anOutsideAddressEqualToHomeIsNotASecondAddress() {
        val network = Network(emptySet())

        assertEquals(home, choose(network, outside = home, current = home))
        assertTrue(network.asked.isEmpty())
    }

    @Test
    fun theHomeCheckIsShort() {
        assertTrue(ServerAddressSelector.PROBE_TIMEOUT_MS in 1_000..2_000)
    }
}
