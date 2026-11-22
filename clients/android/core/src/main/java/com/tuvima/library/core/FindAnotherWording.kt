package com.tuvima.library.core

// The words of the Find another window, the same plain sentences the Dashboard shows (Find another results v2, D9 to D12),
// so a person sees one wording on every screen. US English. Pure functions: nothing here draws anything.

/** How a check reads: green, amber, red, or plain. */
enum class CheckTone { Neutral, Ok, Warn, Bad }

/** One plain status under a result's name, for example "Made for your file". */
data class FindAnotherCheck(val text: String, val tone: CheckTone)

object FindAnotherWording {
    /** A subtitle result at or above this confidence was made for the file in hand (the Dashboard's own threshold). */
    const val SAME_RELEASE_CONFIDENCE = 0.9

    /** A lyrics length off by up to this many seconds reads amber; beyond it, red. */
    const val LYRICS_AMBER_UP_TO_SECONDS = 15.0

    /** A subtitle file that ends up to this many minutes off reads amber; beyond it, red. */
    const val SUBTITLE_AMBER_UP_TO_MINUTES = 15.0

    const val BEST_MATCH = "Best match"
    const val IN_USE = "In use"
    const val HEARING_IMPAIRED = "Hearing impaired"
    const val MADE_FOR_YOUR_FILE = "Made for your file"
    const val DIFFERENT_RELEASE = "A different release, may be out of step"
    const val FITS_THE_FILM = "Fits the film"
    const val FITS_THE_SONG = "Fits the song"
    const val SYNCED = "Synced"
    const val WORDS_ONLY = "Words only, not timed"
    const val READING = "Reading…"
    const val COULD_NOT_READ = "Could not read this one"
    const val NOT_READ_YET = "Select this result to read its first lines."
    const val UNTITLED = "Untitled result"
    const val USE_THIS = "Use this"
    const val CANCEL = "Cancel"
    const val FIND_SUBTITLES_ENTRY = "Find subtitles…"
    const val FIND_LYRICS_ENTRY = "Find lyrics…"
    const val TRY_AGAIN = "Try again"

    /** The window's title next to the item's name. */
    fun windowTitle(kind: TextTrackKind): String = if (kind == TextTrackKind.Lyrics) "Find lyrics" else "Find subtitles"

    /** The footer row of the Audio & subtitles panel (and of a Lyrics panel). */
    fun entryLabel(kind: TextTrackKind): String = if (kind == TextTrackKind.Lyrics) FIND_LYRICS_ENTRY else FIND_SUBTITLES_ENTRY

    /** Shown in the list area while the sources are asked; the rows then appear together. */
    fun loadingText(kind: TextTrackKind): String =
        if (kind == TextTrackKind.Lyrics) "Looking for lyrics…" else "Looking for subtitles…"

    /** The row's own name: the release name for a subtitle, the source's name for a song. */
    fun rowTitle(item: TextTrackAlternative, kind: TextTrackKind): String =
        (if (kind == TextTrackKind.Lyrics) item.title ?: item.releaseName else item.releaseName)
            ?.takeIf { it.isNotBlank() } ?: UNTITLED

    /** The muted line under a subtitle row's name: its language (when the list mixes them) and "Hearing impaired". */
    fun subtitleMeta(item: TextTrackAlternative, showLanguage: Boolean): String? =
        listOfNotNull(
            if (showLanguage) FindAnotherLanguages.nameFor(item.language) else null,
            if (item.isHearingImpaired) HEARING_IMPAIRED else null,
        ).joinToString(" · ").takeIf { it.isNotBlank() }

    /** The muted line under a lyrics row's name: artist, album and length. */
    fun lyricsMeta(item: TextTrackAlternative): String? =
        listOfNotNull(
            item.artist?.takeIf { it.isNotBlank() },
            item.album?.takeIf { it.isNotBlank() },
            item.lengthSeconds?.takeIf { it > 0 }?.let(::clock),
        ).joinToString(" · ").takeIf { it.isNotBlank() }

    /** The checks under a subtitle row: made for your file, then once the file is read whether its timing fits the film. */
    fun subtitleChecks(item: TextTrackAlternative): List<FindAnotherCheck> = buildList {
        add(
            if (item.confidence >= SAME_RELEASE_CONFIDENCE) FindAnotherCheck(MADE_FOR_YOUR_FILE, CheckTone.Ok)
            else FindAnotherCheck(DIFFERENT_RELEASE, CheckTone.Warn),
        )
        val check = item.lengthCheck
        val difference = item.lengthDifferenceSeconds
        if (check != null && difference != null) {
            val minutes = kotlin.math.abs(difference) / 60.0
            add(
                if (check == "Fits") {
                    FindAnotherCheck(FITS_THE_FILM, CheckTone.Ok)
                } else {
                    FindAnotherCheck(
                        minutesOffText(difference, minutes),
                        if (minutes <= SUBTITLE_AMBER_UP_TO_MINUTES) CheckTone.Warn else CheckTone.Bad,
                    )
                },
            )
        }
    }

