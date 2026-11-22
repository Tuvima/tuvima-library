package com.tuvima.library.core

import org.json.JSONObject

// "Find another" wire models: the other subtitle (or lyrics) results the Engine's sources offer for one item, one result's
// words read on request, and the choice of one. Names mirror `TextTrackResponses.cs` in Contracts (snake_case on the wire).
// Pure Kotlin with no screen types, so the television window and the phone sheet share it.

/** The two kinds of text the window searches for. The wire word is [wire]. */
enum class TextTrackKind(val wire: String) {
    Subtitles("subtitles"),
    Lyrics("lyrics"),
}

/** One line of a result's preview. [atSeconds] is set for subtitles only. */
data class TextTrackPreviewLine(val atSeconds: Double?, val text: String)

/** One result a source offers. [previewLines] is empty for a subtitle result that has not been read yet. */
data class TextTrackAlternative(
    val provider: String,
    val providerName: String,
    val sourceId: String,
    val language: String,
    val isTimed: Boolean,
    val confidence: Double,
    val durationMatchScore: Double?,
    val isHearingImpaired: Boolean,
    val releaseName: String?,
    val isCurrent: Boolean,
    val title: String?,
    val artist: String?,
    val album: String?,
    val lengthSeconds: Double?,
    val itemLengthSeconds: Double?,
    val previewLines: List<TextTrackPreviewLine>,
    val endsAtSeconds: Double?,
    /** `Fits` or `Off`; null when a length is unknown (a subtitle file that has not been read has none). */
    val lengthCheck: String?,
    /** Seconds longer (positive) or shorter (negative) than the song, or the file's end against the film's length. */
    val lengthDifferenceSeconds: Double?,
) {
    /** The identity of a result inside one list: the same key the Dashboard uses. */
    val key: String get() = "$provider:$sourceId"

    /** True once the real words are known (the best match arrives with them; any other row is read on request). */
    val hasWords: Boolean get() = previewLines.isNotEmpty()
}

/**
 * The list the sources offered. [status] is the Engine's word (`Found`, `NoneFound`, `NoSourceOn`, `NotAllowed`, `Blocked`,
 * `Unavailable`, `AssetMissing`, `Unsupported`, `IdentityUnverified`, `SourceKeyRequired`, `LimitReached`, `LanguageUnknown`).
 */
data class TextTrackAlternatives(
    val kind: String,
    val status: String,
    val message: String,
    val items: List<TextTrackAlternative>,
    val sourceName: String?,
    val language: String?,
) {
    /** True when there are rows to show. */
    val hasRows: Boolean get() = status == "Found" && items.isNotEmpty()
}

/** One result read on request: [status] is `Found`, `NotFound`, `Unavailable` or a source status such as `LimitReached`. */
data class TextTrackAlternativePreview(val status: String, val item: TextTrackAlternative?) {
    /** True when the words came back and can be shown. */
    val isRead: Boolean get() = status == "Found" && item != null && item.hasWords
}

/** Whether a source is switched on for the item, answered from settings alone (no source is contacted). */
data class TextTrackSources(val status: String) {
    /** The "Find subtitles…" entry is shown only for this answer; every other status hides it. */
    val isReady: Boolean get() = status == "Ready"

    companion object {
        /** What a failed or unreadable answer means: nothing to search, so the entry stays hidden. */
        val Unavailable = TextTrackSources("Unavailable")
    }
}

/** Body of the choose call. [language] is the language the list was searched in, so the same result is found again. */
data class ChooseTextTrackRequest(
    val kind: TextTrackKind,
    val provider: String,
    val sourceId: String,
    val language: String? = null,
) {
    fun toJson(): JSONObject {
        val body = JSONObject()
            .put("kind", kind.wire)
            .put("provider", provider)
            .put("source_id", sourceId)
        if (!language.isNullOrBlank()) body.put("language", language)
        return body
    }
}

/** The answer to a choice. [trackId] is the track now in use, or null when none is. */
data class TextTrackChoiceResult(val status: String, val message: String, val trackId: String?) {
    /** The Engine says `Updated` when the result is now the one in use. */
    val isUpdated: Boolean get() = status == "Updated"
}
