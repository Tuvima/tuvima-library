package com.tuvima.library.core

// The television Lyrics panel without a screen (spec "Find another on TV", D6 and WP5): reading the lyrics file the Engine
// serves as text, which lyrics track shows, which line is current at the song's position, and what the remote's keys do in
// the panel. The same rules as the TV web and Roku panels, so a song reads the same on every television.

/** One lyric line. [atSeconds] is set for a synced line only. */
data class LyricLine(val atSeconds: Double?, val text: String)

/** A lyrics file turned into lines. [synced] is true when the file carried times; then [lines] are in time order. */
data class ParsedLyrics(val synced: Boolean, val lines: List<LyricLine>)

/** The lyrics tracks of one asset, as the track list reports them (the list also holds subtitle tracks). */
data class LyricsTrackInfo(val id: String, val kind: String, val isPreferred: Boolean)

/** Where the panel is in reading the song's lyrics. */
enum class LyricsPhase { Loading, Ready, None, Failed }

/** What the panel draws. [note] replaces the loading sentence while new lyrics are read ("Updating lyrics…"). */
data class LyricsView(val phase: LyricsPhase, val parsed: ParsedLyrics?, val note: String?) {
    companion object {
        fun loading(note: String? = null) = LyricsView(LyricsPhase.Loading, null, note)
        val None = LyricsView(LyricsPhase.None, null, null)
        val Failed = LyricsView(LyricsPhase.Failed, null, null)
        fun ready(parsed: ParsedLyrics) = LyricsView(LyricsPhase.Ready, parsed, null)
    }
}

/** What a key does in the open Lyrics panel. */
enum class LyricsPanelEffect { None, FindLyrics, Close }

object LyricsFollow {
    const val HEADING = "Lyrics"
    const val UNSYNCED = "Unsynced"
    const val LOOKING = "Looking for lyrics…"
    const val UPDATING = "Updating lyrics…"
    const val NO_LYRICS = "There are no lyrics for this song yet."
    const val NOT_LOADED = "The lyrics could not be loaded."

    /** A line shown for an instrumental gap (a timed line with no words). */
    const val GAP = "♪"

    // [mm:ss.xx] (or [mm:ss], [mm:ss:xx]) starts a timed line; <mm:ss.xx> inside a line is a per-word stamp and is dropped.
    private val LineStamp = Regex("""\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?]""")
    private val WordStamp = Regex("""<\d{1,3}:\d{2}(?:[.:]\d{1,3})?>""")

    // A tag line such as [ar:Artist], [ti:Title] or [offset:0]: not a lyric.
    private val TagOnly = Regex("""^\[[A-Za-z#]+:[^\]]*]$""")

    private fun stampSeconds(minutes: String, seconds: String, fraction: String): Double {
        val fractionValue = if (fraction.isEmpty()) 0.0 else fraction.toInt() / Math.pow(10.0, fraction.length.toDouble())
        return minutes.toInt() * 60 + seconds.toInt() + fractionValue
    }

    /**
     * Reads a lyrics file the Engine serves as text. Lines with times make it synced: a line with several stamps appears at
     * each, word stamps are dropped and the lines are kept in time order (equal times keep the file's order). A file with no
     * timed line is plain words (unsynced): one line per row, tag lines left out.
     */
    fun parse(content: String?): ParsedLyrics {
        val raw = (content ?: "").removePrefix("﻿").replace("\r\n", "\n").replace('\r', '\n').split('\n')
        val timed = mutableListOf<LyricLine>()
        val plain = mutableListOf<LyricLine>()
        for (rawLine in raw) {
            val stamps = LineStamp.findAll(rawLine).map { stampSeconds(it.groupValues[1], it.groupValues[2], it.groupValues[3]) }.toList()
            val words = rawLine.replace(LineStamp, "").replace(WordStamp, "").replace(Regex("""\s+"""), " ").trim()
            if (stamps.isNotEmpty()) {
                stamps.forEach { timed.add(LyricLine(it, words)) }
            } else if (words.isNotEmpty() && !TagOnly.matches(words)) {
                plain.add(LyricLine(null, words))
            }
        }
        // sortedBy is stable, so lines with the same time keep the file's order.
        return if (timed.isNotEmpty()) ParsedLyrics(true, timed.sortedBy { it.atSeconds }) else ParsedLyrics(false, plain)
    }

    /** The Synced or Unsynced chip beside the heading. */
    fun chip(parsed: ParsedLyrics?): String = if (parsed?.synced == true) FindAnotherWording.SYNCED else UNSYNCED

    /**
     * The line that is current at [positionSeconds]: the last timed line at or before it. -1 before the first line, when the
     * lyrics are not synced or when the position is unknown.
     */
    fun currentIndex(lines: List<LyricLine>, positionSeconds: Double?): Int {
        if (positionSeconds == null || positionSeconds.isNaN()) return -1
        var found = -1
        for ((index, line) in lines.withIndex()) {
            val at = line.atSeconds ?: return -1
            if (at <= positionSeconds) found = index else break
        }
        return found
    }

    /** The text a line draws: an instrumental gap shows a note sign. */
    fun displayText(line: LyricLine): String = line.text.ifBlank { GAP }

    /** Which text track shows as the song's lyrics: the one the Engine marks preferred, else the first lyrics track. */
    fun pickTrack(tracks: List<LyricsTrackInfo>): LyricsTrackInfo? {
        val lyrics = tracks.filter { it.kind.equals("lyrics", ignoreCase = true) && it.id.isNotBlank() }
        return lyrics.firstOrNull { it.isPreferred } ?: lyrics.firstOrNull()
    }

    /** Whether a playback manifest's media type is music (the Lyrics panel is offered there only; audiobooks are not music). */
    fun isMusic(mediaType: String?): Boolean {
        val value = mediaType?.trim()?.lowercase().orEmpty()
        if (value.isEmpty() || "audiobook" in value) return false
        return "music" in value || "song" in value || "track" in value
    }

    /**
     * The remote in the open panel. Select on the footer row opens Find lyrics (only when it is shown); Back or Left closes
     * the panel; the arrows do nothing because the lines follow the song by themselves.
     */
    fun panelKey(key: FindAnotherKey, canFind: Boolean): LyricsPanelEffect = when (key) {
        FindAnotherKey.Select -> if (canFind) LyricsPanelEffect.FindLyrics else LyricsPanelEffect.None
        FindAnotherKey.Back, FindAnotherKey.Left -> LyricsPanelEffect.Close
        else -> LyricsPanelEffect.None
    }
}
