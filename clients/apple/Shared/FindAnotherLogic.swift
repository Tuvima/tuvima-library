import Foundation

// The rules behind the "Find another" window on Apple TV and iPhone (Find another on TV, WP7): the Engine routes, the
// one-second reading rule and the wording of each row. Pure Swift with no views, no UIKit and no real timers, so a plain
// logic test can check every rule and a tvOS target and the iPhone app can share it.

// MARK: Routes

extension ClientRoutes {
    private static var textTracksRoot: String { "/api/v1/stream" }

    private static func textTracks(_ assetID: UUID) -> String {
        "\(textTracksRoot)/\(assetID.uuidString.lowercased())/text-tracks"
    }

    /// `GET`: whether a source is switched on for this item (no source is contacted).
    public static func textTrackSources(assetID: UUID, kind: TextTrackKind) -> String {
        "\(textTracks(assetID))/sources?kind=\(kind.rawValue)"
    }

    /// `GET`: the results the sources offer. `language` is for subtitles; blank means the Engine's own choice.
    public static func textTrackAlternatives(assetID: UUID, kind: TextTrackKind, language: String? = nil) -> String {
        var parts = ["kind=\(kind.rawValue)"]
        if let value = clean(language) { parts.append("language=\(percentEncode(value))") }
        return "\(textTracks(assetID))/alternatives?\(parts.joined(separator: "&"))"
    }

    /// `GET`: one result read on request.
    public static func textTrackPreview(
        assetID: UUID,
        kind: TextTrackKind,
        provider: String,
        sourceID: String,
        language: String? = nil
    ) -> String {
        var parts = [
            "kind=\(kind.rawValue)",
            "provider=\(percentEncode(provider))",
            "sourceId=\(percentEncode(sourceID))",
        ]
        if let value = clean(language) { parts.append("language=\(percentEncode(value))") }
        return "\(textTracks(assetID))/alternatives/preview?\(parts.joined(separator: "&"))"
    }

    /// `POST`: use one result. The body is `ChooseTextTrackAlternativeRequest.jsonObject`.
    public static func textTrackChoose(assetID: UUID) -> String {
        "\(textTracks(assetID))/alternatives/choose"
    }

    private static func clean(_ value: String?) -> String? {
        guard let value else { return nil }
        let text = value.trimmingCharacters(in: .whitespaces)
        return text.isEmpty ? nil : text
    }
}

// MARK: Reading on focus (D1, D10)

/// A cancellable piece of waiting work.
public protocol DwellCancellable: AnyObject {
    func cancel()
}

/// Runs an action after a delay and lets it be cancelled. The app uses `MainQueueDwellScheduler`; a test uses its own
/// manual one so nothing waits for real.
public protocol DwellScheduling: AnyObject {
    func schedule(after seconds: TimeInterval, _ action: @escaping () -> Void) -> DwellCancellable
}

/// The scheduler the apps use: the main queue.
public final class MainQueueDwellScheduler: DwellScheduling {
    private final class Item: DwellCancellable {
        let work: DispatchWorkItem
        init(_ work: DispatchWorkItem) { self.work = work }
        func cancel() { work.cancel() }
    }

    public init() {}

    public func schedule(after seconds: TimeInterval, _ action: @escaping () -> Void) -> DwellCancellable {
        let work = DispatchWorkItem(block: action)
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: work)
        return Item(work)
    }
}

/// When to read a result's words. A source's daily allowance is limited, so:
///
/// - the best match is read as soon as the list opens;
/// - any other row is read only when focus has rested on it for one second (`restSeconds`), so moving quickly through
///   ten rows reads nothing;
/// - moving away before the second is up cancels the read;
/// - a row already read (or asked for) in this window is never read again: the read-once cache.
///
/// One instance serves one window. Call it from one thread (the main one); `onRead` is called with the row's key.
public final class FindAnotherReadingRule {
    /// How long focus must rest on a row before it is read (D10).
    public static let restSeconds: TimeInterval = 1.0

    private let rest: TimeInterval
    private let scheduler: DwellScheduling
    private let onRead: (String) -> Void
    private var requested: Set<String> = []
    private var pending: DwellCancellable?
    private var pendingKey: String?
    /// Counts focus changes, so a read that was scheduled for an earlier focus can tell it has been overtaken.
    private var generation = 0

    public init(
        rest: TimeInterval = FindAnotherReadingRule.restSeconds,
        scheduler: DwellScheduling,
        onRead: @escaping (String) -> Void
    ) {
        self.rest = rest
        self.scheduler = scheduler
        self.onRead = onRead
    }

    /// Whether this row has been read, or asked for, in this window.
    public func hasRead(_ key: String) -> Bool { requested.contains(key) }

    /// The key a one-second wait is running for, if any.
    public var waitingKey: String? { pendingKey }

