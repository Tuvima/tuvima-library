import CarPlay
import Foundation
import TuvimaCore

@MainActor
final class CarPlaySceneDelegate: UIResponder, CPTemplateApplicationSceneDelegate {
    private var interfaceController: CPInterfaceController?

    func templateApplicationScene(
        _ templateApplicationScene: CPTemplateApplicationScene,
        didConnect interfaceController: CPInterfaceController
    ) {
        self.interfaceController = interfaceController
        let loading = CPListTemplate(title: "Tuvima Library", sections: [
            CPListSection(items: [CPListItem(text: "Loading Listen…", detailText: nil)])
        ])
        interfaceController.setRootTemplate(loading, animated: false)
        Task { await loadRoot() }
    }

    func templateApplicationScene(
        _ templateApplicationScene: CPTemplateApplicationScene,
        didDisconnect interfaceController: CPInterfaceController
    ) {
        self.interfaceController = nil
    }

    private func loadRoot() async {
        guard let api = AppEnvironment.shared.api else {
            let unavailable = CPInformationTemplate(
                title: "Open Tuvima on iPhone",
                layout: .leading,
                items: [CPInformationItem(title: "Pairing required", detail: "Connect and pair this iPhone before using CarPlay.")],
                actions: []
            )
            _ = try? await interfaceController?.setRootTemplate(unavailable, animated: true)
            return
        }
        let definitions = [
            ("Music", "Music"),
            ("Audiobooks", "Audiobooks"),
            ("Playlists", "Playlists"),
            ("Queue", "Queue"),
            ("Recent", "Recent"),
        ]
        let items = definitions.map { title, mediaType in
            let item = CPListItem(text: title, detailText: nil)
            item.handler = { [weak self] _, completion in
                Task { @MainActor in
                    await self?.open(title: title, mediaType: mediaType, api: api)
                    completion()
                }
            }
            return item
        }
        _ = try? await interfaceController?.setRootTemplate(
            CPListTemplate(title: "Listen", sections: [CPListSection(items: items)]),
            animated: true
        )
    }

    private func open(title: String, mediaType: String, api: TuvimaAPIClient) async {
        do {
            let entries: [(asset: UUID, title: String, subtitle: String?)]
            if mediaType == "Queue" {
                entries = try await api.playerQueue().compactMap { item in
                    item.assetId.map { ($0, item.title, item.subtitle) }
                }
            } else {
                let page: DisplayPage
                if mediaType == "Recent" {
                    page = try await api.continuePage(lane: "listen")
                } else {
                    page = try await api.browse(lane: "listen", mediaType: mediaType)
                }
                let cards = page.shelves.flatMap(\.items).reduce(into: [UUID: DisplayCard]()) { result, card in
                    if let asset = card.playableAssetID { result[asset] = card }
                }
                entries = cards.map { ($0.key, $0.value.title, $0.value.subtitle) }
            }
            let items = entries.map { entry in
                let item = CPListItem(text: entry.title, detailText: entry.subtitle)
                item.handler = { _, completion in
                    Task { @MainActor in
                        AppEnvironment.shared.playback.play(assetID: entry.asset, title: entry.title)
                        self.interfaceController?.pushTemplate(CPNowPlayingTemplate.shared, animated: true)
                        completion()
                    }
                }
                return item
            }
            _ = try? await interfaceController?.pushTemplate(
                CPListTemplate(title: title, sections: [CPListSection(items: Array(items.prefix(100)))]),
                animated: true
            )
        } catch {
            let template = CPAlertTemplate(titleVariants: ["Tuvima could not load \(title)."], actions: [
                CPAlertAction(title: "OK", style: .default) { _ in }
            ])
            _ = try? await interfaceController?.presentTemplate(template, animated: true)
        }
    }
}
