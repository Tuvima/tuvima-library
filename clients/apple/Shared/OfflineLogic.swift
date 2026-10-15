import Foundation

// Pure rules behind offline downloads. Nothing here touches the network, the disk or UIKit, so every rule can be
// checked by plain unit tests. The app stores the records, makes the calls and shows the screens; this file
// decides what the records mean. It mirrors the Android app's OfflineLogic so both phones behave the same.

public enum OfflineLogic {
    public static let qualitySmall = "mobile-small"
    public static let qualityStandard = "mobile-standard"
    public static let qualityAudio = "audio-mobile"

    /// Kept photos are the 2048 px preview (a fraction of the original's size); originals are not kept.
    public static let photoProfileKey = "photo-preview-2048"
    public static let photoEntityType = "photo"
    public static let photoPreviewEdge = 2048

    /// What one 2048 px preview is assumed to weigh until it is on disk; only used to plan how many to queue.
    public static let photoEstimateBytes: Int64 = 600 * 1024

    /// The Engine lists at most this many entries of any kind per device.
    public static let maxListedDownloads = 500

    /// Storage limit choices shown in Settings, in gigabytes, and the default.
    public static let capOptionsGB: [Int] = [2, 5, 10, 20, 50]
    public static let defaultCapGB = 10

    public static func capBytes(forGB gb: Int) -> Int64 { Int64(max(gb, 0)) * 1_073_741_824 }

    /// Times this close are treated as the same moment, to allow for the Engine rounding a stored time.
    private static let timeTolerance: TimeInterval = 1
    private static let positionTolerance = 0.5
    private static let percentTolerance = 0.5

    private static let allQualities: [QualityChoice] = [
        QualityChoice(key: qualitySmall, label: "Small (540p)"),
        QualityChoice(key: qualityStandard, label: "Standard (720p)"),
        QualityChoice(key: qualityAudio, label: "Audio (96 kbps)"),
    ]

    // MARK: Storage cap

    /// Bytes the downloads hold or have reserved: what is on disk, and for unfinished ones the size still to come.
    public static func committedBytes(_ records: [DownloadRecord]) -> Int64 {
        records.reduce(Int64(0)) { sum, record in
            if record.isComplete { return sum + max(record.bytesDone, 0) }
            return sum + max(max(record.bytesDone, record.totalBytes), 0)
        }
    }

    /// True when a new download may be queued. Blocked when the committed bytes already reach the cap, or when the
    /// estimated size (0 when the Engine has not said yet) would take them past it.
    public static func canQueue(committedBytes: Int64, capBytes: Int64, estimatedBytes: Int64 = 0) -> Bool {
        if capBytes <= 0 { return false }
        if committedBytes >= capBytes { return false }
        return committedBytes + max(estimatedBytes, 0) <= capBytes
    }

    // MARK: Expiry

    /// Compares the phone's finished downloads with the Engine's list. A finished download that was reported but whose
    /// row is gone has expired on the Engine and is purged; one whose listed expiry has passed is purged too; one still
    /// listed takes the Engine's expiry. A download not reported yet cannot be judged by the list and stays unless its
    /// own expiry has passed. Downloads still in progress are never touched.
    public static func reconcileWithServer(
        local: [DownloadRecord],
        server: [ServerDownloadEntry],
        now: Date
    ) -> ExpiryResult {
        var keep: [DownloadRecord] = []
        var purge: [DownloadRecord] = []
        for record in local {
            // Downloads still in progress are never judged, and neither is a kind this app does not keep yet (books).
            if !record.isComplete || record.itemKind == .book {
                keep.append(record)
                continue
            }
            let listed = server.first { serverEntryMatches($0, record) }
            if record.reportedToServer && listed == nil {
                purge.append(record)
            } else if let expiry = listed?.expiresAt, expiry <= now {
                purge.append(record)
            } else if listed == nil, let expiry = record.expiresAt, expiry <= now {
                purge.append(record)
            } else if let listed {
                var updated = record
                updated.expiresAt = listed.expiresAt
                keep.append(updated)
            } else {
                keep.append(record)
            }
        }
        return ExpiryResult(keep: keep, purge: purge)
    }

    /// True when this row of the Engine's list is the one for this local record: same kind, then the photo item or the
    /// asset and variant.
    public static func serverEntryMatches(_ entry: ServerDownloadEntry, _ record: DownloadRecord) -> Bool {
        switch record.itemKind {
        case .photo:
            return entry.kind == .photo && entry.photoItemId == record.assetId
        case .variant:
            return entry.kind == .variant && entry.assetId == record.assetId && entry.variantId == record.variantId
        case .book:
            return false
        }
    }

