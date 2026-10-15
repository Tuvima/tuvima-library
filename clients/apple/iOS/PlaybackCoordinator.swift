import AVFoundation
import Foundation
import MediaPlayer
import Network
import TuvimaCore

@MainActor
final class PlaybackCoordinator: ObservableObject {
    @Published private(set) var title: String?
    @Published private(set) var isPlaying = false
    @Published private(set) var error: String?

    private var api: TuvimaAPIClient?
    private var offline: OfflineDownloadManager?
    private var player: AVPlayer?
    private var activeAssetID: UUID?
    /// The file being played from this device's own storage, when it is not streaming. Local plays record their
    /// progress on the device and the Engine receives it later; they send no live heartbeats.
    private var localAssetID: UUID?
    private var endObserver: NSObjectProtocol?
    private var heartbeatTask: Task<Void, Never>?
    private var timeObserver: Any?
    private let pathMonitor = NWPathMonitor()

    init() {
        configureRemoteCommands()
        pathMonitor.pathUpdateHandler = { [weak self] _ in
            Task { @MainActor in await self?.recoverAfterNetworkChange() }
        }
        pathMonitor.start(queue: DispatchQueue(label: "com.tuvima.library.network"))
    }

    func attach(_ api: TuvimaAPIClient) { self.api = api }

    func attachOffline(_ manager: OfflineDownloadManager) { offline = manager }

    /// Plays an asset. With `preferLocal`, a finished download on this device plays from its file (no connection
    /// needed); otherwise, and for everything not downloaded, it streams from the server.
    func play(assetID: UUID, title: String, preferLocal: Bool = false) {
        self.title = title
        activeAssetID = assetID
        error = nil
        recordLocalProgress()
        if preferLocal, let file = offline?.playableLocalFile(assetID: assetID) {
            playLocal(assetID: assetID, file: file)
            return
        }
        localAssetID = nil
        Task {
            do {
                let manifest = try await playableManifest(assetID)
                try await start(manifest)
            } catch {
                self.error = error.localizedDescription
            }
        }
    }

    func pause() {
        player?.pause()
        isPlaying = false
        updateNowPlaying()
        if localAssetID != nil {
            recordLocalProgress()
            offline?.scheduleSync()
        }
    }

    func resume() { player?.play(); isPlaying = true; updateNowPlaying() }

    // MARK: Playing a downloaded file

    private func playLocal(assetID: UUID, file: URL) {
        heartbeatTask?.cancel()
        localAssetID = assetID
        do {
            try AVAudioSession.sharedInstance().setCategory(.playback, mode: .default, options: [.allowAirPlay, .allowBluetoothA2DP])
            try AVAudioSession.sharedInstance().setActive(true)
        } catch {
            self.error = error.localizedDescription
            return
        }
        if let observer = timeObserver {
            player?.removeTimeObserver(observer)
            timeObserver = nil
        }
        if let ended = endObserver {
            NotificationCenter.default.removeObserver(ended)
            endObserver = nil
        }
        player?.pause()
        let item = AVPlayerItem(url: file)
        let local = AVPlayer(playerItem: item)
        player = local
        let start = offline?.resumeSeconds(assetID: assetID) ?? 0
        if start > 0 { local.seek(to: CMTime(seconds: start, preferredTimescale: 600)) }
        local.play()
        isPlaying = true
        timeObserver = local.addPeriodicTimeObserver(
            forInterval: CMTime(seconds: 1, preferredTimescale: 1),
            queue: .main
        ) { [weak self] _ in Task { @MainActor in self?.updateNowPlaying() } }
        endObserver = NotificationCenter.default.addObserver(
            forName: .AVPlayerItemDidPlayToEndTime,
            object: item,
            queue: .main
        ) { [weak self] _ in Task { @MainActor in self?.localPlaybackEnded() } }
        beginLocalProgress()
        updateNowPlaying()
    }

    /// Saves where the person is in the downloaded file; the Engine gets it when the device is back online.
    private func recordLocalProgress(finished: Bool = false) {
        guard let assetID = localAssetID, let player, let manager = offline else { return }
        let seconds = player.currentTime().seconds
        guard seconds.isFinite else { return }
        let duration = player.currentItem?.duration.seconds
        manager.recordProgress(
            assetID: assetID,
            position: seconds,
            duration: duration?.isFinite == true ? duration : nil,
            finished: finished
        )
    }

