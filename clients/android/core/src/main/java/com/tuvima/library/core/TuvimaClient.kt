package com.tuvima.library.core

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.URI
import java.net.URLEncoder
import java.net.URL
import java.nio.charset.StandardCharsets
import java.io.File
import java.io.FileOutputStream

class TuvimaClient(
    serverAddress: String,
    private val clientId: String,
    private val clientName: String,
    private val clientVersion: String,
    private val deviceName: String,
    private val deviceClass: String,
    private val capabilities: ClientCapabilities,
    private val tokenStore: SecureTokenStore,
) {
    val serverOrigin: String = normalizeOrigin(serverAddress)
    private val refreshMutex = Mutex()
    private val session = SessionHolder(tokenStore.load()?.takeIf { it.first == serverOrigin }?.second)

    /**
     * Called (on a background thread) after the Engine refused to refresh this device's sign-in and the tokens were cleared.
     * The app clears its per-profile offline data and returns to pairing.
     */
    @Volatile
    var onSessionRevoked: (() -> Unit)? = null
    private var discovery: Discovery? = null

    suspend fun discover(): Discovery {
        val json = requestJson("GET", "/.well-known/tuvima", authenticated = false)
        val value = Discovery(
            serverName = json.optString("server_name", "Tuvima Library"),
            apiBaseUrl = json.getString("api_base_url"),
            deviceAuthorizationEndpoint = json.getString("device_authorization_endpoint"),
            tokenEndpoint = json.getString("token_endpoint"),
            verificationUri = json.getString("verification_uri"),
            supportedApiVersions = json.optJSONArray("supported_api_versions")?.strings().orEmpty(),
            capabilities = json.optJSONArray("capabilities")?.strings().orEmpty().toSet(),
        )
        require("1" in value.supportedApiVersions) { "This server does not advertise API v1." }
        discovery = value
        return value
    }

    suspend fun beginPairing(scopes: String): PairingSession {
        val endpoints = discovery ?: discover()
        val body = JSONObject()
            .put("client_id", clientId)
            .put("client_name", clientName)
            .put("client_version", clientVersion)
            .put("device_name", deviceName)
            .put("device_class", deviceClass)
            .put("scope", scopes)
            .put("capabilities", capabilities.toJson())
        val json = requestJson("POST", endpoints.deviceAuthorizationEndpoint, body, authenticated = false)
        return PairingSession(
            deviceCode = json.getString("device_code"),
            userCode = json.getString("user_code"),
            verificationUri = json.getString("verification_uri"),
            verificationUriComplete = json.getString("verification_uri_complete"),
            expiresInSeconds = json.getInt("expires_in"),
            pollingIntervalSeconds = json.getInt("interval"),
        )
    }

    suspend fun pollPairing(session: PairingSession): PairingPollResult {
        val endpoint = (discovery ?: discover()).tokenEndpoint
        return try {
            val json = requestJson(
                "POST",
                endpoint,
                JSONObject()
                    .put("grant_type", DEVICE_GRANT)
                    .put("client_id", clientId)
                    .put("device_code", session.deviceCode),
                authenticated = false,
            )
            PairingPollResult.Authorized(acceptToken(json))
        } catch (error: TuvimaHttpException) {
            val json = runCatching { JSONObject(error.responseBody) }.getOrDefault(JSONObject())
            val code = json.optString("error", "request_failed")
            if (code == "authorization_pending" || code == "slow_down") {
                PairingPollResult.Pending(json.optInt("interval", session.pollingIntervalSeconds))
            } else {
                PairingPollResult.Failed(code, json.optionalString("error_description"))
            }
        }
    }

    suspend fun home(): DisplayPage = parseDisplayPage(requestJson("GET", "/api/v1/display/home"))

    suspend fun continuePage(lane: String): DisplayPage =
        parseDisplayPage(requestJson("GET", "/api/v1/display/continue?lane=${encode(lane)}"))

    suspend fun browse(lane: String, mediaType: String? = null, query: String? = null): DisplayPage {
        val parameters = buildList {
            add("lane=${encode(lane)}")
            mediaType?.let { add("mediaType=${encode(it)}") }
            query?.takeIf(String::isNotBlank)?.let { add("q=${encode(it)}") }
        }
        return parseDisplayPage(requestJson("GET", "/api/v1/display/browse?${parameters.joinToString("&")}"))
    }

    suspend fun search(query: String): JSONObject =
        requestJson("GET", "/api/v1/display/search?q=${encode(query)}")

    suspend fun details(entityType: String, id: String): JSONObject =
        requestJson("GET", "/api/v1/details/${encode(entityType)}/${encode(id)}")

    suspend fun playbackManifest(assetId: String, connectionPath: String): PlaybackManifest {
        val json = requestJson(
            "GET",
            "/api/v1/playback/${encode(assetId)}/manifest?connectionPath=${encode(connectionPath)}",
        )
        val resume = json.optJSONObject("resume")
        return PlaybackManifest(
            assetId = json.getString("assetId"),
            recommendedDelivery = json.getString("recommendedDelivery"),
            directPlaySupported = json.optBoolean("directPlaySupported"),
            directStreamUrl = absolute(json.optionalString("directStreamUrl")),
            hlsUrl = absolute(json.optionalString("hlsUrl")),
            hlsStatus = json.optionalString("hlsStatus"),
            hlsExpiresAt = json.optionalString("hlsExpiresAt"),
            resumeSeconds = resume?.optDouble("positionSeconds")?.takeUnless(Double::isNaN),
            durationSeconds = resume?.optDouble("durationSeconds")?.takeUnless(Double::isNaN),
            offlineVariants = json.optJSONArray("offlineVariants")?.objects { variant ->
                OfflineVariant(
                    id = variant.getString("id"),
                    assetId = variant.getString("assetId"),
                    status = variant.getString("status"),
                    downloadUrl = absolute(variant.optionalString("downloadUrl")),
                    fileSizeBytes = variant.optLong("fileSizeBytes").takeIf { it > 0 },
                    profileKey = variant.optionalString("profileKey"),
                    expiresAtMs = OfflineLogic.parseIsoMillis(variant.optionalString("expiresAt")),
                )
            }.orEmpty(),
            warnings = json.optJSONArray("warnings")?.strings().orEmpty(),
        )
    }

    suspend fun heartbeat(payload: JSONObject): JSONObject =
        requestJson("POST", "/api/v1/player/heartbeat", payload)

    suspend fun playerState(): JSONObject = requestJson("GET", "/api/v1/player/state")

    suspend fun replaceQueue(payload: JSONObject): JSONObject =
        requestJson("POST", "/api/v1/player/queue/replace", payload)

    suspend fun requestOfflineVariant(assetId: String, profileKey: String = "mobile-standard"): JSONObject =
        requestJson(
            "POST",
            "/api/v1/playback/${encode(assetId)}/encode",
            JSONObject().put("profileKey", profileKey),
        )

    suspend fun encodeJobs(): JSONArray = requestArray("GET", "/api/v1/playback/encode/jobs")

    // ---- Offline downloads: the device download list, next episodes and offline progress replay ----

    /** What the Engine believes this device stores. A variant missing from it has expired on the Engine. */
    suspend fun deviceDownloads(): List<ServerDownloadEntry> =
        parseDeviceDownloads(requestJson("GET", ClientRoutes.DEVICE_DOWNLOADS))

    /** Tells the Engine this device now stores a variant. Returns the HTTP status; 204 is success. */
    suspend fun reportDownloadStored(assetId: String, variantId: String, storedBytes: Long, storedAtMs: Long): Int =
        requestStatus(
            "POST",
            ClientRoutes.DEVICE_DOWNLOADS,
            JSONObject()
                .put("asset_id", assetId)
                .put("variant_id", variantId)
                .put("stored_bytes", storedBytes)
                .put("stored_at", OfflineLogic.formatIsoUtc(storedAtMs)),
        )

    /** Tells the Engine this device no longer stores a variant. Returns the HTTP status; 204 is success. */
    suspend fun reportDownloadDeleted(variantId: String): Int =
        requestStatus("DELETE", ClientRoutes.deviceDownload(variantId), null)

    /**
     * Tells the Engine this device keeps a photo (its 2048 px preview). Returns the HTTP status; 204 is success, 404 means
     * the photo is gone or this person cannot see it, 403 means the token has no photo access, 409 the list is full.
     */
    suspend fun reportPhotoStored(itemId: String, storedBytes: Long, storedAtMs: Long): Int =
        requestStatus(
            "POST",
            ClientRoutes.DEVICE_DOWNLOADS,
            JSONObject()
                .put("kind", OfflineKind.Photo.wire)
                .put("photo_item_id", itemId)
                .put("stored_bytes", storedBytes)
                .put("stored_at", OfflineLogic.formatIsoUtc(storedAtMs)),
        )

    /** Tells the Engine this device no longer keeps a photo. Returns the HTTP status; 204 is success, 404 means it was not listed. */
    suspend fun reportPhotoDeleted(itemId: String): Int =
        requestStatus("DELETE", ClientRoutes.devicePhotoDownload(itemId), null)

    /** The owned episodes that follow [afterAssetId] in the show's watch order. */
    suspend fun nextEpisodes(showId: String, afterAssetId: String, count: Int = 3): NextEpisodes =
        parseNextEpisodes(requestJson("GET", ClientRoutes.nextEpisodes(showId, afterAssetId, count)), ::absolute)

    /** The Engine's progress for an asset, or null when it has none (HTTP 404). */
    suspend fun serverProgress(assetId: String): ServerProgress? = try {
        parseServerProgress(requestJson("GET", ClientRoutes.progress(assetId)))
    } catch (error: TuvimaHttpException) {
        if (error.statusCode == 404) null else throw error
    }

    /**
     * Replays progress recorded offline (`offline: true` with `client_timestamp`). The Engine keeps the later of its own
     * record and this one and answers 200 with the state that stands; [OfflineLogic.isServerWon] tells which it was.
     */
    suspend fun replayOfflineProgress(entry: OfflineProgressEntry, expectedRevision: Long): ServerProgress {
        val recordedAt = OfflineLogic.formatIsoUtc(entry.recordedAtMs)
        val extended = JSONObject()
            .put("position_seconds", OfflineLogic.formatSeconds(entry.positionSeconds))
            .put("playback_state", "paused")
            .put("player_client", "tuvima-android")
            .put("updated_at", recordedAt)
        entry.durationSeconds?.let { extended.put("duration_seconds", OfflineLogic.formatSeconds(it)) }
        val body = JSONObject()
            .put("progress_pct", entry.progressPct)
            .put("expected_revision", expectedRevision)
            .put("offline", true)
            .put("client_timestamp", recordedAt)
            .put("extended_properties", extended)
        return parseServerProgress(requestJson("PUT", ClientRoutes.progress(entry.assetId), body))
    }

    // ---- App screens: Home, Libraries, Details, View, Collections, profiles (all under /api/v1) ----

    /** The Home shelves without the full catalogue, which a phone does not need. */
    suspend fun homeShelves(): DisplayPage =
        parseDisplayPage(requestJson("GET", ClientRoutes.home(includeCatalog = false)))

    /** Continue across every lane, most recent first. */
    suspend fun continueAll(limit: Int = 24): DisplayPage =
        parseDisplayPage(requestJson("GET", ClientRoutes.continueAll(limit)))

    /** The cards of one group or collection; they carry the asset ids that a collection item list does not. */
    suspend fun groupCards(collectionId: String): DisplayPage =
        parseDisplayPage(requestJson("GET", ClientRoutes.group(collectionId)))

    /**
     * Work id to the file that plays it, for the works of one collection. A collection's own item list names works
     * but not files; the group's cards carry both. Empty when the group cannot be read.
     */
    suspend fun playableAssetsByWork(collectionId: String): Map<String, String> {
        val page = runCatching { groupCards(collectionId) }.getOrNull() ?: return emptyMap()
        return mapPlayableAssets(page.shelves.flatMap { it.items })
    }

    /** One page of a library lane with the Dashboard's sort and filters applied by the Engine. */
    suspend fun browsePage(query: BrowseQuery): BrowsePage {
        val json = requestJson("GET", ClientRoutes.browse(query))
        val catalog = json.optJSONArray("catalog")?.objects(::parseCard).orEmpty()
        val cards = catalog.ifEmpty {
            json.optJSONArray("shelves")?.objects(::parseShelf).orEmpty().flatMap { it.items }
        }
        val facets = json.optJSONObject("facets")
        return BrowsePage(
            cards = cards,
            totalCount = json.optInt("totalCount", cards.size),
            genres = facets?.optJSONArray("genres")?.strings().orEmpty(),
            creators = facets?.optJSONArray("creators")?.strings().orEmpty(),
            years = facets?.optJSONArray("years")?.strings().orEmpty(),
        )
    }

    suspend fun detailPage(entityType: String, id: String): DetailModel =
        parseDetailModel(requestJson("GET", ClientRoutes.details(entityType, id)), ::absolute)

    /** The newest-first photo and video timeline. Throws [PhotoAccessDeniedException] when photos were not allowed. */
    suspend fun viewTimeline(cursor: String? = null, limit: Int = 100): ViewTimelinePage =
        photoCall { parseViewTimelinePage(requestJson("GET", ClientRoutes.viewTimeline(cursor, limit))) }

    suspend fun viewGalleries(): ViewGalleries =
        photoCall { parseViewGalleries(requestJson("GET", ClientRoutes.VIEW_GALLERIES)) }

    suspend fun viewGalleryItems(galleryId: String, cursor: String? = null, limit: Int = 100): ViewItemPage =
        photoCall { parseViewItemPage(requestJson("GET", ClientRoutes.viewGalleryItems(galleryId, cursor, limit))) }

    suspend fun viewItem(id: String): ViewItem =
        photoCall { parseViewItem(requestJson("GET", ClientRoutes.viewItem(id))) }

    /** JPEG bytes of a thumbnail; [size] snaps to 160, 320 or 640. */
    suspend fun viewThumbnail(id: String, size: Int = 320): ByteArray =
        photoCall { authenticatedBytes(ClientRoutes.viewThumbnail(id, size)) }

    /** JPEG bytes of a larger preview; [size] snaps to 1280 or 2048. */
    suspend fun viewPreview(id: String, size: Int = 2048): ByteArray =
        photoCall { authenticatedBytes(ClientRoutes.viewPreview(id, size)) }

    /** The Collections catalogue the Dashboard shows, one page at a time. */
    suspend fun collectionsPage(cursor: String? = null, limit: Int = 24): CollectionsPage =
        parseCollectionsPage(requestJson("GET", ClientRoutes.collections(cursor, limit)), ::absolute)

    suspend fun collectionItems(collectionId: String, cursor: String? = null, limit: Int = 24): CollectionItemsPage =
        parseCollectionItemsPage(
            requestJson("GET", ClientRoutes.collectionItems(collectionId, cursor, limit)),
            ::absolute,
        )

    /** A Universe by Wikidata QID, with its member shelves. */
    suspend fun universe(qid: String): UniverseDetail {
        val json = requestJson("GET", ClientRoutes.universe(qid))
        return UniverseDetail(
            qid = json.optString("qid", qid),
            name = json.optString("name", qid),
            description = json.optionalString("description"),
            itemCount = json.optInt("item_count"),
            shelves = json.optJSONArray("shelves")?.objects { shelf ->
                UniverseShelf(
                    collectionId = shelf.optString("collection_id"),
                    groupType = shelf.optString("group_type", "series"),
                    itemCount = shelf.optInt("item_count"),
                    shelf = parseShelf(shelf.optJSONObject("shelf") ?: JSONObject()),
                )
            }.orEmpty(),
        )
    }

    /** The people this paired app may switch to. */
    suspend fun deviceProfiles(): List<DeviceProfile> =
        parseDeviceProfiles(requestJson("GET", ClientRoutes.DEVICE_PROFILES))

    /**
     * Switches this app to another person without re-pairing. On success the client holds and stores the new
     * tokens; each refusal (PIN needed 428, not allowed 403, already active 409, locked 429) is a typed result.
     */
    suspend fun switchProfile(profileId: String, pin: String? = null): SwitchProfileResult {
        val body = JSONObject().put("profile_id", profileId)
        if (!pin.isNullOrBlank()) body.put("pin", pin)
        // While the switch is pending, a refused refresh is never taken for a revocation: the Engine's switch revokes every
        // token of the device, so a refresh sent with the previous profile's refresh token is refused with invalid_grant.
        session.beginSwitch()
        var finished = false
        return try {
            val answer = requestJson("POST", ClientRoutes.SWITCH_PROFILE, body)
            val value = parseToken(answer)
            // Taken under the refresh lock (not held during the request, which may itself refresh): a refresh in flight
            // for the previous profile finishes first, so its result can never overwrite the new profile's token. Nothing
            // is stored when the device was signed out while the switch was in flight.
            val stored = refreshMutex.withLock {
                finished = true
                session.finishSwitch(value) { tokenStore.save(serverOrigin, it) }
            }
            if (stored) {
                SwitchProfileResult.Switched(value)
            } else {
                SwitchProfileResult.Failed(0, "signed_out", "This device was signed out while the profile was being switched.")
            }
        } catch (error: TuvimaHttpException) {
            val json = runCatching { JSONObject(error.responseBody) }.getOrDefault(JSONObject())
            classifySwitchFailure(
                error.statusCode,
                json.optionalString("error"),
                json.optionalString("error_description"),
            )
        } finally {
            if (!finished) session.finishSwitch(null)
        }
    }

    /** Removes this device on the server (all its tokens stop working at once). Call [signOut] afterwards. */
    suspend fun revokeCurrentDevice() {
        val deviceId = session.current()?.deviceId ?: error("This device is not paired.")
        requestText("DELETE", ClientRoutes.revokeDevice(deviceId), null, authenticated = true)
    }

    fun currentProfileId(): String? = session.current()?.profileId

    /** The signed-in profile plus a change counter; background work binds to this (see [SyncGuard]) and compares it later. */
    fun profileBinding(): ProfileBinding? = session.binding()

    /** True when the device was given the photo scopes at pairing. */
    fun hasPhotoAccess(): Boolean = session.current()?.let { PhotoScopes.grantedIn(it.scope) } ?: false

    private suspend fun <T> photoCall(block: suspend () -> T): T = try {
        block()
    } catch (error: TuvimaHttpException) {
        throw if (error.statusCode == 403) PhotoAccessDeniedException() else error
    }

    suspend fun authenticatedBytes(path: String): ByteArray = withContext(Dispatchers.IO) {
        val connection = openConnection(path, "GET", authenticatedToken())
        try {
            checkResponse(connection)
            connection.inputStream.use { it.readBytes() }
        } finally {
            connection.disconnect()
        }
    }

    suspend fun currentAccessToken(): String = authenticatedToken()

    fun isPaired(): Boolean = session.current() != null

    suspend fun downloadToFile(path: String, destination: File, onProgress: (Long, Long?) -> Unit = { _, _ -> }): Unit =
        withContext(Dispatchers.IO) {
            destination.parentFile?.mkdirs()
            val existing = destination.takeIf(File::exists)?.length() ?: 0L
            var connection = openConnection(path, "GET", authenticatedToken()).apply {
                if (existing > 0) setRequestProperty("Range", "bytes=$existing-")
            }
            try {
                if (connection.responseCode == HttpURLConnection.HTTP_UNAUTHORIZED) {
                    connection.disconnect()
                    connection = openConnection(path, "GET", refreshToken(force = true)).apply {
                        if (existing > 0) setRequestProperty("Range", "bytes=$existing-")
                    }
                }
                // 416: the file on disk already holds every byte the Engine has.
                if (existing > 0 && connection.responseCode == HTTP_RANGE_NOT_SATISFIABLE) return@withContext
                checkResponse(connection)
                val append = existing > 0 && connection.responseCode == HttpURLConnection.HTTP_PARTIAL
                val initial = if (append) existing else 0L
                val total = connection.getHeaderField("Content-Range")
                    ?.substringAfterLast('/')?.toLongOrNull()
                    ?: connection.contentLengthLong.takeIf { it >= 0 }?.plus(initial)
                FileOutputStream(destination, append).use { output ->
                    connection.inputStream.use { input ->
                        val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
                        var copied = initial
                        while (true) {
                            val count = input.read(buffer)
                            if (count < 0) break
                            // A paused or stopped download must stop at once, not after the file ends.
                            ensureActive()
                            output.write(buffer, 0, count)
                            copied += count
                            onProgress(copied, total)
                        }
                    }
                }
            } finally {
                connection.disconnect()
            }
        }

    fun signOut() {
        // The token store is cleared in the same critical section, so a late save cannot bring the sign-in back.
        session.clear { tokenStore.clear() }
    }

    /** Sends a request and returns only the HTTP status (no body is read); a 401 is retried once with a fresh token. */
    private suspend fun requestStatus(method: String, path: String, body: JSONObject?): Int =
        withContext(Dispatchers.IO) {
            var connection = openConnection(path, method, authenticatedToken(), body)
            try {
                if (connection.responseCode == HttpURLConnection.HTTP_UNAUTHORIZED) {
                    connection.disconnect()
                    connection = openConnection(path, method, refreshToken(force = true), body)
                }
                connection.responseCode
            } finally {
                connection.disconnect()
            }
        }

    private suspend fun requestJson(
        method: String,
        path: String,
        body: JSONObject? = null,
        authenticated: Boolean = true,
    ): JSONObject = JSONObject(requestText(method, path, body, authenticated))

    private suspend fun requestArray(method: String, path: String): JSONArray =
        JSONArray(requestText(method, path, null, authenticated = true))

    private suspend fun requestText(
        method: String,
        path: String,
        body: JSONObject?,
        authenticated: Boolean,
    ): String = withContext(Dispatchers.IO) {
        val credential = if (authenticated) authenticatedToken() else null
        var connection = openConnection(path, method, credential, body)
        try {
            if (connection.responseCode == HttpURLConnection.HTTP_UNAUTHORIZED && authenticated) {
                connection.disconnect()
                connection = openConnection(path, method, refreshToken(force = true), body)
            }
            checkResponse(connection)
            connection.inputStream.bufferedReader().use { it.readText() }
        } finally {
            connection.disconnect()
        }
    }

    /** Refuses to go on when the calling work is bound to a profile that is no longer the one signed in. */
    private suspend fun requireBoundProfile() {
        val bound = currentCoroutineContext()[BoundProfile] ?: return
        if (profileBinding() != bound.binding) throw ProfileChangedException()
    }

    private suspend fun authenticatedToken(): String {
        requireBoundProfile()
        val current = session.current() ?: error("This device is not paired.")
        return if (current.expiresSoon()) refreshToken() else current.accessToken
    }

    private suspend fun refreshToken(force: Boolean = false): String = refreshMutex.withLock {
        requireBoundProfile()
        // The token, its binding and the switch generation in one atomic read: the refresh result is stored only if the
        // session still matches, and a refusal is judged against the switch generation seen here.
        val snapshot = session.snapshot() ?: error("This device is not paired.")
        val current = snapshot.token
        if (!force && !current.expiresSoon()) return@withLock current.accessToken
        val endpoint = (discovery ?: discover()).tokenEndpoint
        val json = try {
            requestJson(
                "POST",
                endpoint,
                JSONObject()
                    .put("grant_type", "refresh_token")
                    .put("client_id", clientId)
                    .put("refresh_token", current.refreshToken),
                authenticated = false,
            )
        } catch (error: TuvimaHttpException) {
            val code = runCatching { JSONObject(error.responseBody).optString("error") }.getOrNull()?.takeIf { it.isNotEmpty() }
            if (SessionLogic.classifyRefreshFailure(error.statusCode, code) == RefreshFailureKind.Revoked) {
                // The device was revoked or its grant changed: drop the sign-in and tell the app, which clears the offline data.
                // Not when a profile switch is pending or happened since this attempt began (the Engine's switch revokes the
                // old tokens, so the refusal is expected and transient: the next refresh uses the new profile's token), and
                // only when this is still the token that was refused. The check and the drop are one step under the lock.
                if (session.clearIfRevoked(snapshot) { tokenStore.clear() }) {
                    onSessionRevoked?.invoke()
                    throw SessionRevokedException()
                }
            }
            throw error
        }
        acceptToken(json, snapshot.binding).accessToken
    }

    private fun acceptToken(json: JSONObject, expected: ProfileBinding? = null): ClientToken {
        val value = parseToken(json)
        // Stored and saved in one critical section, so a sign-out can never be undone by a late save.
        session.accept(value, expected) { tokenStore.save(serverOrigin, it) }
        return value
    }

    private fun parseToken(json: JSONObject): ClientToken {
        return ClientToken(
            accessToken = json.getString("access_token"),
            refreshToken = json.getString("refresh_token"),
            expiresAtEpochSeconds = System.currentTimeMillis() / 1000 + json.getLong("expires_in"),
            scope = json.getString("scope"),
            deviceId = json.getString("device_id"),
            profileId = json.getString("profile_id"),
        )
    }

    private fun openConnection(
        path: String,
        method: String,
        bearer: String?,
        body: JSONObject? = null,
    ): HttpURLConnection = (URL(absolute(path) ?: error("A request path is required.")).openConnection() as HttpURLConnection).apply {
        requestMethod = method
        connectTimeout = 15_000
        readTimeout = 30_000
        setRequestProperty("Accept", "application/json")
        bearer?.let { setRequestProperty("Authorization", "Bearer $it") }
        if (body != null) {
            doOutput = true
            setRequestProperty("Content-Type", "application/json; charset=utf-8")
            outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
        }
    }

    private fun checkResponse(connection: HttpURLConnection) {
        if (connection.responseCode in 200..299) return
        val body = connection.errorStream?.bufferedReader()?.use { it.readText() }.orEmpty()
        throw TuvimaHttpException(connection.responseCode, body)
    }

    private fun parseDisplayPage(json: JSONObject): DisplayPage = DisplayPage(
        key = json.optString("key"),
        title = json.optString("title", "Tuvima Library"),
        subtitle = json.optionalString("subtitle"),
        shelves = json.optJSONArray("shelves")?.objects(::parseShelf).orEmpty(),
    )

    private fun parseShelf(json: JSONObject): DisplayShelf = DisplayShelf(
        key = json.optString("key"),
        title = json.optString("title"),
        subtitle = json.optionalString("subtitle"),
        items = json.optJSONArray("items")?.objects(::parseCard).orEmpty(),
        seeAllRoute = json.optionalString("seeAllRoute"),
    )

    private fun parseCard(json: JSONObject): DisplayCard {
        val artwork = json.optJSONObject("artwork") ?: JSONObject()
        val progress = json.optJSONObject("progress")
        return DisplayCard(
            id = json.optString("id"),
            workId = json.optionalString("workId"),
            assetId = json.optionalString("assetId"),
            collectionId = json.optionalString("collectionId"),
            mediaType = json.optString("mediaType"),
            title = json.optString("title"),
            subtitle = json.optionalString("subtitle"),
            facts = json.optJSONArray("facts")?.strings().orEmpty(),
            description = json.optionalString("description"),
            artworkUrl = absolute(
                artwork.optionalString("backgroundLargeUrl")
                    ?: artwork.optionalString("coverLargeUrl")
                    ?: artwork.optionalString("squareLargeUrl")
                    ?: artwork.optionalString("backgroundUrl")
                    ?: artwork.optionalString("coverUrl")
                    ?: artwork.optionalString("squareUrl"),
            ),
            actions = json.optJSONArray("actions")?.objects { action ->
                DisplayAction(
                    type = action.optString("type"),
                    label = action.optString("label"),
                    workId = action.optionalString("workId"),
                    assetId = action.optionalString("assetId"),
                    collectionId = action.optionalString("collectionId"),
                    webUrl = action.optionalString("webUrl"),
                )
            }.orEmpty(),
            progressPercent = progress?.optDouble("percent")?.takeUnless(Double::isNaN),
            posterUrl = absolute(
                artwork.optionalString("coverLargeUrl")
                    ?: artwork.optionalString("coverUrl")
                    ?: artwork.optionalString("squareLargeUrl")
                    ?: artwork.optionalString("squareUrl"),
            ),
        )
    }

    private fun absolute(path: String?): String? {
        if (path.isNullOrBlank()) return null
        val uri = URI(path)
        return if (uri.isAbsolute) uri.toString() else URI(serverOrigin).resolve(path).toString()
    }

    private companion object {
        const val DEVICE_GRANT = "urn:ietf:params:oauth:grant-type:device_code"
        const val HTTP_RANGE_NOT_SATISFIABLE = 416

        fun normalizeOrigin(address: String): String {
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

        fun encode(value: String): String = URLEncoder.encode(value, StandardCharsets.UTF_8.toString())
    }
}

internal fun <T> JSONArray.objects(mapper: (JSONObject) -> T): List<T> = buildList {
    for (index in 0 until length()) optJSONObject(index)?.let { add(mapper(it)) }
}
