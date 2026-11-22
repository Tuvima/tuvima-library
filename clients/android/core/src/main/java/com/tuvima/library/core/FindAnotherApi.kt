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
        override suspend fun alternatives(language: String?) = textTrackAlternatives(assetId, kind, language)

        override suspend fun preview(item: TextTrackAlternative, language: String?) =
            textTrackAlternativePreview(assetId, item.provider, item.sourceId, kind, language)

        override suspend fun choose(item: TextTrackAlternative, language: String?) =
            chooseTextTrackAlternative(assetId, ChooseTextTrackRequest(kind, item.provider, item.sourceId, language))
    }
