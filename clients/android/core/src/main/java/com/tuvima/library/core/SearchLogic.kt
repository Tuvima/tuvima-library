package com.tuvima.library.core

import org.json.JSONObject

// The phone's search overlay (`GET /api/v1/display/search?q=`): the Engine's ranked answer read into one column of rows, a heading per kind
// of result. The words are the Dashboard's overlay words. Pure wire reading and rules; nothing here draws anything.

/** One result. [entityType] and [id] are what the details page opens by; [line] is the grey line under the title. */
data class SearchHit(
    val entityType: String,
    val id: String,
    val title: String,
    val mediaType: String,
    val line: String?,
    val artworkUrl: String?,
)

data class SearchSection(val key: String, val title: String, val hits: List<SearchHit>, val totalCount: Int)

data class SearchResults(
    val query: String,
    val topResult: SearchHit?,
    val sections: List<SearchSection>,
    val totalCount: Int,
)

/** What the one-column list draws: a heading, then the hits under it. */
sealed interface SearchRow {
    data class Heading(val title: String) : SearchRow
    data class Hit(val hit: SearchHit) : SearchRow
}

object SearchLogic {
    /** The Engine is not asked until at least this many characters are typed. */
    const val MIN_QUERY_LENGTH = 2

    /** How long typing must pause before the search is sent. */
    const val DEBOUNCE_MS = 350L

    const val RESULT_LIMIT = 24
    const val PLACEHOLDER = "Search your library"
    const val CANCEL = "Cancel"
    const val TOP_RESULT = "Top result"
    const val HINT = "Find movies, shows, books, comics, music and audiobooks you own."
    const val CLEAR = "Clear search"

    /** The text to send, or null when it is too short to be worth a request. */
    fun queryToSend(typed: String): String? = typed.trim().takeIf { it.length >= MIN_QUERY_LENGTH }

    fun noResults(query: String): String = "No results for “${query.trim()}”."

    fun parse(json: JSONObject, absolute: (String?) -> String?): SearchResults = SearchResults(
        query = json.optString("query"),
        topResult = json.optJSONObject("topResult")?.let { parseHit(it, absolute) },
        sections = json.optJSONArray("sections")?.objects { section ->
            val hits = section.optJSONArray("results")?.objects { parseHit(it, absolute) }.orEmpty().filterNotNull()
            SearchSection(
                key = section.optString("key"),
                title = section.optString("title"),
                hits = hits,
                totalCount = section.optInt("totalCount", hits.size),
            )
        }.orEmpty(),
        totalCount = json.optInt("totalCount"),
    )

    private fun parseHit(json: JSONObject, absolute: (String?) -> String?): SearchHit? {
        val target = detailTargetFromRoute(json.optionalString("detailRoute"))
        val id = target?.second ?: json.optString("id")
        val entityType = target?.first ?: json.optString("entityType", "work").ifBlank { "work" }
        val title = json.optString("title")
        if (id.isBlank() || title.isBlank()) return null
        // Playlists are not on the phone in version 1 (no route reads them), and the details page refuses their route.
        if (isPlaylist(json)) return null
        val first = json.optionalString("subtitle") ?: json.optionalString("creator")
        val creator = json.optionalString("creator")
        val line = listOfNotNull(first, creator, json.optionalString("year"))
            .map(String::trim).filter(String::isNotEmpty).distinct().joinToString(" · ").takeIf { it.isNotEmpty() }
        return SearchHit(
            entityType = entityType,
            id = id,
            title = title,
            mediaType = json.optString("mediaType"),
            line = line,
            artworkUrl = absolute(json.optionalString("artworkUrl")),
        )
    }

    private fun isPlaylist(json: JSONObject): Boolean =
        json.optString("entityType").contains("playlist", ignoreCase = true) ||
            json.optString("mediaType").contains("playlist", ignoreCase = true) ||
            json.optString("detailRoute").contains("/playlist", ignoreCase = true)

    /**
     * The rows to draw: the top result under its own heading, then each kind of result under its heading. A title that already
     * appeared (the top result is repeated in its kind's list, and a title can sit under two kinds) is shown once, and a kind left
     * with nothing is not drawn.
     */
    fun rows(results: SearchResults): List<SearchRow> = buildList {
        val seen = HashSet<String>()
        results.topResult?.takeIf { seen.add(keyOf(it)) }?.let {
            add(SearchRow.Heading(TOP_RESULT))
            add(SearchRow.Hit(it))
        }
        results.sections.forEach { section ->
            val fresh = section.hits.filter { seen.add(keyOf(it)) }
            if (fresh.isEmpty()) return@forEach
            add(SearchRow.Heading(section.title.ifBlank { "Results" }))
            fresh.forEach { add(SearchRow.Hit(it)) }
        }
    }

    /** True when there is at least one hit to draw. */
    fun hasHits(results: SearchResults): Boolean = rows(results).any { it is SearchRow.Hit }

    private fun keyOf(hit: SearchHit): String = "${hit.entityType.lowercase()}:${hit.id.lowercase()}"
}
