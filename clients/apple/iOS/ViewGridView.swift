import AVKit
import SwiftUI
import TuvimaCore

// MARK: Timeline model

/// The signed-in person's photos and videos, newest first, loaded a page at a time. A month can continue on the
/// next page, so pages are merged by year and month.
@MainActor
final class PhotoTimelineModel: ObservableObject {
    @Published private(set) var months: [ViewTimelineMonth] = []
    @Published private(set) var isLoading = false
    @Published private(set) var errorMessage: String?
    @Published private(set) var hasMore = false
    private var cursor: String?
    private var didStart = false
    private var generation = 0

    /// Every item in display order, for the full-screen viewer.
    var allItems: [ViewItem] { months.flatMap { $0.items } }

    func loadIfNeeded(api: TuvimaAPIClient?) {
        guard !didStart else { return }
        didStart = true
        loadMore(api: api)
    }

    func reload(api: TuvimaAPIClient?) {
        generation += 1
        months = []
        cursor = nil
        hasMore = false
        didStart = true
        errorMessage = nil
        isLoading = false
        loadMore(api: api)
    }

    func loadMore(api: TuvimaAPIClient?) {
        guard !isLoading, errorMessage == nil, months.isEmpty || hasMore else { return }
        guard let api else {
            errorMessage = "This device is not connected to a server."
            return
        }
        isLoading = true
        let requestedCursor = cursor
        let mine = generation
        Task {
            do {
                let page = try await api.viewTimeline(cursor: requestedCursor)
                guard mine == generation else { return }
                months = mergeTimelineMonths(existing: months, incoming: page.months)
                cursor = page.nextCursor
                hasMore = (page.hasMore ?? false) && page.nextCursor != nil
            } catch {
                guard mine == generation else { return }
                if !isCancellation(error) { errorMessage = error.localizedDescription }
            }
            isLoading = false
        }
    }
}

/// The items of one Gallery, newest first.
@MainActor
final class GalleryItemsModel: ObservableObject {
    @Published private(set) var items: [ViewItem] = []
    @Published private(set) var isLoading = false
    @Published private(set) var errorMessage: String?
    @Published private(set) var hasMore = false
    private var cursor: String?
    private var didStart = false

    func reload(api: TuvimaAPIClient?, galleryID: UUID) {
        items = []
        cursor = nil
        hasMore = false
        errorMessage = nil
        isLoading = false
        didStart = true
        loadMore(api: api, galleryID: galleryID)
    }

    func loadIfNeeded(api: TuvimaAPIClient?, galleryID: UUID) {
        guard !didStart else { return }
        didStart = true
        loadMore(api: api, galleryID: galleryID)
    }

    func loadMore(api: TuvimaAPIClient?, galleryID: UUID) {
        guard !isLoading, errorMessage == nil, items.isEmpty || hasMore else { return }
        guard let api else {
            errorMessage = "This device is not connected to a server."
            return
        }
        isLoading = true
        let requestedCursor = cursor
        Task {
            do {
                let page = try await api.viewGalleryItems(galleryID: galleryID, cursor: requestedCursor)
                let known = Set(items.map { $0.id })
                items += page.items.filter { !known.contains($0.id) }
                cursor = page.nextCursor
                hasMore = (page.hasMore ?? false) && page.nextCursor != nil
            } catch {
                if !isCancellation(error) { errorMessage = error.localizedDescription }
            }
            isLoading = false
        }
    }
}

/// What the full-screen viewer opens: the photos around the one tapped, and which one to start on.
struct ViewerSelection: Identifiable {
    let id = UUID()
    let items: [ViewItem]
    let index: Int
}

private let photoColumns = [GridItem(.adaptive(minimum: 96), spacing: 2)]

// MARK: View tab

/// View: the person's own photos and videos as a timeline with month headings, plus their Galleries.
struct ViewGridView: View {
    enum Mode: String, CaseIterable, Identifiable {
        case timeline = "Timeline"
        case galleries = "Galleries"

