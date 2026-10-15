package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Test

class OrientationPolicyTest {
    @Test
    fun onlyVideoMayRotate() {
        assertEquals(OrientationRule.Sensor, OrientationPolicy.ruleFor(ScreenKind.Video))
    }

    @Test
    fun everythingElseStaysPortrait() {
        listOf(ScreenKind.Browse, ScreenKind.Audio, ScreenKind.Photo).forEach {
            assertEquals(OrientationRule.Portrait, OrientationPolicy.ruleFor(it))
        }
    }
}
