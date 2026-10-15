import SwiftUI
import TuvimaCore
import UIKit

/// The offline library: every title downloaded to this device (or on its way), with artwork, size, quality and
/// expiry, and playing them without a connection. Photos kept from View are listed here too, under the same storage
/// limit, and open in the photo viewer from the device. Downloads belong to the person using the app.
struct DownloadsView: View {
    @EnvironmentObject private var environment: AppEnvironment

    var body: some View {
        DownloadsList(manager: environment.downloads)
    }
}

private struct DownloadsList: View {
    @ObservedObject var manager: OfflineDownloadManager
    @EnvironmentObject private var playback: PlaybackCoordinator
    @AppStorage("tuvima.downloadCapGB") private var capGB = 10
    @State private var pendingDelete: DownloadRecord?
    @State private var viewer: ViewerSelection?

    var body: some View {
        NavigationStack {
            Group {
                if manager.records.isEmpty {
                    ContentUnavailableView(
                        "No downloads yet",
                        systemImage: "arrow.down.circle",
                        description: Text("Download a movie, episode or audiobook from its details page, or from the download button on a poster, and it will be listed here, ready to play without a connection. Photos and galleries you keep on this device from View are listed here too.")
                    )
                } else {
                    list
                }
            }
            .navigationTitle("Downloads")
        }
        .miniPlayerInset()
        .fullScreenCover(item: $viewer) { selection in
            PhotoViewer(items: selection.items, startIndex: selection.index)
        }
        .task { await manager.refresh() }
        .confirmationDialog(
            "Delete this download?",
            isPresented: Binding(
                get: { pendingDelete != nil },
                set: { shown in if !shown { pendingDelete = nil } }
            ),
            titleVisibility: .visible
        ) {
            Button("Delete", role: .destructive) {
                if let held = pendingDelete { manager.delete(held.assetId) }
                pendingDelete = nil
            }
            Button("Cancel", role: .cancel) { pendingDelete = nil }
        } message: {
            Text("The copy on this device is removed. The title stays in your library on the server.")
        }
    }

    private var list: some View {
        List {
            Section {
                LabeledContent(
                    "Storage used",
                    value: "\(OfflineLogic.formatBytes(manager.usedBytes)) of \(OfflineLogic.formatBytes(OfflineLogic.capBytes(forGB: capGB)))"
                )
            } footer: {
                Text("Change the limit in Settings. New downloads are blocked once the limit is reached.")
            }
            Section {
                ForEach(manager.records) { held in
                    DownloadRow(
                        manager: manager,
                        held: held,
                        onPlay: { playback.play(assetID: held.assetId, title: held.title, preferLocal: true) },
                        onView: {
                            let items = manager.keptPhotoItems()
                            viewer = ViewerSelection(items: items, index: items.firstIndex { $0.id == held.assetId } ?? 0)
                        },
                        onDelete: { pendingDelete = held }
                    )
                }
            }
        }
        .refreshable { await manager.refresh() }
    }
}

private struct DownloadRow: View {
    @ObservedObject var manager: OfflineDownloadManager
    let held: DownloadRecord
    let onPlay: () -> Void
    let onView: () -> Void
    let onDelete: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .top, spacing: 12) {
                LocalArtworkImage(manager: manager, held: held)
                    .frame(width: 60, height: held.isPhoto ? 60 : 90)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                VStack(alignment: .leading, spacing: 4) {
                    Text(held.title).font(.headline).lineLimit(2)
                    if let subtitle = held.subtitle, !subtitle.isEmpty {
                        Text(subtitle).font(.subheadline).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Text(OfflineLogic.statusText(held, bytesDone: manager.displayBytes(for: held), now: Date()))
                        .font(.caption)
                        .foregroundStyle(held.state == .failed ? Color.red : Color.secondary)
                    if held.state == .downloading || held.state == .paused {
                        if let fraction = OfflineLogic.fraction(done: manager.displayBytes(for: held), total: held.totalBytes) {
                            ProgressView(value: fraction)
                        }
                    }
                }
                Spacer(minLength: 0)
            }
            HStack(spacing: 16) {
                primaryAction
                Button(held.isComplete ? "Delete" : "Cancel", role: .destructive, action: onDelete)
                if held.isComplete, let showId = held.showId {
                    Button("Download next episodes") {
                        Task {
                            await manager.downloadNextEpisodes(
                                showID: showId,
                                afterAssetID: held.assetId,
                                profileKey: held.profileKey
                            )
                        }
                    }
                }
            }
            .font(.subheadline)
            .buttonStyle(.borderless)
        }
        .padding(.vertical, 4)
    }

    @ViewBuilder
    private var primaryAction: some View {
        switch held.state {
        case .complete:
            if held.isPhoto {
                Button(action: onView) { Label("View", systemImage: "photo") }
            } else {
                Button(action: onPlay) { Label("Play", systemImage: "play.fill") }
            }
        case .paused:
            Button { manager.resume(held.assetId) } label: { Label("Resume", systemImage: "arrow.down.circle") }
        case .failed:
            Button { manager.resume(held.assetId) } label: { Label("Try again", systemImage: "arrow.clockwise") }
        case .queued, .preparing, .downloading:
            Button { manager.pause(held.assetId) } label: { Label("Pause", systemImage: "pause.fill") }
        }
    }
}

/// The artwork saved on the device when it has it, otherwise the server's (which needs a connection).
private struct LocalArtworkImage: View {
    @ObservedObject var manager: OfflineDownloadManager
    let held: DownloadRecord
    @State private var image: UIImage?

    var body: some View {
        Color.secondary.opacity(0.18)
            .overlay {
                if let image {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFill()
                } else if !held.hasLocalArtwork && !held.isPhoto {
                    ArtworkImage(path: held.artworkPath)
                }
            }
            .clipped()
            .task(id: "\(held.hasLocalArtwork)-\(held.state.rawValue)") {
                // A kept photo is its own picture: read from the device, never from the server.
                if held.isPhoto {
                    image = manager.localPhotoImage(itemID: held.assetId, maxEdge: 256)
                } else if let url = manager.artworkFileURL(for: held) {
                    image = UIImage(contentsOfFile: url.path)
                }
            }
    }
}