    /// True when a finished download should no longer be played because its expiry has passed.
    public static func isExpired(_ record: DownloadRecord, now: Date) -> Bool {
        guard record.isComplete, let expiry = record.expiresAt else { return false }
        return expiry <= now
    }

    // MARK: Reporting to the Engine's download list

    /// Reads the HTTP status of a report. Any 2xx is success, and 204 is what the Engine really answers. A delete the
    /// Engine does not know (404) is already done. A store the Engine refuses (404 unknown or expired variant, 409 the
    /// list is full) will never succeed, so it is dropped. Everything else, such as 401, 429 or 5xx, is tried again later.
    public static func classifyReport(kind: ReportKind, statusCode: Int) -> ReportOutcome {
        if (200...299).contains(statusCode) { return .done }
        if kind == .delete && statusCode == 404 { return .done }
        if kind == .store && (statusCode == 404 || statusCode == 409) { return .drop }
        if isPermanentHttpFailure(statusCode) { return .drop }
        return .retry
    }

    /// True for a 4xx answer that will not change on a retry (401, 408 and 429 are worth trying again).
    public static func isPermanentHttpFailure(_ statusCode: Int) -> Bool {
        (400...499).contains(statusCode) && statusCode != 401 && statusCode != 408 && statusCode != 429
    }

    /// Adds a report to the queue; a newer report for the same variant or photo replaces the older one (store then
    /// delete = delete).
    public static func queueReport(_ queue: [PendingReport], _ report: PendingReport) -> [PendingReport] {
        queue.filter { !($0.variantId == report.variantId && $0.item == report.item) } + [report]
    }

    /// True when this report is about this local record (the photo item for a photo, the variant for a variant).
    public static func reportMatches(_ report: PendingReport, _ record: DownloadRecord) -> Bool {
        switch report.item {
        case .photo: return record.itemKind == .photo && record.assetId == report.assetId
        case .variant: return record.itemKind == .variant && record.variantId == report.variantId
        case .book: return false
        }
    }

    // MARK: Keeping photos

    /// The file a kept photo's preview is saved under, in the person's offline folder.
    public static func photoFileName(_ itemID: UUID) -> String { "\(itemID.uuidString.lowercased()).jpg" }

    /// Chooses which photos of a gallery to keep. Videos are skipped (only a still frame is kept for a photo, which is
    /// not a useful copy of a video), as are photos already held and repeats in the list. It stops at the storage cap,
    /// counting `estimatePerPhoto` for each photo already chosen, and at the Engine's list limit (`maxListed` entries
    /// of any kind, of which `listedCount` are already used). The gallery is read in order, so the newest photos come
    /// first.
    public static func planPhotoKeep(
        candidates: [PhotoCandidate],
        heldIds: Set<UUID>,
        committedBytes: Int64,
        capBytes: Int64,
        listedCount: Int,
        estimatePerPhoto: Int64 = OfflineLogic.photoEstimateBytes,
        maxListed: Int = OfflineLogic.maxListedDownloads
    ) -> PhotoKeepPlan {
        var chosen: [UUID] = []
        var chosenSet = Set<UUID>()
        var alreadyKept = 0
        var skippedVideos = 0
        var stop = PhotoKeepStop.finished
        for candidate in candidates {
            if candidate.isVideo {
                skippedVideos += 1
                continue
            }
            if heldIds.contains(candidate.id) {
                alreadyKept += 1
                continue
            }
            if chosenSet.contains(candidate.id) { continue }
            if listedCount + chosen.count >= maxListed {
                stop = .listFull
                break
            }
            let reserved = Int64(chosen.count) * max(estimatePerPhoto, 0)
            if !canQueue(committedBytes: committedBytes + reserved, capBytes: capBytes, estimatedBytes: estimatePerPhoto) {
                stop = .storageCap
                break
            }
            chosen.append(candidate.id)
            chosenSet.insert(candidate.id)
        }
        return PhotoKeepPlan(toKeep: chosen, alreadyKept: alreadyKept, skippedVideos: skippedVideos, stop: stop)
    }

