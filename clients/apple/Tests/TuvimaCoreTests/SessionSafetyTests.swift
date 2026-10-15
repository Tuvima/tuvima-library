import XCTest
@testable import TuvimaCore

/// Pure-logic tests for keeping a sync run on one person and for telling a revoked sign-in from a bad moment. They need
/// no device or server. They are not part of any target yet (see clients/apple/README.md for the project.yml stanza that
/// turns them on), so CI does not run them.
final class SessionSafetyTests: XCTestCase {
    private let profileA = UUID(uuidString: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")!
    private let profileB = UUID(uuidString: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")!

    func testSameProfileContinues() {
        let start = ProfileBinding(profileID: profileA, epoch: 1)
        let syncGuard = SyncGuard.start(current: start)
        XCTAssertNotNil(syncGuard)
        XCTAssertEqual(syncGuard?.profileID, profileA)
        // A token refresh keeps the profile and the epoch, so the run goes on.
        XCTAssertTrue(syncGuard?.isBound(current: ProfileBinding(profileID: profileA, epoch: 1)) ?? false)
    }

    func testSwitchingProfileMidRunAborts() {
        let syncGuard = SyncGuard.start(current: ProfileBinding(profileID: profileA, epoch: 1))
        XCTAssertFalse(syncGuard?.isBound(current: ProfileBinding(profileID: profileB, epoch: 2)) ?? true)
    }

    func testSwitchingAwayAndBackStillAborts() {
        let syncGuard = SyncGuard.start(current: ProfileBinding(profileID: profileA, epoch: 1))
        XCTAssertFalse(syncGuard?.isBound(current: ProfileBinding(profileID: profileA, epoch: 3)) ?? true)
    }

    func testSignOutMidRunAborts() {
        let syncGuard = SyncGuard.start(current: ProfileBinding(profileID: profileA, epoch: 1))
        XCTAssertFalse(syncGuard?.isBound(current: nil) ?? true)
    }

    func testNobodySignedInGivesNoGuard() {
        XCTAssertNil(SyncGuard.start(current: nil))
        XCTAssertNil(SyncGuard.start(expectedProfile: profileA, current: nil))
    }

    func testStartForRefusesAnotherProfile() {
        let signedIn = ProfileBinding(profileID: profileB, epoch: 4)
        XCTAssertNil(SyncGuard.start(expectedProfile: profileA, current: signedIn))
        XCTAssertEqual(SyncGuard.start(expectedProfile: profileB, current: signedIn)?.binding, signedIn)
    }

    func testInvalidGrantIsRevoked() {
        XCTAssertEqual(SessionLogic.classifyRefreshFailure(statusCode: 400, errorCode: "invalid_grant"), .revoked)
    }

    func testAnythingWithoutTheExactCodeIsTransient() {
        // A proxy's HTML 401 or 403, a host filter's 400 and a bare 400 carry no OAuth code and must not sign anyone out.
        for status in [400, 401, 403, 404, 408, 429, 500, 502, 503, 504] {
            XCTAssertEqual(SessionLogic.classifyRefreshFailure(statusCode: status, errorCode: nil), .transient)
            XCTAssertEqual(SessionLogic.classifyRefreshFailure(statusCode: status, errorCode: ""), .transient)
        }
        XCTAssertEqual(SessionLogic.classifyRefreshFailure(statusCode: nil, errorCode: nil), .transient)
    }

    func testOtherOAuthCodesAreTransient() {
        XCTAssertEqual(SessionLogic.classifyRefreshFailure(statusCode: 400, errorCode: "invalid_request"), .transient)
        XCTAssertEqual(SessionLogic.classifyRefreshFailure(statusCode: 400, errorCode: "invalid_client"), .transient)
        XCTAssertEqual(SessionLogic.classifyRefreshFailure(statusCode: 503, errorCode: "temporarily_unavailable"), .transient)
        XCTAssertEqual(SessionLogic.classifyRefreshFailure(statusCode: 400, errorCode: "INVALID_GRANT"), .transient)
    }

    func testARefusalWithNoSwitchAroundIsARealRevocation() {
        var tracker = SwitchTracker()
        let captured = tracker.generation
        XCTAssertTrue(tracker.mayTreatRefusalAsRevoked(capturedGeneration: captured))
        // A switch that ended before the attempt began leaves a newer generation, which a later attempt captures.
        tracker.begin()
        tracker.finish()
        XCTAssertTrue(tracker.mayTreatRefusalAsRevoked(capturedGeneration: tracker.generation))
    }

    func testARefusalWhileASwitchIsPendingIsTransient() {
        var tracker = SwitchTracker()
        let captured = tracker.generation
        tracker.begin()
        XCTAssertEqual(tracker.pending, 1)
        XCTAssertFalse(tracker.mayTreatRefusalAsRevoked(capturedGeneration: captured))
        // Even an attempt that began after the switch started is held back until the switch ends.
        XCTAssertFalse(tracker.mayTreatRefusalAsRevoked(capturedGeneration: tracker.generation))
    }

    func testARefusalAfterASwitchStartedDuringTheAttemptIsTransient() {
        var tracker = SwitchTracker()
        let captured = tracker.generation
        tracker.begin()
        tracker.finish()
        // The switch is over, but it happened during this attempt: A's refresh token was revoked by it.
        XCTAssertEqual(tracker.pending, 0)
        XCTAssertFalse(tracker.mayTreatRefusalAsRevoked(capturedGeneration: captured))
    }

    func testFinishWithoutBeginNeverGoesNegative() {
        var tracker = SwitchTracker()
        tracker.finish()
        XCTAssertEqual(tracker.pending, 0)
    }
}
