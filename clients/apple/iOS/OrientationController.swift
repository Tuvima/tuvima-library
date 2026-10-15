import SwiftUI
import TuvimaCore
import UIKit

/// Holds the orientation the iPhone app currently allows. Portrait unless a video is on screen; the video player sets
/// `.video` while it is shown and puts `.browse` back when it goes away.
final class OrientationController {
    static let shared = OrientationController()
    private(set) var kind: ScreenKind = .browse

    @MainActor
    func set(_ newKind: ScreenKind) {
        guard newKind != kind else { return }
        kind = newKind
        // Tell the system to re-read the allowed orientations; leaving video turns the phone back to portrait.
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        scene?.windows.first?.rootViewController?.setNeedsUpdateOfSupportedInterfaceOrientations()
        if newKind != .video, UIDevice.current.userInterfaceIdiom == .phone {
            scene?.requestGeometryUpdate(.iOS(interfaceOrientations: .portrait))
        }
    }
}

final class TuvimaAppDelegate: NSObject, UIApplicationDelegate {
    func application(_ application: UIApplication, supportedInterfaceOrientationsFor window: UIWindow?) -> UIInterfaceOrientationMask {
        // iPad keeps its own orientations from Info.plist.
        if UIDevice.current.userInterfaceIdiom != .phone { return .all }
        switch OrientationPolicy.rule(for: OrientationController.shared.kind) {
        case .portrait: return .portrait
        case .rotatable: return .allButUpsideDown
        }
    }
}

extension View {
    /// Marks this screen as video playback: the phone may rotate to landscape while it is shown.
    func allowsVideoRotation(_ active: Bool) -> some View {
        onChange(of: active, initial: true) { _, value in
            OrientationController.shared.set(value ? .video : .browse)
        }
        .onDisappear { OrientationController.shared.set(.browse) }
    }
}
