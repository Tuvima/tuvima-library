import Foundation
import TuvimaCore

final class OfflineDownloadManager: NSObject, ObservableObject, URLSessionDownloadDelegate {
    @Published private(set) var progress: [UUID: Double] = [:]
    @Published private(set) var lastError: String?
    private var api: TuvimaAPIClient?
    private lazy var session: URLSession = {
        let configuration = URLSessionConfiguration.background(withIdentifier: "com.tuvima.library.offline.v1")
        configuration.waitsForConnectivity = true
        configuration.isDiscretionary = false
        return URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
    }()

    func attach(_ api: TuvimaAPIClient) { self.api = api }

    /// The default download quality chosen in Settings; a transcoding profile key such as `mobile-standard`.
    static var defaultProfileKey: String {
        UserDefaults.standard.string(forKey: "tuvima.downloadQuality") ?? "mobile-standard"
    }

    func download(assetID: UUID, profileKey: String = OfflineDownloadManager.defaultProfileKey) {
        Task {
            do { try await requestAndStart(assetID: assetID, profileKey: profileKey) }
            catch { await MainActor.run { self.lastError = error.localizedDescription } }
        }
    }

    private func requestAndStart(assetID: UUID, profileKey: String) async throws {
        guard let api else { return }
        try await api.requestOfflineVariant(assetID: assetID, profileKey: profileKey)
        for _ in 0..<180 {
            let manifest = try await api.playbackManifest(assetID: assetID)
            if let path = manifest.offlineVariants.first(where: { $0.status == "ready" })?.downloadUrl {
                var request = try await api.authorizedRequest(path: path)
                request.setValue("no-store", forHTTPHeaderField: "Cache-Control")
                let task = session.downloadTask(with: request)
                task.taskDescription = assetID.uuidString
                task.resume()
                return
            }
            try await Task.sleep(for: .seconds(2))
        }
    }

    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didWriteData bytesWritten: Int64,
        totalBytesWritten: Int64,
        totalBytesExpectedToWrite: Int64
    ) {
        guard let value = downloadTask.taskDescription.flatMap(UUID.init(uuidString:)) else { return }
        Task { @MainActor in
            progress[value] = totalBytesExpectedToWrite > 0
                ? Double(totalBytesWritten) / Double(totalBytesExpectedToWrite)
                : 0
        }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        guard let value = downloadTask.taskDescription.flatMap(UUID.init(uuidString:)) else { return }
        do {
            let root = try FileManager.default.url(
                for: .applicationSupportDirectory,
                in: .userDomainMask,
                appropriateFor: nil,
                create: true
            ).appendingPathComponent("Offline", isDirectory: true)
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            var values = URLResourceValues()
            values.isExcludedFromBackup = true
            var mutableRoot = root
            try mutableRoot.setResourceValues(values)
            let destination = root.appendingPathComponent("\(value.uuidString).media")
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.moveItem(at: location, to: destination)
            Task { @MainActor in progress[value] = 1 }
        } catch {
            Task { @MainActor in progress[value] = nil }
        }
    }
}
