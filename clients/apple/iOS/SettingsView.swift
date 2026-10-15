import SwiftUI
import TuvimaCore

/// Switching profile: the people this app may use, the PIN step, and what to say after each refusal.
@MainActor
final class ProfileSwitchModel: ObservableObject {
    @Published private(set) var profiles: [DeviceProfile] = []
    @Published private(set) var isWorking = false
    @Published private(set) var loaded = false
    @Published var message: String?
    @Published var pinTarget: DeviceProfile?

    func load(api: TuvimaAPIClient?) async {
        guard let api else { return }
        do {
            profiles = try await api.deviceProfiles()
            loaded = true
        } catch {
            if !isCancellation(error) { message = error.localizedDescription }
        }
    }

    /// Switches to `profile`. A profile with a PIN asks for it first; the Engine checks it at every switch.
    /// Returns true when the switch worked.
    func choose(_ profile: DeviceProfile, pin: String?, api: TuvimaAPIClient?) async -> Bool {
        guard let api, !isWorking, !profile.isActive else { return false }
        if profile.requiresPin && (pin ?? "").isEmpty {
            message = nil
            pinTarget = profile
            return false
        }
        isWorking = true
        message = nil
        let result = await api.switchProfile(profileID: profile.profileId, pin: pin)
        isWorking = false
        switch result {
        case .switched:
            pinTarget = nil
            await load(api: api)
            return true
        case .pinRequired:
            message = profileSwitchMessage(result, pinWasSent: !(pin ?? "").isEmpty)
            pinTarget = profile
        case .alreadyActive:
            message = profileSwitchMessage(result, pinWasSent: false)
            await load(api: api)
        default:
            message = profileSwitchMessage(result, pinWasSent: false)
        }
        return false
    }
}

/// Settings: server address, profile switch, photo access, default download quality, and sign out.
struct SettingsView: View {
    @EnvironmentObject private var environment: AppEnvironment
    @StateObject private var model = ProfileSwitchModel()
    @AppStorage("tuvima.downloadQuality") private var downloadQuality = "mobile-standard"
    @AppStorage("tuvima.downloadCapGB") private var downloadCapGB = 10
    @State private var pin = ""
    @State private var showingPin = false
    @State private var photoAccess = false
    @State private var confirmingSignOut = false

    private let qualityChoices: [(key: String, label: String)] = [
        ("mobile-standard", "Standard (720p)"),
        ("mobile-small", "Small (540p)"),
    ]

    var body: some View {
        NavigationStack {
            Form {
                Section("Server") {
                    LabeledContent("Address", value: environment.serverAddress)
                    Text("Sign out to connect to a different server.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
                profileSection
                Section {
                    LabeledContent("Photos and videos", value: photoAccess ? "Allowed" : "Not allowed")
                } header: {
                    Text("Photos")
                } footer: {
                    Text("Photo access is chosen when this device is paired. To change it, sign out, tick Allow access to my photos and videos on the connect screen, and pair again.")
                }
                Section {
                    Picker("Default quality", selection: $downloadQuality) {
                        ForEach(qualityChoices, id: \.key) { choice in Text(choice.label).tag(choice.key) }
                    }
                    Picker("Storage limit", selection: $downloadCapGB) {
                        ForEach(OfflineLogic.capOptionsGB, id: \.self) { gigabytes in
                            Text("\(gigabytes) GB").tag(gigabytes)
                        }
                    }
                    StorageUsageRow(manager: environment.downloads, capGB: downloadCapGB)
                } header: {
                    Text("Downloads")
                } footer: {
                    Text("You choose the quality each time you download. New downloads are blocked once downloads use the whole limit; delete a download to make room.")
                }
                Section {
                    Button("Sign out", role: .destructive) { confirmingSignOut = true }
                } footer: {
                    Text("Signing out removes this device from your library on the server.")
                }
            }
            .navigationTitle("Settings")
        }
        .miniPlayerInset()
        .task {
            await model.load(api: environment.api)
            if let api = environment.api { photoAccess = await api.hasPhotoAccess }
        }
        .alert("Enter PIN", isPresented: $showingPin) {
            SecureField("PIN", text: $pin)
                .keyboardType(.numberPad)
            Button("Cancel", role: .cancel) {
                pin = ""
                model.pinTarget = nil
            }
            Button("Switch") { submitPin() }
        } message: {
            Text("Enter the PIN for \(model.pinTarget?.displayName ?? "this profile").")
        }
        .confirmationDialog("Sign out of this server?", isPresented: $confirmingSignOut, titleVisibility: .visible) {
            Button("Sign out", role: .destructive) { Task { await environment.signOut() } }
            Button("Cancel", role: .cancel) {}
        }
    }

    @ViewBuilder
    private var profileSection: some View {
        Section {
            if !model.loaded {
                Text(model.message ?? "Loading profiles…").foregroundStyle(.secondary)
            } else {
                ForEach(model.profiles) { profile in
                    Button {
                        Task { await choose(profile, pin: nil) }
                    } label: {
                        HStack {
                            VStack(alignment: .leading) {
                                Text(profile.displayName).foregroundStyle(.primary)
                                if profile.isKids { Text("Kids").font(.caption).foregroundStyle(.secondary) }
                            }
                            Spacer()
                            if profile.requiresPin && !profile.isActive {
                                Image(systemName: "lock.fill").foregroundStyle(.secondary)
                            }
                            if profile.isActive {
                                Image(systemName: "checkmark").foregroundStyle(Color.accentColor)
                            }
                        }
                    }
                    .disabled(model.isWorking)
                }
                if let message = model.message {
                    Text(message).font(.footnote).foregroundStyle(.red)
                }
            }
        } header: {
            Text("Profile")
        } footer: {
            Text("Switching profile does not pair this device again. Playback stops, and every screen loads for the chosen person.")
        }
    }

    private func submitPin() {
        guard let target = model.pinTarget else { return }
        let entered = pin
        pin = ""
        Task { await choose(target, pin: entered) }
    }

    private func choose(_ profile: DeviceProfile, pin: String?) async {
        let switched = await model.choose(profile, pin: pin, api: environment.api)
        if switched {
            environment.profileSwitched()
            if let api = environment.api { photoAccess = await api.hasPhotoAccess }
        } else if model.pinTarget != nil {
            // The PIN is missing or was wrong: ask again.
            showingPin = true
        }
    }
}

/// How much of the storage limit the downloads use.
private struct StorageUsageRow: View {
    @ObservedObject var manager: OfflineDownloadManager
    let capGB: Int

    var body: some View {
        LabeledContent(
            "Used",
            value: "\(OfflineLogic.formatBytes(manager.usedBytes)) of \(OfflineLogic.formatBytes(OfflineLogic.capBytes(forGB: capGB)))"
        )
    }
}
