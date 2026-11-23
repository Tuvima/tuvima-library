package com.tuvima.library.tv

import android.content.Context
import android.os.Build
import com.tuvima.library.core.TvDevice
import com.tuvima.library.core.TvDeviceChoices

/**
 * What this television keeps for itself. The profile's playback preferences are read-only for apps, so a choice made in
 * Playback settings, the Autoplay switch and the subtitle appearance stay on the device (never sent to the Engine).
 */
class TvDeviceStore(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    fun load(): TvDeviceChoices = TvDeviceChoices(
        skipBack = prefs.takeIf { it.contains(SKIP_BACK) }?.getInt(SKIP_BACK, 0),
        skipForward = prefs.takeIf { it.contains(SKIP_FORWARD) }?.getInt(SKIP_FORWARD, 0),
        autoplay = prefs.takeIf { it.contains(AUTOPLAY) }?.getBoolean(AUTOPLAY, true),
        speed = prefs.takeIf { it.contains(SPEED) }?.getFloat(SPEED, 1f)?.toDouble(),
        subtitleStyle = prefs.getString(SUBTITLE_STYLE, null),
        subtitleBackground = prefs.getString(SUBTITLE_BACKGROUND, null),
        timelineWholeBook = prefs.getBoolean(TIMELINE_WHOLE_BOOK, false),
        showBookmarksOnTimeline = prefs.getBoolean(SHOW_TICKS, true),
    )

    fun save(choices: TvDeviceChoices) {
        prefs.edit().apply {
            putOrRemove(SKIP_BACK, choices.skipBack)
            putOrRemove(SKIP_FORWARD, choices.skipForward)
            if (choices.autoplay == null) remove(AUTOPLAY) else putBoolean(AUTOPLAY, choices.autoplay!!)
            if (choices.speed == null) remove(SPEED) else putFloat(SPEED, choices.speed!!.toFloat())
            if (choices.subtitleStyle == null) remove(SUBTITLE_STYLE) else putString(SUBTITLE_STYLE, choices.subtitleStyle)
            if (choices.subtitleBackground == null) remove(SUBTITLE_BACKGROUND) else putString(SUBTITLE_BACKGROUND, choices.subtitleBackground)
            putBoolean(TIMELINE_WHOLE_BOOK, choices.timelineWholeBook)
            putBoolean(SHOW_TICKS, choices.showBookmarksOnTimeline)
        }.apply()
    }

    private fun android.content.SharedPreferences.Editor.putOrRemove(key: String, value: Int?) {
        if (value == null) remove(key) else putInt(key, value)
    }

    private companion object {
        const val FILE = "tv_player_device_choices"
        const val SKIP_BACK = "skip_back"
        const val SKIP_FORWARD = "skip_forward"
        const val AUTOPLAY = "autoplay"
        const val SPEED = "speed"
        const val SUBTITLE_STYLE = "subtitle_style"
        const val SUBTITLE_BACKGROUND = "subtitle_background"
        const val TIMELINE_WHOLE_BOOK = "timeline_whole_book"
        const val SHOW_TICKS = "show_ticks"
    }
}

/** Fire TV is Amazon's: the maker says Amazon or the system has `amazon.hardware.fire_tv` (the rule is [TvDevice.isFireTv]). */
fun isFireTv(context: Context): Boolean =
    TvDevice.isFireTv(Build.MANUFACTURER, context.packageManager.hasSystemFeature(TvDevice.FIRE_TV_FEATURE))
