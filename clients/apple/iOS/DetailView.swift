import SwiftUI
import TuvimaCore

/// A title's details: poster, title, year, runtime, synopsis, cast, editions, and Play or Resume with Download.
/// The play and download buttons need a file to act on: they appear when the person opened the title from a
/// card that names its file (Home, a Library, a Collection or a Universe).
struct DetailView: View {
    @EnvironmentObject private var environment: AppEnvironment
    @EnvironmentObject private var playback: PlaybackCoordinator
    let route: DetailRoute
    @State private var state: LoadState<DetailPage> = .loading

    var body: some View {
        content
            .navigationBarTitleDisplayMode(.inline)
            .task {
                if case .loaded = state { return }
                await load()
            }
    }

    @ViewBuilder
    private var content: some View {
        switch state {
        case .loading:
            ProgressView("Loading details…")
        case .failed(let message):
            LoadFailedView(message: message) { Task { await load() } }
        case .loaded(let detail):
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    header(detail)
                    actions(detail)
                    if let tagline = detail.tagline, !tagline.isEmpty {
                        Text(tagline).font(.headline)
                    }
                    if let description = detail.description, !description.isEmpty {
                        Text(description)
                    }
                    cast(detail)
                    editions(detail)
                }
                .padding()
            }
        }
    }

    private func header(_ detail: DetailPage) -> some View {
        HStack(alignment: .top, spacing: 16) {
            ArtworkImage(path: detail.posterPath)
                .frame(width: 120, height: 180)
                .clipShape(RoundedRectangle(cornerRadius: 10))
            VStack(alignment: .leading, spacing: 6) {
                Text(detail.title).font(.title2.bold())
                if let subtitle = detail.subtitle, !subtitle.isEmpty {
                    Text(subtitle).font(.subheadline).foregroundStyle(.secondary)
                }
                if let summary = detail.facts?.summary, !summary.isEmpty {
                    Text(summary).font(.subheadline)
                }
                if let genres = detail.facts?.genres, !genres.isEmpty {
                    Text(genres.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary)
                }
            }
        }
    }

    @ViewBuilder
    private func actions(_ detail: DetailPage) -> some View {
        if let asset = route.assetID {
            HStack {
                Button {
                    playback.play(assetID: asset, title: detail.title)
                } label: {
                    Label(detail.isInProgress ? "Resume" : "Play", systemImage: "play.fill")
                }
                .buttonStyle(.borderedProminent)
                DownloadButton(assetID: asset)
            }
        } else {
            Text("Play and Download appear when you open a title from Home, a Library or a Collection.")
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
    }

    @ViewBuilder
    private func cast(_ detail: DetailPage) -> some View {
        let people = detail.cast
        if !people.isEmpty {
            VStack(alignment: .leading, spacing: 4) {
                Text("Cast").font(.title3.bold())
                ForEach(people) { person in
                    if let role = person.role, !role.isEmpty {
                        Text("\(person.name) · \(role)")
                    } else {
                        Text(person.name)
                    }
                }
            }
        }
    }

    @ViewBuilder
    private func editions(_ detail: DetailPage) -> some View {
        let formats = detail.ownedFormats ?? []
        if !formats.isEmpty {
            VStack(alignment: .leading, spacing: 8) {
                Text("Editions").font(.title3.bold())
                ForEach(Array(formats.enumerated()), id: \.offset) { _, edition in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(edition.title).font(.subheadline.bold())
                        if let details = edition.details {
                            Text(details).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(12)
                    .background(.quaternary, in: RoundedRectangle(cornerRadius: 10))
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
            state = .loaded(try await api.details(entityType: route.entityType, id: route.id))
        } catch {
            if isCancellation(error) { return }
            state = .failed(error.localizedDescription)
        }
    }
}

/// Starts an offline download at the default quality chosen in Settings and shows its progress.
struct DownloadButton: View {
    @EnvironmentObject private var environment: AppEnvironment
    let assetID: UUID

    var body: some View {
        DownloadProgressButton(manager: environment.downloads, assetID: assetID)
    }
}

private struct DownloadProgressButton: View {
    @ObservedObject var manager: OfflineDownloadManager
    let assetID: UUID

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Button {
                manager.download(assetID: assetID)
            } label: {
                Label("Download", systemImage: "arrow.down.circle")
            }
            .buttonStyle(.bordered)
            .disabled(manager.progress[assetID] != nil)
            if let value = manager.progress[assetID] {
                ProgressView(value: value)
                    .frame(maxWidth: 120)
            }
            if let message = manager.lastError {
                Text(message).font(.caption2).foregroundStyle(.red)
            }
        }
    }
}
