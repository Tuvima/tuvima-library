package com.tuvima.library.mobile

import com.tuvima.library.core.AudioNow
import com.tuvima.library.core.SleepTimer
import com.tuvima.library.core.TvSkip
import kotlinx.coroutines.flow.MutableStateFlow

/**
 * What the media service and the music and audiobook screens share. They run in one app process, so the screens read what is playing and set the
 * Sleep timer, Shuffle and the skip seconds here, and the service (which owns the player and the timer's ticking) reads them back. Nothing here goes
 * to the Engine (spec native-apps WP10c).
 */
internal object AudioBridge {
    /** What the service is playing, or [AudioNow.None] while nothing is loaded or a video is open. */
    val now = MutableStateFlow(AudioNow.None)

    /** The audiobook Sleep timer. The service pauses playback when it is due and sets it back to Off. */
    val sleepTimer = MutableStateFlow<SleepTimer>(SleepTimer.Off)

    /** True while Shuffle is on for the music queue. */
    val shuffle = MutableStateFlow(false)

    /** The profile's skip seconds for audio, used by the lock screen's skip buttons as well as the screen's. */
    val skip = MutableStateFlow(TvSkip(10, 30))

    /** Words for something that could not be played (a song the Engine would not prepare); the screen shows them as a line, never a toast. */
    val problem = MutableStateFlow<String?>(null)
}