    /// A new list has opened (or been searched again). Starts a fresh window and reads the best match at once, unless
    /// it came back already read (`alreadyRead` lists the keys whose words came with the list).
    public func listOpened(bestMatchKey: String?, alreadyRead: [String] = []) {
        reset()
        requested.formUnion(alreadyRead)
        if let bestMatchKey, !requested.contains(bestMatchKey) {
            requested.insert(bestMatchKey)
            onRead(bestMatchKey)
        }
    }

    /// Focus moved to a row (nil when it left the list). Cancels any wait for the previous row, then waits one second for
    /// this one unless it was already read.
    public func focusChanged(to key: String?) {
        cancelPending()
        generation += 1
        guard let key, !requested.contains(key) else { return }
        let expected = generation
        pendingKey = key
        pending = scheduler.schedule(after: rest) { [weak self] in
            guard let self, self.generation == expected, self.pendingKey == key else { return }
            self.pending = nil
            self.pendingKey = nil
            // Marked before the call, so a read that fails is still not repeated in this window.
            self.requested.insert(key)
            self.onRead(key)
        }
    }

    /// The person selected a row: read it now, once, without waiting for the second to pass.
    public func readNow(_ key: String) {
        if pendingKey == key { cancelPending() }
        guard !requested.contains(key) else { return }
        requested.insert(key)
        onRead(key)
    }

    /// The window closed: nothing more is read and the cache is dropped.
    public func close() { reset() }

    private func reset() {
        cancelPending()
        generation += 1
        requested.removeAll()
    }

    private func cancelPending() {
        pending?.cancel()
        pending = nil
        pendingKey = nil
    }
}

// MARK: Row wording

/// How a check reads in colour: green, amber, red, or plain.
public enum FindAnotherTone: Equatable, Sendable {
    case neutral, ok, warn, bad
}

/// One plain status on a result's row ("Synced", "Fits the film", "Ends 3 minutes short of the film").
public struct FindAnotherCheck: Equatable, Sendable {
    public let text: String
    public let tone: FindAnotherTone

    public init(_ text: String, _ tone: FindAnotherTone = .neutral) {
        self.text = text
        self.tone = tone
    }
}

/// One real line of the result's words, with the time it comes up when the result is timed.
public struct FindAnotherLine: Equatable, Sendable {
    public let time: String?
    public let text: String

    public init(time: String?, text: String) {
        self.time = time
        self.text = text
    }
}

/// The words of the "Find another" window, the same plain sentences the Dashboard uses (US English).
public enum FindAnotherWording {
    /// A subtitle at or above this confidence was made for the file in hand ("Made for your file").
    public static let sameReleaseConfidence = 0.9

    /// A lyrics length off by up to this many seconds reads amber; beyond it, red.
    public static let lyricsAmberUpToSeconds = 15.0

    /// A subtitle file off by up to this many minutes reads amber; beyond it, red.
    public static let subtitleAmberUpToMinutes = 15.0

    public static let readingText = "Reading…"
    public static let couldNotReadText = "Could not read this one"
    public static let untitledResult = "Untitled result"
    public static let hearingImpaired = "Hearing impaired"
    public static let bestMatchChip = "Best match"
    public static let inUseChip = "In use"
    public static let couldNotUseText = "Could not use this one"

    // MARK: Rows

    /// A subtitle row's title: the release name.
    public static func subtitleTitle(_ offer: TextTrackAlternative) -> String {
        nonBlank(offer.releaseName) ?? untitledResult
    }

