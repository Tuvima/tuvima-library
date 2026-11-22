import XCTest
@testable import TuvimaCore

/// Pure-logic and wire-shape tests for Find another (Find another on TV, WP7): the models, the Engine routes, the
/// one-second reading rule and the row wording. They need no device, server or real waiting. Like the other tests they
/// are not part of any target yet: see clients/apple/README.md.
final class FindAnotherTests: XCTestCase {
    private let asset = UUID(uuidString: "6f0c2a3e-0000-0000-0000-0000000000a1")!

    private func decode<T: Decodable>(_ type: T.Type, _ json: String) throws -> T {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return try decoder.decode(T.self, from: Data(json.utf8))
    }

    // MARK: Models

    func testAnAlternativesAnswerDecodesTheEnginesSnakeCaseNames() throws {
        let json = """
        {
          "asset_id": "6f0c2a3e-0000-0000-0000-0000000000a1",
          "kind": "Subtitles",
          "status": "Found",
          "message": "",
          "source_name": "SubDL",
          "language": "en",
          "items": [
            {
              "provider": "subdl", "provider_name": "SubDL", "source_id": "abc-1", "language": "en",
              "is_timed": true, "confidence": 0.95, "duration_match_score": 0.8, "is_hearing_impaired": true,
              "release_name": "Dune.2021.2160p", "preview": null, "is_current": true,
              "length_seconds": null, "item_length_seconds": 9300.0,
              "preview_lines": [{"at_seconds": 62.5, "text": "Hello"}],
              "ends_at_seconds": 9290.0, "length_check": "Fits", "length_difference_seconds": -10.0
            }
          ]
        }
        """
        let response = try decode(TextTrackAlternativesResponse.self, json)
        XCTAssertEqual(response.assetId, asset)
        XCTAssertEqual(response.status, "Found")
        XCTAssertEqual(response.sourceName, "SubDL")
        let item = try XCTUnwrap(response.items.first)
        XCTAssertEqual(item.sourceId, "abc-1")
        XCTAssertEqual(item.providerName, "SubDL")
        XCTAssertEqual(item.key, "subdl:abc-1")
        XCTAssertTrue(item.isTimed)
        XCTAssertTrue(item.isHearingImpaired)
        XCTAssertTrue(item.isCurrent)
        XCTAssertEqual(item.durationMatchScore, 0.8)
        XCTAssertNil(item.preview)
        XCTAssertEqual(item.itemLengthSeconds, 9300)
        XCTAssertEqual(item.previewLines, [TextTrackPreviewLine(atSeconds: 62.5, text: "Hello")])
        XCTAssertEqual(item.endsAtSeconds, 9290)
        XCTAssertEqual(item.lengthCheck, "Fits")
        XCTAssertEqual(item.lengthDifferenceSeconds, -10)
    }

    func testAnAbsentFieldNeverFailsTheWholeList() throws {
        let json = """
        {"asset_id": "6f0c2a3e-0000-0000-0000-0000000000a1", "status": "NoSourceOn",
         "items": [{"provider": "lrclib", "source_id": "7"}]}
        """
        let response = try decode(TextTrackAlternativesResponse.self, json)
        XCTAssertEqual(response.message, "")
        XCTAssertEqual(response.items.count, 1)
        XCTAssertEqual(response.items[0].language, "und")
        XCTAssertEqual(response.items[0].previewLines, [])
        XCTAssertNil(response.sourceName)
    }

    func testAPreviewAnswerDecodesItsItem() throws {
        let json = """
        {"asset_id": "6f0c2a3e-0000-0000-0000-0000000000a1", "status": "Found",
         "item": {"provider": "subdl", "source_id": "z", "preview_lines": [{"at_seconds": 1.0, "text": "One"}]}}
        """
        let response = try decode(TextTrackAlternativePreviewResponse.self, json)
        XCTAssertEqual(response.status, "Found")
        XCTAssertEqual(response.item?.previewLines.first?.text, "One")

        let missing = try decode(
            TextTrackAlternativePreviewResponse.self,
            #"{"asset_id": "6f0c2a3e-0000-0000-0000-0000000000a1", "status": "NotFound", "item": null}"#
        )
        XCTAssertNil(missing.item)
    }

