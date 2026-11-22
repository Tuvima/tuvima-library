package com.tuvima.library.core

/**
 * The short list the Language button opens (nothing can be typed on a television). It holds the profile's own languages
 * first, then the languages of the item's tracks, then a few common ones, and never more than [MAX_CHOICES].
 * Names are English names; the Engine accepts a name or a code when it searches.
 */
object FindAnotherLanguages {
    const val MAX_CHOICES = 10

    private val COMMON = listOf(
        "English", "Spanish", "French", "German", "Italian", "Portuguese", "Japanese", "Korean", "Chinese", "Dutch",
    )

    // Two and three-letter codes to the name shown. Anything not listed is shown as given.
    private val NAMES: Map<String, String> = buildMap {
        fun add(name: String, vararg codes: String) {
            put(name.lowercase(), name)
            codes.forEach { put(it, name) }
        }
        add("English", "en", "eng")
        add("Spanish", "es", "spa")
        add("French", "fr", "fra", "fre")
        add("German", "de", "deu", "ger")
        add("Italian", "it", "ita")
        add("Portuguese", "pt", "por")
        add("Japanese", "ja", "jpn")
        add("Korean", "ko", "kor")
        add("Chinese", "zh", "zho", "chi")
        add("Dutch", "nl", "nld", "dut")
        add("Russian", "ru", "rus")
        add("Arabic", "ar", "ara")
        add("Hindi", "hi", "hin")
        add("Swedish", "sv", "swe")
        add("Polish", "pl", "pol")
        add("Turkish", "tr", "tur")
        add("Hebrew", "he", "heb")
        add("Greek", "el", "ell", "gre")
        add("Danish", "da", "dan")
        add("Finnish", "fi", "fin")
        add("Norwegian", "no", "nor")
        add("Czech", "cs", "ces", "cze")
        add("Ukrainian", "uk", "ukr")
        add("Thai", "th", "tha")
        add("Vietnamese", "vi", "vie")
        add("Indonesian", "id", "ind")
        add("Hungarian", "hu", "hun")
        add("Romanian", "ro", "ron", "rum")
    }

    /** The English name for a language code or name ("eng" gives "English"); an unknown value comes back as given. */
    fun nameFor(code: String?): String {
        val value = code?.trim().orEmpty()
        if (value.isEmpty() || value.equals("und", ignoreCase = true)) return "Unknown"
        val base = value.substringBefore('-').substringBefore('_').substringBefore(' ').lowercase()
        return NAMES[base] ?: value
    }

    /**
     * The languages to offer. [preferred] (the profile's subtitle language) is first, then [others] in order (the profile's
     * audio language, the languages of the item's tracks), then the common ones; each language appears once.
     */
    fun choices(preferred: String?, others: List<String?> = emptyList()): List<String> {
        val seen = LinkedHashSet<String>()
        val result = ArrayList<String>()
        fun add(value: String?) {
            if (value.isNullOrBlank() || value.equals("und", ignoreCase = true)) return
            val name = nameFor(value)
            if (seen.add(name.lowercase())) result.add(name)
        }
        add(preferred)
        others.forEach(::add)
        COMMON.forEach(::add)
        return result.take(MAX_CHOICES)
    }
}
