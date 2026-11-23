package com.tuvima.library.mobile

import android.content.Context
import android.content.SharedPreferences
import com.tuvima.library.core.PhoneDeviceChoices

/**
 * What this phone keeps for the people who use it while they play: the profile's playback preferences have no write route for apps, so
 * Autoplay, the skip seconds, the speed, the subtitle appearance, "Show on timeline" and the subtitle a person last chose stay here and
 * are never sent to the Engine. Each person (profile) has their own set, read and written as one [PhoneDeviceChoices].
 */
internal class PhoneDeviceStore(context: Context) {
    private val prefs: SharedPreferences = context.applicationContext.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    fun load(profileId: String?): PhoneDeviceChoices {
        val p = scope(profileId)
        return PhoneDeviceChoices(
            skipBack = int(p + SKIP_BACK),
            skipForward = int(p + SKIP_FORWARD),
            autoplay = if (prefs.contains(p + AUTOPLAY)) prefs.getBoolean(p + AUTOPLAY, true) else null,
            videoSpeed = float(p + VIDEO_SPEED),
            audiobookSpeed = float(p + BOOK_SPEED),
            subtitleStyle = prefs.getString(p + SUBTITLE_STYLE, null),
            subtitleBackground = prefs.getString(p + SUBTITLE_BACKGROUND, null),
            timelineWholeBook = prefs.getBoolean(p + WHOLE_BOOK, false),
            showBookmarksOnTimeline = prefs.getBoolean(p + SHOW_TICKS, true),
            rememberedSubtitle = prefs.getString(p + REMEMBERED_SUBTITLE, null),
        )
    }

    /** Reads this person's choices, lets [change] adjust them, and keeps the result. Returns what was kept. */
    fun update(profileId: String?, change: (PhoneDeviceChoices) -> PhoneDeviceChoices): PhoneDeviceChoices {
        val next = change(load(profileId))
        save(profileId, next)
        return next
    }

    fun save(profileId: String?, choices: PhoneDeviceChoices) {
        val p = scope(profileId)
        val editor = prefs.edit()
        putOrRemove(editor, p + SKIP_BACK, choices.skipBack)
        putOrRemove(editor, p + SKIP_FORWARD, choices.skipForward)
        val autoplay = choices.autoplay
        if (autoplay == null) editor.remove(p + AUTOPLAY) else editor.putBoolean(p + AUTOPLAY, autoplay)
        putOrRemove(editor, p + VIDEO_SPEED, choices.videoSpeed)
        putOrRemove(editor, p + BOOK_SPEED, choices.audiobookSpeed)
        putOrRemove(editor, p + SUBTITLE_STYLE, choices.subtitleStyle)
        putOrRemove(editor, p + SUBTITLE_BACKGROUND, choices.subtitleBackground)
        editor.putBoolean(p + WHOLE_BOOK, choices.timelineWholeBook)
        editor.putBoolean(p + SHOW_TICKS, choices.showBookmarksOnTimeline)
        putOrRemove(editor, p + REMEMBERED_SUBTITLE, choices.rememberedSubtitle)
        editor.apply()
    }

    private fun scope(profileId: String?): String = (profileId?.takeIf { it.isNotBlank() } ?: "anonymous") + "."

    private fun int(key: String): Int? = if (prefs.contains(key)) prefs.getInt(key, 0) else null

    private fun float(key: String): Double? = if (prefs.contains(key)) prefs.getFloat(key, 1f).toDouble() else null

    private fun putOrRemove(editor: SharedPreferences.Editor, key: String, value: Int?) {
        if (value == null) editor.remove(key) else editor.putInt(key, value)
    }

    private fun putOrRemove(editor: SharedPreferences.Editor, key: String, value: Double?) {
        if (value == null) editor.remove(key) else editor.putFloat(key, value.toFloat())
    }

    private fun putOrRemove(editor: SharedPreferences.Editor, key: String, value: String?) {
        if (value == null) editor.remove(key) else editor.putString(key, value)
    }

    private companion object {
        const val FILE = "phone_player_choices"
        const val SKIP_BACK = "skip_back"
        const val SKIP_FORWARD = "skip_forward"
        const val AUTOPLAY = "autoplay"
        const val VIDEO_SPEED = "video_speed"
        const val BOOK_SPEED = "audiobook_speed"
        const val SUBTITLE_STYLE = "subtitle_style"
        const val SUBTITLE_BACKGROUND = "subtitle_background"
        const val WHOLE_BOOK = "timeline_whole_book"
        const val SHOW_TICKS = "show_ticks"
        const val REMEMBERED_SUBTITLE = "remembered_subtitle"
    }
}
