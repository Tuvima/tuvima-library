package com.tuvima.library.tv

import android.app.Application
import android.os.Build
import com.tuvima.library.core.ClientCapabilities
import com.tuvima.library.core.SecureTokenStore
import com.tuvima.library.core.TuvimaClient

class TuvimaTvApplication : Application() {
    var client: TuvimaClient? = null
        private set

    fun connect(serverAddress: String): TuvimaClient {
        getSharedPreferences("tuvima_tv", MODE_PRIVATE).edit().putString("server", serverAddress).apply()
        return TuvimaClient(
            serverAddress = serverAddress,
            clientId = "tuvima-android-tv",
            clientName = "Tuvima for Android TV",
            clientVersion = BuildConfig.VERSION_NAME,
            deviceName = Build.MODEL,
            deviceClass = "television",
            capabilities = ClientCapabilities(
                containers = listOf("mp4", "mpegts"),
                videoCodecs = listOf("h264", "hevc", "vp9", "av1"),
                audioCodecs = listOf("aac", "ac3", "eac3", "opus"),
                maxWidth = 3840,
                maxHeight = 2160,
                maxAudioChannels = 8,
                supportsHdr = true,
            ),
            tokenStore = SecureTokenStore(this),
        ).also { client = it }
    }

    fun savedServer(): String? = getSharedPreferences("tuvima_tv", MODE_PRIVATE).getString("server", null)

    /** Whether the person ticked "photos and videos" before pairing. Off unless they opt in. */
    fun photosOptIn(): Boolean = getSharedPreferences("tuvima_tv", MODE_PRIVATE).getBoolean("photos_opt_in", false)

    fun setPhotosOptIn(value: Boolean) {
        getSharedPreferences("tuvima_tv", MODE_PRIVATE).edit().putBoolean("photos_opt_in", value).apply()
    }
}
