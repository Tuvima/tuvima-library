package com.tuvima.library.core

import org.json.JSONObject

// The phone Home hero card (Dashboard `PhoneHeroCard`): one fixed card at the top of Home that the person swipes through, a title, one meta
// line and one full-width main button, with the My List plus in the corner. Read from the `spotlights` (else `hero`) of
// `GET /api/v1/display/home`. Pure wire reading and rules; nothing here draws anything.

/** One slide. [card] is the same card shape every other screen opens, so a tap and the My List plus behave as they do on a poster. */
data class HeroSlide(
    val card: DisplayCard,
    val eyebrow: String?,
    val metaLine: String,
    /** Cover addresses of the "Recently added music" collage slide; empty for every other slide. */
    val collageUrls: List<String> = emptyList(),
) {
    /**
     * True for the "Recently added music" slide. The Engine builds it from the newest album's own card, so its id and work are one
     * album's, but it stands for all the new music: it never opens, saves or plays that one album.
     */
    val isCollage: Boolean get() = collageUrls.isNotEmpty()
}

/** The main button: [assetId] is set when it plays something; [opensMusic] when it opens the Listen section; otherwise it opens the details. */
data class HeroPrimary(val label: String, val assetId: String?, val opensMusic: Boolean = false)

/** What Home draws from one read: the shelves and the hero slides. */
data class HomeContent(val page: DisplayPage, val heroes: List<HeroSlide>)

object HomeHeroLogic {
    /** The Dashboard rotates a handful of featured titles; more would only be a long swipe. */
    const val MAX_SLIDES = 5

    const val OPEN_MUSIC = "Open music"
    const val OPEN_DETAILS = "Details"
    const val PLAY = "Play"
    const val RESUME = "Resume"

    /** The action types that start something playing (a file, or an album's first track). */
    private val playTypes = setOf("playasset", "playalbum")

    /** The slides of a Home answer: the spotlights, or the single hero when there are none. A slide with no title or id is skipped. */
    fun parse(json: JSONObject, absolute: (String?) -> String?): List<HeroSlide> {
        val spotlights = json.optJSONArray("spotlights")?.objects { it }.orEmpty()
        val sources = spotlights.ifEmpty { listOfNotNull(json.optJSONObject("hero")) }
        val seen = HashSet<String>()
        return sources.mapNotNull { source ->
            val parsed = parseDisplayCard(source, absolute)
            val id = parsed.id.ifBlank { parsed.workId ?: parsed.collectionId ?: "" }
            if (id.isBlank() || parsed.title.isBlank() || !seen.add(id.lowercase())) return@mapNotNull null
            val card = parsed.copy(id = id)
            val collage = source.optJSONArray("collageUrls")?.strings().orEmpty().mapNotNull { absolute(it) }
            HeroSlide(card, source.optionalString("eyebrow"), if (collage.isEmpty()) metaLine(card) else "", collage)
        }.take(MAX_SLIDES)
    }

    /** The one line under the title: the first facts (year, rating, length) in the Engine's order, else its subtitle. */
    fun metaLine(card: DisplayCard): String =
        card.facts.map(String::trim).filter(String::isNotEmpty).take(3).joinToString(" · ")
            .ifEmpty { card.subtitle?.trim().orEmpty() }

    /**
     * The main button. A title with a file to play says Resume when the person is part-way through, otherwise Play, and plays that
     * file. Anything else (a book or comic, a show or group, a title with no file) opens its details: the phone has no reader yet.
     */
    fun primary(slide: HeroSlide): HeroPrimary {
        if (slide.isCollage) return HeroPrimary(OPEN_MUSIC, null, opensMusic = true)
        val play = slide.card.actions.firstOrNull { it.type.lowercase() in playTypes && !it.assetId.isNullOrBlank() }
            ?: return HeroPrimary(OPEN_DETAILS, null)
        val percent = slide.card.progressPercent
        val resuming = (percent != null && percent > 0.0 && percent < 99.5) ||
            play.label.contains("resume", ignoreCase = true) || play.label.contains("continue", ignoreCase = true)
        return HeroPrimary(if (resuming) RESUME else PLAY, play.assetId)
    }

    /** The progress line is drawn only while the title is started and not finished. */
    fun showsProgress(card: DisplayCard): Boolean = card.progressPercent?.let { it > 0.0 && it < 99.5 } == true

    /** The words a screen reader says for one dot of the pager. */
    fun dotLabel(index: Int, count: Int, title: String): String = "Show featured item ${index + 1} of $count: $title"
}
