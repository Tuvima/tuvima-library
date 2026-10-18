# Spec: Open from the start — retire "Who can connect"

> **Status:** In progress · **Version:** 3 · **Last updated:** 2026-10-17 · **Related docs:** [Product overview](../product/overview.md) · [Network and remote access (public)](../../docs/architecture/network-and-remote-access.md) · [Remote access guide (public)](../../docs/guides/remote-access.md) · [Native clients (public)](../../docs/product/native-clients.md)

## 1. Goal

Tuvima is open from the moment it is installed. Any device that can reach the server can sign in (Tuvima sign-in is always required), and phone and TV apps can pair straight away, with no remote-access setup first. The leftover **Who can connect** setting and its Anywhere checklist go away, because they no longer protect anything and they block apps.

## 2. Scope

**In scope**
- Remove the Who can connect setting (This computer / Home network / Anywhere) everywhere: settings, screens, Engine actions, wire types, docs.
- Remove the rule that apps may connect only when Who can connect is Anywhere, and the app on/off setting itself. Apps can always pair.
- Remove the "Before Anywhere can be turned on" checklist. Setting up a secure outside address (Tailscale or an HTTPS reverse proxy) stays available as an optional extra with its **Test remote access** check, but it no longer unlocks anything.
- The Android app knows both the home and the outside address and uses whichever works (D7, Q3).
- Remove leftover code that still reads the mode but has no effect.

