package com.tuvima.library.core

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The outside address is learned only by discovery (pairing and app start). A quick address check must never set, replace
 * or clear it, or a hiccup on one answer could change where the phone goes. Reads the client's source, because the client
 * itself needs the Android JSON classes and cannot run in a JVM unit test.
 */
class OutsideAddressLearningGuardrailTest {
    private val source = File("src/main/java/com/tuvima/library/core/TuvimaClient.kt").readText().replace("\r\n", "\n")

    private fun body(startMarker: String): String {
        val start = source.indexOf(startMarker)
        assertTrue("$startMarker not found", start >= 0)
        val next = Regex("\n    (private |internal )?(suspend )?fun ").find(source, start + startMarker.length)?.range?.first ?: source.length
        return source.substring(start, next)
    }

    @Test
    fun onlyDiscoveryLearnsTheOutsideAddress() {
        val calls = Regex("""rememberOutside\(""").findAll(source).count()
        // The definition and the one call in discover().
        assertEquals(2, calls)
        assertTrue(body("suspend fun discover()").contains("rememberOutside("))
    }

    @Test
    fun theAddressChecksNeverTouchTheStoredAddress() {
        listOf("private suspend fun isReachable(", "private suspend fun fetchDiscoveryQuickly(", "suspend fun refreshServerAddress()")
            .forEach { marker ->
                val text = body(marker)
                assertTrue("$marker must not learn the outside address", !text.contains("rememberOutside") && !text.contains("updateOutside"))
            }
    }
}
