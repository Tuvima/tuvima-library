import SwiftUI
import TuvimaCore

// MARK: Paged loading

struct PageResult<Element: Sendable>: Sendable {
    let items: [Element]
    let nextCursor: String?
    let hasMore: Bool
}

/// Reads a cursor-paged list from the Engine one page at a time.
@MainActor
final class PagedModel<Element: Identifiable & Sendable>: ObservableObject {
    @Published private(set) var items: [Element] = []
    @Published private(set) var isLoading = false
    @Published private(set) var loaded = false
    @Published private(set) var errorMessage: String?
    private var cursor: String?
    private var hasMore = true
    private var didStart = false
    private let fetch: (TuvimaAPIClient, String?) async throws -> PageResult<Element>

    init(fetch: @escaping (TuvimaAPIClient, String?) async throws -> PageResult<Element>) {
        self.fetch = fetch
    }

    func loadIfNeeded(api: TuvimaAPIClient?) {
        guard !didStart else { return }
        didStart = true
        loadMore(api: api)
    }

    func retry(api: TuvimaAPIClient?) {
        errorMessage = nil
        loadMore(api: api)
    }

    func loadMore(api: TuvimaAPIClient?) {
        guard !isLoading, hasMore, errorMessage == nil else { return }
        guard let api else {
            errorMessage = "This device is not connected to a server."
            return
        }
        isLoading = true
        let requested = cursor
        Task {
            do {
                let page = try await fetch(api, requested)
                let known = Set(items.map { $0.id })
                items += page.items.filter { !known.contains($0.id) }
                cursor = page.nextCursor
                hasMore = page.hasMore && page.nextCursor != nil
                loaded = true
            } catch {
                if !isCancellation(error) { errorMessage = error.localizedDescription }
            }
            isLoading = false
        }
    }
}

// MARK: Catalogue

/// The Collections catalogue: one tile per show, series, collection or list the profile may see.
struct CollectionsView: View {
    @EnvironmentObject private var environment: AppEnvironment
    @StateObject private var model = PagedModel<CollectionEntry> { api, cursor in
        let page = try await api.collections(cursor: cursor, limit: 24)
        return PageResult(items: page.items, nextCursor: page.nextCursor, hasMore: page.canLoadMore)
    }

    private let columns = [GridItem(.adaptive(minimum: 150, maximum: 220), spacing: 12, alignment: .top)]