    private func beginLocalProgress() {
        heartbeatTask?.cancel()
        heartbeatTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(10))
                guard let self else { return }
                if self.isPlaying { self.recordLocalProgress() }
            }
        }
    }

    private func localPlaybackEnded() {
        recordLocalProgress(finished: true)
        isPlaying = false
        updateNowPlaying()
        offline?.scheduleSync()
    }

    private func playableManifest(_ assetID: UUID) async throws -> PlaybackManifest {
        guard let api else { throw TuvimaClientError.notPaired }
        for _ in 0..<60 {
            let manifest = try await api.playbackManifest(assetID: assetID)
            if manifest.recommendedDelivery == "direct-stream", manifest.directStreamUrl != nil { return manifest }
            if manifest.recommendedDelivery == "hls", manifest.hlsStatus == "ready", manifest.hlsUrl != nil { return manifest }
            if manifest.hlsStatus != "preparing" { throw TuvimaClientError.noPlayableDelivery }
            try await Task.sleep(for: .seconds(2))
        }
        throw TuvimaClientError.noPlayableDelivery
    }

    private func start(_ manifest: PlaybackManifest) async throws {
        guard let api else { throw TuvimaClientError.notPaired }
        let path = manifest.recommendedDelivery == "hls" ? manifest.hlsUrl : manifest.directStreamUrl
        guard let path else { throw TuvimaClientError.noPlayableDelivery }
        let url = try await api.absoluteURL(path: path)
        let options: [String: Any]
        if manifest.recommendedDelivery == "direct-stream" {
            let request = try await api.authorizedRequest(path: path)
            options = ["AVURLAssetHTTPHeaderFieldsKey": request.allHTTPHeaderFields ?? [:]]
        } else {
            options = [:]
        }
        try AVAudioSession.sharedInstance().setCategory(.playback, mode: .default, options: [.allowAirPlay, .allowBluetoothA2DP])
        try AVAudioSession.sharedInstance().setActive(true)
        let item = AVPlayerItem(asset: AVURLAsset(url: url, options: options))
        if let observer = timeObserver { player?.removeTimeObserver(observer) }
        if let ended = endObserver {
            NotificationCenter.default.removeObserver(ended)
            endObserver = nil
        }
        player = AVPlayer(playerItem: item)
        if let resume = manifest.resume, !resume.completed {
            await player?.seek(to: CMTime(seconds: resume.positionSeconds, preferredTimescale: 600))
        }
        player?.play()
        isPlaying = true
        timeObserver = player?.addPeriodicTimeObserver(
            forInterval: CMTime(seconds: 1, preferredTimescale: 1),
            queue: .main
        ) { [weak self] _ in Task { @MainActor in self?.updateNowPlaying() } }
        beginHeartbeats()
        updateNowPlaying()
    }

    private func beginHeartbeats() {
        heartbeatTask?.cancel()
        heartbeatTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(15))
                guard let self, let api = self.api, let assetID = self.activeAssetID, let player = self.player else { continue }
                let duration = player.currentItem?.duration.seconds
                try? await api.heartbeat(
                    assetID: assetID,
                    isPlaying: player.rate > 0,
                    position: player.currentTime().seconds,
                    duration: duration?.isFinite == true ? duration : nil,
                    rate: Double(player.rate)
                )
            }
        }
    }

    private func recoverAfterNetworkChange() async {
        guard isPlaying, localAssetID == nil, let assetID = activeAssetID, let current = player?.currentTime().seconds else { return }
        do {
            let manifest = try await playableManifest(assetID)
            try await start(manifest)
            await player?.seek(to: CMTime(seconds: current, preferredTimescale: 600))
        } catch { self.error = error.localizedDescription }
    }

    private func configureRemoteCommands() {
        let center = MPRemoteCommandCenter.shared()
        center.playCommand.addTarget { [weak self] _ in
            Task { @MainActor in self?.resume() }
            return .success
        }
        center.pauseCommand.addTarget { [weak self] _ in
            Task { @MainActor in self?.pause() }
            return .success
        }
        center.changePlaybackPositionCommand.addTarget { [weak self] event in
            guard let event = event as? MPChangePlaybackPositionCommandEvent else { return .commandFailed }
            Task { @MainActor in
                self?.player?.seek(to: CMTime(seconds: event.positionTime, preferredTimescale: 600))
            }
            return .success
        }
    }

    private func updateNowPlaying() {
        guard let player else { return }
        MPNowPlayingInfoCenter.default().nowPlayingInfo = [
            MPMediaItemPropertyTitle: title ?? "Tuvima Library",
            MPNowPlayingInfoPropertyElapsedPlaybackTime: player.currentTime().seconds,
            MPNowPlayingInfoPropertyPlaybackRate: player.rate,
            MPMediaItemPropertyPlaybackDuration: player.currentItem?.duration.seconds ?? 0,
        ]
    }
}