    /// The saved file name of a finished photo copy that may be read straight from the phone: a complete record of a
    /// photo whose file `fileExists`. The viewer asks this before it goes to the network.
    public static func localPhotoFileName(_ record: DownloadRecord?, fileExists: (String) -> Bool) -> String? {
        guard let record, record.itemKind == .photo, record.isComplete, let name = record.fileName else { return nil }
        return fileExists(name) ? name : nil
    }

    /// What the person is told after asking to keep a whole gallery.
    public static func keepGalleryMessage(found: Int, queued: Int, stop: PhotoKeepStop) -> String {
        var limit = ""
        switch stop {
        case .storageCap: limit = " Stopped at the storage limit; raise it in Settings to keep more."
        case .listFull: limit = " Stopped because the server tracks no more downloads for this device."
        case .finished: break
        }
        if found == 0 { return "This gallery has no photos to keep." }
        if queued == 0 && stop == .finished { return "These photos are already kept on this device." }
        if queued == 0 { return "No photos were kept." + limit }
        return "Keeping \(queued) photo(s) as 2048 px previews to save space. Track them under Downloads." + limit
    }

    // MARK: Offline progress replay

    /// Inserts or replaces the progress for an asset, keeping the revision last seen for it.
    public static func recordProgress(_ entries: [OfflineProgressEntry], _ entry: OfflineProgressEntry) -> [OfflineProgressEntry] {
        var merged = entry
        if let old = entries.first(where: { $0.assetId == entry.assetId }) { merged.revision = old.revision }
        return entries.filter { $0.assetId != entry.assetId } + [merged]
    }

    /// The entries still to replay, oldest recording first, so the Engine sees them in the order they happened.
    public static func replayOrder(_ entries: [OfflineProgressEntry]) -> [OfflineProgressEntry] {
        entries.filter { $0.pending }.sorted { left, right in
            if left.recordedAt != right.recordedAt { return left.recordedAt < right.recordedAt }
            return left.assetId.uuidString < right.assetId.uuidString
        }
    }

    /// True when the Engine kept its own record instead of the one just replayed. A write the Engine took is stamped
    /// with the replayed time and carries a newer revision; so the server won when its record is later than the
    /// replay, when its revision did not move past the one expected, or (a tie in time) when its position is not the
    /// one sent.
    public static func isServerWon(sent: OfflineProgressEntry, expectedRevision: Int64, server: ServerProgress) -> Bool {
        if let last = server.lastAccessed, last.timeIntervalSince(sent.recordedAt) > timeTolerance { return true }
        if server.revision <= expectedRevision { return true }
        if let position = server.positionSeconds { return abs(position - sent.positionSeconds) > positionTolerance }
        return abs(server.progressPct - sent.progressPct) > percentTolerance
    }

    /// The phone's record after the Engine's own state won: it takes the server's position, percent, time and revision.
    public static func adoptServerState(_ entry: OfflineProgressEntry, server: ServerProgress, now: Date) -> OfflineProgressEntry {
        var updated = entry
        if let position = server.positionSeconds {
            updated.positionSeconds = position
        } else if let duration = entry.durationSeconds, duration > 0 {
            updated.positionSeconds = duration * server.progressPct / 100
        }
        updated.progressPct = server.progressPct
        updated.recordedAt = server.lastAccessed ?? now
        updated.pending = false
        updated.revision = server.revision
        return updated
    }

    /// The phone's record after the Engine took it.
    public static func markReplayed(_ entry: OfflineProgressEntry, server: ServerProgress) -> OfflineProgressEntry {
        var updated = entry
        updated.pending = false
        updated.revision = server.revision
        return updated
    }

    /// What to keep after a replay finished: the answer when the entry did not change while it was being sent,
    /// otherwise the newer local entry (still pending) with the revision the Engine just reported.
    public static func applyReplayResult(
        current: OfflineProgressEntry,
        sent: OfflineProgressEntry,
        updated: OfflineProgressEntry
    ) -> OfflineProgressEntry {
        if current.recordedAt == sent.recordedAt && current.positionSeconds == sent.positionSeconds { return updated }
        var newer = current
        newer.revision = updated.revision
        return newer
    }

    /// True when the Engine's progress should replace what the phone holds: nothing is held, or what is held was
    /// already synced and the Engine's record is newer or has a later revision. Unsynced progress is never replaced.
    public static func shouldAdoptServerProgress(entry: OfflineProgressEntry?, server: ServerProgress) -> Bool {
        guard let entry else { return true }
        if entry.pending { return false }
        if server.revision > entry.revision { return true }
        if let last = server.lastAccessed { return last.timeIntervalSince(entry.recordedAt) > timeTolerance }
        return false
    }

