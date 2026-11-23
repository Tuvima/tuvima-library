package com.tuvima.library.core

import java.util.Locale

// "Edit" in the "..." menu of a title's Details page (spec native-apps D7 and D14): version 1 of the phone has no editor of its own, so Edit opens the
// Dashboard in the phone's browser on that title's own page, where the Dashboard's "..." menu has its Edit. The link is only the page: a deep link
// never opens the editor (CLAUDE.md, universal editor rules), and nothing about a sign-in travels with it (audit decision 3: open the page and sign
// in there if asked). A Kids profile has no Edit. Pure rules; nothing here opens anything.

object EditInBrowser {
    /** The menu row's words. The Dashboard's own menu says Edit too. */
    const val LABEL = "Edit"

    private val guid = Regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")

    /**
     * Whether the menu offers Edit at all: every phone and tablet may edit (the Dashboard's `EditorDeviceRule.CanEditItems` is true for any
     * form factor except TV), a Kids profile may not, and the Engine's own permission check decides who may save once the Dashboard opens.
     * [treatAsKids] is the shell's fail-closed answer, so Edit never flashes for a child's profile while the profile list is still loading.
     */
    fun isOffered(treatAsKids: Boolean): Boolean = !treatAsKids

    /**
     * The Dashboard page of a title, or null when the page has none the Dashboard can open: a podcast, an id that is not a Library id, an episode whose
     * show is not known (the Dashboard opens an episode through its show), or a server address that is not a web address.
     * [serverAddress] is the address the app stores (the one typed at pairing).
     */
    fun pageUrl(serverAddress: String?, entityType: String, id: String, showId: String? = null): String? {
        val base = baseOf(serverAddress) ?: return null
        val type = entityType.trim().replace("-", "").replace("_", "").lowercase(Locale.ROOT)
        val cleanId = id.trim().substringBefore('?').substringBefore('#')
        if (type.isEmpty() || "podcast" in type || !isLibraryId(cleanId)) return null
        // An episode has no page of its own: the Dashboard opens its show on that episode.
        if (type == "tvepisode") {
            val show = showId?.trim()?.takeIf { isLibraryId(it) } ?: return null
            return "$base/details/tvshow/$show?episode=$cleanId"
        }
        return "$base/details/$type/$cleanId"
    }

    /** The address without a trailing slash, only when it is a web address. */
    internal fun baseOf(serverAddress: String?): String? {
        val trimmed = serverAddress?.trim()?.trimEnd('/') ?: return null
        val lower = trimmed.lowercase(Locale.ROOT)
        if (!(lower.startsWith("http://") || lower.startsWith("https://"))) return null
        if (trimmed.substringAfter("://").isBlank()) return null
        return trimmed
    }

    private fun isLibraryId(value: String): Boolean = guid.matches(value)
}
