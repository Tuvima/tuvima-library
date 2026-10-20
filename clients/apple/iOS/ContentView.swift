import SwiftUI
import TuvimaCore

/// The first screen: connect to a server and pair, then hand over to the tabs.
struct ContentView: View {
    @EnvironmentObject private var environment: AppEnvironment
    @StateObject private var model = ConnectionModel()

    var body: some View {
        Group {
            switch model.phase {
            case .server:
                ServerSetupView(model: model)
            case .loading(let message):
                ProgressView(message)
            case .pairing(let pairing):
                PairingView(pairing: pairing) { model.cancelPairing() }
            case .ready:
                MainTabView()
            case .error(let message):
                ContentUnavailableView(
                    "Library needs attention",
                    systemImage: "exclamationmark.triangle",
                    description: Text(message)
                ).overlay(alignment: .bottom) {
                    Button("Connect again") { model.phase = .server }.buttonStyle(.borderedProminent).padding()
                }
            }
        }
        .task { model.start(environment: environment) }
        .onChange(of: environment.api == nil) { _, isSignedOut in
            if isSignedOut { model.cancelPairing() }
        }
    }
}

/// The server address, and the one opt-in for photos and videos.
struct ServerSetupView: View {
    @EnvironmentObject private var environment: AppEnvironment
    @ObservedObject var model: ConnectionModel
    @State private var server = ""
    @AppStorage(PairingSettings.photosKey) private var allowPhotos = false

    var body: some View {
        NavigationStack {
            Form {
                Section("Connect to Library") {
                    TextField("https://library.example", text: $server)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .keyboardType(.URL)
                    Button("Connect") { model.connect(server, environment: environment) }
                        .disabled(server.trimmingCharacters(in: .whitespaces).isEmpty)
                }
                Section {
                    Toggle("Allow access to my photos and videos", isOn: $allowPhotos)
                } footer: {
                    Text("Browse your own photos and videos, view and download originals, and your galleries, including galleries shared with you. You still approve this on the server's pairing screen, and it is off unless you turn it on here.")
                }
            }
            .navigationTitle("Library")
        }
    }
}

struct PairingView: View {
    let pairing: PairingSession
    let cancel: () -> Void

    var body: some View {
        VStack(spacing: 20) {
            Text("Pair this device").font(.largeTitle.bold())
            Text(pairing.verificationURI)
            Text(pairing.userCode).font(.system(size: 44, weight: .bold, design: .monospaced))
            ProgressView("Waiting for approval…")
            Button("Cancel", role: .cancel, action: cancel)
        }
        .padding()
    }
}
