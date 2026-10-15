import SwiftUI
import TuvimaCore

/// Search across the library; each result opens its details.
struct SearchView: View {
    @EnvironmentObject private var environment: AppEnvironment
    @State private var query = ""
    @State private var state: LoadState<SearchResponse>?

    var body: some View {
        NavigationStack {
            content
                .navigationTitle("Search")
                .searchable(text: $query, prompt: "Search your library")
                .onSubmit(of: .search) { Task { await search() } }
                .appDestinations()
        }
        .miniPlayerInset()
    }

    @ViewBuilder
    private var content: some View {
        switch state {
        case .none:
            ContentUnavailableView(
                "Search your library",
                systemImage: "magnifyingglass",
                description: Text("Find movies, shows, books, music and more.")
            )
        case .some(.loading):
            ProgressView("Searching…")
        case .some(.failed(let message)):
            LoadFailedView(message: message) { Task { await search() } }
        case .some(.loaded(let response)):
            if response.sections.allSatisfy({ $0.results.isEmpty }) {
                ContentUnavailableView.search(text: response.query)
            } else {
                List {
                    ForEach(response.sections) { section in
                        if !section.results.isEmpty {
                            Section(section.title) {
                                ForEach(section.results) { result in row(result) }
                            }
                        }
                    }
                }
            }
        }
    }

    @ViewBuilder
    private func row(_ result: SearchResult) -> some View {
        let label = VStack(alignment: .leading) {
            Text(result.title)
            if let subtitle = result.subtitle {
                Text(subtitle).font(.caption).foregroundStyle(.secondary)
            }
        }
        if let target = result.detailTarget {
            NavigationLink(value: DetailRoute(entityType: target.entityType, id: target.id)) { label }
        } else {
            label
        }
    }

    private func search() async {
        let text = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, let api = environment.api else { return }
        state = .loading
        do {
            state = .loaded(try await api.search(query: text))
        } catch {
            if isCancellation(error) { return }
            state = .failed(error.localizedDescription)
        }
    }
}
