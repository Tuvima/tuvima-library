import SwiftUI
import TuvimaCore

/// Home: Continue first, then the Home shelves in the order the server gives them.
struct HomeView: View {
    @EnvironmentObject private var environment: AppEnvironment
    @Binding var path: NavigationPath
    @State private var state: LoadState<[DisplayShelf]> = .loading

    var body: some View {
        NavigationStack(path: $path) {
            content
                .navigationTitle("Home")
                .appDestinations()
        }
        .miniPlayerInset()
        .task {
            if case .loaded = state { return }
            await load()
        }
    }

    @ViewBuilder
    private var content: some View {
        switch state {
        case .loading:
            ProgressView("Loading Home…")
        case .failed(let message):
            LoadFailedView(message: message) { Task { await load() } }
        case .loaded(let shelves):
            if shelves.isEmpty {
                ContentUnavailableView(
                    "Nothing here yet",
                    systemImage: "tray",
                    description: Text("Titles you continue and your library shelves appear here.")
                )
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 24) {
                        ForEach(shelves) { shelf in ShelfRow(shelf: shelf) }
                    }
                    .padding(.vertical)
                }
                .refreshable { await load() }
            }
        }
    }

    private func load() async {
        guard let api = environment.api else {
            state = .failed("This device is not connected to a server.")
            return
        }
        do {
            let home = try await api.homeShelves()
            // Continue is a bonus: if it cannot load, Home still shows its own shelves.
            let continuePage = try? await api.continueAll()
            let continueShelves = (continuePage?.shelves ?? []).filter { !$0.items.isEmpty }
            let known = Set(continueShelves.map { $0.key })
            let homeShelves = home.shelves.filter { !$0.items.isEmpty && !known.contains($0.key) }
            state = .loaded(continueShelves + homeShelves)
        } catch {
            if isCancellation(error) { return }
            state = .failed(error.localizedDescription)
        }
    }
}
