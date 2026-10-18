package com.tuvima.library.core

import java.net.InetAddress
import java.net.URI

/**
 * Turns what a person typed (or what the server announced) into a server origin: scheme, host and port, nothing else.
 * Remote connections must be HTTPS; plain HTTP is accepted only for loopback, local-network and `.local` hosts.
 */
object ServerOrigins {
    fun normalize(address: String): String {
        val withScheme = address.trim().let { if (it.contains("://")) it else "https://$it" }
        val uri = URI(withScheme)
        val localHttp = uri.scheme == "http" && runCatching {
            uri.host.endsWith(".local", ignoreCase = true) || InetAddress.getByName(uri.host).let {
                it.isLoopbackAddress || it.isSiteLocalAddress || it.isLinkLocalAddress
            }
        }.getOrDefault(false)
        require(uri.scheme == "https" || localHttp) {
            "Remote Tuvima connections require HTTPS; HTTP is accepted only on the local network."
        }
        require(!uri.host.isNullOrBlank()) { "Enter a valid Tuvima Dashboard address." }
        return URI(uri.scheme, null, uri.host, uri.port, null, null, null).toString().trimEnd('/')
    }

    /** [normalize], or null for a blank or unusable address (an announced outside address is never worth failing over). */
    fun normalizeOrNull(address: String?): String? {
        if (address.isNullOrBlank()) return null
        return try {
            normalize(address)
        } catch (error: IllegalArgumentException) {
            null
        } catch (error: java.net.URISyntaxException) {
            null
        }
    }
}

/**
 * The two addresses of one server and the one in use right now.
 *
 * - [home] is the address typed at pairing. It is the server's stable identity: saved sign-in tokens are keyed to it, so
 *   changing [current] never signs anyone out.
 * - [outside] is the address the server announced for use away from home (null when the owner has not set one up).
 * - [current] is what every request is built against, read when the request is made.
 */
class ServerAddresses(val home: String, outside: String? = null) {
    private val lock = Any()

    @Volatile
    private var outsideOrigin: String? = outside?.takeUnless { it == home }

    @Volatile
    private var currentOrigin: String = home

    /** The address in use. A request reads it once, when it starts, and keeps it to the end. */
    val current: String get() = currentOrigin

    val outside: String? get() = outsideOrigin

    /**
     * Records the outside address a discovery answer announced (null or blank when it announced none); [answeredBy] is the
     * address that answer came from. An address equal to [home] is no second address.
     *
     * A new address always replaces the old one. "None announced" removes the stored address only when the answer came from
     * the home address: an answer received over the outside address that omits it (a server hiccup) must not make the
     * phone forget the only way back in while away. Returns true when the stored address changed.
     */
    fun updateOutside(announced: String?, answeredBy: String): Boolean = synchronized(lock) {
        val next = ServerOrigins.normalizeOrNull(announced)?.takeUnless { it == home }
        val previous = outsideOrigin
        if (next == null && answeredBy != home) return false
        if (next == previous) return false
        outsideOrigin = next
        if (currentOrigin == previous) currentOrigin = next ?: home
        true
    }

    /** Makes [origin] the address in use when it is one of the two known addresses. Returns true when [current] changed. */
    fun select(origin: String): Boolean = synchronized(lock) {
        if (origin != home && origin != outsideOrigin) return false
        if (origin == currentOrigin) return false
        currentOrigin = origin
        true
    }

    /**
     * [url] with the origin of either known address replaced by [current] (or by [onto]), so a URL that was built while
     * the other address was in use still reaches the server. Anything else (another host, a path) is returned as it is.
     */
    fun rebase(url: String, onto: String = currentOrigin): String {
        for (origin in listOfNotNull(home, outsideOrigin)) {
            if (origin == onto || !url.startsWith(origin)) continue
            val rest = url.substring(origin.length)
            if (rest.isEmpty() || rest[0] == '/' || rest[0] == '?' || rest[0] == '#') return onto + rest
        }
        return url
    }

    /** [path] as an absolute URL on [origin]: a server-relative path is resolved against it, an absolute URL is rebased onto it. */
    fun resolve(path: String?, origin: String = currentOrigin): String? {
        if (path.isNullOrBlank()) return null
        val uri = URI(path)
        return if (uri.isAbsolute) rebase(uri.toString(), origin) else URI(origin).resolve(path).toString()
    }
}

/** Where the outside address is remembered between launches, next to (not inside) the signed-in tokens. */
interface ServerAddressStore {
    /** The outside address saved for the server whose home address is [home], or null. */
    fun loadOutside(home: String): String?

    fun saveOutside(home: String, outside: String?)

    object None : ServerAddressStore {
        override fun loadOutside(home: String): String? = null
        override fun saveOutside(home: String, outside: String?) = Unit
    }
}

/** Which of the two addresses to use. Pure: the caller supplies the reachability check. */
object ServerAddressSelector {
    /** How long a reachability check may take before the address counts as unreachable. */
    const val PROBE_TIMEOUT_MS = 1_500

    /**
     * The home address when it answers; otherwise the outside address when it answers; otherwise [current], unchanged, so
     * nothing flaps while the phone is simply offline (the app then shows its usual "Can't reach your Tuvima server").
     * With no outside address nothing is checked at all: there is nothing to choose between.
     */
    suspend fun choose(
        home: String,
        outside: String?,
        current: String,
        isReachable: suspend (String) -> Boolean,
    ): String {
        if (outside == null || outside == home) return home
        if (isReachable(home)) return home
        if (isReachable(outside)) return outside
        return current
    }
}
