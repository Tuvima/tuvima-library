import Foundation
import Network

public actor TuvimaAPIClient {
    public let origin: URL
    private let clientID: String
    private let clientName: String
    private let clientVersion: String
    private let deviceName: String
    private let capabilities: [String: Any]
    private let store: any TokenStoring
    private let session: URLSession
    private var discovery: Discovery?
    private var token: ClientToken?
    /// Counts profile changes and sign-outs, so background work bound to a profile can tell a switch (even a switch back).
    private var profileEpoch = 0
    /// Called after the Engine refused to refresh this device's sign-in and the tokens were cleared; the app then clears
    /// its offline data and returns to pairing.
    private let onSessionRevoked: (@Sendable () -> Void)?

    public init(
        serverAddress: String,
        clientID: String = "tuvima-ios",
        clientName: String = "Tuvima for iOS",
        clientVersion: String,
        deviceName: String,
        store: any TokenStoring = KeychainTokenStore(),
        session: URLSession? = nil,
        onSessionRevoked: (@Sendable () -> Void)? = nil
    ) throws {
        guard var components = URLComponents(string: serverAddress.contains("://") ? serverAddress : "https://\(serverAddress)"),
              let host = components.host else { throw TuvimaClientError.invalidServer }
        components.path = ""
        components.query = nil
        components.fragment = nil
        guard let origin = components.url else { throw TuvimaClientError.invalidServer }
        if origin.scheme != "https" && !Self.isLocalHost(host) { throw TuvimaClientError.insecureRemoteServer }
        self.origin = origin
        self.clientID = clientID
        self.clientName = clientName
        self.clientVersion = clientVersion
        self.deviceName = deviceName
        self.store = store
        self.onSessionRevoked = onSessionRevoked
        self.token = store.load(for: origin)
        self.capabilities = [
            "schema_version": 1,
            "containers": ["mp4", "m4a", "mp3", "mov", "mpegts"],
            "video_codecs": ["h264", "hevc"],
            "audio_codecs": ["aac", "mp3", "alac", "flac", "ac3", "eac3"],
            "subtitle_formats": ["webvtt", "vtt"],
            "protocols": ["https", "http-range", "hls"],
            "max_width": 3840,
            "max_height": 2160,
            "max_audio_channels": 8,
            "supports_hdr": true,
            "supports_playback_speed": true,
            "supports_offline_downloads": true,
        ]
        let configuration = URLSessionConfiguration.default
        configuration.waitsForConnectivity = true
        configuration.timeoutIntervalForRequest = 30
        self.session = session ?? URLSession(configuration: configuration)
    }

    public var isPaired: Bool { token != nil }

    public func discoverServer() async throws -> Discovery {
        let value: Discovery = try await get("/.well-known/tuvima", authenticated: false)
        guard value.supportedAPIVersions.contains("1") else { throw TuvimaClientError.unsupportedAPI }
        discovery = value
        return value
    }

    public func beginPairing(scopes: String) async throws -> PairingSession {
        let endpoints = try await endpoints()
        return try await post(endpoints.deviceAuthorizationEndpoint, json: [
            "client_id": clientID,
            "client_name": clientName,
            "client_version": clientVersion,
            "device_name": deviceName,
            "device_class": "mobile",
            "scope": scopes,
            "capabilities": capabilities,
        ], authenticated: false)
    }

    public func pollPairing(_ pairing: PairingSession) async throws -> PairingPoll {
        let endpoints = try await endpoints()
        do {
            let response: TokenResponse = try await post(endpoints.tokenEndpoint, json: [
                "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
                "client_id": clientID,
                "device_code": pairing.deviceCode,
            ], authenticated: false)
            return .authorized(try accept(response))
        } catch TuvimaClientError.http(_, let body) {
            let error = try? JSONDecoder().decode(OAuthError.self, from: Data(body.utf8))
            if error?.error == "authorization_pending" || error?.error == "slow_down" {
                return .pending(interval: error?.interval ?? pairing.interval)
            }
            return .failed(code: error?.error ?? "request_failed", description: error?.errorDescription)
        }
    }

    public func home() async throws -> DisplayPage { try await get("/api/v1/display/home") }

    public func continuePage(lane: String) async throws -> DisplayPage {
        try await get("/api/v1/display/continue?lane=\(lane)")
    }

    public func browse(lane: String, mediaType: String? = nil) async throws -> DisplayPage {
        var components = URLComponents()
        components.queryItems = [URLQueryItem(name: "lane", value: lane)]
        if let mediaType { components.queryItems?.append(URLQueryItem(name: "mediaType", value: mediaType)) }
        return try await get("/api/v1/display/browse?\(components.percentEncodedQuery ?? "")")
    }

    public func search(query: String) async throws -> SearchResponse {
        var components = URLComponents()
        components.queryItems = [URLQueryItem(name: "q", value: query)]
        return try await get("/api/v1/display/search?\(components.percentEncodedQuery ?? "")")
    }

    public func details(entityType: String, id: UUID) async throws -> DetailPage {
        try await get(ClientRoutes.details(entityType: entityType, id: id))
    }

    // MARK: App screens (Home, Libraries, View, Collections, profiles)

    /// The Home shelves without the full catalogue, which a phone does not need.
    public func homeShelves() async throws -> DisplayPage { try await get(ClientRoutes.home()) }

    /// Continue across every lane, most recent first.
    public func continueAll(limit: Int = 24) async throws -> DisplayPage {
        try await get(ClientRoutes.continueAll(limit: limit))
    }

    /// The cards of one group or collection; they carry the asset ids that a collection item list does not.
    public func groupCards(collectionID: UUID) async throws -> DisplayPage {
        try await get(ClientRoutes.group(collectionID))
    }

    /// One page of a library lane with the Dashboard's sort and filters applied by the Engine.
    public func browsePage(_ query: BrowseQuery) async throws -> BrowsePage {
        let response: BrowseResponse = try await get(ClientRoutes.browse(query))
        return response.page
    }

    /// The newest-first photo and video timeline. Throws `photoAccessDenied` when photos were not allowed.
    public func viewTimeline(cursor: String? = nil, limit: Int = 100) async throws -> ViewTimelinePage {
        try await getPhoto(ClientRoutes.viewTimeline(cursor: cursor, limit: limit))
    }

    public func viewGalleries() async throws -> ViewGalleries { try await getPhoto(ClientRoutes.viewGalleries) }

    public func viewGalleryItems(galleryID: UUID, cursor: String? = nil, limit: Int = 100) async throws -> ViewItemPage {
        try await getPhoto(ClientRoutes.viewGalleryItems(galleryID, cursor: cursor, limit: limit))
    }

    public func viewItem(id: UUID) async throws -> ViewItem { try await getPhoto(ClientRoutes.viewItem(id)) }

    /// A Collections catalogue page, the same list the Dashboard shows.
    public func collections(cursor: String? = nil, limit: Int = 24) async throws -> PagedList<CollectionEntry> {
        try await get(ClientRoutes.collections(cursor: cursor, limit: limit), keys: .convertFromSnakeCase)
    }

    public func collectionItems(id: UUID, cursor: String? = nil, limit: Int = 24) async throws -> PagedList<CollectionItem> {
        try await get(ClientRoutes.collectionItems(id, cursor: cursor, limit: limit), keys: .convertFromSnakeCase)
    }

    /// A Universe by Wikidata QID, with its member shelves.
    public func universe(qid: String) async throws -> UniverseDetail {
        guard let path = ClientRoutes.universe(qid) else { throw TuvimaClientError.invalidIdentifier }
        return try await get(path, keys: .convertFromSnakeCase)
    }

    /// Bytes of an artwork or photo path (relative to the server), fetched with the device's own credentials.
    public func fetchData(path: String) async throws -> Data {
        var request = URLRequest(url: try resolve(path))
        request.httpMethod = "GET"
        return try await sendRaw(request, authenticated: true)
    }

    /// Like `fetchData` for photo routes: a 403 becomes `photoAccessDenied`.
    public func fetchPhotoData(path: String) async throws -> Data {
        do { return try await fetchData(path: path) }
        catch let error as TuvimaClientError { throw Self.photoError(error) }
    }

    /// The people this paired app may switch to.
    public func deviceProfiles() async throws -> [DeviceProfile] {
        let response: DeviceProfilesResponse = try await get(ClientRoutes.deviceProfiles, keys: .convertFromSnakeCase)
        return response.profiles
    }

    /// Switches this app to another person without re-pairing. On success the client holds and stores the new
    /// tokens; each refusal (PIN needed 428, not allowed 403, already active 409, locked 429) is a typed result.
    public func switchProfile(profileID: UUID, pin: String? = nil) async -> SwitchProfileResult {
        var body: [String: Any] = ["profile_id": profileID.uuidString.lowercased()]
        if let pin, !pin.isEmpty { body["pin"] = pin }
        do {
            let response: TokenResponse = try await post(ClientRoutes.switchProfile, json: body)
            return .switched(try accept(response))
        } catch TuvimaClientError.http(let status, let text) {
            let error = try? JSONDecoder().decode(OAuthError.self, from: Data(text.utf8))
            return classifySwitchFailure(statusCode: status, code: error?.error, description: error?.errorDescription)
        } catch {
            return .failed(statusCode: 0, code: "request_failed", message: error.localizedDescription)
        }
    }

    /// The paired device's current token, for screens that show its scopes or profile.
    public var currentToken: ClientToken? { token }

    /// True when the device was given the photo scopes at pairing.
    public var hasPhotoAccess: Bool { token.map { PhotoScopes.grantedIn($0.scope) } ?? false }

    /// Removes this device on the server (all its tokens stop working at once). Call `signOut()` afterwards.
    public func revokeCurrentDevice() async throws {
        guard let deviceID = token?.deviceID else { throw TuvimaClientError.notPaired }
        var request = URLRequest(url: try resolve(ClientRoutes.revokeDevice(deviceID)))
        request.httpMethod = "DELETE"
        _ = try await sendRaw(request, authenticated: true)
    }

    public func playbackManifest(assetID: UUID, connectionPath: String = "local") async throws -> PlaybackManifest {
        try await get("/api/v1/playback/\(assetID.uuidString)/manifest?connectionPath=\(connectionPath)")
    }

    public func requestOfflineVariant(assetID: UUID, profileKey: String = "mobile-standard") async throws {
        let _: EncodeJobResponse = try await post(
            ClientRoutes.encodeJob(assetID),
            json: ["profileKey": profileKey]
        )
    }

    // MARK: Offline downloads: the device download list, next episodes and offline progress replay

    /// What the Engine believes this device stores. A variant missing from it has expired on the Engine.
    public func deviceDownloads() async throws -> [ServerDownloadEntry] {
        let response: DeviceDownloadsResponse = try await get(ClientRoutes.deviceDownloads, keys: .convertFromSnakeCase)
        return response.downloads.map { $0.entry }
    }

    /// Tells the Engine this device now stores a variant. Returns the HTTP status; 204 is success.
    public func reportDownloadStored(assetID: UUID, variantID: UUID, storedBytes: Int64, storedAt: Date) async throws -> Int {
        var request = URLRequest(url: try resolve(ClientRoutes.deviceDownloads))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "asset_id": assetID.uuidString.lowercased(),
            "variant_id": variantID.uuidString.lowercased(),
            "stored_bytes": storedBytes,
            "stored_at": OfflineLogic.formatIsoUtc(storedAt),
        ] as [String: Any])
        return try await sendForStatus(request).status
    }

    /// Tells the Engine this device no longer stores a variant. Returns the HTTP status; 204 is success.
    public func reportDownloadDeleted(variantID: UUID) async throws -> Int {
        var request = URLRequest(url: try resolve(ClientRoutes.deviceDownload(variantID)))
        request.httpMethod = "DELETE"
        return try await sendForStatus(request).status
    }

    /// Tells the Engine this device keeps a photo (its 2048 px preview). Returns the HTTP status; 204 is success, 404
    /// means the photo is gone or this person cannot see it, 403 that the token has no photo access, 409 a full list.
    public func reportPhotoStored(itemID: UUID, storedBytes: Int64, storedAt: Date) async throws -> Int {
        var request = URLRequest(url: try resolve(ClientRoutes.deviceDownloads))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "kind": OfflineKind.photo.rawValue,
            "photo_item_id": itemID.uuidString.lowercased(),
            "stored_bytes": storedBytes,
            "stored_at": OfflineLogic.formatIsoUtc(storedAt),
        ] as [String: Any])
        return try await sendForStatus(request).status
    }

    /// Tells the Engine this device no longer keeps a photo. Returns the HTTP status; 204 is success, 404 = not listed.
    public func reportPhotoDeleted(itemID: UUID) async throws -> Int {
        var request = URLRequest(url: try resolve(ClientRoutes.devicePhotoDownload(itemID)))
        request.httpMethod = "DELETE"
        return try await sendForStatus(request).status
    }

    /// The owned episodes that follow `afterAssetID` in the show's watch order.
    public func nextEpisodes(showID: UUID, afterAssetID: UUID, count: Int = 3) async throws -> NextEpisodesResponse {
        try await get(
            ClientRoutes.nextEpisodes(showID: showID, afterAssetID: afterAssetID, count: count),
            keys: .convertFromSnakeCase
        )
    }

    /// The Engine's progress for an asset, or nil when it has none (HTTP 404).
    public func serverProgress(assetID: UUID) async throws -> ServerProgress? {
        var request = URLRequest(url: try resolve(ClientRoutes.progress(assetID)))
        request.httpMethod = "GET"
        let answer = try await sendForStatus(request)
        if answer.status == 404 { return nil }
        guard (200...299).contains(answer.status) else {
            throw TuvimaClientError.http(answer.status, String(decoding: answer.data, as: UTF8.self))
        }
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return try decoder.decode(ServerProgressWire.self, from: answer.data).progress
    }

    /// Replays progress recorded offline (`offline: true` with `client_timestamp`). The Engine keeps the later of its
    /// own record and this one and answers 200 with the state that stands; `OfflineLogic.isServerWon` tells which.
    public func replayOfflineProgress(_ entry: OfflineProgressEntry, expectedRevision: Int64) async throws -> ServerProgress {
        let recordedAt = OfflineLogic.formatIsoUtc(entry.recordedAt)
        var extended: [String: String] = [
            "position_seconds": OfflineLogic.formatSeconds(entry.positionSeconds),
            "playback_state": "paused",
            "player_client": clientID,
            "updated_at": recordedAt,
        ]
        if let duration = entry.durationSeconds { extended["duration_seconds"] = OfflineLogic.formatSeconds(duration) }
        var request = URLRequest(url: try resolve(ClientRoutes.progress(entry.assetId)))
        request.httpMethod = "PUT"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "progress_pct": entry.progressPct,
            "expected_revision": expectedRevision,
            "offline": true,
            "client_timestamp": recordedAt,
            "extended_properties": extended,
        ] as [String: Any])
        let wire: ServerProgressWire = try await send(request, authenticated: true, keys: .convertFromSnakeCase)
        return wire.progress
    }

    /// The device's own sign-in profile, or nil before pairing. Offline records are kept per person.
    public var currentProfileID: UUID? { token?.profileID }

    /// The signed-in profile plus a change counter; background work binds to this (see `SyncGuard`) and compares it later.
    public var profileBinding: ProfileBinding? {
        token.map { ProfileBinding(profileID: $0.profileID, epoch: profileEpoch) }
    }

    /// Refuses to go on when the calling work is bound (see `ProfileScope`) to a profile that is no longer signed in.
    private func requireBoundProfile() throws {
        guard let bound = ProfileScope.bound else { return }
        if profileBinding != bound { throw TuvimaClientError.profileChanged }
    }

    /// Sends an authenticated request and returns the status and body without treating a non-2xx answer as an error,
    /// so a caller can read 204, 404 and 409 for what they mean.
    private func sendForStatus(_ original: URLRequest) async throws -> (status: Int, data: Data) {
        var request = original
        request.setValue("Bearer \(try await accessToken())", forHTTPHeaderField: "Authorization")
        var (data, response) = try await session.data(for: request)
        if (response as? HTTPURLResponse)?.statusCode == 401 {
            request.setValue("Bearer \(try await accessToken(forceRefresh: true))", forHTTPHeaderField: "Authorization")
            (data, response) = try await session.data(for: request)
        }
        return ((response as? HTTPURLResponse)?.statusCode ?? 0, data)
    }

    public func playerQueue() async throws -> [PlayerQueueItem] {
        let state: PlayerState = try await get("/api/v1/player/state")
        return state.queue.filter {
            $0.mediaType.caseInsensitiveCompare("Music") == .orderedSame ||
                $0.mediaType.caseInsensitiveCompare("Audiobooks") == .orderedSame
        }
    }

    public func authorizedRequest(path: String) async throws -> URLRequest {
        var request = URLRequest(url: try resolve(path))
        request.setValue("Bearer \(try await accessToken())", forHTTPHeaderField: "Authorization")
        return request
    }

    public func absoluteURL(path: String) throws -> URL { try resolve(path) }

    public func heartbeat(assetID: UUID, isPlaying: Bool, position: Double, duration: Double?, rate: Double) async throws {
        var body: [String: Any] = [
            "assetId": assetID.uuidString,
            "isPlaying": isPlaying,
            "positionSeconds": position,
            "playbackRate": rate,
        ]
        if let duration { body["durationSeconds"] = duration }
        let _: IgnoredResponse = try await post("/api/v1/player/heartbeat", json: body)
    }

    public func signOut() {
        token = nil
        profileEpoch += 1
        store.clear(for: origin)
    }

    private func accessToken(forceRefresh: Bool = false) async throws -> String {
        try requireBoundProfile()
        guard let current = token else { throw TuvimaClientError.notPaired }
        if !forceRefresh && !current.expiresSoon { return current.accessToken }
        let endpoints = try await endpoints()
        let before = profileBinding
        let response: TokenResponse
        do {
            response = try await post(endpoints.tokenEndpoint, json: [
                "grant_type": "refresh_token",
                "client_id": clientID,
                "refresh_token": current.refreshToken,
            ], authenticated: false)
        } catch TuvimaClientError.http(let status, let body) {
            let code = (try? JSONDecoder().decode(OAuthError.self, from: Data(body.utf8)))?.error
            if SessionLogic.classifyRefreshFailure(statusCode: status, errorCode: code) == .revoked {
                // The device was revoked or its grant changed: drop the sign-in and tell the app, which clears the offline
                // data. Only when this is still the sign-in that was refused (a profile switch may have replaced it).
                if token?.refreshToken == current.refreshToken {
                    signOut()
                    onSessionRevoked?()
                }
                throw TuvimaClientError.sessionRevoked
            }
            throw TuvimaClientError.http(status, body)
        }
        // The actor can run other calls while the answer was awaited: never store a refresh for a profile that was left.
        guard profileBinding == before else { throw TuvimaClientError.profileChanged }
        return try accept(response).accessToken
    }

    private func accept(_ response: TokenResponse) throws -> ClientToken {
        let value = ClientToken(
            accessToken: response.accessToken,
            refreshToken: response.refreshToken,
            expiresAt: Date().addingTimeInterval(response.expiresIn),
            scope: response.scope,
            deviceID: response.deviceID,
            profileID: response.profileID
        )
        if token?.profileID != value.profileID { profileEpoch += 1 }
        token = value
        try store.save(value, for: origin)
        return value
    }

    private func endpoints() async throws -> Discovery {
        if let discovery { return discovery }
        return try await discoverServer()
    }

    private func get<T: Decodable>(
        _ path: String,
        authenticated: Bool = true,
        keys: JSONDecoder.KeyDecodingStrategy = .useDefaultKeys
    ) async throws -> T {
        var request = URLRequest(url: try resolve(path))
        request.httpMethod = "GET"
        return try await send(request, authenticated: authenticated, keys: keys)
    }

    /// A GET on a photo route (snake_case): a 403 becomes `photoAccessDenied` so a screen can explain it.
    private func getPhoto<T: Decodable>(_ path: String) async throws -> T {
        do { return try await get(path, keys: .convertFromSnakeCase) }
        catch let error as TuvimaClientError { throw Self.photoError(error) }
    }

    private static func photoError(_ error: TuvimaClientError) -> TuvimaClientError {
        if case .http(let status, _) = error, status == 403 { return .photoAccessDenied }
        return error
    }

    private func post<T: Decodable>(_ path: String, json: [String: Any], authenticated: Bool = true) async throws -> T {
        var request = URLRequest(url: try resolve(path))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: json)
        return try await send(request, authenticated: authenticated)
    }

    private func send<T: Decodable>(
        _ original: URLRequest,
        authenticated: Bool,
        keys: JSONDecoder.KeyDecodingStrategy = .useDefaultKeys
    ) async throws -> T {
        let data = try await sendRaw(original, authenticated: authenticated)
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = keys
        return try decoder.decode(T.self, from: data)
    }

    private func sendRaw(_ original: URLRequest, authenticated: Bool) async throws -> Data {
        var request = original
        if authenticated { request.setValue("Bearer \(try await accessToken())", forHTTPHeaderField: "Authorization") }
        var (data, response) = try await session.data(for: request)
        if authenticated, (response as? HTTPURLResponse)?.statusCode == 401 {
            request.setValue("Bearer \(try await accessToken(forceRefresh: true))", forHTTPHeaderField: "Authorization")
            (data, response) = try await session.data(for: request)
        }
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw TuvimaClientError.http((response as? HTTPURLResponse)?.statusCode ?? 0, String(decoding: data, as: UTF8.self))
        }
        return data
    }

    private func resolve(_ path: String) throws -> URL {
        if let absolute = URL(string: path), absolute.scheme != nil { return absolute }
        guard let resolved = URL(string: path, relativeTo: origin)?.absoluteURL else { throw TuvimaClientError.invalidServer }
        return resolved
    }

    private static func isLocalHost(_ host: String) -> Bool {
        if host == "localhost" || host.hasSuffix(".local") || host.hasPrefix("10.") || host.hasPrefix("192.168.") {
            return true
        }
        let octets = host.split(separator: ".").compactMap { Int($0) }
        return octets.count == 4 && octets[0] == 172 && (16...31).contains(octets[1])
    }
}

private struct EncodeJobResponse: Decodable { let id: UUID }
private struct IgnoredResponse: Decodable {}
