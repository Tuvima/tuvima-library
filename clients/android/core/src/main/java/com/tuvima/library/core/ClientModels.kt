package com.tuvima.library.core

import org.json.JSONArray
import org.json.JSONObject

data class Discovery(
    val serverName: String,
    val apiBaseUrl: String,
    val deviceAuthorizationEndpoint: String,
    val tokenEndpoint: String,
    val verificationUri: String,
    val supportedApiVersions: List<String>,
    val capabilities: Set<String>,
    /** The address the server announced for use away from home, when the owner has set one up. */
    val outsideAddress: String? = null,
)

data class PairingSession(
    val deviceCode: String,
    val userCode: String,
    val verificationUri: String,
    val verificationUriComplete: String,
    val expiresInSeconds: Int,
    val pollingIntervalSeconds: Int,
)

data class ClientToken(
    val accessToken: String,
    val refreshToken: String,
    val expiresAtEpochSeconds: Long,
    val scope: String,
    val deviceId: String,
    val profileId: String,
) {
    fun expiresSoon(nowEpochSeconds: Long = System.currentTimeMillis() / 1000): Boolean =
        expiresAtEpochSeconds <= nowEpochSeconds + 30
}

data class ClientCapabilities(
    val containers: List<String>,
    val videoCodecs: List<String>,
    val audioCodecs: List<String>,
    val subtitleFormats: List<String> = listOf("webvtt", "vtt"),
    val protocols: List<String> = listOf("https", "http-range", "hls"),
    val maxWidth: Int? = null,
    val maxHeight: Int? = null,
    val maxBitrateKbps: Int? = null,
    val maxAudioChannels: Int? = null,
    val supportsHdr: Boolean = false,
    val supportsPlaybackSpeed: Boolean = false,
    val supportsOfflineDownloads: Boolean = false,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("schema_version", 1)
        .put("containers", JSONArray(containers))
        .put("video_codecs", JSONArray(videoCodecs))
        .put("audio_codecs", JSONArray(audioCodecs))
        .put("subtitle_formats", JSONArray(subtitleFormats))
        .put("protocols", JSONArray(protocols))
        .put("max_width", maxWidth)
        .put("max_height", maxHeight)
        .put("max_bitrate_kbps", maxBitrateKbps)
        .put("max_audio_channels", maxAudioChannels)
        .put("supports_hdr", supportsHdr)
        .put("supports_playback_speed", supportsPlaybackSpeed)
        .put("supports_offline_downloads", supportsOfflineDownloads)
}

data class DisplayPage(
    val key: String,
    val title: String,
    val subtitle: String?,
    val shelves: List<DisplayShelf>,
)

data class DisplayShelf(
    val key: String,
    val title: String,
    val subtitle: String?,
    val items: List<DisplayCard>,
    val seeAllRoute: String?,
)

data class DisplayCard(
    val id: String,
    val workId: String?,
    val assetId: String?,
    val collectionId: String?,
    val mediaType: String,
    val title: String,
    val subtitle: String?,
    val facts: List<String>,
    val description: String?,
    val artworkUrl: String?,
    val actions: List<DisplayAction>,
    val progressPercent: Double?,
    /** Portrait-first artwork for poster grids; [artworkUrl] stays landscape-first for hero tiles. */
    val posterUrl: String? = null,
)

data class DisplayAction(
    val type: String,
    val label: String,
    val workId: String?,
    val assetId: String?,
    val collectionId: String?,
    val webUrl: String?,
)

data class PlaybackManifest(
    val assetId: String,
    val recommendedDelivery: String,
    val directPlaySupported: Boolean,
    val directStreamUrl: String?,
    val hlsUrl: String?,
    val hlsStatus: String?,
    val hlsExpiresAt: String?,
    val resumeSeconds: Double?,
    val durationSeconds: Double?,
    val offlineVariants: List<OfflineVariant>,
    val warnings: List<String>,
    val audioTracks: List<AudioTrack> = emptyList(),
    val subtitleTracks: List<SubtitleTrack> = emptyList(),
    val chapters: List<Chapter> = emptyList(),
    val segments: List<PlaybackSegment> = emptyList(),
    val sourceExtension: String? = null,
    val conversionReason: String? = null,
) {
    fun playablePath(): String? = when (recommendedDelivery) {
        "hls" -> if (hlsStatus == "ready") hlsUrl else null
        "direct-stream" -> directStreamUrl
        else -> null
    }
}

data class AudioTrack(
    val index: Int,
    val language: String?,
    val codec: String?,
    val displayName: String?,
    val isDefault: Boolean,
    val channels: Int?,
)

data class SubtitleTrack(
    val index: Int,
    val language: String?,
    val codec: String?,
    val displayName: String?,
    val isDefault: Boolean,
    val isForced: Boolean,
    val deliveryUrl: String?,
)

data class Chapter(
    val index: Int,
    val title: String,
    val startSeconds: Double,
    val endSeconds: Double?,
)

/** A skippable span such as an intro or credits; [kind] is the Engine's own label. */
data class PlaybackSegment(
    val kind: String,
    val startSeconds: Double,
    val endSeconds: Double?,
    val isSkippable: Boolean = true,
    val reviewStatus: String? = null,
)

data class OfflineVariant(
    val id: String,
    val assetId: String,
    val status: String,
    val downloadUrl: String?,
    val fileSizeBytes: Long?,
    val profileKey: String? = null,
    val expiresAtMs: Long? = null,
)

sealed interface PairingPollResult {
    data class Authorized(val token: ClientToken) : PairingPollResult
    data class Pending(val retryAfterSeconds: Int) : PairingPollResult
    data class Failed(val code: String, val description: String?) : PairingPollResult
}

class TuvimaHttpException(
    val statusCode: Int,
    val responseBody: String,
) : RuntimeException("Library request failed with HTTP $statusCode")

internal fun JSONObject.optionalString(name: String): String? =
    if (isNull(name)) null else optString(name).takeIf { it.isNotBlank() }

internal fun JSONObject.optionalInt(name: String): Int? = if (isNull(name)) null else optInt(name)

internal fun JSONObject.optionalLong(name: String): Long? = if (isNull(name)) null else optLong(name)

internal fun JSONObject.optionalDouble(name: String): Double? =
    if (isNull(name)) null else optDouble(name).takeUnless(Double::isNaN)

internal fun JSONArray.strings(): List<String> = buildList {
    for (index in 0 until length()) optString(index).takeIf(String::isNotBlank)?.let(::add)
}
