import SwiftUI
import TuvimaCore

/// The title a download control acts on. The artwork path and title are kept with the download so the Downloads
/// list can show it without a connection.
struct DownloadItem {
    let assetID: UUID
    let title: String
    let subtitle: String?
    let artworkPath: String?
    let entityType: String
    /// The show's id when the item is a show, so Downloads can offer "Download next episodes".
    let showID: UUID?

    func request(profileKey: String) -> DownloadRequest {
        DownloadRequest(
            assetID: assetID,
            title: title,
            subtitle: subtitle,
            artworkPath: artworkPath,
            entityType: entityType,
            showID: showID,
            profileKey: profileKey
        )
    }
}

/// Starts an offline download after the person picks a quality, and shows where the download is. Books, comics and
/// photos are not offered: the control draws nothing for them.
struct DownloadControl: View {
    @EnvironmentObject private var environment: AppEnvironment
    let item: DownloadItem
    var compact = false

    var body: some View {
        DownloadControlBody(manager: environment.downloads, item: item, compact: compact)
    }
}

private struct DownloadControlBody: View {
    @ObservedObject var manager: OfflineDownloadManager
    let item: DownloadItem
    let compact: Bool

    var body: some View {
        let kind = OfflineLogic.downloadKind(forEntityType: item.entityType)
        let choices = OfflineLogic.qualityChoices(for: kind)
        let preferred = OfflineLogic.defaultQuality(for: kind, preferred: OfflineDownloadManager.defaultProfileKey)
        if let held = manager.record(for: item.assetID) {
            status(held)
        } else if !choices.isEmpty {
            Menu {
                ForEach(choices) { choice in
                    Button {
                        manager.requestDownload(item.request(profileKey: choice.key))
                    } label: {
                        if choice.key == preferred {
                            Label(choice.label, systemImage: "checkmark")
                        } else {
                            Text(choice.label)
                        }
                    }
                }
            } label: {
                if compact {
                    Image(systemName: "arrow.down.circle")
                } else {
                    Label("Download", systemImage: "arrow.down.circle")
                }
            }
            .accessibilityLabel("Download \(item.title)")
        }
    }

    @ViewBuilder
    private func status(_ held: DownloadRecord) -> some View {
        switch held.state {
        case .complete:
            if compact {
                Image(systemName: "checkmark.circle.fill").accessibilityLabel("Downloaded")
            } else {
                Label("Downloaded", systemImage: "checkmark.circle.fill")
            }
        case .failed:
            if compact {
                Image(systemName: "exclamationmark.circle").accessibilityLabel("Download failed")
            } else {
                Label("Download failed. See Downloads.", systemImage: "exclamationmark.circle")
                    .font(.caption)
            }
        case .paused:
            if compact {
                Image(systemName: "pause.circle").accessibilityLabel("Download paused")
            } else {
                Label("Paused. See Downloads.", systemImage: "pause.circle").font(.caption)
            }
        case .queued, .preparing, .downloading:
            if compact {
                Image(systemName: "arrow.down.circle.dotted").accessibilityLabel("Downloading")
            } else {
                VStack(alignment: .leading, spacing: 4) {
                    Label("Downloading", systemImage: "arrow.down.circle.dotted").font(.caption)
                    if let fraction = OfflineLogic.fraction(done: manager.displayBytes(for: held), total: held.totalBytes) {
                        ProgressView(value: fraction).frame(maxWidth: 120)
                    }
                }
            }
        }
    }
}

/// Shows the manager's one-off messages (the storage limit, already downloaded, next episodes) as an alert.
struct DownloadNoticeModifier: ViewModifier {
    @ObservedObject var manager: OfflineDownloadManager

    func body(content: Content) -> some View {
        content.alert(
            "Downloads",
            isPresented: Binding(
                get: { manager.notice != nil },
                set: { shown in if !shown { manager.dismissNotice() } }
            )
        ) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(manager.notice ?? "")
        }
    }
}
