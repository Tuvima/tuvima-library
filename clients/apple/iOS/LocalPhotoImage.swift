import ImageIO
import TuvimaCore
import UIKit

extension OfflineDownloadManager {
    /// A photo kept on this device, read from the device at about `maxEdge` pixels on its longest side (no network),
    /// or nil when the photo is not kept, not finished, or its file is gone.
    @MainActor
    func localPhotoImage(itemID: UUID, maxEdge: CGFloat) -> UIImage? {
        guard let url = localPhotoFile(itemID: itemID) else { return nil }
        let key = "file:\(url.path)@\(Int(maxEdge))"
        if let cached = ImageCache.shared.image(for: key) { return cached }
        guard let image = Self.downsampledImage(at: url, maxEdge: maxEdge) else { return nil }
        ImageCache.shared.store(image, for: key)
        return image
    }

    /// Decodes an image file no larger than `maxEdge` pixels, so a grid of 2048 px previews stays light on memory.
    static func downsampledImage(at url: URL, maxEdge: CGFloat) -> UIImage? {
        let sourceOptions: [CFString: Any] = [kCGImageSourceShouldCache: false]
        guard let source = CGImageSourceCreateWithURL(url as CFURL, sourceOptions as CFDictionary) else { return nil }
        let thumbnailOptions: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceShouldCacheImmediately: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxEdge,
        ]
        guard let cgImage = CGImageSourceCreateThumbnailAtIndex(source, 0, thumbnailOptions as CFDictionary) else { return nil }
        return UIImage(cgImage: cgImage)
    }
}
