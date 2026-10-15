import SwiftUI
import TuvimaCore

/// Libraries: Watch, Read, Listen, View (photos) and Collections, switched with chips at the top.
struct LibrariesView: View {
    enum LibrarySection: String, CaseIterable, Identifiable {
        case watch = "Watch"
        case read = "Read"
        case listen = "Listen"
        case view = "View"
        case collections = "Collections"

        var id: String { rawValue }
    }

    @State private var section: LibrarySection = .watch

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 8) {
                        ForEach(LibrarySection.allCases) { item in
                            Button(item.rawValue) { section = item }
                                .buttonStyle(.bordered)
                                .tint(section == item ? Color.accentColor : Color.secondary)
                        }
                    }
                    .padding(.horizontal)
                    .padding(.vertical, 8)
                }
                switch section {
                case .watch: LaneBrowseView(lane: "watch").id("watch")
                case .read: LaneBrowseView(lane: "read").id("read")
                case .listen: LaneBrowseView(lane: "listen").id("listen")
                case .view: ViewGridView()
                case .collections: CollectionsView()
                }
            }
            .navigationTitle("Libraries")
            .navigationBarTitleDisplayMode(.inline)
            .appDestinations()
        }
        .miniPlayerInset()
    }
}

// MARK: Lane browse

struct LaneTab: Hashable {
    let label: String
    let mediaType: String
    var grouping: String? = nil
}

/// Each lane's tabs are the Dashboard's: Movies and TV, Books and Comics, Music (albums) and Audiobooks.
func laneTabs(for lane: String) -> [LaneTab] {
    switch lane {
    case "watch": return [LaneTab(label: "Movies", mediaType: "Movies"), LaneTab(label: "TV", mediaType: "TV", grouping: "shows")]
    case "read": return [LaneTab(label: "Books", mediaType: "Books"), LaneTab(label: "Comics", mediaType: "Comics")]
    default: return [LaneTab(label: "Music", mediaType: "Music", grouping: "timeline"), LaneTab(label: "Audiobooks", mediaType: "Audiobooks")]
    }
}

/// The Dashboard's sort choices for a card grid, and its narrower pair for the dated album timeline.
let gridSortOptions: [(value: String, label: String)] = [
    ("newest", "Newest"),
    ("title", "A-Z"),
    ("oldest", "Oldest"),
    ("creator", "Creator"),
    ("year", "Year"),
]
let timelineSortOptions: [(value: String, label: String)] = [
    ("newest", "Newest first"),
    ("oldest", "Oldest first"),
]

let statusOptions: [(value: String, label: String)] = [
    ("", "All"),
    ("in-progress", "In progress"),
    ("not-started", "Not started"),
    ("favorite", "Favorites"),
    ("finished", "Finished"),
]

/// The cards of one lane tab, loaded a page at a time, with the sort and filter options applied by the Engine.
@MainActor
final class BrowseModel: ObservableObject {
    @Published private(set) var cards: [DisplayCard] = []
    @Published private(set) var totalCount = 0
    @Published private(set) var genres: [String] = []
    @Published private(set) var creators: [String] = []
    @Published private(set) var years: [String] = []
    @Published private(set) var isLoading = false
    @Published private(set) var errorMessage: String?
    @Published private(set) var query: BrowseQuery
    private var generation = 0

    private var didStart = false

    init(base: BrowseQuery) { query = base }

    /// Loads the first page once; coming back to the screen (for example from a title's details) keeps the page.
    func loadIfNeeded(api: TuvimaAPIClient?) {
        guard !didStart else { return }
        didStart = true
        reload(api: api)
    }

    func reload(api: TuvimaAPIClient?, query next: BrowseQuery? = nil) {
        var value = next ?? query
        value.offset = 0
        query = value
        cards = []
        totalCount = 0
        fetch(api: api)
    }

    func loadMore(api: TuvimaAPIClient?) {
        guard !isLoading, errorMessage == nil, !cards.isEmpty, cards.count < totalCount else { return }
        query.offset = cards.count
        fetch(api: api)
    }

    private func fetch(api: TuvimaAPIClient?) {
        guard let api else {
            errorMessage = "This device is not connected to a server."
            return
        }
        generation += 1
        let mine = generation
        let requested = query
        isLoading = true
        errorMessage = nil
        Task {
            do {
                let page = try await api.browsePage(requested)
                guard mine == generation else { return }
                let known = Set(cards.map { $0.id })
                cards += page.cards.filter { !known.contains($0.id) }
                totalCount = page.totalCount
                if requested.offset == 0 || genres.isEmpty { genres = page.genres }
                if requested.offset == 0 || creators.isEmpty { creators = page.creators }
                if requested.offset == 0 || years.isEmpty { years = page.years }
            } catch {
                guard mine == generation, !isCancellation(error) else { return }
                errorMessage = error.localizedDescription
            }
            if mine == generation { isLoading = false }
        }
    }
}

struct LaneBrowseView: View {
    let lane: String
    @State private var tab: LaneTab?

    var body: some View {
        let tabs = laneTabs(for: lane)
        VStack(spacing: 0) {
            Picker("Section", selection: Binding(get: { tab ?? tabs[0] }, set: { tab = $0 })) {
                ForEach(tabs, id: \.self) { item in Text(item.label).tag(item) }
            }
            .pickerStyle(.segmented)
            .padding(.horizontal)
            .padding(.bottom, 8)
            LaneTabGrid(lane: lane, tab: tab ?? tabs[0])
                .id((tab ?? tabs[0]).mediaType)
        }
    }
}