        var id: String { rawValue }
    }

    @State private var mode: Mode = .timeline

    var body: some View {
        VStack(spacing: 0) {
            Picker("View", selection: $mode) {
                ForEach(Mode.allCases) { item in Text(item.rawValue).tag(item) }
            }
            .pickerStyle(.segmented)
            .padding(.horizontal)
            .padding(.bottom, 8)
            switch mode {
            case .timeline: PhotoTimelineView()
            case .galleries: GalleriesListView()
            }
        }
    }
}

/// A grid cell: the thumbnail, with a duration badge on a video.
struct PhotoCell: View {
    let item: ViewItem

    var body: some View {
        ArtworkImage(path: ClientRoutes.viewThumbnail(item.id, size: 320), photo: true)
            .aspectRatio(1, contentMode: .fit)
            .overlay(alignment: .bottomTrailing) {
                if item.isVideo {
                    HStack(spacing: 3) {
                        Image(systemName: "play.fill")
                        if let seconds = item.durationSeconds { Text(durationText(seconds)) }
                    }
                    .font(.caption2.bold())
                    .foregroundStyle(.white)
                    .padding(4)
                    .background(Color.black.opacity(0.55), in: Capsule())
                    .padding(4)
                }
            }
            .clipped()
            .accessibilityLabel(item.isVideo ? "Video \(item.displayTitle)" : "Photo \(item.displayTitle)")
    }
}

func durationText(_ seconds: Double) -> String {
    let total = max(0, Int(seconds.rounded()))
    return String(format: "%d:%02d", total / 60, total % 60)
}

/// Shows the message to display when a photo call fails, with the exact words for a refused device.
struct PhotoErrorView: View {
    let message: String
    let retry: () -> Void

    var body: some View {
        if message == photoAccessDeniedMessage {
            ContentUnavailableView(
                "Photos are not enabled",
                systemImage: "photo.on.rectangle.angled",
                description: Text(message)
            )
        } else {
            LoadFailedView(message: message, retry: retry)
        }
    }
}

struct PhotoTimelineView: View {
    @EnvironmentObject private var environment: AppEnvironment
    @StateObject private var model = PhotoTimelineModel()
    @State private var viewer: ViewerSelection?

    var body: some View {
        Group {
            if let message = model.errorMessage, model.months.isEmpty {
                PhotoErrorView(message: message) { model.reload(api: environment.api) }
            } else if model.months.isEmpty && model.isLoading {
                ProgressView("Loading photos…")
            } else if model.months.isEmpty {
                ContentUnavailableView(
                    "No photos yet",
                    systemImage: "photo",
                    description: Text("Photos and videos in your View appear here.")
                )
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0, pinnedViews: [.sectionHeaders]) {
                        ForEach(model.months) { month in
                            Section {
                                LazyVGrid(columns: photoColumns, spacing: 2) {
                                    ForEach(month.items) { item in
                                        Button { open(item) } label: { PhotoCell(item: item) }
                                            .buttonStyle(.plain)
                                    }
                                }
                            } header: {
                                Text(monthLabel(year: month.year, month: month.month))
                                    .font(.headline)
                                    .padding(.horizontal)
                                    .padding(.vertical, 6)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .background(.bar)
                            }
                        }
                        Color.clear
                            .frame(height: 1)
                            .onAppear { model.loadMore(api: environment.api) }
                        if model.isLoading { ProgressView().padding() }
                    }
                }
                .refreshable { model.reload(api: environment.api) }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .fullScreenCover(item: $viewer) { selection in
            PhotoViewer(items: selection.items, startIndex: selection.index)
        }
        .task { model.loadIfNeeded(api: environment.api) }
    }

    private func open(_ item: ViewItem) {
        let items = model.allItems
        guard let index = items.firstIndex(where: { $0.id == item.id }) else { return }
        viewer = ViewerSelection(items: items, index: index)
    }
}

// MARK: Galleries

