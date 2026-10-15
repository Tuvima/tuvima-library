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
        return normalise(track) == normalise(wanted)
    }

    // Tracks usually carry three-letter codes (eng, jpn) while profiles store two-letter ones (en, ja).
    private fun normalise(code: String): String {
        val base = code.substringBefore('-').substringBefore('_').lowercase()
        return TWO_TO_THREE[base] ?: base
    }

    private val TWO_TO_THREE = mapOf(
        "en" to "eng", "ja" to "jpn", "fr" to "fra", "fre" to "fra", "de" to "deu", "ger" to "deu",
        "es" to "spa", "it" to "ita", "pt" to "por", "ru" to "rus", "zh" to "zho", "chi" to "zho",
        "ko" to "kor", "ar" to "ara", "hi" to "hin", "nl" to "nld", "dut" to "nld", "sv" to "swe",
        "pl" to "pol", "tr" to "tur", "he" to "heb", "el" to "ell", "gre" to "ell", "da" to "dan",
        "fi" to "fin", "no" to "nor", "cs" to "ces", "cze" to "ces", "uk" to "ukr", "th" to "tha",
        "vi" to "vie", "id" to "ind", "hu" to "hun", "ro" to "ron", "rum" to "ron",
    )
}
