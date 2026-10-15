package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Test

class OrientationPolicyTest {
    @Test
    fun moviesAndEpisodesRotateToLandscape() {
        assertEquals(OrientationRule.SensorLandscape, OrientationPolicy.ruleFor(ScreenKind.Video))
    }

    @Test
    fun photoClipsFollowThePhone() {
        assertEquals(OrientationRule.Sensor, OrientationPolicy.ruleFor(ScreenKind.VideoClip))
    }

    @Test
    fun everythingElseStaysPortrait() {
        listOf(ScreenKind.Browse, ScreenKind.Audio, ScreenKind.Photo).forEach {
            assertEquals(OrientationRule.Portrait, OrientationPolicy.ruleFor(it))
        }
    }
}
