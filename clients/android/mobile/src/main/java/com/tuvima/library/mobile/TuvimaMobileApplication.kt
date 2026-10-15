package com.tuvima.library.mobile

import android.app.Application
import android.os.Build
import com.tuvima.library.core.ClientCapabilities
import com.tuvima.library.core.SecureTokenStore
import com.tuvima.library.core.StorageCap
import com.tuvima.library.core.TuvimaClient
import kotlinx.coroutines.flow.MutableStateFlow

class TuvimaMobileApplication : Application() {
    var client: TuvimaClient? = null
        private set

    override fun onCreate() {
        super.onCreate()
        savedServer()?.let(::connect)
    }

    /** Set when the Engine revoked this device's sign-in; the shell then returns to pairing and clears the flag. */
    internal val sessionRevoked = MutableStateFlow(false)

    fun connect(serverAddress: String): TuvimaClient {
        getSharedPreferences("tuvima_mobile", MODE_PRIVATE).edit().putString("server", serverAddress).apply()
        return TuvimaClient(
            serverAddress = serverAddress,
            clientId = "tuvima-android",
            clientName = "Tuvima for Android",
            clientVersion = BuildConfig.VERSION_NAME,
            deviceName = Build.MODEL,
            deviceClass = "mobile",
            capabilities = ClientCapabilities(
                containers = listOf("mp4", "m4a", "mp3", "ogg", "webm", "mpegts"),
                videoCodecs = listOf("h264", "hevc", "vp9", "av1"),
                audioCodecs = listOf("aac", "mp3", "flac", "opus", "vorbis", "ac3", "eac3"),
                maxWidth = 3840,
                maxHeight = 2160,
                maxAudioChannels = 8,
                supportsHdr = true,
                supportsPlaybackSpeed = true,
                supportsOfflineDownloads = true,
            ),
            tokenStore = SecureTokenStore(this),
        ).also { created ->
            // A revoked device behaves like a sign-out: the client has already cleared its tokens; the downloads and
            // unsent records of every person on this phone go with it, then the app returns to pairing.
            created.onSessionRevoked = {
                offline.clearAll()
                sessionRevoked.value = true
            }
            client = created
        }
    }

    fun savedServer(): String? = getSharedPreferences("tuvima_mobile", MODE_PRIVATE).getString("server", null)

    /** Whether the person ticked "photos and videos" before pairing. Off unless they opt in. */
    fun photosOptIn(): Boolean = getSharedPreferences("tuvima_mobile", MODE_PRIVATE).getBoolean("photos_opt_in", false)

    fun setPhotosOptIn(value: Boolean) {
        getSharedPreferences("tuvima_mobile", MODE_PRIVATE).edit().putBoolean("photos_opt_in", value).apply()
    }

    /** The quality profile key a new download asks for. */
    fun downloadQuality(): String =
        getSharedPreferences("tuvima_mobile", MODE_PRIVATE).getString("download_quality", null) ?: DEFAULT_DOWNLOAD_QUALITY

    fun setDownloadQuality(profileKey: String) {
        getSharedPreferences("tuvima_mobile", MODE_PRIVATE).edit().putString("download_quality", profileKey).apply()
    }

    /** How many bytes offline downloads may use on this phone; new downloads are blocked once it is reached. */
    fun storageCapBytes(): Long =
        getSharedPreferences("tuvima_mobile", MODE_PRIVATE).getLong("storage_cap_bytes", StorageCap.DEFAULT_BYTES)

    fun setStorageCapBytes(bytes: Long) {
        getSharedPreferences("tuvima_mobile", MODE_PRIVATE).edit().putLong("storage_cap_bytes", bytes).apply()
    }

    /** Offline downloads: the queue, the local files and the reports back to the Engine. */
    internal val offline: OfflineDownloads by lazy { OfflineDownloads(this) }

    companion object {
        const val DEFAULT_DOWNLOAD_QUALITY = "mobile-standard"
    }
}