    func testASourcesAnswerSaysReadyOnlyForReady() throws {
        let ready = try decode(
            TextTrackSourcesResponse.self,
            #"{"asset_id": "6f0c2a3e-0000-0000-0000-0000000000a1", "kind": "Subtitles", "status": "Ready"}"#
        )
        let none = try decode(
            TextTrackSourcesResponse.self,
            #"{"asset_id": "6f0c2a3e-0000-0000-0000-0000000000a1", "kind": "Subtitles", "status": "NoSourceOn"}"#
        )
        XCTAssertTrue(ready.isReady)
        XCTAssertFalse(none.isReady)
    }

    func testAChoiceAnswerDecodesTheTrackNowInUse() throws {
        let json = """
        {"asset_id": "6f0c2a3e-0000-0000-0000-0000000000a1", "status": "Updated", "message": "",
         "track_id": "6f0c2a3e-0000-0000-0000-0000000000b2"}
        """
        let response = try decode(TextTrackChoiceResponse.self, json)
        XCTAssertTrue(response.isUpdated)
        XCTAssertEqual(response.trackId, UUID(uuidString: "6f0c2a3e-0000-0000-0000-0000000000b2"))

        let refused = try decode(
            TextTrackChoiceResponse.self,
            #"{"asset_id": "6f0c2a3e-0000-0000-0000-0000000000a1", "status": "Failed", "message": "No.", "track_id": null}"#
        )
        XCTAssertFalse(refused.isUpdated)
        XCTAssertNil(refused.trackId)
    }

    func testTheChooseBodyUsesTheEnginesSnakeCaseNamesAndOmitsABlankLanguage() {
        let withLanguage = ChooseTextTrackAlternativeRequest(
            kind: .subtitles, provider: "subdl", sourceId: "abc-1", language: "English"
        ).jsonObject
        XCTAssertEqual(withLanguage["kind"] as? String, "subtitles")
        XCTAssertEqual(withLanguage["provider"] as? String, "subdl")
        XCTAssertEqual(withLanguage["source_id"] as? String, "abc-1")
        XCTAssertEqual(withLanguage["language"] as? String, "English")
        XCTAssertNil(withLanguage["sourceId"])

        let withoutLanguage = ChooseTextTrackAlternativeRequest(kind: .lyrics, provider: "lrclib", sourceId: "7").jsonObject
        XCTAssertEqual(Set(withoutLanguage.keys), ["kind", "provider", "source_id"])
    }

    func testTheModelsRoundTripThroughSnakeCase() throws {
        let original = TextTrackAlternativesResponse(
            assetId: asset, kind: "Lyrics", status: "Found",
            items: [TextTrackAlternative(provider: "lrclib", sourceId: "7", isTimed: true, title: "Smile")],
            sourceName: "LRCLIB"
        )
        let encoder = JSONEncoder()
        encoder.keyEncodingStrategy = .convertToSnakeCase
        let data = try encoder.encode(original)
        XCTAssertTrue(String(decoding: data, as: UTF8.self).contains("\"source_id\""))
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        XCTAssertEqual(try decoder.decode(TextTrackAlternativesResponse.self, from: data), original)
    }

    // MARK: Routes

    func testTheRoutesStayInsideTheAppDoorAndCarryTheEnginesQueryNames() {
        XCTAssertEqual(
            ClientRoutes.textTrackSources(assetID: asset, kind: .subtitles),
            "/api/v1/stream/6f0c2a3e-0000-0000-0000-0000000000a1/text-tracks/sources?kind=subtitles"
        )
        XCTAssertEqual(
            ClientRoutes.textTrackAlternatives(assetID: asset, kind: .lyrics),
            "/api/v1/stream/6f0c2a3e-0000-0000-0000-0000000000a1/text-tracks/alternatives?kind=lyrics"
        )
        XCTAssertEqual(
            ClientRoutes.textTrackAlternatives(assetID: asset, kind: .subtitles, language: "pt-BR"),
            "/api/v1/stream/6f0c2a3e-0000-0000-0000-0000000000a1/text-tracks/alternatives?kind=subtitles&language=pt-BR"
        )
        XCTAssertEqual(
            ClientRoutes.textTrackPreview(assetID: asset, kind: .subtitles, provider: "subdl", sourceID: "a b/c", language: "English"),
            "/api/v1/stream/6f0c2a3e-0000-0000-0000-0000000000a1/text-tracks/alternatives/preview"
                + "?kind=subtitles&provider=subdl&sourceId=a%20b%2Fc&language=English"
        )
        XCTAssertEqual(
            ClientRoutes.textTrackChoose(assetID: asset),
            "/api/v1/stream/6f0c2a3e-0000-0000-0000-0000000000a1/text-tracks/alternatives/choose"
        )
    }

