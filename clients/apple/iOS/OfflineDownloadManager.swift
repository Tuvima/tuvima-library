import Foundation
import Network
import TuvimaCore

/// What a person asked to download. The title and artwork path are kept so the Downloads list reads without the Engine.
struct DownloadRequest {
    let assetID: UUID
    let title: String
    let subtitle: String?
    let artworkPath: String?
    let entityType: String
    let showID: UUID?
    let profileKey: String
}

enum EnqueueResult {
    case queued
    case alreadyHeld(DownloadState)
    case overCap(committed: Int64, cap: Int64)
    case notOffered
    case notPaired
}

/// Names the files an offline copy uses. Each person has their own folder, so switching profile never shows or
/// plays another person's downloads. The whole tree is excluded from device backups.
enum OfflineFiles {
    static func root() throws -> URL {
        let base = try FileManager.default.url(
            for: .applicationSupportDirectory,
            in: .userDomainMask,
            appropriateFor: nil,
            create: true
        )
        let root = base.appendingPathComponent("Offline", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        var mutableRoot = root
        try mutableRoot.setResourceValues(values)
        return root
    }

    static func directory(profile: UUID) throws -> URL {
        let directory = try root().appendingPathComponent(profile.uuidString.lowercased(), isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    static func file(profile: UUID, name: String) throws -> URL {
        try directory(profile: profile).appendingPathComponent(name)
    }

    static func artworkName(_ assetID: UUID) -> String { "\(assetID.uuidString.lowercased()).art" }

    static func resumeName(_ assetID: UUID) -> String { "\(assetID.uuidString.lowercased()).resume" }

    static func load(profile: UUID) -> OfflineState {
        guard let url = try? file(profile: profile, name: "state.json"),
              let data = try? Data(contentsOf: url) else { return OfflineState() }
        // An unreadable state file starts empty rather than blocking every screen; the Engine's list still wins.
        return (try? JSONDecoder().decode(OfflineState.self, from: data)) ?? OfflineState()
    }

    static func save(_ state: OfflineState, profile: UUID) {
        guard let url = try? file(profile: profile, name: "state.json"),
              let data = try? JSONEncoder().encode(state) else { return }
        // Best effort: if the write fails the in-memory state still holds and the next change writes it again.
        try? data.write(to: url, options: .atomic)
    }
}

/// Which person, title and copy a transfer belongs to, carried in the background task's description so the
/// answer can be matched to its record even after the app was closed and opened again.
struct DownloadTaskInfo: Sendable {
    let profile: UUID
    let asset: UUID
    let variant: UUID
    let fileExtension: String

    init(profile: UUID, asset: UUID, variant: UUID, fileExtension: String) {
        self.profile = profile
        self.asset = asset
        self.variant = variant
        self.fileExtension = fileExtension
    }

    init?(description: String?) {
        guard let description else { return nil }
        let parts = description.split(separator: "|").map { String($0) }
        guard parts.count == 4,
              let profile = UUID(uuidString: parts[0]),
              let asset = UUID(uuidString: parts[1]),
              let variant = UUID(uuidString: parts[2]) else { return nil }
        self.init(profile: profile, asset: asset, variant: variant, fileExtension: parts[3])
    }

    var description: String {
        [profile.uuidString.lowercased(), asset.uuidString.lowercased(), variant.uuidString.lowercased(), fileExtension]
            .joined(separator: "|")
    }
}

/// The offline downloads of the person using this device. It owns the queue (a background URLSession does the
/// transfers, so they continue with the app closed and pause and resume), the storage cap, the reports back to
/// the Engine's device download list, and the progress recorded while playing downloads without a connection.
/// Books and photos are not offered here. The app never writes History: the Engine does that when it applies a
/// replayed play.
final class OfflineDownloadManager: NSObject, ObservableObject, URLSessionDownloadDelegate {
    static let capKey = "tuvima.downloadCapGB"
    static let qualityKey = "tuvima.downloadQuality"

    /// The downloads of the person using the app now, in the order they were asked for.
    @Published private(set) var records: [DownloadRecord] = []
    /// Bytes received so far by transfers in flight; kept out of the saved records so progress costs no disk writes.
    @Published private(set) var liveBytes: [UUID: Int64] = [:]
    /// A one-off message for the person, such as reaching the storage limit.
    @Published private(set) var notice: String?

    fileprivate var api: TuvimaAPIClient?
    fileprivate var activeProfile: UUID?
    fileprivate var states: [UUID: OfflineState] = [:]
    fileprivate var preparing: [UUID: Task<Void, Never>] = [:]
    fileprivate var isSyncing = false
    fileprivate var syncAgain = false
    private var monitorStarted = false
    private let monitor = NWPathMonitor()
    fileprivate lazy var session: URLSession = {
        let configuration = URLSessionConfiguration.background(withIdentifier: "com.tuvima.library.offline.v1")
        configuration.waitsForConnectivity = true
        configuration.isDiscretionary = false
        configuration.sessionSendsLaunchEvents = true
        return URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
    }()

    /// The storage limit chosen in Settings (10 GB until changed).
    static var capBytes: Int64 {
        let stored = UserDefaults.standard.integer(forKey: capKey)
        return OfflineLogic.capBytes(forGB: stored > 0 ? stored : OfflineLogic.defaultCapGB)
    }

    /// The default download quality chosen in Settings; a transcoding profile key such as `mobile-standard`.
    static var defaultProfileKey: String {
        UserDefaults.standard.string(forKey: qualityKey) ?? OfflineLogic.qualityStandard
    }

    // These read what is already published, so views can call them from their bodies.

    func record(for assetID: UUID) -> DownloadRecord? {
        records.first { $0.assetId == assetID }
    }

    /// Bytes the downloads hold or have reserved, counted against the storage limit.
    var usedBytes: Int64 { OfflineLogic.committedBytes(records) }

    /// What a row shows as received so far: the live figure while a transfer runs, else what was saved.
    func displayBytes(for record: DownloadRecord) -> Int64 {
        liveBytes[record.assetId] ?? record.bytesDone
    }

    func dismissNotice() { notice = nil }

    /// The saved artwork file of a download, when the device has it.
    func artworkFileURL(for record: DownloadRecord) -> URL? {
        guard let profile = activeProfile, record.hasLocalArtwork,
              let url = try? OfflineFiles.file(profile: profile, name: OfflineFiles.artworkName(record.assetId)),
              FileManager.default.fileExists(atPath: url.path) else { return nil }
        return url
    }

    func attach(_ api: TuvimaAPIClient) {
        self.api = api
        _ = session // reconnects to transfers a previous launch left running
        startMonitoring()
        Task { await self.activate() }
    }

    /// Called after the app switched to another person.
    func profileChanged() {
        Task { await self.activate() }
    }

    private func startMonitoring() {
        guard !monitorStarted else { return }
        monitorStarted = true
        monitor.pathUpdateHandler = { [weak self] path in
            guard path.status == .satisfied else { return }
            Task { @MainActor in self?.scheduleSync() }
        }
        monitor.start(queue: DispatchQueue(label: "com.tuvima.library.offline.network"))
    }

    // MARK: URLSessionDownloadDelegate

    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didWriteData bytesWritten: Int64,
        totalBytesWritten: Int64,
        totalBytesExpectedToWrite: Int64
    ) {
        guard let info = DownloadTaskInfo(description: downloadTask.taskDescription) else { return }
        Task { @MainActor in
            let previous = self.liveBytes[info.asset] ?? 0
            if totalBytesWritten - previous >= 262_144 || totalBytesWritten >= totalBytesExpectedToWrite {
                self.liveBytes[info.asset] = totalBytesWritten
            }
            if totalBytesExpectedToWrite > 0,
               let held = self.record(info.profile, info.asset), held.totalBytes <= 0 {
                self.update(info.profile, info.asset) { $0.totalBytes = totalBytesExpectedToWrite }
            }
        }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        guard let info = DownloadTaskInfo(description: downloadTask.taskDescription) else { return }
        // A refused request still hands over the error body as a "file"; only a 2xx answer is the media.
        let status = (downloadTask.response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200...299).contains(status) else {
            let message = status == 401
                ? "The sign-in for this download expired. Tap Resume to try again."
                : "Tuvima returned HTTP \(status)."
            Task { @MainActor in self.taskFailed(info, message: message, resumeData: nil, cancelled: false) }
            return
        }
        do {
            let destination = try OfflineFiles.file(
                profile: info.profile,
                name: "\(info.variant.uuidString.lowercased()).\(info.fileExtension)"
            )
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.moveItem(at: location, to: destination)
            let size = (try? destination.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0
            Task { @MainActor in self.finishCompleted(info, size: Int64(size)) }
        } catch {
            let message = error.localizedDescription
            Task { @MainActor in self.taskFailed(info, message: message, resumeData: nil, cancelled: false) }
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let error, let info = DownloadTaskInfo(description: task.taskDescription) else { return }
        let urlError = error as? URLError
        let resumeData = urlError?.downloadTaskResumeData
        let cancelled = urlError?.code == .cancelled
        let message = error.localizedDescription
        Task { @MainActor in self.taskFailed(info, message: message, resumeData: resumeData, cancelled: cancelled) }
    }
}

// MARK: Reading

@MainActor
extension OfflineDownloadManager {
    /// The file to play for an asset that is downloaded, finished, not expired and still on disk.
    func playableLocalFile(assetID: UUID) -> URL? {
        guard let profile = activeProfile,
              let held = record(profile, assetID),
              held.isComplete,
              !OfflineLogic.isExpired(held, now: Date()),
              let name = held.fileName,
              let url = try? OfflineFiles.file(profile: profile, name: name),
              FileManager.default.fileExists(atPath: url.path) else { return nil }
        return url
    }

    /// Where a downloaded file starts: where the person left off, or the beginning once it was finished.
    func resumeSeconds(assetID: UUID) -> Double {
        guard let profile = activeProfile else { return 0 }
        return OfflineLogic.resumeSeconds(state(profile).progress.first { $0.assetId == assetID })
    }
}

// MARK: Queue

@MainActor
extension OfflineDownloadManager {
    /// Called when the app opens, after pairing and after a profile switch.
    func activate() async {
        guard let api else { return }
        let signedIn = await api.currentProfileID
        if signedIn != activeProfile {
            // Another person (or nobody) is using the app: stop preparing the previous person's downloads.
            for task in preparing.values { task.cancel() }
            preparing = [:]
        }
        guard let profile = signedIn else {
            activeProfile = nil
            records = []
            return
        }
        activeProfile = profile
        records = state(profile).records
        purgeLocallyExpired()
        for held in state(profile).records where held.isActive && preparing[held.assetId] == nil {
            startPreparing(profile: profile, assetID: held.assetId)
        }
        scheduleSync()
    }

    /// Queues a download and tells the person when it cannot be queued (the storage limit, or already held).
    func requestDownload(_ request: DownloadRequest) {
        notice = Self.message(for: enqueue(request))
    }

    /// Queues a download unless it is already held or the storage limit is reached.
    func enqueue(_ request: DownloadRequest) -> EnqueueResult {
        guard let profile = activeProfile else { return .notPaired }
        if OfflineLogic.downloadKind(forEntityType: request.entityType) == .excluded { return .notOffered }
        if let existing = record(profile, request.assetID) {
            if existing.state != .failed && !OfflineLogic.isExpired(existing, now: Date()) {
                return .alreadyHeld(existing.state)
            }
            // A failed or expired download starts fresh.
            removeLocalCopy(profile, existing)
        }
        let committed = OfflineLogic.committedBytes(state(profile).records)
        let cap = Self.capBytes
        if !OfflineLogic.canQueue(committedBytes: committed, capBytes: cap) {
            return .overCap(committed: committed, cap: cap)
        }
        let added = DownloadRecord(
            assetId: request.assetID,
            title: request.title,
            subtitle: request.subtitle,
            artworkPath: request.artworkPath,
            entityType: request.entityType,
            showId: request.showID,
            profileKey: request.profileKey
        )
        mutate(profile) { $0.records.append(added) }
        startPreparing(profile: profile, assetID: request.assetID)
        return .queued
    }

    /// Queues the next owned episodes after `afterAssetID` (the Engine decides which, up to three).
    func downloadNextEpisodes(showID: UUID, afterAssetID: UUID, profileKey: String) async {
        guard let api else {
            notice = Self.message(for: .notPaired)
            return
        }
        do {
            let next = try await api.nextEpisodes(showID: showID, afterAssetID: afterAssetID, count: 3)
            var queued = 0
            var overCap = false
            for episode in next.episodes {
                let name = (episode.title ?? "").isEmpty ? "Episode" : (episode.title ?? "Episode")
                let result = enqueue(DownloadRequest(
                    assetID: episode.assetId,
                    title: name,
                    subtitle: OfflineLogic.episodeLabel(season: episode.seasonNumber, episode: episode.episodeNumber),
                    artworkPath: episode.stillUrl,
                    entityType: "tvEpisode",
                    showID: showID,
                    profileKey: profileKey
                ))
                if case .queued = result { queued += 1 }
                if case .overCap = result {
                    overCap = true
                    break
                }
            }
            notice = OfflineLogic.nextEpisodesMessage(found: next.episodes.count, queued: queued, overCap: overCap)
        } catch {
            notice = error.localizedDescription
        }
    }

    /// Stops the transfer and keeps what was received; Resume continues it from there.
    func pause(_ assetID: UUID) {
        guard let profile = activeProfile else { return }
        update(profile, assetID) { held in
            if held.isActive {
                held.state = .paused
                held.error = nil
            }
        }
        preparing[assetID]?.cancel()
        preparing[assetID] = nil
        stopTransfer(profile: profile, assetID: assetID)
    }

    func resume(_ assetID: UUID) {
        guard let profile = activeProfile, let held = record(profile, assetID),
              held.state == .paused || held.state == .failed else { return }
        update(profile, assetID) { $0.state = .queued; $0.error = nil }
        startPreparing(profile: profile, assetID: assetID)
    }

    /// Deletes the file and tells the Engine's list (queued and retried later when there is no connection).
    func delete(_ assetID: UUID) {
        guard let profile = activeProfile, let held = record(profile, assetID) else { return }
        removeLocalCopy(profile, held)
        if let variantId = held.variantId {
            let report = PendingReport(kind: .delete, assetId: assetID, variantId: variantId, storedBytes: 0, storedAt: Date())
            mutate(profile) { $0.reports = OfflineLogic.queueReport($0.reports, report) }
        }
        scheduleSync()
    }

    /// Called when the Downloads screen opens or is pulled down.
    func refresh() async {
        purgeLocallyExpired()
        scheduleSync()
    }

    /// Removes every download and every unsent record from this device; used when it signs out.
    func clearAll() {
        for task in preparing.values { task.cancel() }
        preparing = [:]
        Task {
            let tasks = await session.allTasks
            for task in tasks { task.cancel() }
        }
        states = [:]
        records = []
        liveBytes = [:]
        activeProfile = nil
        if let root = try? OfflineFiles.root() {
            // Best effort: anything left is purged again by the next sign-in's sync.
            try? FileManager.default.removeItem(at: root)
        }
    }

    static func message(for result: EnqueueResult) -> String? {
        switch result {
        case .queued:
            return nil
        case .alreadyHeld(let state):
            return state == .complete ? "This is already downloaded." : "This is already in your downloads."
        case .overCap(let committed, let cap):
            return "Storage limit reached (\(OfflineLogic.formatBytes(committed)) of \(OfflineLogic.formatBytes(cap))). "
                + "Delete a download or raise the limit in Settings."
        case .notOffered:
            return "Books and photos are not downloaded by this app yet."
        case .notPaired:
            return "Connect this device to a server first."
        }
    }
}

// MARK: Progress recorded offline

@MainActor
extension OfflineDownloadManager {
    /// Keeps where the person is in a downloaded file. It is replayed to the Engine, oldest first, once there is a
    /// connection; nothing is written to History from here.
    func recordProgress(assetID: UUID, position: Double, duration: Double?, finished: Bool = false) {
        guard let profile = activeProfile, record(profile, assetID) != nil else { return }
        if !finished && position < 1 { return }
        let total: Double? = (duration ?? 0) > 0 ? duration : nil
        let entry = OfflineProgressEntry(
            assetId: assetID,
            positionSeconds: finished ? (total ?? position) : position,
            durationSeconds: total,
            progressPct: finished ? 100 : OfflineLogic.progressPercent(position: position, duration: total),
            recordedAt: Date(),
            pending: true,
            revision: 0
        )
        mutate(profile) { $0.progress = OfflineLogic.recordProgress($0.progress, entry) }
    }
}

// MARK: Storage of the state

@MainActor
extension OfflineDownloadManager {
    fileprivate func state(_ profile: UUID) -> OfflineState {
        if let value = states[profile] { return value }
        let loaded = OfflineFiles.load(profile: profile)
        states[profile] = loaded
        return loaded
    }

    fileprivate func mutate(_ profile: UUID, _ change: (inout OfflineState) -> Void) {
        var value = state(profile)
        change(&value)
        states[profile] = value
        OfflineFiles.save(value, profile: profile)
        if profile == activeProfile { records = value.records }
    }

    fileprivate func record(_ profile: UUID, _ assetID: UUID) -> DownloadRecord? {
        state(profile).records.first { $0.assetId == assetID }
    }

    @discardableResult
    fileprivate func update(_ profile: UUID, _ assetID: UUID, _ change: (inout DownloadRecord) -> Void) -> DownloadRecord? {
        var result: DownloadRecord?
        mutate(profile) { current in
            if let index = current.records.firstIndex(where: { $0.assetId == assetID }) {
                change(&current.records[index])
                result = current.records[index]
            }
        }
        return result
    }

    fileprivate func removeFiles(_ profile: UUID, _ held: DownloadRecord) {
        var names = [OfflineFiles.artworkName(held.assetId), OfflineFiles.resumeName(held.assetId)]
        if let name = held.fileName { names.append(name) }
        for name in names {
            if let url = try? OfflineFiles.file(profile: profile, name: name) {
                // Best effort: a file that is already gone is the goal.
                try? FileManager.default.removeItem(at: url)
            }
        }
    }

    /// Stops any transfer, removes the files and forgets the download. Progress still waiting to be replayed stays.
    fileprivate func removeLocalCopy(_ profile: UUID, _ held: DownloadRecord) {
        preparing[held.assetId]?.cancel()
        preparing[held.assetId] = nil
        let assetID = held.assetId
        Task {
            let tasks = await session.allTasks
            for task in tasks where DownloadTaskInfo(description: task.taskDescription)?.asset == assetID {
                task.cancel()
            }
        }
        removeFiles(profile, held)
        liveBytes[assetID] = nil
        mutate(profile) { current in
            current.records.removeAll { $0.assetId == assetID }
            current.progress.removeAll { $0.assetId == assetID && !$0.pending }
        }
    }

    /// Removes finished downloads whose expiry has passed. The Engine already dropped them, so nothing is reported.
    fileprivate func purgeLocallyExpired() {
        guard let profile = activeProfile else { return }
        let now = Date()
        for held in state(profile).records where OfflineLogic.isExpired(held, now: now) {
            removeLocalCopy(profile, held)
        }
    }
}

// MARK: Transfers

@MainActor
extension OfflineDownloadManager {
    fileprivate func startPreparing(profile: UUID, assetID: UUID) {
        preparing[assetID]?.cancel()
        preparing[assetID] = Task {
            await self.prepareAndStart(profile: profile, assetID: assetID)
            if !Task.isCancelled { self.preparing[assetID] = nil }
        }
    }

    fileprivate func hasRunningTransfer(assetID: UUID) async -> Bool {
        let tasks = await session.allTasks
        return tasks.contains { task in
            DownloadTaskInfo(description: task.taskDescription)?.asset == assetID
                && (task.state == .running || task.state == .suspended)
        }
    }

    fileprivate func fail(_ profile: UUID, _ assetID: UUID, _ message: String) {
        update(profile, assetID) { $0.state = .failed; $0.error = message }
        liveBytes[assetID] = nil
    }

    /// Asks the Engine to prepare the copy (once), waits until it is ready, then starts the background transfer.
    fileprivate func prepareAndStart(profile: UUID, assetID: UUID) async {
        guard let api, let begun = record(profile, assetID), begun.state != .paused, !begun.isComplete else { return }
        if await hasRunningTransfer(assetID: assetID) {
            update(profile, assetID) { $0.state = .downloading; $0.error = nil }
            return
        }
        update(profile, assetID) { $0.state = .preparing; $0.error = nil }
        do {
            await saveArtwork(api: api, profile: profile, assetID: assetID)
            guard var current = record(profile, assetID) else { return }
            var variant = try await readyVariant(api: api, assetID: assetID, held: current)
            if variant == nil, current.variantId != nil {
                // The copy this download was using is no longer ready on the Engine: start over with a new one.
                removeFiles(profile, current)
                let restarted = update(profile, assetID) { reset in
                    reset.variantId = nil
                    reset.fileName = nil
                    reset.bytesDone = 0
                    reset.totalBytes = 0
                    reset.variantRequested = false
                    reset.hasLocalArtwork = false
                }
                if let restarted { current = restarted }
            }
            if variant == nil {
                if !current.variantRequested {
                    try await api.requestOfflineVariant(assetID: assetID, profileKey: current.profileKey)
                    if let requested = update(profile, assetID, { $0.variantRequested = true }) { current = requested }
                }
                // The Engine prepares the copy in the background; look again every two seconds for about three minutes.
                for _ in 0..<90 {
                    try await Task.sleep(for: .seconds(2))
                    guard let live = record(profile, assetID), live.isActive else { return }
                    variant = try await readyVariant(api: api, assetID: assetID, held: current)
                    if variant != nil { break }
                }
            }
            guard let ready = variant else {
                fail(profile, assetID, "The server is still preparing this copy. Tap Resume to try again.")
                return
            }
            guard let latest = record(profile, assetID), latest.isActive else { return }
            let expected = ready.fileSizeBytes ?? 0
            let others = state(profile).records.filter { $0.assetId != assetID }
            let fits = OfflineLogic.canQueue(
                committedBytes: OfflineLogic.committedBytes(others),
                capBytes: Self.capBytes,
                estimatedBytes: expected
            )
            if expected > 0 && !fits {
                fail(profile, assetID, "Storage limit reached. Delete a download or raise the limit in Settings.")
                return
            }
            let fileExtension = OfflineLogic.fileExtension(forProfileKey: latest.profileKey)
            let fileName = "\(ready.id.uuidString.lowercased()).\(fileExtension)"
            update(profile, assetID) { started in
                started.variantId = ready.id
                started.totalBytes = expected
                started.expiresAt = ready.expiresAtDate
                started.fileName = fileName
                started.state = .downloading
                started.error = nil
            }
            try Task.checkCancellation()
            let task = try await makeTask(api: api, profile: profile, assetID: assetID, variantID: ready.id)
            try Task.checkCancellation()
            task.taskDescription = DownloadTaskInfo(
                profile: profile,
                asset: assetID,
                variant: ready.id,
                fileExtension: fileExtension
            ).description
            task.resume()
        } catch {
            if isCancellation(error) {
                // Paused, deleted or the person switched: leave it queued so it starts again when it is wanted.
                update(profile, assetID) { if $0.state == .preparing { $0.state = .queued } }
                return
            }
            fail(profile, assetID, error.localizedDescription)
        }
    }

    fileprivate func readyVariant(api: TuvimaAPIClient, assetID: UUID, held: DownloadRecord) async throws -> OfflineVariant? {
        let manifest = try await api.playbackManifest(assetID: assetID)
        return OfflineLogic.chooseReadyVariant(manifest.offlineVariants, profileKey: held.profileKey, knownVariantId: held.variantId)
    }

    /// Keeps the artwork on the device so the list still shows it without a connection.
    fileprivate func saveArtwork(api: TuvimaAPIClient, profile: UUID, assetID: UUID) async {
        guard let held = record(profile, assetID), !held.hasLocalArtwork,
              let path = held.artworkPath, !path.isEmpty else { return }
        // Best effort: a missing image only leaves the neutral placeholder in the list.
        guard let bytes = try? await api.fetchData(path: path), !bytes.isEmpty,
              let url = try? OfflineFiles.file(profile: profile, name: OfflineFiles.artworkName(assetID)) else { return }
        do {
            try bytes.write(to: url, options: .atomic)
            update(profile, assetID) { $0.hasLocalArtwork = true }
        } catch {
            // Same as above: the list falls back to the Engine's artwork while there is a connection.
            return
        }
    }

    /// A transfer that continues from the bytes already received when resume data was kept, otherwise a fresh one.
    fileprivate func makeTask(api: TuvimaAPIClient, profile: UUID, assetID: UUID, variantID: UUID) async throws -> URLSessionDownloadTask {
        if let url = try? OfflineFiles.file(profile: profile, name: OfflineFiles.resumeName(assetID)),
           let data = try? Data(contentsOf: url), !data.isEmpty {
            try? FileManager.default.removeItem(at: url)
            return session.downloadTask(withResumeData: data)
        }
        var request = try await api.authorizedRequest(path: ClientRoutes.offlineFile(assetID, variantID))
        request.setValue("no-store", forHTTPHeaderField: "Cache-Control")
        return session.downloadTask(with: request)
    }

    fileprivate func stopTransfer(profile: UUID, assetID: UUID) {
        Task {
            let tasks = await session.allTasks
            for task in tasks where DownloadTaskInfo(description: task.taskDescription)?.asset == assetID {
                if let download = task as? URLSessionDownloadTask {
                    download.cancel(byProducingResumeData: { data in
                        guard let data,
                              let url = try? OfflineFiles.file(profile: profile, name: OfflineFiles.resumeName(assetID)) else { return }
                        try? data.write(to: url, options: .atomic)
                    })
                } else {
                    task.cancel()
                }
            }
        }
    }

    fileprivate func finishCompleted(_ info: DownloadTaskInfo, size: Int64) {
        liveBytes[info.asset] = nil
        let name = "\(info.variant.uuidString.lowercased()).\(info.fileExtension)"
        guard record(info.profile, info.asset) != nil else {
            // Deleted while it was finishing.
            if let url = try? OfflineFiles.file(profile: info.profile, name: name) {
                try? FileManager.default.removeItem(at: url)
            }
            return
        }
        let now = Date()
        let report = PendingReport(kind: .store, assetId: info.asset, variantId: info.variant, storedBytes: size, storedAt: now)
        mutate(info.profile) { current in
            if let index = current.records.firstIndex(where: { $0.assetId == info.asset }) {
                current.records[index].state = .complete
                current.records[index].variantId = info.variant
                current.records[index].fileName = name
                current.records[index].bytesDone = size
                current.records[index].totalBytes = size
                current.records[index].storedAt = now
                current.records[index].reportedToServer = false
                current.records[index].error = nil
            }
            current.reports = OfflineLogic.queueReport(current.reports, report)
        }
        if info.profile == activeProfile { scheduleSync() }
    }

    /// A transfer ended with an error. A pause or delete cancels the task on purpose, and is not a failure.
    fileprivate func taskFailed(_ info: DownloadTaskInfo, message: String, resumeData: Data?, cancelled: Bool) {
        guard let held = record(info.profile, info.asset) else { return }
        if let resumeData, !resumeData.isEmpty,
           let url = try? OfflineFiles.file(profile: info.profile, name: OfflineFiles.resumeName(info.asset)) {
            try? resumeData.write(to: url, options: .atomic)
        }
        if held.state == .paused || held.isComplete { return }
        if cancelled && held.state == .queued { return }
        fail(info.profile, info.asset, resumeData == nil ? message : "Interrupted. Tap Resume to continue.")
    }
}

// MARK: Sync with the Engine

@MainActor
extension OfflineDownloadManager {
    /// Brings the Engine and this device back in step once there is a connection: reports stored and deleted copies,
    /// drops copies the Engine expired, and replays progress recorded offline. Calling it while offline simply waits.
    func scheduleSync() {
        if isSyncing {
            syncAgain = true
            return
        }
        isSyncing = true
        Task {
            repeat {
                syncAgain = false
                await runSync()
            } while syncAgain
            isSyncing = false
        }
    }

    fileprivate func runSync() async {
        guard let api, let profile = activeProfile else { return }
        // Only the person using the app is synced, and only while the app's sign-in is still theirs.
        let signedIn = await api.currentProfileID
        guard signedIn == profile else { return }
        _ = await flushReports(api, profile)
        _ = await purgeExpired(api, profile)
        _ = await replayProgress(api, profile)
        await refreshProgress(api, profile)
    }

    /// True when every queued report was answered.
    fileprivate func flushReports(_ api: TuvimaAPIClient, _ profile: UUID) async -> Bool {
        for report in state(profile).reports {
            var status = 0
            do {
                switch report.kind {
                case .store:
                    status = try await api.reportDownloadStored(
                        assetID: report.assetId,
                        variantID: report.variantId,
                        storedBytes: report.storedBytes,
                        storedAt: report.storedAt
                    )
                case .delete:
                    status = try await api.reportDownloadDeleted(variantID: report.variantId)
                }
            } catch {
                // No connection, or the Engine could not be reached: keep the report and try again later.
                return false
            }
            switch OfflineLogic.classifyReport(kind: report.kind, statusCode: status) {
            case .done:
                removeReport(profile, report)
                if report.kind == .store { markReported(profile, report) }
            case .drop:
                removeReport(profile, report)
                // 404: the Engine no longer knows this copy, so its list will not show it and the next list read
                // removes the local copy.
                if report.kind == .store && status == 404 { markReported(profile, report) }
            case .retry:
                return false
            }
        }
        return true
    }

    fileprivate func removeReport(_ profile: UUID, _ report: PendingReport) {
        mutate(profile) { current in
            current.reports.removeAll { $0.variantId == report.variantId && $0.kind == report.kind }
        }
    }

    fileprivate func markReported(_ profile: UUID, _ report: PendingReport) {
        update(profile, report.assetId) { held in
            if held.variantId == report.variantId { held.reportedToServer = true }
        }
    }

    /// True when the Engine's list was read and applied. A copy that was reported but is no longer listed has
    /// expired on the Engine, so the local copy goes too.
    fileprivate func purgeExpired(_ api: TuvimaAPIClient, _ profile: UUID) async -> Bool {
        // Take this device's side first: a copy reported after this point cannot be mistaken for a missing row.
        let local = state(profile).records
        let listing: [ServerDownloadEntry]
        do {
            listing = try await api.deviceDownloads()
        } catch {
            // Offline: judge again at the next sync.
            return false
        }
        let result = OfflineLogic.reconcileWithServer(local: local, server: listing, now: Date())
        for gone in result.purge {
            if let current = record(profile, gone.assetId), current.variantId == gone.variantId, current.isComplete {
                removeLocalCopy(profile, current)
            }
        }
        for kept in result.keep {
            let before = local.first { $0.assetId == kept.assetId }
            if before?.expiresAt != kept.expiresAt {
                update(profile, kept.assetId) { $0.expiresAt = kept.expiresAt }
            }
        }
        return true
    }

    /// True when every pending entry was answered. Oldest first, so the Engine sees them in the order they happened.
    fileprivate func replayProgress(_ api: TuvimaAPIClient, _ profile: UUID) async -> Bool {
        for entry in OfflineLogic.replayOrder(state(profile).progress) {
            let answer: ServerProgress
            do {
                answer = try await api.replayOfflineProgress(entry, expectedRevision: entry.revision)
            } catch TuvimaClientError.http(let status, _) {
                if !OfflineLogic.isPermanentHttpFailure(status) { return false }
                // The Engine refuses this title for good (gone, or not allowed for this person): stop retrying it.
                var settled = entry
                settled.pending = false
                storeReplayResult(profile, sent: entry, updated: settled)
                continue
            } catch {
                return false
            }
            let updated: OfflineProgressEntry
            if OfflineLogic.isServerWon(sent: entry, expectedRevision: entry.revision, server: answer) {
                updated = OfflineLogic.adoptServerState(entry, server: answer, now: Date())
            } else {
                updated = OfflineLogic.markReplayed(entry, server: answer)
            }
            storeReplayResult(profile, sent: entry, updated: updated)
        }
        return true
    }

    fileprivate func storeReplayResult(_ profile: UUID, sent: OfflineProgressEntry, updated: OfflineProgressEntry) {
        mutate(profile) { current in
            if let index = current.progress.firstIndex(where: { $0.assetId == sent.assetId }) {
                current.progress[index] = OfflineLogic.applyReplayResult(
                    current: current.progress[index],
                    sent: sent,
                    updated: updated
                )
            }
        }
    }

    /// Takes the Engine's progress for finished downloads that were not played offline since, so a title watched
    /// elsewhere resumes from the right place. Progress waiting to be replayed is never replaced.
    fileprivate func refreshProgress(_ api: TuvimaAPIClient, _ profile: UUID) async {
        for held in state(profile).records where held.isComplete {
            let existing = state(profile).progress.first { $0.assetId == held.assetId }
            if existing?.pending == true { continue }
            let server: ServerProgress?
            do {
                server = try await api.serverProgress(assetID: held.assetId)
            } catch {
                // Offline: try again at the next sync.
                return
            }
            guard let server, OfflineLogic.shouldAdoptServerProgress(entry: existing, server: server) else { continue }
            mutate(profile) { current in
                let latest = current.progress.first { $0.assetId == held.assetId }
                if latest?.pending == true { return }
                current.progress.removeAll { $0.assetId == held.assetId }
                current.progress.append(
                    OfflineLogic.entryFromServer(assetId: held.assetId, existing: latest, server: server, now: Date())
                )
            }
        }
    }
}