struct LaneTabGrid: View {
    @EnvironmentObject private var environment: AppEnvironment
    let lane: String
    let tab: LaneTab
    @StateObject private var model: BrowseModel
    @State private var showingOptions = false

    @MainActor
    init(lane: String, tab: LaneTab) {
        self.lane = lane
        self.tab = tab
        _model = StateObject(
            wrappedValue: BrowseModel(
                base: BrowseQuery(
                    lane: lane,
                    mediaType: tab.mediaType,
                    grouping: tab.grouping,
                    sort: "newest"
                )
            )
        )
    }

    private var sortOptions: [(value: String, label: String)] {
        tab.grouping == "timeline" ? timelineSortOptions : gridSortOptions
    }

    var body: some View {
        Group {
            if let message = model.errorMessage, model.cards.isEmpty {
                LoadFailedView(message: message) { model.reload(api: environment.api) }
            } else if model.cards.isEmpty && model.isLoading {
                ProgressView("Loading \(tab.label)…")
            } else if model.cards.isEmpty {
                ContentUnavailableView(
                    "Nothing found",
                    systemImage: "tray",
                    description: Text(model.query.hasFilters ? "No titles match these filters." : "This library is empty.")
                )
            } else {
                ScrollView {
                    LazyVGrid(columns: posterColumns, spacing: 16) {
                        ForEach(model.cards) { card in PosterTile(card: card) }
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
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button {
                    showingOptions = true
                } label: {
                    Label("Sort and filter", systemImage: model.query.hasFilters
                        ? "line.3.horizontal.decrease.circle.fill"
                        : "line.3.horizontal.decrease.circle")
                }
            }
        }
        .sheet(isPresented: $showingOptions) {
            BrowseOptionsSheet(
                current: model.query,
                sortOptions: sortOptions,
                genres: model.genres,
                creators: model.creators,
                years: model.years
            ) { applied in
                model.reload(api: environment.api, query: applied)
            }
            .presentationDetents([.medium, .large])
        }
        .task { model.loadIfNeeded(api: environment.api) }
    }
}

// MARK: Sort and filter sheet

private let maxFacetValues = 30

/// The Dashboard's sort and filter in a sheet: sort order, progress status, and the genre, year and creator
/// values the Engine reported for this library page. Nothing is sent until Apply.
struct BrowseOptionsSheet: View {
    @Environment(\.dismiss) private var dismiss
    let current: BrowseQuery
    let sortOptions: [(value: String, label: String)]
    let genres: [String]
    let creators: [String]
    let years: [String]
    let onApply: (BrowseQuery) -> Void

    @State private var sort: String
    @State private var status: String
    @State private var pickedGenres: [String]
    @State private var pickedYears: [String]
    @State private var pickedCreators: [String]

    init(
        current: BrowseQuery,
        sortOptions: [(value: String, label: String)],
        genres: [String],
        creators: [String],
        years: [String],
        onApply: @escaping (BrowseQuery) -> Void
    ) {
        self.current = current
        self.sortOptions = sortOptions
        self.genres = genres
        self.creators = creators
        self.years = years
        self.onApply = onApply
        let firstSort = sortOptions.first?.value ?? "newest"
        let currentSort = current.sort ?? firstSort
        _sort = State(initialValue: sortOptions.contains(where: { $0.value == currentSort }) ? currentSort : firstSort)
        _status = State(initialValue: current.status ?? "")
        _pickedGenres = State(initialValue: current.genres)
        _pickedYears = State(initialValue: current.years)
        _pickedCreators = State(initialValue: current.creators)
    }

    var body: some View {
        NavigationStack {
            Form {
                Section("Sort by") {
                    Picker("Sort by", selection: $sort) {
                        ForEach(sortOptions, id: \.value) { option in Text(option.label).tag(option.value) }
                    }
                    .pickerStyle(.inline)
                    .labelsHidden()
                }
                Section("Status") {
                    Picker("Status", selection: $status) {
                        ForEach(statusOptions, id: \.value) { option in Text(option.label).tag(option.value) }
                    }
                    .pickerStyle(.menu)
                }
                multiSelect("Genre", values: genres, picked: $pickedGenres)
                multiSelect("Year", values: years, picked: $pickedYears)
                multiSelect("Creator", values: creators, picked: $pickedCreators)
            }
            .navigationTitle("Sort and filter")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Apply") {
                        var applied = current
                        applied.sort = sort
                        applied.status = status.isEmpty ? nil : status
                        applied.genres = pickedGenres
                        applied.years = pickedYears
                        applied.creators = pickedCreators
                        onApply(applied)
                        dismiss()
                    }
                }
                ToolbarItem(placement: .bottomBar) {
                    Button("Clear filters") {
                        status = ""
                        pickedGenres = []
                        pickedYears = []
                        pickedCreators = []
                    }
                }
            }
        }
    }

    @ViewBuilder
    private func multiSelect(_ title: String, values: [String], picked: Binding<[String]>) -> some View {
        if !values.isEmpty {
            Section(title) {
                ForEach(Array(values.prefix(maxFacetValues)), id: \.self) { value in
                    Button {
                        if let index = picked.wrappedValue.firstIndex(of: value) {
                            picked.wrappedValue.remove(at: index)
                        } else {
                            picked.wrappedValue.append(value)
                        }
                    } label: {
                        HStack {
                            Text(value).foregroundStyle(.primary)
                            Spacer()
                            if picked.wrappedValue.contains(value) {
                                Image(systemName: "checkmark").foregroundStyle(Color.accentColor)
                            }
                        }
                    }
                }
            }
        }
    }
}