    func testABlankLanguageIsLeftOutOfTheRoutes() {
        XCTAssertFalse(ClientRoutes.textTrackAlternatives(assetID: asset, kind: .subtitles, language: "  ").contains("language"))
        XCTAssertFalse(
            ClientRoutes.textTrackPreview(assetID: asset, kind: .subtitles, provider: "p", sourceID: "s", language: "").contains("language")
        )
    }

    // MARK: Reading on focus

    /// A hand-written scheduler: nothing runs until the test moves the clock.
    private final class ManualScheduler: DwellScheduling {
        private final class Job: DwellCancellable {
            let due: TimeInterval
            let action: () -> Void
            var cancelled = false
            init(due: TimeInterval, action: @escaping () -> Void) { self.due = due; self.action = action }
            func cancel() { cancelled = true }
        }

        private var jobs: [Job] = []
        private(set) var now: TimeInterval = 0

        func schedule(after seconds: TimeInterval, _ action: @escaping () -> Void) -> DwellCancellable {
            let job = Job(due: now + seconds, action: action)
            jobs.append(job)
            return job
        }

        func advance(by seconds: TimeInterval) {
            now += seconds
            let ready = jobs.filter { !$0.cancelled && $0.due <= now }
            jobs.removeAll { $0.cancelled || $0.due <= now }
            ready.forEach { $0.action() }
        }
    }

    private func makeRule(_ scheduler: ManualScheduler, reads: @escaping (String) -> Void) -> FindAnotherReadingRule {
        FindAnotherReadingRule(scheduler: scheduler, onRead: reads)
    }

    func testTheBestMatchIsReadAsSoonAsTheListOpens() {
        let scheduler = ManualScheduler()
        var read: [String] = []
        let rule = makeRule(scheduler) { read.append($0) }
        rule.listOpened(bestMatchKey: "a")
        XCTAssertEqual(read, ["a"])
    }

    func testABestMatchThatCameBackAlreadyReadIsNotReadAgain() {
        let scheduler = ManualScheduler()
        var read: [String] = []
        let rule = makeRule(scheduler) { read.append($0) }
        rule.listOpened(bestMatchKey: "a", alreadyRead: ["a"])
        XCTAssertEqual(read, [])
        rule.focusChanged(to: "a")
        scheduler.advance(by: 5)
        XCTAssertEqual(read, [])
    }

    func testARowIsReadOnlyAfterFocusRestsOnItForOneSecond() {
        let scheduler = ManualScheduler()
        var read: [String] = []
        let rule = makeRule(scheduler) { read.append($0) }
        rule.listOpened(bestMatchKey: "a")
        rule.focusChanged(to: "b")
        scheduler.advance(by: 0.9)
        XCTAssertEqual(read, ["a"])
        XCTAssertEqual(rule.waitingKey, "b")
        scheduler.advance(by: 0.1)
        XCTAssertEqual(read, ["a", "b"])
        XCTAssertNil(rule.waitingKey)
    }

    func testScrollingQuicklyThroughRowsReadsNothing() {
        let scheduler = ManualScheduler()
        var read: [String] = []
        let rule = makeRule(scheduler) { read.append($0) }
        rule.listOpened(bestMatchKey: nil)
        for key in ["b", "c", "d", "e", "f", "g", "h", "i", "j", "k"] {
            rule.focusChanged(to: key)
            scheduler.advance(by: 0.4)
        }
        rule.focusChanged(to: nil)
        scheduler.advance(by: 5)
        XCTAssertEqual(read, [])
    }

    func testMovingAwayBeforeTheSecondIsUpCancelsTheRead() {
        let scheduler = ManualScheduler()
        var read: [String] = []
        let rule = makeRule(scheduler) { read.append($0) }
        rule.listOpened(bestMatchKey: nil)
        rule.focusChanged(to: "b")
        scheduler.advance(by: 0.6)
        rule.focusChanged(to: "c")
        scheduler.advance(by: 0.6)
        XCTAssertEqual(read, [])
        scheduler.advance(by: 0.4)
        XCTAssertEqual(read, ["c"])
    }

