import XCTest
@testable import TuvimaCore

final class OrientationPolicyTests: XCTestCase {
    func testOnlyVideoMayRotate() {
        XCTAssertEqual(OrientationPolicy.rule(for: .video), .rotatable)
    }

    func testEverythingElseStaysPortrait() {
        for kind in [ScreenKind.browse, .audio, .photo] {
            XCTAssertEqual(OrientationPolicy.rule(for: kind), .portrait)
        }
    }
}
