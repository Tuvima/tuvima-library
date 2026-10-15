import Foundation

/// What a phone screen is for, as far as turning the device goes.
public enum ScreenKind: Equatable, Sendable {
    case browse, audio, photo, video
}

/// `portrait` locks the phone to portrait; `rotatable` lets it turn to landscape (everything except upside down).
public enum OrientationRule: Equatable, Sendable {
    case portrait, rotatable
}

/// The iPhone app is locked to portrait everywhere except actual video playback, which may rotate to landscape.
/// Audio, books and photos stay portrait. Kept free of UIKit so a plain logic test can check it.
public enum OrientationPolicy {
    public static func rule(for kind: ScreenKind) -> OrientationRule {
        kind == .video ? .rotatable : .portrait
    }
}