    func testARowAlreadyReadIsNeverReadAgainInTheSameWindow() {
        let scheduler = ManualScheduler()
        var read: [String] = []
        let rule = makeRule(scheduler) { read.append($0) }
        rule.listOpened(bestMatchKey: "a")
        rule.focusChanged(to: "b")
        scheduler.advance(by: 1)
        rule.focusChanged(to: "a")
        scheduler.advance(by: 3)
        rule.focusChanged(to: "b")
        scheduler.advance(by: 3)
        XCTAssertEqual(read, ["a", "b"])
        XCTAssertTrue(rule.hasRead("a"))
        XCTAssertTrue(rule.hasRead("b"))
    }

    func testAReadThatFailedIsNotRepeatedEither() {
        let scheduler = ManualScheduler()
        var read: [String] = []
        let rule = makeRule(scheduler) { read.append($0) }
        rule.listOpened(bestMatchKey: nil)
        rule.focusChanged(to: "b")
        scheduler.advance(by: 1)
        rule.focusChanged(to: "c")
        rule.focusChanged(to: "b")
        scheduler.advance(by: 5)
        XCTAssertEqual(read, ["b"])
    }

    func testSelectingARowReadsItAtOnceAndOnlyOnce() {
        let scheduler = ManualScheduler()
        var read: [String] = []
        let rule = makeRule(scheduler) { read.append($0) }
        rule.listOpened(bestMatchKey: nil)
        rule.focusChanged(to: "b")
        scheduler.advance(by: 0.3)
        rule.readNow("b")
        XCTAssertEqual(read, ["b"])
        scheduler.advance(by: 5)
        rule.readNow("b")
        XCTAssertEqual(read, ["b"])
    }

    func testANewWindowStartsFreshAndDropsTheWaitingRead() {
        let scheduler = ManualScheduler()
        var read: [String] = []
        let rule = makeRule(scheduler) { read.append($0) }
        rule.listOpened(bestMatchKey: "a")
        rule.focusChanged(to: "b")
        rule.listOpened(bestMatchKey: "a")
        scheduler.advance(by: 5)
        XCTAssertEqual(read, ["a", "a"])
        XCTAssertFalse(rule.hasRead("b"))
    }

    func testClosingTheWindowStopsAnyWaitingRead() {
        let scheduler = ManualScheduler()
        var read: [String] = []
        let rule = makeRule(scheduler) { read.append($0) }
        rule.listOpened(bestMatchKey: nil)
        rule.focusChanged(to: "b")
        rule.close()
        scheduler.advance(by: 5)
        XCTAssertEqual(read, [])
    }

    func testTheRestIsOneSecond() {
        XCTAssertEqual(FindAnotherReadingRule.restSeconds, 1.0)
    }

    // MARK: Row wording

    func testASubtitleRowSaysWhetherItWasMadeForYourFile() {
        let same = TextTrackAlternative(provider: "p", sourceId: "1", confidence: 0.9)
        let other = TextTrackAlternative(provider: "p", sourceId: "2", confidence: 0.89)
        XCTAssertEqual(FindAnotherWording.subtitleChecks(same), [FindAnotherCheck("Made for your file", .ok)])
        XCTAssertEqual(
            FindAnotherWording.subtitleChecks(other),
            [FindAnotherCheck("A different release, may be out of step", .warn)]
        )
    }

    func testTheFilmTimingCheckAppearsOnlyOnceTheFileHasBeenRead() {
        let fits = TextTrackAlternative(provider: "p", sourceId: "1", confidence: 1, lengthCheck: "Fits", lengthDifferenceSeconds: -20)
        let short = TextTrackAlternative(provider: "p", sourceId: "2", confidence: 1, lengthCheck: "Off", lengthDifferenceSeconds: -180)
        let long = TextTrackAlternative(provider: "p", sourceId: "3", confidence: 1, lengthCheck: "Off", lengthDifferenceSeconds: 20 * 60)
        let one = TextTrackAlternative(provider: "p", sourceId: "4", confidence: 1, lengthCheck: "Off", lengthDifferenceSeconds: 60)
        let unread = TextTrackAlternative(provider: "p", sourceId: "5", confidence: 1)
        XCTAssertEqual(FindAnotherWording.subtitleChecks(fits).last, FindAnotherCheck("Fits the film", .ok))
        XCTAssertEqual(FindAnotherWording.subtitleChecks(short).last, FindAnotherCheck("Ends 3 minutes short of the film", .warn))
        XCTAssertEqual(FindAnotherWording.subtitleChecks(long).last, FindAnotherCheck("Ends 20 minutes past the film", .bad))
        XCTAssertEqual(FindAnotherWording.subtitleChecks(one).last, FindAnotherCheck("Ends 1 minute past the film", .warn))
        XCTAssertEqual(FindAnotherWording.subtitleChecks(unread).count, 1)
    }