struct GalleriesListView: View {
    @EnvironmentObject private var environment: AppEnvironment
    @State private var state: LoadState<ViewGalleries> = .loading

    var body: some View {
        Group {
            switch state {
            case .loading:
                ProgressView("Loading galleries…")
            case .failed(let message):
                PhotoErrorView(message: message) { Task { await load() } }
            case .loaded(let galleries):
                if galleries.owned.isEmpty && galleries.sharedWithYou.isEmpty {
                    ContentUnavailableView(
                        "No galleries yet",
                        systemImage: "rectangle.stack",
                        description: Text("Galleries you make, and galleries shared with you, appear here.")
                    )
                } else {
                    List {
                        if !galleries.owned.isEmpty {
                            Section("Your galleries") {
                                ForEach(galleries.owned) { gallery in row(gallery) }
                            }
                        }
                        if !galleries.sharedWithYou.isEmpty {
                            Section("Shared with you") {
                                ForEach(galleries.sharedWithYou) { gallery in row(gallery) }
                            }
                        }
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .task {
            if case .loaded = state { return }
            await load()
        }
    }

    private func row(_ gallery: ViewGallery) -> some View {
        NavigationLink(value: GalleryRoute(id: gallery.id, name: gallery.name)) {
            HStack(spacing: 12) {
                if let cover = gallery.coverItemId {
                    ArtworkImage(path: ClientRoutes.viewThumbnail(cover, size: 160), photo: true)
                        .frame(width: 56, height: 56)
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                }
                VStack(alignment: .leading) {
                    Text(gallery.name)
                    Text("\(gallery.itemCount ?? 0) items").font(.caption).foregroundStyle(.secondary)
                }
            }
        }
    }

    private func load() async {
        guard let api = environment.api else {
            state = .failed("This device is not connected to a server.")
            return
        }
        state = .loading
        do {
            state = .loaded(try await api.viewGalleries())
        } catch {
            if isCancellation(error) { return }
            state = .failed(error.localizedDescription)
        }
    }
}

struct GalleryItemsView: View {
    @EnvironmentObject private var environment: AppEnvironment
    let route: GalleryRoute
    @StateObject private var model = GalleryItemsModel()
    @State private var viewer: ViewerSelection?

    var body: some View {
        Group {
            if let message = model.errorMessage, model.items.isEmpty {
                PhotoErrorView(message: message) { model.reload(api: environment.api, galleryID: route.id) }
            } else if model.items.isEmpty && model.isLoading {
                ProgressView("Loading gallery…")
            } else if model.items.isEmpty {
                ContentUnavailableView("Empty gallery", systemImage: "photo", description: Text("This gallery has no photos yet."))
            } else {
                ScrollView {
                    LazyVGrid(columns: photoColumns, spacing: 2) {
                        ForEach(model.items) { item in
                            Button { open(item) } label: { PhotoCell(item: item) }
                                .buttonStyle(.plain)
                        }
                    }
                    Color.clear
                        .frame(height: 1)
                        .onAppear { model.loadMore(api: environment.api, galleryID: route.id) }
                    if model.isLoading { ProgressView().padding() }
                }
            }
        }
        .navigationTitle(route.name)
        .navigationBarTitleDisplayMode(.inline)
        .fullScreenCover(item: $viewer) { selection in
            PhotoViewer(items: selection.items, startIndex: selection.index)
        }
        .task { model.loadIfNeeded(api: environment.api, galleryID: route.id) }
    }

    private func open(_ item: ViewItem) {
        guard let index = model.items.firstIndex(where: { $0.id == item.id }) else { return }
        viewer = ViewerSelection(items: model.items, index: index)
    }
}

// MARK: Full-screen viewer

/// Full-screen photo viewer: swipe between photos, pinch to zoom, double-tap to zoom in or out.
struct PhotoViewer: View {
    @Environment(\.dismiss) private var dismiss
    let items: [ViewItem]
    @State private var selection: Int

    init(items: [ViewItem], startIndex: Int) {
        self.items = items
        _selection = State(initialValue: startIndex)
    }

    var body: some View {
        ZStack(alignment: .topTrailing) {
            Color.black.ignoresSafeArea()
            TabView(selection: $selection) {
                ForEach(Array(items.enumerated()), id: \.offset) { index, item in
                    PhotoViewerPage(item: item).tag(index)
                }
            }
            .tabViewStyle(.page(indexDisplayMode: .never))
            .ignoresSafeArea()
            Button {
                dismiss()
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .font(.title)
                    .symbolRenderingMode(.hierarchical)
                    .foregroundStyle(.white)
                    .padding()
            }
            .accessibilityLabel("Close")
        }
    }
}

/// One page of the viewer: the large preview of a photo, or a video with a Play button.
struct PhotoViewerPage: View {
    @EnvironmentObject private var environment: AppEnvironment
    let item: ViewItem
    @State private var image: UIImage?
    @State private var failed = false
    @State private var player: AVPlayer?

    var body: some View {
        ZStack {
            if let player {
                VideoPlayer(player: player)
            } else if let image {
                ZoomableImage(image: image)
                    .overlay {
                        if item.isVideo {
                            Button {
                                Task { await startVideo() }
                            } label: {
                                Image(systemName: "play.circle.fill")
                                    .font(.system(size: 64))
                                    .foregroundStyle(.white)
                            }
                            .accessibilityLabel("Play video")
                        }
                    }
            } else if failed {
                Text("This photo could not be loaded.").foregroundStyle(.white)
            } else {
                ProgressView().tint(.white)
            }
        }
        .task(id: item.id) { await loadPreview() }
        .onDisappear { player?.pause() }
    }

    private func loadPreview() async {
        let path = ClientRoutes.viewPreview(item.id, size: 2048)
        if let loaded = await loadArtwork(path: path, api: environment.api, photo: true) {
            image = loaded
        } else {
            failed = true
        }
    }

    /// Plays the original file with the device's own credentials (the video route needs the originals scope).
    private func startVideo() async {
        guard let api = environment.api else { return }
        do {
            let path = ClientRoutes.viewOriginal(item.id)
            let url = try await api.absoluteURL(path: path)
            let request = try await api.authorizedRequest(path: path)
            let options: [String: Any] = ["AVURLAssetHTTPHeaderFieldsKey": request.allHTTPHeaderFields ?? [:]]
            let value = AVPlayer(playerItem: AVPlayerItem(asset: AVURLAsset(url: url, options: options)))
            value.play()
            player = value
        } catch {
            failed = true
        }
    }
}

/// An image that pinches to zoom (up to 6x), drags while zoomed, and double-taps between fit and 2x.
struct ZoomableImage: View {
    let image: UIImage
    @State private var scale: CGFloat = 1
    @State private var lastScale: CGFloat = 1
    @State private var offset: CGSize = .zero
    @State private var lastOffset: CGSize = .zero

    var body: some View {
        Image(uiImage: image)
            .resizable()
            .scaledToFit()
            .scaleEffect(scale)
            .offset(offset)
            .gesture(
                MagnifyGesture()
                    .onChanged { value in
                        scale = min(max(lastScale * value.magnification, 1), 6)
                    }
                    .onEnded { _ in
                        lastScale = scale
                        if scale <= 1.01 { reset() }
                    }
            )
            .gesture(
                DragGesture()
                    .onChanged { value in
                        offset = CGSize(
                            width: lastOffset.width + value.translation.width,
                            height: lastOffset.height + value.translation.height
                        )
                    }
                    .onEnded { _ in lastOffset = offset },
                including: scale > 1 ? .all : .none
            )
            .onTapGesture(count: 2) {
                withAnimation {
                    if scale > 1 {
                        reset()
                    } else {
                        scale = 2
                        lastScale = 2
                    }
                }
            }
    }

    private func reset() {
        scale = 1
        lastScale = 1
        offset = .zero
        lastOffset = .zero
    }
}
