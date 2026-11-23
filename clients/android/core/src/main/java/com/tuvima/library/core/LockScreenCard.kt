package com.tuvima.library.core

// What the phone's lock screen, notification shade and media overlay show and which of their buttons work (spec native-apps WP10b, the contract's
// "Lock screen and system controls"). The operating system draws the card; the app supplies the lines and the actions. Pure rules: the media
// service turns [LockScreenCard] into a media item's metadata and the player's available commands.

/** The system's own action names, as the contract's lock-screen table writes them. */
object LockAction {
    const val PLAY = "play"
    const val PAUSE = "pause"
    const val SEEK_BACKWARD = "seekbackward"
    const val SEEK_FORWARD = "seekforward"
    const val PREVIOUS_TRACK = "previoustrack"
    const val NEXT_TRACK = "nexttrack"
    const val SEEK_TO = "seekto"
}

/**
 * What one card carries. [visible] false means the card shows nothing at all (a Vault item while Vault is locked). [artworkSize] is the side of
 * the square cover or the width of the still the app asks the Engine for: 512 for the card, 1024 for the larger lock-screen picture.
 */
data class LockScreenCard(
    val visible: Boolean,
    val title: String?,
    val line2: String?,
    val actions: List<String>,
) {
    companion object {
        /** A card with no metadata: the system shows nothing rather than the title. */
        val Hidden = LockScreenCard(visible = false, title = null, line2 = null, actions = emptyList())

        /** The artwork sizes the card asks for, in pixels (contract: "at 512 and 1024 pixels"). */
        val ARTWORK_SIZES = listOf(512, 1024)
    }
}

/** What the card is built from. Anything the Engine did not say is null and simply leaves its words out. */
data class LockScreenFacts(
    val kind: TvMediaKind,
    val title: String?,
    val artist: String? = null,
    val album: String? = null,
    val author: String? = null,
    val chapterTitle: String? = null,
    val show: String? = null,
    val season: Int? = null,
    val episode: Int? = null,
    val year: String? = null,
    val hasNext: Boolean = false,
    val hasChapters: Boolean = false,
    /** True when the profile's Spoilers setting hides episode titles. The phone has no read for it yet, so the service passes false. */
    val hideEpisodeTitle: Boolean = false,
    /** True for an item in the Vault while Vault is locked. The apps cannot reach Vault today, so the service passes false. */
    val vaultLocked: Boolean = false,
)

object LockScreen {
    private const val SEP = " · "

    /** "S1 E1": the season and episode as the contract writes them; a part with no number is left out. */
    fun episodeCode(season: Int?, episode: Int?): String? {
        val s = season?.takeIf { it > 0 }?.let { "S$it" }
        val e = episode?.takeIf { it > 0 }?.let { "E$it" }
        return when {
            s != null && e != null -> "$s $e"
            else -> s ?: e
        }
    }

    /**
     * The second line, the same as the bar's: artist · album (music), author · chapter name (audiobook), show · S1 E1 (episode) and year · Movie
     * (film). A part the Engine did not say is left out together with its separator.
     */
    fun line2(facts: LockScreenFacts): String? {
        val parts = when (facts.kind) {
            TvMediaKind.Music -> listOf(facts.artist, facts.album)
            TvMediaKind.Audiobook -> listOf(facts.author, facts.chapterTitle)
            TvMediaKind.Episode -> listOf(facts.show, episodeCode(facts.season, facts.episode))
            TvMediaKind.Lesson -> listOf(facts.show, facts.episode?.takeIf { it > 0 }?.toString())
            TvMediaKind.Film -> listOf(facts.year, "Movie")
        }
        return parts.mapNotNull { it?.trim()?.takeIf(String::isNotEmpty) }.joinToString(SEP).takeIf { it.isNotEmpty() }
    }

    /**
     * Line 1 is the title. A hidden episode title (the Spoilers setting) is never shown: the card then names the episode by its number alone, and
     * the second line never carries it either.
     */
    fun line1(facts: LockScreenFacts): String? {
        val title = facts.title?.trim()?.takeIf(String::isNotEmpty)
        if (facts.kind == TvMediaKind.Episode && facts.hideEpisodeTitle) return episodeCode(facts.season, facts.episode) ?: facts.show
        return title
    }

    /**
     * The actions per media (the contract's lock-screen table): music has previous and next track; an audiobook adds the two skips and uses
     * previous and next for chapters (only when it has chapters, otherwise they are left out); an episode has the skips and Next only when another
     * episode follows; a film has the skips alone. Every media has play, pause and seekto.
     */
    fun actions(kind: TvMediaKind, hasNext: Boolean, hasChapters: Boolean): List<String> = buildList {
        add(LockAction.PLAY)
        add(LockAction.PAUSE)
        when (kind) {
            TvMediaKind.Music -> {
                add(LockAction.PREVIOUS_TRACK)
                add(LockAction.NEXT_TRACK)
                add(LockAction.SEEK_TO)
            }
            TvMediaKind.Audiobook -> {
                add(LockAction.SEEK_BACKWARD)
                add(LockAction.SEEK_FORWARD)
                if (hasChapters) {
                    add(LockAction.PREVIOUS_TRACK)
                    add(LockAction.NEXT_TRACK)
                }
                add(LockAction.SEEK_TO)
            }
            TvMediaKind.Episode, TvMediaKind.Lesson -> {
                add(LockAction.SEEK_BACKWARD)
                add(LockAction.SEEK_FORWARD)
                add(LockAction.SEEK_TO)
                if (hasNext) add(LockAction.NEXT_TRACK)
            }
            TvMediaKind.Film -> {
                add(LockAction.SEEK_BACKWARD)
                add(LockAction.SEEK_FORWARD)
                add(LockAction.SEEK_TO)
            }
        }
    }

    /** The whole card. A Vault item sends no metadata at all while Vault is locked: the card shows nothing rather than the title. */
    fun card(facts: LockScreenFacts): LockScreenCard {
        if (facts.vaultLocked) return LockScreenCard.Hidden
        return LockScreenCard(
            visible = true,
            title = line1(facts),
            line2 = line2(facts),
            actions = actions(facts.kind, facts.hasNext, facts.hasChapters),
        )
    }
}
