import SwiftUI

/// A placeholder: the offline library (the list of downloaded titles and playing them without a connection)
/// arrives in a later update. Starting a download from a title's details already works.
struct DownloadsView: View {
    var body: some View {
        NavigationStack {
            ContentUnavailableView(
                "Downloads are coming soon",
                systemImage: "arrow.down.circle",
                description: Text("Your downloaded titles will be listed here, ready to play without a connection. You can already start a download from a title's details page.")
            )
            .navigationTitle("Downloads")
        }
        .miniPlayerInset()
    }
}