    /// A subtitle row's muted line: the language (only when the list mixes languages) and "Hearing impaired".
    public static func subtitleDetail(_ offer: TextTrackAlternative, showLanguage: Bool, languageName: String) -> String? {
        var parts: [String] = []
        if showLanguage { parts.append(languageName) }
        if offer.isHearingImpaired { parts.append(hearingImpaired) }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    /// The two checks of a subtitle row: made for your file or not, then (once the file has been read) the timing.
    public static func subtitleChecks(
        _ offer: TextTrackAlternative,
        sameReleaseConfidence: Double = FindAnotherWording.sameReleaseConfidence
    ) -> [FindAnotherCheck] {
        var checks = [
            offer.confidence >= sameReleaseConfidence
                ? FindAnotherCheck("Made for your file", .ok)
                : FindAnotherCheck("A different release, may be out of step", .warn),
        ]
        // The timing check needs the file's last line, so it appears only after the file has been read.
        if let check = offer.lengthCheck, let difference = offer.lengthDifferenceSeconds {
            if check == "Fits" {
                checks.append(FindAnotherCheck("Fits the film", .ok))
            } else {
                let minutes = abs(difference) / 60
                let how = difference < 0 ? "short of" : "past"
                checks.append(FindAnotherCheck(
                    "Ends \(count(minutes, "minute")) \(how) the film",
                    minutes <= subtitleAmberUpToMinutes ? .warn : .bad
                ))
            }
        }
        return checks
    }

    /// A lyrics row's title: the source's own name for the song.
    public static func lyricsTitle(_ offer: TextTrackAlternative) -> String {
        nonBlank(offer.title) ?? nonBlank(offer.releaseName) ?? untitledResult
    }

    /// A lyrics row's muted line: artist, album and length.
    public static func lyricsDetail(_ offer: TextTrackAlternative) -> String? {
        var parts: [String] = []
        if let artist = nonBlank(offer.artist) { parts.append(artist) }
        if let album = nonBlank(offer.album) { parts.append(album) }
        if let length = offer.lengthSeconds, length > 0 { parts.append(clock(length)) }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    /// The checks of a lyrics row: synced or words only, then (when the source gave a length) how it fits the song.
    public static func lyricsChecks(_ offer: TextTrackAlternative) -> [FindAnotherCheck] {
        var checks = [
            offer.isTimed
                ? FindAnotherCheck("Synced", .ok)
                : FindAnotherCheck("Words only, not timed"),
        ]
        if let check = offer.lengthCheck, let difference = offer.lengthDifferenceSeconds {
            if check == "Fits" {
                checks.append(FindAnotherCheck("Fits the song", .ok))
            } else {
                let how = difference > 0 ? "longer" : "shorter"
                checks.append(FindAnotherCheck(
                    "\(count(abs(difference), "second")) \(how) than the song",
                    abs(difference) <= lyricsAmberUpToSeconds ? .warn : .bad
                ))
            }
        }
        return checks
    }

    /// The words of a result for the pane: the first lines, each with the time it comes up when `timed` is true.
    /// Empty until the result has been read.
    public static func lines(_ offer: TextTrackAlternative, timed: Bool) -> [FindAnotherLine] {
        offer.previewLines.map { line in
            FindAnotherLine(time: timed ? line.atSeconds.map(clock) : nil, text: line.text)
        }
    }

    /// Minutes and seconds ("3:42"), or with hours when long ("1:05:09").
    public static func clock(_ seconds: Double) -> String {
        let total = Int(max(0, seconds.rounded()))
        let hours = total / 3600
        let minutes = (total % 3600) / 60
        let rest = total % 60
        return hours > 0
            ? "\(hours):\(twoDigits(minutes)):\(twoDigits(rest))"
            : "\(minutes):\(twoDigits(rest))"
    }

    // MARK: Problems

    /// The one plain sentence for a list that is empty or came back with a problem; nil when there are results to show.
    /// The Engine's own message is used when it sent one.
    /// - Parameters:
    ///   - languageName: the language that was searched, as a person says it ("English").
    ///   - isEpisode: for subtitles, whether the item is an episode rather than a movie.
    public static func problemSentence(
        for response: TextTrackAlternativesResponse,
        kind: TextTrackKind,
        languageName: String? = nil,
        isEpisode: Bool = false
    ) -> String? {
        if response.status == "Found" && !response.items.isEmpty { return nil }
        if let message = nonBlank(response.message) { return message }
        let source = nonBlank(response.sourceName) ?? "The \(kind == .lyrics ? "lyrics" : "subtitle") source"
        let thing = kind == .lyrics ? "song" : (isEpisode ? "episode" : "movie")
        switch response.status {
        case "NoSourceOn":
            return kind == .lyrics ? "No lyrics source is switched on." : "No subtitle source is switched on."
        case "SourceKeyRequired":
            return "\(source) did not accept its key."
        case "IdentityUnverified":
            return "This \(thing) could not be matched for an online search."
        case "LimitReached":
            return "\(source) has reached its limit for now. Try again later."
        case "Unavailable":
            return "\(source) could not be reached."
        case "Blocked":
            return "This library never looks anything up online."
        case "NotAllowed":
            return kind == .lyrics ? "Your profile cannot search for lyrics." : "Your profile cannot search for subtitles."
        case "LanguageUnknown":
            return "That language is not supported for subtitle search."
        case "NoneFound", "Found":
            if kind == .lyrics { return "No lyrics were found for this song." }
            let language = nonBlank(languageName).map { "\($0) " } ?? ""
            return "No \(language)subtitles were found for this \(thing)."
        default:
            return "The search could not be completed."
        }
    }

    /// What the pane says when a row could not be read: the limit sentence when the source's allowance is used up,
    /// otherwise "Could not read this one". The row can still be used either way.
    public static func previewProblemSentence(status: String, sourceName: String? = nil) -> String {
        if status == "LimitReached" {
            return "\(nonBlank(sourceName) ?? "The source") has reached its limit for now. Try again later."
        }
        return couldNotReadText
    }

    /// A refused use-this call: the Engine's message when it sent one.
    public static func choiceProblemSentence(_ response: TextTrackChoiceResponse) -> String {
        nonBlank(response.message) ?? couldNotUseText
    }

    // MARK: Helpers

    private static func nonBlank(_ value: String?) -> String? {
        guard let text = value?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty else { return nil }
        return text
    }

    /// "1 minute", "3 minutes": a whole number, never below one.
    private static func count(_ value: Double, _ unit: String) -> String {
        let whole = max(1, Int(value.rounded()))
        return "\(whole) \(unit)\(whole == 1 ? "" : "s")"
    }

    private static func twoDigits(_ value: Int) -> String { value < 10 ? "0\(value)" : "\(value)" }
}
