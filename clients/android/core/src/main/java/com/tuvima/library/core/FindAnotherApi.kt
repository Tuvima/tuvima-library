package com.tuvima.library.core

/**
 * The three Engine calls the Find another window makes for one item, as an interface so a window can be driven (and tested)
 * without a network. [TuvimaClient.findAnotherApi] is the real one.
 */
interface FindAnotherApi {
    suspend fun alternatives(language: String?): TextTrackAlternatives

    /** Reads one result's words. */
    suspend fun preview(item: TextTrackAlternative, language: String?): TextTrackAlternativePreview

    /** Uses one result. [language] is the language the list was searched in. */
    suspend fun choose(item: TextTrackAlternative, language: String?): TextTrackChoiceResult
}

/** The window's calls for [assetId], through this paired app's authenticated client. */
fun TuvimaClient.findAnotherApi(assetId: String, kind: TextTrackKind = TextTrackKind.Subtitles): FindAnotherApi =
    object : FindAnotherApi {
        override suspend fun alternatives(language: String?) = textTrackAlternatives(assetId, kind, languageFor(kind, language))

        override suspend fun preview(item: TextTrackAlternative, language: String?) =
            textTrackAlternativePreview(assetId, item.provider, item.sourceId, kind, languageFor(kind, language))

        override suspend fun choose(item: TextTrackAlternative, language: String?) =
            chooseTextTrackAlternative(assetId, ChooseTextTrackRequest(kind, item.provider, item.sourceId, languageFor(kind, language)))
    }

/** Lyrics are searched in the song's own language: the window has no Language button and no language is ever sent for them. */
internal fun languageFor(kind: TextTrackKind, language: String?): String? = if (kind == TextTrackKind.Lyrics) null else language