    var body: some View {
        Group {
            if let message = model.errorMessage, model.items.isEmpty {
                LoadFailedView(message: message) { model.retry(api: environment.api) }
            } else if model.items.isEmpty && !model.loaded {
                ProgressView("Loading collections…")
            } else if model.items.isEmpty {
                ContentUnavailableView(
                    "No collections yet",
                    systemImage: "square.stack.3d.up",
                    description: Text("Series, shows, collections and lists you can see appear here.")
                )
            } else {
                ScrollView {
                    LazyVGrid(columns: columns, spacing: 16) {
                        ForEach(model.items) { entry in
                            NavigationLink(value: route(for: entry)) {
                                CollectionTile(entry: entry)
                            }
                            .buttonStyle(.plain)
                        }
                    }
                    .padding(.horizontal)
                    Color.clear
                        .frame(height: 1)
                        .onAppear { model.loadMore(api: environment.api) }
                    if model.isLoading { ProgressView().padding() }
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .task { model.loadIfNeeded(api: environment.api) }
    }

    private func route(for entry: CollectionEntry) -> CollectionRoute {
        CollectionRoute(
            id: entry.id,
            name: entry.name,
            groupType: entry.groupType ?? "collection",
            universeQid: entry.resolvedUniverseQid
        )
    }
}

struct CollectionTile: View {
    let entry: CollectionEntry

    var body: some View {
        let count = entry.itemCount ?? 0
        let countText = count == 1 ? "1 item" : "\(count) items"
        VStack(alignment: .leading, spacing: 4) {
            ArtworkImage(path: entry.coverPaths.first)
                .aspectRatio(1, contentMode: .fit)
                .clipShape(RoundedRectangle(cornerRadius: 10))
            Text(entry.name).font(.subheadline.bold()).lineLimit(1)
            Text("\(entry.kindLabel) · \(countText)")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}

// MARK: One collection

/// One collection's items in the collection's own order.
struct CollectionDetailView: View {
    @EnvironmentObject private var environment: AppEnvironment
    let route: CollectionRoute
    @StateObject private var model: PagedModel<CollectionItem>
    @State private var playable: [UUID: UUID] = [:]

    @MainActor
    init(route: CollectionRoute) {
        self.route = route
        let id = route.id
        _model = StateObject(wrappedValue: PagedModel<CollectionItem> { api, cursor in
            let page = try await api.collectionItems(id: id, cursor: cursor, limit: 24)
            return PageResult(items: page.items, nextCursor: page.nextCursor, hasMore: page.canLoadMore)
        })
    }

    var body: some View {
        Group {
            if let message = model.errorMessage, model.items.isEmpty {
                LoadFailedView(message: message) { model.retry(api: environment.api) }
            } else if model.items.isEmpty && !model.loaded {
                ProgressView("Loading…")
            } else if model.items.isEmpty {
                ContentUnavailableView(
                    "Nothing here",
                    systemImage: "tray",
                    description: Text("There is nothing in this \(groupTypeLabel(route.groupType).lowercased()) that you can see.")
                )
            } else {
                ScrollView {
                    if let qid = route.universeQid {
                        NavigationLink(value: UniverseRoute(qid: qid, name: route.name)) {
                            Label("See the Universe shelves", systemImage: "square.stack.3d.up")
                        }
                        .padding(.horizontal)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    LazyVGrid(columns: posterColumns, spacing: 16) {
                        ForEach(model.items) { item in itemTile(item) }
                    }
                    .padding(.horizontal)
                    Color.clear
                        .frame(height: 1)
                        .onAppear { model.loadMore(api: environment.api) }
                    if model.isLoading { ProgressView().padding() }
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .navigationTitle(route.name)
        .navigationBarTitleDisplayMode(.inline)
        .task {
            model.loadIfNeeded(api: environment.api)
            await loadPlayableAssets()
        }
    }

    private func itemTile(_ item: CollectionItem) -> some View {
        let target = item.detailTarget
        let asset = item.workId.flatMap { playable[$0] }
        return NavigationLink(value: DetailRoute(entityType: target.entityType, id: target.id, assetID: asset)) {
            VStack(alignment: .leading, spacing: 4) {
                ArtworkImage(path: item.coverUrl)
                    .aspectRatio(2.0 / 3.0, contentMode: .fit)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                Text(item.title).font(.subheadline).lineLimit(1)
                if let creator = item.creator, !creator.isEmpty {
                    Text(creator).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
            }
        }
        .buttonStyle(.plain)
    }

    /// A collection's item list names works but not files; the group's cards carry the file to play for each work.
    private func loadPlayableAssets() async {
        guard let api = environment.api else { return }
        // Best effort: without the group's cards the items still open their details, just without Play.
        guard let page = try? await api.groupCards(collectionID: route.id) else { return }
        playable = playableAssets(byWork: page.shelves.flatMap { $0.items })
    }
}

// MARK: Universe

/// A Universe: the series and shows that belong to one franchise, each as a shelf.
struct UniverseView: View {
    @EnvironmentObject private var environment: AppEnvironment
    let route: UniverseRoute
    @State private var state: LoadState<UniverseDetail> = .loading

    var body: some View {
        Group {
            switch state {
            case .loading:
                ProgressView("Loading Universe…")
            case .failed(let message):
                LoadFailedView(message: message) { Task { await load() } }
            case .loaded(let universe):
                ScrollView {
                    VStack(alignment: .leading, spacing: 20) {
                        VStack(alignment: .leading, spacing: 6) {
                            Text(universe.name).font(.largeTitle.bold())
                            let totals = universeTotalsLine(universe)
                            if !totals.isEmpty {
                                Text(totals)
                                    .font(.subheadline)
                                    .foregroundStyle(.secondary)
                            }
                            if let description = universe.description, !description.isEmpty {
                                Text(description)
                            }
                        }
                        .padding(.horizontal)
                        ForEach(universe.shelves) { member in
                            if let rows = universeShelfPresentation(member) {
                                seriesSection(rows)
                            }
                        }
                        if !universe.cast.isEmpty { castSection(universe.cast) }
                        if !universe.adaptations.isEmpty { adaptationsSection(universe.adaptations) }
                    }
                    .padding(.vertical)
                }
            }
        }
        .navigationTitle(route.name)
        .navigationBarTitleDisplayMode(.inline)
        .task {
            if case .loaded = state { return }
            await load()
        }
    }

    /// One Series: its name and kind line, the numbered run when it has one, then its "Also in this world" row.
    private func seriesSection(_ rows: UniverseShelfPresentation) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            VStack(alignment: .leading, spacing: 2) {
                Text(rows.title).font(.title3.bold())
                Text(rows.kindLine).font(.caption).foregroundStyle(.secondary)
            }
            .padding(.horizontal)
            if rows.showsRun {
                ShelfRow(shelf: rows.runShelf, showsTitle: false)
            }
            if let also = rows.alsoShelf, let heading = rows.alsoHeading {
                // With no run, the Series heading above already says "Also in this world · N items".
                ShelfRow(shelf: also, title: heading, showsTitle: rows.showsRun)
            }
        }
    }

    /// The Universe's cast: read-only, so no row opens anything.
    private func castSection(_ cast: [UniverseCastMember]) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Cast").font(.title3.bold())
            ForEach(cast) { member in
                HStack(spacing: 12) {
                    if let headshot = member.headshotUrl, !headshot.isEmpty {
                        ArtworkImage(path: headshot)
                            .frame(width: 44, height: 44)
                            .clipShape(Circle())
                    }
                    VStack(alignment: .leading, spacing: 2) {
                        Text(member.name).font(.subheadline.bold())
                        if let line = castCharactersLine(member) {
                            Text(line).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
            }
        }
        .padding(.horizontal)
    }

    /// Works in the Universe that adapt another work: read-only text rows.
    private func adaptationsSection(_ adaptations: [UniverseAdaptation]) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Adaptations").font(.title3.bold())
            ForEach(adaptations) { adaptation in
                Text(adaptationLine(adaptation)).font(.subheadline)
            }
        }
        .padding(.horizontal)
    }

    private func load() async {
        guard let api = environment.api else {
            state = .failed("This device is not connected to a server.")
            return
        }
        state = .loading
        do {
            state = .loaded(try await api.universe(qid: route.qid))
        } catch {
            if isCancellation(error) { return }
            state = .failed(error.localizedDescription)
        }
    }
}
