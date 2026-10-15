import SwiftUI

@main
struct TuvimaApp: App {
    @UIApplicationDelegateAdaptor(TuvimaAppDelegate.self) private var appDelegate
    @StateObject private var environment = AppEnvironment.shared

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(environment)
                .environmentObject(environment.playback)
                .onOpenURL(perform: environment.handleDeepLink)
        }
    }
}
