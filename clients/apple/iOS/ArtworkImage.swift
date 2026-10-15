import SwiftUI
import TuvimaCore
import UIKit

/// Decoded artwork kept for the life of the app so scrolling back does not download it again.
final class ImageCache: @unchecked Sendable {
    static let shared = ImageCache()
    private let cache = NSCache<NSString, UIImage>()

    private init() { cache.countLimit = 300 }

    func image(for key: String) -> UIImage? { cache.object(forKey: key as NSString) }

    func store(_ image: UIImage, for key: String) { cache.setObject(image, forKey: key as NSString) }

    func removeAll() { cache.removeAllObjects() }
}

/// The Engine limits how many image requests run at once, so the app keeps its own downloads under that.
actor ImageGate {
    static let shared = ImageGate(limit: 6)
    private let limit: Int
    private var running = 0
    private var waiters: [CheckedContinuation<Void, Never>] = []

    init(limit: Int) { self.limit = limit }

    func acquire() async {
        if running < limit {
            running += 1
            return
        }
        await withCheckedContinuation { continuation in
            waiters.append(continuation)
        }
    }

    func release() {
        if waiters.isEmpty {
            running -= 1
        } else {
            waiters.removeFirst().resume()
        }
    }
}

/// Fetches an image with the device's own credentials. `AsyncImage` cannot send the bearer token, so every
/// artwork and photo goes through this loader. A nil or failed load leaves the neutral placeholder.
@MainActor
func loadArtwork(path: String?, api: TuvimaAPIClient?, photo: Bool = false) async -> UIImage? {
    guard let path, !path.isEmpty, let api else { return nil }
    if let cached = ImageCache.shared.image(for: path) { return cached }
    await ImageGate.shared.acquire()
    var data: Data?
    do {
        if photo {
            data = try await api.fetchPhotoData(path: path)
        } else {
            data = try await api.fetchData(path: path)
        }
    } catch {
        // A missing image is not worth an error: the tile keeps its placeholder.
        data = nil
    }
    await ImageGate.shared.release()
    guard let data, let image = UIImage(data: data) else { return nil }
    ImageCache.shared.store(image, for: path)
    return image
}

/// A server-relative image path shown with a neutral placeholder behind it, filled and cropped to its frame.
struct ArtworkImage: View {
    @EnvironmentObject private var environment: AppEnvironment
    let path: String?
    var photo: Bool = false
    @State private var image: UIImage?

    var body: some View {
        Color.secondary.opacity(0.18)
            .overlay {
                if let image {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFill()
                }
            }
            .clipped()
            .task(id: path) {
                image = ImageCache.shared.image(for: path ?? "")
                if image == nil { image = await loadArtwork(path: path, api: environment.api, photo: photo) }
            }
    }
}