    /// The phone's record built from the Engine's progress, keeping the duration already known.
    public static func entryFromServer(assetId: UUID, existing: OfflineProgressEntry?, server: ServerProgress, now: Date) -> OfflineProgressEntry {
        OfflineProgressEntry(
            assetId: assetId,
            positionSeconds: server.positionSeconds ?? 0,
            durationSeconds: existing?.durationSeconds,
            progressPct: server.progressPct,
            recordedAt: server.lastAccessed ?? now,
            pending: false,
            revision: server.revision
        )
    }

    /// Where a downloaded file starts playing: where it was left, or the start once it was (almost) finished.
    public static func resumeSeconds(_ entry: OfflineProgressEntry?) -> Double {
        guard let entry, entry.positionSeconds > 0, entry.progressPct < 95 else { return 0 }
        return entry.positionSeconds
    }

    public static func progressPercent(position: Double, duration: Double?) -> Double {
        guard let duration, duration > 0 else { return 0 }
        return min(max(position / duration * 100, 0), 100)
    }

    // MARK: Quality and kinds

    public static func downloadKind(forEntityType entityType: String) -> DownloadKind {
        let value = entityType.lowercased()
        let audioWords = ["audiobook", "music", "track", "album", "song"]
        if audioWords.contains(where: { value.contains($0) }) { return .audio }
        let excludedWords = ["photo", "book", "comic"]
        if excludedWords.contains(where: { value.contains($0) }) { return .excluded }
        let videoWords = ["movie", "episode", "tv", "show", "video"]
        if videoWords.contains(where: { value.contains($0) }) { return .video }
        return .unknown
    }

    public static func isShowEntity(_ entityType: String) -> Bool {
        ["tvshow", "show", "tv_show", "tv-show"].contains(entityType.lowercased())
    }

    public static func qualityChoices(for kind: DownloadKind) -> [QualityChoice] {
        switch kind {
        case .video: return allQualities.filter { $0.key != qualityAudio }
        case .audio: return allQualities.filter { $0.key == qualityAudio }
        case .unknown: return allQualities
        case .excluded: return []
        }
    }

    /// The quality to preselect: the person's default when it fits the kind, otherwise the first choice that does.
    public static func defaultQuality(for kind: DownloadKind, preferred: String) -> String {
        let choices = qualityChoices(for: kind)
        if choices.contains(where: { $0.key == preferred }) { return preferred }
        if choices.contains(where: { $0.key == qualityStandard }) { return qualityStandard }
        return choices.first?.key ?? preferred
    }

    public static func qualityLabel(_ profileKey: String) -> String {
        if profileKey == photoProfileKey { return "Preview (2048 px)" }
        return allQualities.first { $0.key == profileKey }?.label ?? profileKey
    }

    /// The file extension a downloaded copy is saved with: the player needs a real one to recognise the container.
    public static func fileExtension(forProfileKey profileKey: String) -> String {
        profileKey == qualityAudio ? "m4a" : "mp4"
    }

    /// The offline variant to download: the known one when resuming, otherwise the best ready variant for the profile
    /// (the one that lives longest). Nil while the Engine is still preparing it.
    public static func chooseReadyVariant(_ variants: [OfflineVariant], profileKey: String, knownVariantId: UUID?) -> OfflineVariant? {
        let ready = variants.filter { $0.status == "ready" }
        if let knownVariantId { return ready.first { $0.id == knownVariantId } }
        var matching = ready.filter { $0.profileKey == profileKey }
        if matching.isEmpty { matching = ready.filter { $0.profileKey == nil } }
        return matching.max { ($0.expiresAtDate ?? .distantPast) < ($1.expiresAtDate ?? .distantPast) }
    }

    // MARK: Words

    public static func formatBytes(_ bytes: Int64) -> String {
        let value = Double(max(bytes, 0))
        let kilo = 1024.0
        let mega = kilo * 1024.0
        let giga = mega * 1024.0
        if value >= giga { return String(format: "%.1f GB", value / giga) }
        if value >= mega { return String(format: "%.0f MB", value / mega) }
        if value >= kilo { return String(format: "%.0f KB", value / kilo) }
        return "\(Int64(value)) B"
    }

