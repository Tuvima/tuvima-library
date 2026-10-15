package com.tuvima.library.core

/** How the profile wants subtitles: [Auto] only when the audio is not in the subtitle language. */
enum class ShowSubtitles { Auto, Always, Off }

data class TrackChoice(val audioIndex: Int?, val subtitleIndex: Int?)

/** Picks the starting audio and subtitle tracks from the profile's language preferences. Pure Kotlin. */
object PlaybackTrackPicker {
    fun pick(
        audioTracks: List<AudioTrack>,
        subtitleTracks: List<SubtitleTrack>,
        audioLanguage: String?,
        subtitleLanguage: String?,
        showSubtitles: ShowSubtitles,
    ): TrackChoice {
        val audio = audioTracks.firstOrNull { matches(it.language, audioLanguage) }
            ?: audioTracks.firstOrNull { it.isDefault }
            ?: audioTracks.firstOrNull()
        val wantsSubtitles = when (showSubtitles) {
            ShowSubtitles.Off -> false
            ShowSubtitles.Always -> true
            ShowSubtitles.Auto -> !matches(audio?.language, subtitleLanguage)
        }
        val subtitle = if (!wantsSubtitles) null else {
            subtitleTracks.firstOrNull { matches(it.language, subtitleLanguage) && !it.isForced }
                ?: subtitleTracks.firstOrNull { matches(it.language, subtitleLanguage) }
                ?: subtitleTracks.firstOrNull { it.isDefault }
                ?: subtitleTracks.firstOrNull()
        }
        return TrackChoice(audio?.index, subtitle?.index)
    }

    private fun matches(track: String?, wanted: String?): Boolean {
        if (track.isNullOrBlank() || wanted.isNullOrBlank()) return false
        return track.substringBefore('-').equals(wanted.substringBefore('-'), ignoreCase = true)
    }
}
