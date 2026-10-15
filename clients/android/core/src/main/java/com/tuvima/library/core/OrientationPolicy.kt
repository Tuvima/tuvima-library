package com.tuvima.library.core

/** What a phone screen is for, as far as turning the device goes. */
enum class ScreenKind { Browse, Audio, Photo, Video }

/** The orientation the phone app asks for. [Portrait] locks to portrait; [Sensor] lets the device rotate freely. */
enum class OrientationRule { Portrait, Sensor }

/**
 * The phone app is locked to portrait everywhere except actual video playback, which may rotate to landscape.
 * Audio, books and photos stay portrait. Pure Kotlin so a plain JVM test can check it.
 */
object OrientationPolicy {
    fun ruleFor(kind: ScreenKind): OrientationRule =
        if (kind == ScreenKind.Video) OrientationRule.Sensor else OrientationRule.Portrait
}
