import SwiftUI
import TuvimaCore

/// The paired app: Home, Libraries, Search, Downloads and Settings.
struct MainTabView: View {
    enum MainTab: Hashable {
        case home, libraries, search, downloads, settings
    }

    @EnvironmentObject private var environment: AppEnvironment
    @State private var selection: MainTab = .home
    @State private var homePath = NavigationPath()

    var body: some View {
        TabView(selection: $selection) {
            HomeView(path: $homePath)
                .id(environment.sessionRevision)
                .tabItem { Label("Home", systemImage: "house") }
                .tag(MainTab.home)
            LibrariesView()
                .id(environment.sessionRevision)
                .tabItem { Label("Libraries", systemImage: "books.vertical") }
                .tag(MainTab.libraries)
            SearchView()
                .id(environment.sessionRevision)
                .tabItem { Label("Search", systemImage: "magnifyingglass") }
                .tag(MainTab.search)
            DownloadsView()
                .tabItem { Label("Downloads", systemImage: "arrow.down.circle") }
                .tag(MainTab.downloads)
            SettingsView()
                .tabItem { Label("Settings", systemImage: "gearshape") }
                .tag(MainTab.settings)
        }
        .task { openPendingDetail() }
        .onChange(of: environment.pendingDetail) { _, _ in openPendingDetail() }
    }

    /// A `tuvima://details/{type}/{id}` link opens its details on the Home tab.
    private func openPendingDetail() {
        guard let link = environment.consumePendingDetail() else { return }
        selection = .home
        homePath.append(DetailRoute(entityType: link.entityType, id: link.id))
    }
}