    /** The checks under a lyrics row: Synced or Words only; then Fits the song or how many seconds longer or shorter. */
    fun lyricsChecks(item: TextTrackAlternative): List<FindAnotherCheck> = buildList {
        add(if (item.isTimed) FindAnotherCheck(SYNCED, CheckTone.Ok) else FindAnotherCheck(WORDS_ONLY, CheckTone.Neutral))
        val check = item.lengthCheck
        val difference = item.lengthDifferenceSeconds
        if (check != null && difference != null) {
            val seconds = whole(kotlin.math.abs(difference))
            add(
                if (check == "Fits") {
                    FindAnotherCheck(FITS_THE_SONG, CheckTone.Ok)
                } else {
                    FindAnotherCheck(
                        if (difference > 0) "$seconds seconds longer than the song" else "$seconds seconds shorter than the song",
                        if (kotlin.math.abs(difference) <= LYRICS_AMBER_UP_TO_SECONDS) CheckTone.Warn else CheckTone.Bad,
                    )
                },
            )
        }
    }

    /** "Ends 4 minutes short of the film" or "Ends 4 minutes past the film" (a file read as one minute off says "1 minute"). */
    private fun minutesOffText(differenceSeconds: Double, minutes: Double): String {
        val count = whole(minutes)
        val unit = if (count == 1) "minute" else "minutes"
        return if (differenceSeconds < 0) "Ends $count $unit short of the film" else "Ends $count $unit past the film"
    }

    private fun whole(value: Double): Int = maxOf(1L, Math.round(value)).toInt()

    /** Minutes and seconds ("3:42"), or hours when long ("1:05:09"). */
    fun clock(seconds: Double): String {
        val total = maxOf(0L, Math.round(seconds))
        val hours = total / 3600
        val minutes = (total % 3600) / 60
        val rest = total % 60
        return if (hours >= 1) "$hours:${two(minutes)}:${two(rest)}" else "$minutes:${two(rest)}"
    }

    private fun two(value: Long): String = if (value < 10) "0$value" else value.toString()

    /**
     * One sentence for an answer with no rows, centered in the list area with Cancel focused. The Engine's own message is
     * preferred when it wrote one; otherwise the plain sentence for the status. [itemNoun] is "film", "episode" or "song".
     */
    fun statusSentence(
        kind: TextTrackKind,
        status: String,
        message: String?,
        sourceName: String?,
        language: String?,
        itemNoun: String = if (kind == TextTrackKind.Lyrics) "song" else "film",
    ): String {
        if (!message.isNullOrBlank()) return message.trim()
        val text = if (kind == TextTrackKind.Lyrics) "lyrics" else "subtitle"
        val source = sourceName?.takeIf { it.isNotBlank() } ?: "The $text source"
        return when (status) {
            "NoSourceOn" -> "No $text source is switched on."
            "SourceKeyRequired" -> "$source did not accept its key."
            "IdentityUnverified" -> "This $itemNoun could not be matched for an online search."
            "LimitReached" -> "$source has reached its limit for now. Try again later."
            "Unavailable" -> "$source could not be reached."
            "Blocked" -> "This library never looks anything up online."
            "NotAllowed" -> "Your profile cannot search for $text${if (kind == TextTrackKind.Lyrics) "" else "s"}."
            "LanguageUnknown" -> "That language is not supported for subtitle search."
            "NoneFound", "Found" ->
                if (kind == TextTrackKind.Lyrics) "No lyrics were found for this $itemNoun."
                else "No ${language?.takeIf { it.isNotBlank() }?.let { "$it " } ?: ""}subtitles were found for this $itemNoun."
            else -> "The search could not be completed."
        }
    }

    /** The pane's text for a row that has no words: reading, could not read, or the prompt to select it. */
    fun paneNote(reading: Boolean, failed: Boolean): String = when {
        reading -> READING
        failed -> COULD_NOT_READ
        else -> NOT_READ_YET
    }

    /** One pane line: the time it appears (subtitles) then the words. */
    fun paneLine(line: TextTrackPreviewLine): String =
        line.atSeconds?.let { "${clock(it)}  ${line.text}" } ?: line.text
}