    /// "Expires in 12 days", "Expires in 5 hours", "Expired"; nil when the download never expires.
    public static func expiryLabel(_ expiresAt: Date?, now: Date) -> String? {
        guard let expiresAt else { return nil }
        let remaining = expiresAt.timeIntervalSince(now)
        if remaining <= 0 { return "Expired" }
        let days = Int(remaining / 86_400)
        if days >= 2 { return "Expires in \(days) days" }
        if days == 1 { return "Expires in 1 day" }
        let hours = Int(remaining / 3_600)
        return hours >= 1 ? "Expires in \(hours) hours" : "Expires within the hour"
    }

    public static func episodeLabel(season: Int?, episode: Int?) -> String? {
        if let season, season > 0, let episode { return "Season \(season), episode \(episode)" }
        if season == 0, let episode { return "Special \(episode)" }
        if let episode { return "Episode \(episode)" }
        return nil
    }

    /// 0.0 to 1.0, or nil while the size is unknown.
    public static func fraction(done: Int64, total: Int64) -> Double? {
        guard total > 0 else { return nil }
        return min(max(Double(done) / Double(total), 0), 1)
    }

    /// The status line of a row in the Downloads list.
    public static func statusText(_ record: DownloadRecord, bytesDone: Int64, now: Date) -> String {
        let separator = " \u{00B7} "
        var parts: [String] = []
        switch record.state {
        case .complete:
            parts.append(qualityLabel(record.profileKey))
            parts.append(formatBytes(record.bytesDone))
            if let expiry = expiryLabel(record.expiresAt, now: now) { parts.append(expiry) }
        case .downloading, .paused:
            parts.append(record.state == .paused ? "Paused" : "Downloading")
            if record.totalBytes > 0 {
                parts.append("\(formatBytes(bytesDone)) of \(formatBytes(record.totalBytes))")
            } else {
                parts.append(formatBytes(bytesDone))
            }
        case .preparing:
            parts.append("Waiting for the server to prepare this copy")
        case .queued:
            parts.append("Queued")
        case .failed:
            parts.append(record.error ?? "The download failed")
        }
        return parts.joined(separator: separator)
    }

    /// What the person is told after asking for the next episodes of a show.
    public static func nextEpisodesMessage(found: Int, queued: Int, overCap: Bool) -> String {
        if found == 0 { return "There are no more episodes after this one in your library." }
        if overCap && queued == 0 { return "Storage limit reached. Delete a download or raise the limit in Settings." }
        if overCap { return "Started \(queued) download(s); stopped at the storage limit." }
        if queued == 0 { return "The next episodes are already in your downloads." }
        if queued == 1 { return "Started downloading the next episode." }
        return "Started downloading the next \(queued) episodes."
    }

    // MARK: Times

    /// Parses an ISO 8601 time with or without an offset (no offset reads as UTC) and with any number of fraction
    /// digits; nil when blank or unreadable.
    public static func parseIso(_ text: String?) -> Date? {
        guard var value = text?.trimmingCharacters(in: .whitespaces), !value.isEmpty else { return nil }
        // The Engine (.NET) can write seven fraction digits; the system parser reads three.
        var hasFraction = false
        if let dot = value.firstIndex(of: ".") {
            var end = value.index(after: dot)
            while end < value.endIndex, value[end].isASCII, value[end].isNumber { end = value.index(after: end) }
            let digits = String(value[value.index(after: dot)..<end])
            if !digits.isEmpty {
                let millis = String(digits.prefix(3)).padding(toLength: 3, withPad: "0", startingAt: 0)
                value = String(value[..<dot]) + "." + millis + String(value[end...])
                hasFraction = true
            }
        }
        if !hasZone(value) { value += "Z" }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = hasFraction
            ? [.withInternetDateTime, .withFractionalSeconds]
            : [.withInternetDateTime]
        return formatter.date(from: value)
    }

    private static func hasZone(_ value: String) -> Bool {
        if value.hasSuffix("Z") || value.hasSuffix("z") { return true }
        guard let marker = value.firstIndex(where: { $0 == "T" || $0 == "t" }) else { return false }
        let time = value[value.index(after: marker)...]
        return time.contains("+") || time.contains("-")
    }

    /// UTC ISO 8601 with a trailing Z, the form the Engine's `client_timestamp` and `stored_at` read.
    public static func formatIsoUtc(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: date)
    }

    public static func formatSeconds(_ seconds: Double) -> String {
        String(format: "%.3f", max(seconds, 0))
    }
}
