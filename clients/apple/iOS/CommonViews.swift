import SwiftUI
import TuvimaCore

// MARK: Loading

enum LoadState<Value> {
    case loading
    case loaded(Value)
    case failed(String)
}

/// True for the errors a cancelled `.task` produces; a screen leaving is not a failure to show.
func isCancellation(_ error: Error) -> Bool {
    error is CancellationError || (error as? URLError)?.code == .cancelled
}

struct LoadFailedView: View {
    let message: String
    let retry: () -> Void

    var body: some View {
        ContentUnavailableView {
            Label("Something went wrong", systemImage: "exclamationmark.triangle")
        } description: {
            Text(message)
        } actions: {
            Button("Try again", action: retry)
        }
    }
}

// MARK: Navigation routes

/// Opens a title's details. `assetID` is the file to play when the person arrived from a card that names it.
struct DetailRoute: Hashable {
    let entityType: String
    let id: UUID
    var assetID: UUID? = nil
}

extension DetailRoute {
    init(card: DisplayCard) {
        let target = cardDetailTarget(card)
        self.init(entityType: target.entityType, id: target.id, assetID: card.playAssetID)
    }
}

struct CollectionRoute: Hashable {
    let id: UUID
    let name: String
    let groupType: String
    var universeQid: String? = nil
}

struct UniverseRoute: Hashable {
    let qid: String
    let name: String
}

struct GalleryRoute: Hashable {
    let id: UUID
    let name: String
}

extension View {
    /// Registers every screen a card, collection or gallery can open, once per navigation stack.
    func appDestinations() -> some View {
        navigationDestination(for: DetailRoute.self) { route in DetailView(route: route) }
            .navigationDestination(for: CollectionRoute.self) { route in CollectionDetailView(route: route) }
            .navigationDestination(for: UniverseRoute.self) { route in UniverseView(route: route) }
            .navigationDestination(for: GalleryRoute.self) { route in GalleryItemsView(route: route) }
    }

    /// Keeps the now-playing bar above the tab bar while something is playing.
    func miniPlayerInset() -> some View { modifier(MiniPlayerInset()) }
}

// MARK: Now playing

struct MiniPlayerInset: ViewModifier {
    @EnvironmentObject private var playback: PlaybackCoordinator

    func body(content: Content) -> some View {
        content.safeAreaInset(edge: .bottom) {
            if let title = playback.title {
                HStack {
                    VStack(alignment: .leading) {
                        Text(title).lineLimit(1)
                        if let error = playback.error {
                            Text(error).font(.caption).foregroundStyle(.red).lineLimit(1)
                        }
                    }
                    Spacer()
                    Button(playback.isPlaying ? "Pause" : "Play") {
                        playback.isPlaying ? playback.pause() : playback.resume()
                    }
                }
                .padding()
                .background(.ultraThinMaterial)
            }
        }
    }
}

// MARK: Cards and shelves

/// A poster with its title, a progress line once started, and Play and Download for playable titles.
struct PosterTile: View {
    @EnvironmentObject private var environment: AppEnvironment
    @EnvironmentObject private var playback: PlaybackCoordinator
    let card: DisplayCard

    var body: some View {
        let ratio: CGFloat = card.preferredShape == "square" ? 1 : 2.0 / 3.0
        VStack(alignment: .leading, spacing: 4) {
            NavigationLink(value: DetailRoute(card: card)) {
                VStack(alignment: .leading, spacing: 4) {
                    ArtworkImage(path: card.artworkPath)
                        .aspectRatio(ratio, contentMode: .fit)
                        .overlay(alignment: .bottom) {
                            if let percent = card.progress?.percent, percent > 0 {
                                ProgressView(value: min(percent, 100), total: 100).padding(6)
                            }
                        }
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                    Text(card.title).font(.subheadline).lineLimit(1)
                    if let subtitle = card.subtitle {
                        Text(subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                }
            }
            .buttonStyle(.plain)
            if let asset = card.playAssetID {
                HStack(spacing: 18) {
                    Button {
                        playback.play(assetID: asset, title: card.title, preferLocal: true)
                    } label: {
                        Image(systemName: "play.fill")
                    }
                    .accessibilityLabel("Play \(card.title)")
                    DownloadControl(
                        item: DownloadItem(
                            assetID: asset,
                            title: card.title,
                            subtitle: card.subtitle,
                            artworkPath: card.artworkPath,
                            entityType: detailEntityType(forMediaType: card.mediaType),
                            showID: nil
                        ),
                        compact: true
                    )
                }
                .buttonStyle(.borderless)
            }
        }
    }
}

/// One titled shelf that scrolls sideways.
struct ShelfRow: View {
    let shelf: DisplayShelf
    var title: String? = nil

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title ?? shelf.title).font(.title3.bold()).padding(.horizontal)
            ScrollView(.horizontal, showsIndicators: false) {
                LazyHStack(alignment: .top, spacing: 12) {
                    ForEach(shelf.items) { card in
                        PosterTile(card: card).frame(width: 130)
                    }
                }
                .padding(.horizontal)
            }
        }
    }
}

/// Two-to-four column poster grid for a list of cards.
let posterColumns = [GridItem(.adaptive(minimum: 110, maximum: 170), spacing: 12, alignment: .top)]