    func testALyricsRowSaysSyncedOrWordsOnlyAndHowItFitsTheSong() {
        let synced = TextTrackAlternative(provider: "p", sourceId: "1", isTimed: true, lengthCheck: "Fits", lengthDifferenceSeconds: 2)
        let words = TextTrackAlternative(provider: "p", sourceId: "2", isTimed: false, lengthCheck: "Off", lengthDifferenceSeconds: 28)
        let far = TextTrackAlternative(provider: "p", sourceId: "3", isTimed: true, lengthCheck: "Off", lengthDifferenceSeconds: -61)
        let noLength = TextTrackAlternative(provider: "p", sourceId: "4", isTimed: true)
        XCTAssertEqual(FindAnotherWording.lyricsChecks(synced), [FindAnotherCheck("Synced", .ok), FindAnotherCheck("Fits the song", .ok)])
        XCTAssertEqual(
            FindAnotherWording.lyricsChecks(words),
            [FindAnotherCheck("Words only, not timed"), FindAnotherCheck("28 seconds longer than the song", .bad)]
        )
        XCTAssertEqual(FindAnotherWording.lyricsChecks(far).last, FindAnotherCheck("61 seconds shorter than the song", .bad))
        XCTAssertEqual(FindAnotherWording.lyricsChecks(noLength), [FindAnotherCheck("Synced", .ok)])
        let near = TextTrackAlternative(provider: "p", sourceId: "5", lengthCheck: "Off", lengthDifferenceSeconds: 15)
        XCTAssertEqual(FindAnotherWording.lyricsChecks(near).last?.tone, .warn)
    }

    func testRowTitlesAndMutedLinesFollowTheDashboard() {
        let lyrics = TextTrackAlternative(
            provider: "p", sourceId: "1", title: "Smile", artist: "Empty Soul", album: "Silhouettes", lengthSeconds: 222
        )
        XCTAssertEqual(FindAnotherWording.lyricsTitle(lyrics), "Smile")
        XCTAssertEqual(FindAnotherWording.lyricsDetail(lyrics), "Empty Soul · Silhouettes · 3:42")
        XCTAssertEqual(FindAnotherWording.lyricsTitle(TextTrackAlternative(provider: "p", sourceId: "2")), "Untitled result")

        let subtitle = TextTrackAlternative(provider: "p", sourceId: "3", isHearingImpaired: true, releaseName: "Dune.2021")
        XCTAssertEqual(FindAnotherWording.subtitleTitle(subtitle), "Dune.2021")
        XCTAssertEqual(FindAnotherWording.subtitleDetail(subtitle, showLanguage: false, languageName: "English"), "Hearing impaired")
        XCTAssertEqual(
            FindAnotherWording.subtitleDetail(subtitle, showLanguage: true, languageName: "English"),
            "English · Hearing impaired"
        )
        XCTAssertNil(
            FindAnotherWording.subtitleDetail(TextTrackAlternative(provider: "p", sourceId: "4"), showLanguage: false, languageName: "English")
        )
    }

    func testPreviewLinesShowTheirTimeOnlyForSubtitles() {
        let offer = TextTrackAlternative(
            provider: "p", sourceId: "1",
            previewLines: [TextTrackPreviewLine(atSeconds: 3725, text: "Hi"), TextTrackPreviewLine(text: "No time")]
        )
        XCTAssertEqual(
            FindAnotherWording.lines(offer, timed: true),
            [FindAnotherLine(time: "1:02:05", text: "Hi"), FindAnotherLine(time: nil, text: "No time")]
        )
        XCTAssertEqual(FindAnotherWording.lines(offer, timed: false).map { $0.time }, [nil, nil])
        XCTAssertEqual(FindAnotherWording.clock(62.4), "1:02")
        XCTAssertEqual(FindAnotherWording.clock(-5), "0:00")
    }