**Out of scope**
- Adding HTTPS to the Dashboard itself, or any certificate handling.
- Browser-based TV apps (`allow_browser_apps`, config-only today): unchanged.
- The no-password account (D5): unchanged.
- Two-address support in the Apple, Roku and TV web apps: their own later work.
- Router port mapping, streaming limits and upload protection: unchanged (they don't depend on the mode).
- The Android connect-screen wording and contrast bugs (QA notes A1, A2): separate fixes.

## 3. Decisions made

- **D1.** (2026-10-11) Network location checks were removed: every request is allowed whatever Who can connect says (save point a8d4c3ed7). Today the setting only blocks apps and shows status text.
- **D2.** (2026-10-17, Shy) Remove the restriction entirely; Tuvima is open from the start. Reason: an accidentally forwarded port is unlikely, and the setting blocks the common case (phones and TVs at home) for no real protection.
- **D3.** Sign-in is always required for everyone (except the no-password account, see D5). No guest access, no localhost administration (CLAUDE.md Never #7 still holds).
- **D4.** Pre-release: no compatibility handling. The `who_can_connect` key is removed from shipped config, and an old value left in a user's `network.json` is simply ignored.
- **D5 (Q1).** (2026-10-17, Shy) The no-password "use Tuvima on this computer" account stays as it is today: any device that can reach the server can use it. Closed router ports are the gatekeeper. Consequence, accepted: a guest on the home network can open Tuvima as that account, including its administrator rights. Adding a password or passkey closes this for that account.
- **D6 (Q2).** (2026-10-17, Shy) Apps are always allowed: no "Allow apps to connect" switch and no `native_app_access.enabled` setting. Reason: no restrictions by default; closed ports already keep outsiders out.
- **D7 (Q2).** (2026-10-17, Shy) Apps work at home and away, like Immich: the app knows the server's home-network address and, when the owner has set one up, its outside address, and uses whichever works. Shy is open to a better approach; see D8.
- **D8 (Q3).** (2026-10-17, Shy) The server tells the app both addresses: at pairing, the discovery answer carries the home-network address it was reached on and the outside address set in Remote Access (if any). The app saves both, tries home first with a short timeout, falls back to outside, and rechecks when the network changes. No second address to type, no Wi-Fi-name or location permission. Built for Android here; Apple, Roku and TV web follow separately.
- **Locked** by Shy 2026-10-17; build started the same day.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| First run (setup) | No question about who can connect. Setup copy no longer says "keep this on your private network until..." |
| Settings → Network (overview) | No Who can connect card or warning. Status shows the address(es) people can use. |
| Settings → Network → Remote Access | Titled for the optional secure outside address: **How the internet reaches Tuvima** (Not set up / Tailscale / HTTPS reverse proxy) and **Test remote access**. No Who can connect radio group, no Anywhere checklist. |
| Settings → Administration → Devices | No Allow apps switch. An **Address to type in the app** panel shows this server's home-network address with Copy and Test, plus the outside address when one is set ("Apps use this away from home"). Paired devices and Signed-in sessions are unchanged. |
| Phone or TV app, fresh install | Typing the server's address pairs normally (code approved in the Dashboard). No 404. |
| Android app, away from home | With an outside address set: the app switches to it on its own and keeps working. Without one: "Can't reach your Tuvima server" with Retry (as today). |
| Android app, back home | Switches back to the home address on its own (faster, no internet round trip). |
| Error: Engine unreachable | Unchanged. |

## 5. Data / model changes

- Configuration: `who_can_connect` and `native_app_access.enabled` are removed from `network.json` and its schema (`allow_browser_apps` stays).
- Wire types: the Who can connect field, the app-access flag and the Anywhere-readiness shapes leave the Engine↔Dashboard contract. Contracts tests and fixtures are regenerated.
- App discovery answer: gains the outside address when one is set (per Q3).
- Android app: stores a second (outside) address next to the one typed at pairing. Existing paired installs re-pair (pre-release).
- Data store: no change. No reset needed.

## 6. Work packets

### WP1 — Engine and settings: drop the mode and the app gate
- **What it does:** Removes `WhoCanConnect`/`AllowsInternet`, `WhoCanConnectModes` and `NativeAppAccess.Enabled` from network settings, `config/network.json` and its schema, the network contract and mapper (`AllowBrowserApps` stays). The settings loader, network save, network reset and first-run setup lose every app-access and Anywhere rule. The save action loses the Anywhere readiness refusal (409). The Dashboard's app door (`INativeAppAccessGate`, `ClientApiEdgeEndpoints`, cast relay, CORS) is always open; browser-app CORS still follows `allow_browser_apps`. The readiness service keeps only what **Test remote access** needs.
- **Files likely touched:** `src/MediaEngine.Domain/Configuration/NetworkSettings.cs`, `config/network.json`, `config/schemas/network.schema.json`, `src/MediaEngine.Contracts/Settings/NetworkSettingsContracts.cs`, `src/MediaEngine.Api/Endpoints/NetworkContractMapper.cs`, `NetworkEndpoints.cs`, `SetupEndpoints.cs`, `src/MediaEngine.Storage/ConfigurationDirectoryLoader.cs`, `LegacyAccessSettingsCheck.cs`, `src/MediaEngine.Api/Services/Networking/RemoteAccessReadinessService.cs`, Contracts fixtures, `tests/MediaEngine.Storage.Tests/NetworkConfigurationTests.cs`, `tests/MediaEngine.Api.Tests/RemoteAccessReadinessServiceTests.cs`, `DockerfileGuardrailTests.cs`.
- **Depends on:** none
- **Size:** Complex (wire-contract change; access change, so independent review)
- **Done when:** `GET /.well-known/tuvima` on the Dashboard answers 200 on a fresh install with no settings changed; Contracts tests pass with regenerated fixtures; no code reads `who_can_connect` or `native_app_access.enabled`.

### WP2 — Android: home and outside address
- **What it does:** Implements D8. The discovery answer adds the outside address set in Remote Access, when there is one (new optional field, Contracts + `clients/contracts/v1`). The Android app saves both at pairing, tries the home address first with a short timeout, falls back to the outside address, and rechecks when the phone's network changes. Streaming, heartbeats and token refresh use whichever address is current.
- **Files likely touched:** `src/MediaEngine.Web/Endpoints/ClientApiEdgeEndpoints.cs`, `TuvimaDiscoveryResponse` in `src/MediaEngine.Contracts/`, `clients/contracts/v1/`, `clients/android/core/src/main/java/com/tuvima/library/core/TuvimaClient.kt`, `ClientModels.kt`, Android tests, `clients/android/README.md`.
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** Android unit tests cover home-first, fallback and switch-back; on the emulator, pairing at `http://10.0.2.2:5016` works, and with a fake outside address set, discovery returns it and the app stores it.

### WP3 — Dashboard screens
- **What it does:** Removes the Who can connect section, the Anywhere checklist and "step down from Anywhere" from Remote Access; the Who can connect card and warning from the Network overview; the Allow apps switch and its alerts from Devices. **Address to type in the app** shows the home-network address, plus the outside address when set. Removes the dead `ExposurePolicy` middleware and the no-op mode reads in session revalidation and open-screen tracking. Updates setup copy.
- **Files likely touched:** `src/MediaEngine.Web/Components/Settings/RemoteAccessSettingsPanel.razor`, `NetworkOverviewPanel.razor`, `AppsAndDevicesPanel.razor`, `AdvancedNetworkSettingsPanel.razor`, `src/MediaEngine.Web/Models/ViewDTOs/RemoteAccessChecklist.cs`, `src/MediaEngine.Web/Services/Integration/ExposurePolicy.cs`, `EngineApiClient.Network.cs`, `SessionRevalidatingAuthenticationStateProvider.cs`, `OpenScreenRegistry.cs`, `src/MediaEngine.Web/Program.cs`, `Components/Setup/*Stage.razor`, `tests/MediaEngine.Web.Tests/NetworkSettingsUiTests.cs`, `NativeAppCorsTests.cs`, `CastReceiverRelayTests.cs`.
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** the three Settings pages show no Who can connect, Anywhere or Allow apps wording at desktop and 390px width; the Android emulator pairs at `http://10.0.2.2:5016` on a fresh install.

### WP4 — Docs
- **What it does:** Rewrites the network and remote-access docs to the open-from-the-start model, and removes the descriptions of refusals, the 426 HTTPS requirement and "proxy port counts as remote" that the code no longer does. States D5 plainly in the account-security guide (the no-password account is usable from any device that reaches the server; add a password to close that).
- **Files likely touched:** `docs/architecture/network-and-remote-access.md`, `docs/guides/remote-access.md`, `docs/reference/configuration.md`, `docs/product/presentation-rules.md` (Network, Devices), `docs/product/native-clients.md`, `docs/architecture/security.md`, `docs/guides/account-security.md`, `docs/reference/glossary.md`, `docs/reference/api-endpoints.md`.
- **Depends on:** WP1, WP2, WP3
- **Size:** Mechanical
- Also adds an ADR in `engineering/architecture/decisions/` recording D2 (open from the start), what it trades away and when to revisit it.
- **Done when:** a search of `docs/` for "Who can connect" and "Anywhere" finds no current-behaviour mentions; the docs site builds.

## 7. Risks and open questions

**Risks**
- **Sign-in over plain http from outside.** If someone does forward the Dashboard port, passwords and pairing codes cross the internet unencrypted (the Dashboard serves http only). Accepted under D2; the remote access guide should say to use Tailscale or an HTTPS proxy for outside access.
- **Pairing throttle.** It keys on connection address. Without the mode it's the main brake on code guessing; worth a reviewer check.

**Open questions**

- None.

**Answered**

- **Q3. How the app learns and picks the two addresses.** Answered: (a), the server tells the app both. See D8.
- **Q1. The no-password account.** Answered: (c), see D5. It was designed (October 9) to work *only on this computer*, with anything that widened access refused until it had a password. Since the October 11 change, any device that reaches the server counts as local, so that account, and first-run no-password setup, can be used from any phone or laptop on the network. Once the server is open from the start, that means anyone who can reach it.
  - (a) **Only the real computer.** No-password sign-in and no-password setup work only from the computer running Tuvima (loopback, with no forwarding headers). Everyone else must use a password or passkey. *Recommended:* it keeps the easy desktop start and closes the gap.
  - (b) **Remove no-password accounts.** Setup always creates a password or passkey. Simplest and safest; loses the one-step desktop start.
  - (c) **Leave as is.** Anyone who reaches the server can use the no-password account.
- **Q2. Keep an off switch for apps?** Answered: no switch; apps always allowed, with home and outside addresses. See D6, D7.
