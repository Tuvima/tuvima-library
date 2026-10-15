package com.tuvima.library.core

/**
 * What a phone screen is for, as far as turning the device goes. [Video] is a movie or episode; [VideoClip] is a
 * short clip in Photos.
 */
enum class ScreenKind { Browse, Audio, Photo, Video, VideoClip }

/**
 * The orientation the phone app asks for. [Portrait] locks to portrait, [Sensor] follows the phone, and
 * [SensorLandscape] rotates between the two landscape directions only.
 */
enum class OrientationRule { Portrait, Sensor, SensorLandscape }

/**
 * The phone app is locked to portrait everywhere except video. Movies and episodes rotate to landscape, Photos
 * clips follow the phone, and audio, books and photos stay portrait. Pure Kotlin so a plain JVM test can check it.
 */
object OrientationPolicy {
    fun ruleFor(kind: ScreenKind): OrientationRule = when (kind) {
        ScreenKind.Video -> OrientationRule.SensorLandscape
        ScreenKind.VideoClip -> OrientationRule.Sensor
        else -> OrientationRule.Portrait
    }
}