    // MARK: Problems in plain words

    private func answer(_ status: String, message: String = "", source: String? = nil, items: [TextTrackAlternative] = [])
        -> TextTrackAlternativesResponse
    {
        TextTrackAlternativesResponse(assetId: asset, kind: "Subtitles", status: status, message: message, items: items, sourceName: source)
    }

    func testAListWithResultsHasNoProblemSentence() {
        let found = answer("Found", items: [TextTrackAlternative(provider: "p", sourceId: "1")])
        XCTAssertNil(FindAnotherWording.problemSentence(for: found, kind: .subtitles))
    }

    func testEachStatusReadsAsOnePlainSentence() {
        func sentence(_ status: String, kind: TextTrackKind = .subtitles, source: String? = nil, episode: Bool = false) -> String? {
            FindAnotherWording.problemSentence(
                for: answer(status, source: source), kind: kind, languageName: "English", isEpisode: episode
            )
        }
        XCTAssertEqual(sentence("NoSourceOn"), "No subtitle source is switched on.")
        XCTAssertEqual(sentence("NoSourceOn", kind: .lyrics), "No lyrics source is switched on.")
        XCTAssertEqual(sentence("SourceKeyRequired", source: "SubDL"), "SubDL did not accept its key.")
        XCTAssertEqual(sentence("IdentityUnverified", episode: true), "This episode could not be matched for an online search.")
        XCTAssertEqual(sentence("IdentityUnverified"), "This movie could not be matched for an online search.")
        XCTAssertEqual(sentence("LimitReached", source: "SubDL"), "SubDL has reached its limit for now. Try again later.")
        XCTAssertEqual(sentence("Unavailable", source: "SubDL"), "SubDL could not be reached.")
        XCTAssertEqual(sentence("Blocked"), "This library never looks anything up online.")
        XCTAssertEqual(sentence("NotAllowed"), "Your profile cannot search for subtitles.")
        XCTAssertEqual(sentence("LanguageUnknown"), "That language is not supported for subtitle search.")
        XCTAssertEqual(sentence("NoneFound"), "No English subtitles were found for this movie.")
        XCTAssertEqual(sentence("NoneFound", kind: .lyrics), "No lyrics were found for this song.")
        XCTAssertEqual(sentence("Found"), "No English subtitles were found for this movie.")
        XCTAssertEqual(sentence("AssetMissing"), "The search could not be completed.")
        XCTAssertEqual(sentence("SomethingNew"), "The search could not be completed.")
        XCTAssertEqual(sentence("Unavailable"), "The subtitle source could not be reached.")
    }

    func testTheEnginesOwnMessageIsUsedWhenItSentOne() {
        let withMessage = answer("LimitReached", message: "  SubDL is resting until tomorrow.  ", source: "SubDL")
        XCTAssertEqual(
            FindAnotherWording.problemSentence(for: withMessage, kind: .subtitles),
            "SubDL is resting until tomorrow."
        )
        let blank = answer("Blocked", message: "   ")
        XCTAssertEqual(FindAnotherWording.problemSentence(for: blank, kind: .subtitles), "This library never looks anything up online.")
    }

    func testAPreviewThatCouldNotBeReadSaysSoAndNamesALimit() {
        XCTAssertEqual(FindAnotherWording.previewProblemSentence(status: "Unavailable"), "Could not read this one")
        XCTAssertEqual(FindAnotherWording.previewProblemSentence(status: "NotFound"), "Could not read this one")
        XCTAssertEqual(
            FindAnotherWording.previewProblemSentence(status: "LimitReached", sourceName: "SubDL"),
            "SubDL has reached its limit for now. Try again later."
        )
    }

    func testARefusedChoiceUsesTheEnginesMessageOrAPlainFallback() {
        XCTAssertEqual(
            FindAnotherWording.choiceProblemSentence(TextTrackChoiceResponse(assetId: asset, status: "Failed", message: "Not allowed.")),
            "Not allowed."
        )
        XCTAssertEqual(
            FindAnotherWording.choiceProblemSentence(TextTrackChoiceResponse(assetId: asset, status: "Failed")),
            "Could not use this one"
        )
    }
}
