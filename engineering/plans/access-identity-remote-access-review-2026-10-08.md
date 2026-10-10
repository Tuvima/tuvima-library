# Tuvima Library access, identity, and remote access review plan

Status: **Proposal for product and independent technical review; not implementation approval or a completed security audit.** Prepared October 8, 2026 against checkout `8e9d91cf`. No application, identity database, network configuration, or running service was changed. Research combines source inspection with official external documentation checked on this date. Existing test files and historical acceptance records were inspected; tests and live provider journeys were not rerun for this planning exercise.

Recommendation: offer a genuinely simple **Use on this computer** start with no email or password, protected by proof of access to the host and a restricted session. Require explicit account security before extending access to other devices. Keep email/password as the familiar default for secured accounts, add a real username alternative, retain passkeys, and make standards-based external identity optional. Preserve the existing account/profile/permission architecture. Establish identity and connectivity contracts for future Tuvima ID and Tuvima Social without building either service now.

## 1. Plain-English product walkthrough

The proposed experience has eight changes. Their numbers map to technical work packages below. These are proposed outcomes, not descriptions of features already delivered.

1. **Start using Library without creating an online account.** On a desktop installation, the app offers **Use on this computer** and **Set up sign-in**. The first choice creates a private local owner experience without asking for an email address or password. The installer/launcher proves that the person is using the host; merely finding an open web page does not make someone the owner. They can add folders and use their library. A persistent settings status says **This computer only — remote access off**. A NAS/container owner proves host access with a short-lived setup code or protected setup link obtained through the host console. **Acceptance:** a fresh desktop install reaches the library without email/password; a different computer cannot claim or browse that installation; no cloud account is contacted. Maps to **WP1–WP3**.

2. **Understand what local profiles are for.** Replace the current raw “Profile ID” login with **Choose a local profile**, available only on an approved local entry path. Explain: “For people using this computer or an explicitly approved household device. These profiles do not provide remote sign-in.” Show friendly profile names only after the device/session has permission to see them. A PIN can prevent casual household switching, but is not presented as internet account protection. The first-run owner capability is separate from ordinary local-profile entry. **Acceptance:** nobody needs to copy a GUID; local profile entry never grants administration by itself; public visitors cannot enumerate household members. Maps to **WP2–WP4**.

3. **Secure access when adding another device.** **Add another device** takes the owner through account security first. Email/password is the initial form; **Use a username instead** avoids requiring an email address. Passkey-first enrollment is offered when the address and recovery flow are ready. Save recovery codes before finishing; optional email is clearly marked as a recovery/contact feature. Existing history, favorites, personal photos, and libraries stay attached to the same account/profile when it is secured. **Acceptance:** the upgrade preserves personal state, invalidates the old provisional session, and does not invent an email address. A forgotten password remains recoverable without SMTP. Maps to **WP2–WP5**.

4. **See exactly who can access what.** Keep separate account identity, personal profile, library permissions, and app/integration permissions. Users & Access shows effective access in readable terms: allowed lanes, libraries, profiles, administrator access, and personal-space sharing. A household member cannot open an unauthorized item by guessing its address. Sharing a profile explicitly warns that its history and Personal Space are shared. **Acceptance:** removing a grant takes effect on open screens, streams, downloads, and connected apps within a documented short bound; private photos do not leak into search, artwork, counts, or events. Maps to **WP4, WP8**.

5. **Choose remote access with clear tradeoffs.** Offer **Private devices** as the recommended initial path, using the existing Tailscale integration. Keep **Existing HTTPS proxy** for experienced users. Develop **Direct HTTPS** as an advanced option for those who do not want a separate proxy. Its wizard explains domain, certificate, router/firewall, and internet-provider requirements. Reaching the server and signing into it are separate checks. **Acceptance:** no method enables remote use until account and connection checks pass; disabling remote access blocks existing remote sessions too; the Engine is never published. Maps to **WP1, WP5, WP6, WP8**.

6. **Use an existing identity provider without surrendering library control.** Account Security lets users link an approved provider after authenticating to both accounts. Offer tested setup guides for a small provider set, including a self-hosted privacy-oriented option. Do not display a provider as supported merely because an OAuth form can be filled in. External sign-in proves who someone is; the Library owner still decides what they can access. **Acceptance:** matching email addresses alone cannot merge accounts; provider outages leave a working local recovery path; linking does not change permissions. Maps to **WP3–WP5, WP8**.

7. **Keep future Tuvima products optional and compatible.** Later, Tuvima ID can appear as another sign-in provider; Tuvima Social can request narrowly defined, explicit sharing permission. Neither is required for Library startup or local playback. A future connection service may help discover a server or relay encrypted connections, but it is a separate choice from identity. **Acceptance:** an integration prototype uses standard interfaces without a mandatory Tuvima cloud account, shared cross-product browser cookie, or automatic upload of library activity. Maps to **WP7, WP8**.

8. **Recover safely and understand connection problems.** Show separate status for account readiness, encrypted transport, external reachability, and provider availability. Losing a certificate or provider does not erase the library or reopen first-run setup. A host operator can recover access using the protected recovery console. **Acceptance:** an expired certificate, blocked port, unavailable provider, or restored backup produces specific guidance; none silently downgrades security. Maps to **WP2, WP3, WP5, WP6, WP8**.

Representative journeys: a single-computer reader chooses local use and imports books; a household owner later secures that same identity and pairs a tablet; a NAS owner claims setup through its console; a remote family member signs in with a linked provider and sees only granted libraries; a privacy-focused operator uses a self-hosted OIDC provider; an owner who loses that provider uses a separate recovery method.

What stays the same: local-first storage, media organization and playback, lane experiences, personal history, the distinction between accounts and profiles, Engine-enforced permissions, and original-media protection. This work must not casually replace the accepted Access architecture.

Scope boundaries: this plan reviews authentication, authorization, first-run ownership, session lifecycle, public/private connectivity, recovery, and future integration seams. It does not build Tuvima ID, Social, a hosted relay, federation, billing, content moderation, or cloud media storage. Implementation requires a subsequent work request. No testing against real household data or public exposure is authorized by this plan alone.

## 2. What the current app actually does

### 2.1 The confusing local sign-in option

The login page literally renders **Sign in with a local profile**, with a **Profile ID** field and optional PIN. It submits a profile ID instead of email/password. The Engine requires `AllowLocalOnlyAccounts` and a Dashboard-supplied trusted-local classification before allowing this entry path. A local-only account with no configured profile PIN can receive a `ProfileEntry` session. With an empty trusted-network list, the Dashboard's sign-in classifier accepts loopback only. This is household convenience, not an alternative internet login and not an administrator bypass. Evidence: **R1–R3**.

It does **not** currently remove the initial administrator requirement. `BootstrapAdministratorAsync` validates a password and email and creates the first administrator account. The setup screen begins a separate setup session before that step. Ordinary local-only accounts can then be created by an administrator. Evidence: **R2, R4**.

There are multiple meanings of “local” today: authentication uses loopback plus explicitly trusted CIDRs; the HTTP transport guard uses a broader private-address classifier; development can separately enable a localhost bypass. These meanings should become explicit policies rather than similarly named settings. Evidence: **R1, R5, R6**.

### 2.2 Foundations to preserve

| Area | Verified source foundation | Evidence limit |
| --- | --- | --- |
| Human access | Accounts, profile grants, lane/library access, administrator eligibility, optional grant-specific admin PIN | Inspect live enforcement for every surface, not just UI visibility. R6, R7 |
| Applications | Separate transport, service-application, delegated-user, setup, and host-recovery principal kinds; registered permissions and revocable credentials | Existing architecture should be extended, not replaced by generic roles alone. R6, R7 |
| Sessions | Hashed token lookup, enabled-account/profile checks, password/PIN stamps, Dashboard cookie revalidation | Local-origin restrictions on **existing** sessions need a dedicated end-to-end proof. R2, R8 |
| Password and recovery | ASP.NET password hashers, lockout, recovery codes, invitations, email recovery, elevated host recovery | Current minimum password length is eight; reassess policy and deployment cost. R2, R9 |
| Passkeys | .NET Identity `IUserPasskeyStore<Account>` implementation and registration/sign-in routes | Presence is not browser/device/canonical-origin certification. R10 |
| External sign-in | OIDC code flow; PKCE enabled by default; HTTPS discovery; OAuth user-info adapter; tokens not saved in Dashboard auth properties | Generic adapters do not prove every documented provider works. R11 |
| Identity linking | Provider + issuer + stable subject; short-lived single-use transaction; linking bound to account/session; no email auto-link | Check fresh authentication, exact issuer treatment, concurrency, and provider changes. R11, R12 |
| Remote setup | Tailscale/custom proxy/direct-only modes; readiness checks; trusted proxies; router mapping targets a TLS terminator | `direct-only` currently still means a separate TLS reverse proxy. R5, R13 |
| Historical verification | Access acceptance record reports completed integration and a passing isolated mutation matrix | September evidence is not a fresh security audit of this checkout. R14 |

### 2.3 Prioritized gaps and review hypotheses

**Confirmed** means visible in inspected source. **Hypothesis** means a credible issue needing a reproducer before declaring a vulnerability. P0 blocks shipping a credential-free first run or a newly supported public exposure mode; P1 blocks claiming a particular affected feature is production-ready; P2 is follow-on improvement.

| ID / priority | Finding and confidence | Required response |
| --- | --- | --- |
| G01 / P0 | **Confirmed gap:** no credential-free first owner flow; bootstrap requires email/password. R2, R4 | Add a host-claimed, constrained local owner lifecycle, not an anonymous administrator. |
| G02 / P0 | **Hypothesis:** setup authority proves Dashboard transport and possession of a setup session, but the inspected setup page/session issuance path does not itself prove host possession. R4 | Test an unclaimed deployment reached via LAN, forwarded port, and trusted HTTPS proxy; require host proof before setup can inspect folders or claim ownership. |
| G03 / P0 | **Confirmed assurance gap:** remote readiness checks administrator bootstrap completion and bypass-off, plus transport. It does not fully establish current enabled admin/authenticator/recovery readiness. R13 | Compute readiness from effective live authority and usable sign-in methods; bootstrap history alone is insufficient. |
| G04 / P0 | **Hypothesis:** changing `network.remote.enabled` appears to govern setup/status/router actions, while login uses separate auth flags. The inspected Dashboard guard rejects remote HTTP, not all remote requests when remote access is off. R3, R5, R13 | Prove a global exposure policy for manual forwarding, direct HTTPS and existing connections. Do not claim the switch is a firewall until tests establish its effect. |
| G05 / P0 | **Hypothesis:** local-only admission is checked at login; inspected session validation lacks ingress context. R2, R8 | Replay a valid local-only cookie/session over a remote path; bind session eligibility to ingress on every use, including circuits and streams. |
| G06 / P0 | **Confirmed inconsistency:** local HTTP classification treats missing address as local and accepts private IPv4 broadly; login classification fails closed and uses explicit CIDRs. R1, R5 | Separate transport provenance, configured exposure, and account eligibility. Null/unknown is never authority. Test proxy/container address rewriting. |
| G07 / P1 | **Confirmed UX gap:** local profile entry exposes GUID input and unconditional login controls; modes include `Local`, `Optional`, `Required`, `DisabledLocalOnly`. R1, R3 | Publish one capability/readiness projection and use plain-English mode labels; hide impossible methods with useful explanations. |
| G08 / P1 | **Confirmed coupling:** passkey/external readiness uses `Auth.PasswordReset.PublicBaseUrl`. R3 | Introduce a canonical Dashboard origin independent of email settings; migrate with explicit precedence and origin validation. |
| G09 / P1 | **Confirmed mismatch:** Dashboard comments preserve exact issuer; Engine provider admission strips trailing slashes. R3, R11 | Specify exact issuer semantics end-to-end; do not rewrite existing linked identities silently. |
| G10 / P1 | **High-confidence compatibility hypothesis:** docs suggest Microsoft `common`, but Engine admission compares issuer against configured authority/issuer; tenant-specific token issuers differ. R3, R15 | Certify single-tenant first; support multi-tenant only with validated tenant/issuer rules and negative tests. Never disable issuer validation to make it work. |
| G11 / P1 | **Confirmed integration risk:** OIDC disables inbound mapping yet reads email/name using `ClaimTypes` names. Exact framework/UserInfo mappings determine results. R11 | Assert standard `email`, `name`, `sub`, `iss`, missing email, and claim conflicts with actual provider responses. Do not assert all users are broken. |
| G12 / P1 | **Confirmed operational gap:** providers register at Dashboard startup; saving settings alone does not prove middleware is active. R11 and Web Program | Show restart-required versus tested-ready state; certify rotation, disablement, and recovery. |
| G13 / P1 | **Review gap:** fresh reauthentication/MFA, policy-bound revocation, throttling through a shared Dashboard service, key-ring protection, reset races and provider-removal races need integrated evidence. | Use WP3/WP8. Existing tests are a starting point, not proof of all attack paths. |
| G14 / P1 | **Confirmed scope gap:** no supported direct-Kestrel HTTPS lifecycle in the current remote workflow. R13 | Add a bounded direct-HTTPS mode only after certificate, deployment and exposure gates pass. |
| G15 / P2 | **Design gap:** future ID, discovery, relay and social consent boundaries are not yet an accepted product contract. | Freeze minimal standards-based seams; defer hosted implementation. |

## 3. First-run and exposure design

### 3.1 Distinguish passwordless from unauthenticated

No typed email/password must not mean that any HTTP caller becomes the owner. A native launcher can deliver a short-lived one-use capability to the intended local browser; the server exchanges it for an opaque, HttpOnly local session. Store capability material with OS-user/service ACLs, keep it out of logs, referrers and normal URLs, use same-origin POST/antiforgery, and expire/consume it atomically. Design the browser handoff before implementation; a fragment-to-POST flow still needs script/CSP and leak review. A generic public `/setup/begin` response is not proof of host possession.

For a headless install, bind a setup listener to a host-controlled interface or explicit loopback container port and require a console-issued one-use claim code. An SSH tunnel plus that proof is acceptable. Do not assume a container's loopback is the host's loopback or that the first LAN visitor is the owner. Do not place persistent claim secrets in ordinary container logs. Device and installer packaging must support this workflow before advertising it.

Create durable account/profile IDs for local use without email, password, or fabricated identity. Model a **host-claimed owner capability** explicitly, scoped to the host-proven session. It can configure local libraries and use the product, but cannot enable remote ingress, issue remote invitations, publish integrations/webhooks, or delegate administrator access until secured. This is deliberately stronger and narrower than a PIN-free local household account. Do not repurpose `localhost_bypass` or infer administrator identity from loopback.

### 3.2 Proposed states and transitions

| State | Who can enter | Allowed experience | Transition condition |
| --- | --- | --- | --- |
| Unclaimed | Host-proof setup capability only | Minimal setup and local storage readiness | Atomic claim; no race between browsers/processes |
| Local host use | Host-bound owner session and explicitly eligible local profiles | Full local media experience; restricted administrative setup | Secure the owner and record recovery readiness before access from another device |
| Secured local / household | Authenticated accounts; paired household profile entry where explicitly permitted | Account/profile/library permissions; remote ingress off | Explicit remote enablement after account, transport and origin readiness |
| Private remote | Authenticated users over approved private connectivity | Same permission model, private device reachability | Explicit public-mode approval and independent public checks |
| Public remote | Authenticated users over approved HTTPS edge | Granted access only; no profile/PIN-only remote admission | Disablement/revocation immediately narrows exposure |
| Recovery required | Protected host recovery and any surviving eligible admin | Diagnose/recover without weakening public access | Restore usable credentials and re-run readiness |

Exposure mode and account security state are separate fields, with one evaluator deciding their permitted intersection. “Private remote” is still remote for authentication even if the overlay uses private IP addresses. Policy changes are atomic, versioned and audited. Crash or partial persistence during an upgrade must retain the narrower exposure. Losing the last admin, credentials, or a database key must never transition an installed server to Unclaimed.

### 3.3 What can actually prevent accidental internet sharing?

Use layered controls: loopback/default-private listener bindings, install/deployment firewall guidance, no automatic router mapping before readiness, strict allowed host/origin checks, a request-time exposure gate, and a session whose privileges depend on how it was established. Apply the gate to HTML, APIs, images, downloads, reader resources, HLS manifests/segments, uploads, WebSockets/SignalR, native-client exchanges and public-share entry points. Give health and certificate challenges only narrow non-sensitive exceptions.

The Dashboard remains the only public entry point. The Engine accepts only authenticated internal transport or explicitly supported client authority on a private network. A remote proxy's connection to loopback is not a local user's identity. Trust forwarding headers only from configured peers; distinguish ingress/listener provenance from a claimed client IP. Normalize IPv4-mapped addresses and test IPv6, Docker, NAT hairpin and multi-hop proxies. Never accept browser-supplied `OriginalClientIsLocal` as authority; the Dashboard derives and transmits context over its authenticated channel, and the Engine independently applies relevant session restrictions.

**Limit of the guarantee:** the app cannot prevent a machine administrator from installing an arbitrary tunnel, altering binaries, or forwarding a host-authenticated browser/session. IP addresses alone cannot reliably distinguish all NAT/proxy arrangements. The promise should be: supported deployments do not grant unauthenticated remote access, do not open mappings before security is ready, and reject disallowed sessions even when a port is manually exposed. Host compromise and deliberate authorized re-export remain outside that promise.

### 3.4 Readiness and continuous enforcement

Require an enabled owner/admin with an effective grant; a tested primary authenticator plus independent recovery; bypass disabled; approved canonical origin; HTTPS suitable for the selected transport; correct proxy trust or direct-listener provenance; protected persistent keys; and explicit remote consent. For public administration, require fresh phishing-resistant passkey verification or an approved MFA method; do not treat a profile/admin convenience PIN as that method. Password-only ordinary access may remain supported under the password policy; an email address is not a security factor.

Separate **configured**, **security-ready**, **transport-verified**, **externally reachable**, and **enabled**. A request from the host back to its public URL does not prove off-network reachability. The current nonce echo identifies a Tuvima-like response, not cryptographic possession of this specific installation: bind any stronger readiness challenge to installation identity, expire it, and prevent arbitrary URL probes from becoming SSRF. Approved local/private targets need explicit scope; block redirects, metadata endpoints and rebinding outside it.

Re-evaluate policy at request/session use and on configuration changes. Closing remote access revokes/disconnects remote circuits and streams without destroying local state. Credential compromise, invalid origin or insecure transport fails closed; a temporary reachability-monitor failure reports uncertainty rather than needlessly logging everyone out. Establish measurable bounds in WP1: proposed maximum five seconds for revocation/disconnect notification, with every new protected request denied immediately after the authoritative change. Already delivered/buffered bytes cannot be recalled.

## 4. Accounts, authenticators, permissions, and privacy

### 4.1 Account and sign-in contract

- Use immutable account IDs; add normalized, unique usernames as an alternative login identifier. Email is nullable contact/recovery data, verified before email-based recovery. Define case/Unicode/confusable handling and rename rules. Do not use email as the primary foreign key or invent placeholder email addresses.
- Keep account, experience profile, profile grant, external identity, session, device, application and service credential distinct. A new login method cannot change ownership or grant rights.
- Retain native .NET password hashing with versioned upgrade/rehash behavior; benchmark work factors on supported small servers. Proposed policy: long passphrases, at least 15 characters for password-only accounts, support at least 64 characters, breached-password screening that can run without sending passwords to a third party, no arbitrary composition or routine expiry. Review accessibility and lockout abuse before finalizing.
- Passkeys are a preferred optional primary authenticator. Require user verification for sensitive step-up, one-use expiring challenges, explicit RP ID and exact allowed origins, credential ownership checks, replay/race tests and sane enrollment limits. Do not assume a synced passkey automatically satisfies every MFA policy. Keep recovery codes/another independent method. Domain changes may require re-enrollment; localhost passkeys must not be assumed to work at a later public domain.
- Require fresh reauthentication for remote enablement, adding/removing authenticators, changing recovery destinations, sensitive administration, and generating powerful credentials. Specify a short freshness window and support an alternative method if the current provider is unavailable. Add TOTP only if needed for the accepted public-admin fallback; it is not implied to exist today.
- Keep single-use hashed invitations and reset/recovery tokens, anti-enumeration responses, throttling and revocation. Make token consumption plus credential change transactional. Hash recovery codes, show them once, record acknowledgement without retaining plaintext, and test simultaneous redemption. Recovery assurance must not be weaker than the account being recovered.
- Bound both idle and absolute session lifetime; expose device/session revocation. Distinguish a harmless Engine outage from a proven invalid cookie. Review Data Protection key persistence, OS ACL/encryption, container backup/restore, secure cookies, SameSite during external callbacks, CSRF, clickjacking and sensitive response caching.

### 4.2 Permission review matrix

Effective human access remains the intersection of enabled account, active valid profile grant, account features/libraries, profile restrictions, resource sharing rules and operation policy. Delegated clients additionally intersect registered application permissions, consent, device and live token. Service credentials never inherit the owner’s personal data rights by accident.

| Actor | Expected rights | Required negative cases |
| --- | --- | --- |
| Anonymous/public visitor | Minimal login/health/claim entry; explicitly approved public share only | No setup ownership, profile enumeration, artwork leaks, search counts or Engine data |
| Host-claimed provisional owner | Local use and bounded local setup | No remote session, remote invitation or unrestricted application credential |
| Local-only household account | Granted profiles/lanes on approved local/pairing path | No internet replay, implicit admin or unrestricted private-space access |
| Secured member | Granted libraries and personal state | Cross-account/profile IDs, variant leakage, restricted metadata/stream bypass |
| Administrator | Explicit system operations with effective admin grant and required step-up | Admin navigation alone cannot authorize actions; View inspection stays explicit and audited |
| Child/restricted profile | Allowed content and features | Ratings/search/recommendations/history/artwork bypass; switching to adult profile without authorization |
| Native/delegated client | Consent-scoped account/profile rights | Token audience/client/device mismatch; rights surviving account/app revocation |
| Service integration/plugin | Only registered available permissions | Transport identity treated as a human; undeclared plugin capabilities; private View export |
| Gallery/share recipient | Exact shared members and authorized derivatives | Sibling photos, filesystem paths, metadata/EXIF/location leakage, replay after revoke |

Inventory every mapped endpoint and hub, then trace resource reads/writes. Include stream/download equivalence, offline copies, queue control, private playlists/collections, uploader contributions, deletion, exports, credentials, folder browsing, plugins, provider setup, logs and diagnostics. Decide explicitly whether viewing implies downloading; hiding Download cannot prevent a legitimate viewer from copying content already delivered. Verify filtering before counts/grouping/paging/representative artwork. Cache keys must include relevant authority scope and revisions; shared HTTP caches must not retain private responses.

Review revocation on already-open Blazor circuits, HLS sessions, background jobs and event subscriptions. Test stale asynchronous results after profile switches. Existing shared-profile semantics intentionally share personal state; explain them rather than silently changing them. Host operators with disk/database access are trusted operators, not prevented from reading files by UI permissions.

### 4.3 Privacy requirements

Default to no cloud identity, telemetry, social activity export, public profile directory, or contact discovery. Inventory every off-host request, including optional metadata providers, SMTP, breach checks, DNS, certificate issuance, SSO and connectivity services. Identity privacy does not erase existing metadata-provider disclosure; report both separately.

Keep audit events bounded and access-controlled; redact passwords, PINs, session/refresh tokens, invitation links, provider secrets, and media paths where unnecessary. Set documented retention/deletion rules for IP/device history and provider claims. Avoid media titles in identity/connection logs. Explain backups and that identity deletion must not delete original media, transfer a Personal Space, or promise immediate deletion from offline backups. Protect GPS/face data in View exports and shared derivatives.

## 5. Internet access options, including no proxy

Three different questions need separate answers: **Can the device reach the server? Is the connection encrypted? Who is allowed to use it?** An SSO provider answers only the third identity step; a tunnel does not grant library permissions.

| Option | Proxy / router requirement | Privacy and operational tradeoff | Recommendation |
| --- | --- | --- | --- |
| Direct Kestrel HTTPS | No separate reverse proxy. Usually inbound firewall/router access, public DNS and reachable IPv4 or IPv6 | TLS terminates at the user's server; operator owns certificate renewal, exposed-server hardening and availability. CGNAT/blocked inbound may prevent it | Build as advanced supported mode after WP6 gates |
| Tailscale private access | Client software on participating devices; Serve is a local HTTPS proxy; typically no manual inbound forwarding | WireGuard protects traffic, including relay paths; hosted control plane retains operational metadata | Keep first recommendation for private remote access; test TVs/native clients |
| Headscale / NetBird | Self-hosted coordination and client software; topology/relay requirements vary | More control of coordination metadata, more maintenance; do not assume feature parity with the existing Tailscale adapter | Document/evaluate optional paths; no automatic certified label |
| Caddy on the user's host | Local reverse proxy plus reachable ingress | No necessary third-party media relay; automatic HTTPS reduces certificate burden | Maintain supported option for users comfortable with a proxy |
| Cloudflare DNS only | Direct HTTPS or a local proxy still required | DNS service is not an HTTP media intermediary; does not solve CGNAT or origin protection | Useful optional DNS/ACME integration |
| Cloudflare Tunnel + Access | Outbound connector; Cloudflare is the intermediary | Simplifies ingress; TLS terminates at the edge and Access adds a separate login boundary. Streaming terms and client compatibility matter | Optional evaluated deployment, not default media relay |
| Future Tuvima connectivity service | Optional discovery, rendezvous and possibly relaying | Easy joining but real cloud metadata, bandwidth, availability and abuse responsibilities | Define interfaces now; build as a separate future offering |

Microsoft supports Kestrel as an internet-facing server without a reverse proxy. That establishes feasibility, not readiness of this deployment. [Kestrel hosting guidance](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/when-to-use-a-reverse-proxy?view=aspnetcore-10.0). Tailscale documents encrypted traffic with operational metadata visible to its service. [Tailscale security](https://tailscale.com/security). Headscale implements a self-hosted Tailscale control server with a deliberately bounded scope; NetBird also offers self-hosted coordination and optional OIDC. [Headscale](https://headscale.net/stable/), [NetBird identity documentation](https://docs.netbird.io/selfhosted/identity-providers).

### 5.1 Direct HTTPS design spike

Expose only the Dashboard's TLS listener. Keep internal HTTP and the Engine on protected bindings. Offer **Bring a certificate** first; then evaluate automated ACME via an OS-managed client or a maintained .NET integration. Require hostname/SAN validation, protected private keys, atomic certificate reload, renewal health/expiry warnings, staging tests and recovery. Do not use development certificates or certificate-validation bypasses. Set explicit hosts, TLS policy, request/header/body limits, timeouts, upload/stream/concurrency budgets, and WebSocket origin handling.

DNS-01 can obtain a certificate without an inbound HTTP challenge; it does not make a CGNAT-hosted server reachable. Use narrowly scoped DNS tokens or delegated challenge records. HTTP-01 requires its challenge path; do not open other HTTP functionality to satisfy it. Dynamic DNS and IPv6 need explicit diagnostics and firewall rules. Certificate transparency can expose hostnames; do not encode a person's identity in generated names. [Let's Encrypt challenge types](https://letsencrypt.org/docs/challenge-types/).

Direct public hosting exposes the household IP and cannot absorb volumetric attacks merely through application rate limits. Explain this tradeoff in advanced setup. Do not promise to defeat CGNAT with UPnP, PCP or a certificate. Preserve router automation as explicit opt-in, remove only Tuvima-owned mappings, and test restart/crash/stale leases. A supported deployment must pass long playback, seek/range, HLS, WebSockets, large View uploads and certificate-renewal tests.

### 5.2 Where Cloudflare helps and where it does not

DNS and DNS challenges can help without sending media through Cloudflare. Tunnel can avoid inbound router rules; Access can add an outer admission layer, but must not replace Tuvima permissions or trust arbitrary identity headers. Public HTTP proxying terminates client TLS at Cloudflare, so the edge is a trusted content-processing intermediary; it is not end-to-end private against that intermediary. Browser gates also need testing with native players and long streams. [Cloudflare TLS model](https://developers.cloudflare.com/ssl/concepts/).

Cloudflare's current Tunnel FAQ explicitly addresses large-file/video restrictions on Free, Pro and Business plans and directs users to appropriate paid services. Do not recommend it as an unrestricted free personal-media relay. Recheck the exact product/plan terms at implementation time. [Tunnel FAQ](https://developers.cloudflare.com/cloudflare-one/faq/cloudflare-tunnels-faq/). This is a product-fit constraint, not a reason to upload local libraries to a cloud video service.

### 5.3 What to learn from Plex

Plex's convenience is more than a hosted web page: remote clients can connect directly when reachable, with Relay as a fallback for supported apps. Its relay is an additional connection path, not proof that all personal media is hosted in Plex's cloud. [Plex Relay explanation](https://support.plex.tv/articles/216766168-accessing-a-server-through-relay/).

The applicable design lesson is to separate sign-in, server discovery, secure connection establishment and fallback relay. A hosted frontend alone will not host this Blazor Server UI: its interaction still runs on the user's Dashboard. Future Tuvima “connect” functionality must account for that and for WebSocket/session routing. Leave a connectivity-provider interface and installation identity seam now; defer relay economics, fleet operations and public service launch.

## 6. External sign-in support and useful products/libraries

### 6.1 Provider support policy

Use **OIDC Authorization Code + PKCE** as the preferred integration. OAuth alone is not an identity protocol; provider-specific adapters must fetch and validate a stable user identity. Keep exact redirects, state/nonce, issuer/audience/signature/lifetime validation, no implicit/password grants, and no open redirect. Pin trust to configured issuers, not arbitrary user-supplied discovery URLs. Review against [OAuth security BCP, RFC 9700](https://www.rfc-editor.org/rfc/rfc9700.html).

Use four support labels: **implemented adapter**, **configuration validated**, **integration tested**, **supported deployment**. Publish a versioned matrix with last test date, issuer/tenant mode, callback origin, linking/unlinking, recovery, and browser/native limitations. Every supported provider needs a real callback test with consent, cancellation, missing claims, wrong issuer/audience, key rotation, replay and account disablement. Mocked tests remain essential but do not replace this proof.

| Provider/product | Current Tuvima position | Proposed treatment |
| --- | --- | --- |
| Google | OIDC adapter and documented configuration | Certify sign-in/linking and optional email claims; user-chosen convenience, not privacy default |
| Microsoft Entra / Microsoft accounts | Documented `common` example; issuer concern G10 | Certify tenant-specific mode first. Treat organizational multi-tenant and personal-account support as separate tested configurations |
| GitHub | Generic OAuth/user-info configuration | Test numeric stable ID mapping, absent/private email, scopes and callback errors; request email scope only if needed |
| Facebook | Generic OAuth shape described in guide | Do not advertise as tested; defer unless demand justifies versioned Graph/PKCE/permissions maintenance |
| Pocket ID | No dedicated deployment certification found | First lightweight self-hosted privacy-oriented OIDC candidate, particularly for passkey-led households |
| authentik | Generic OIDC integration candidate | Good candidate for owner-managed household/business SSO; keep app permissions local |
| Keycloak | Generic OIDC integration candidate | Evaluate for advanced self-hosters and future hosted Tuvima ID operations |
| Authelia | Generic OIDC integration candidate | Useful for operators already using it; configure OIDC rather than accepting unverified proxy identity headers |
| Future Tuvima ID | Not implemented | Standard optional OIDC provider with explicit linkage and narrow claims |

Official evidence: [Pocket ID](https://pocket-id.org/docs/introduction) describes self-hosted passkey OIDC; [authentik](https://docs.goauthentik.io/add-secure-apps/providers/oauth2) documents OIDC/PKCE; [Keycloak](https://www.keycloak.org/securing-apps/overview) documents standards-based application integration; [Authelia](https://www.authelia.com/configuration/identity-providers/openid-connect/clients/) documents OIDC client configuration. These support candidate selection, not a claim they have passed Tuvima tests. Microsoft's tenant-independent issuer handling needs explicit rules. [Microsoft multi-tenant guidance](https://learn.microsoft.com/en-us/azure/active-directory/develop/active-directory-devhowto-multi-tenant-overview).

Self-hosting controls who operates the identity service; it does not automatically make it private. The operator can observe sign-ins and identifiers. Review defaults, retention, backups, telemetry, licensing, patch cadence, recovery and administrator access. Do not assume a privacy-branded email service offers a supported OIDC identity-provider API.

### 6.2 Library and platform choices

| Capability | Recommendation | Rationale / adoption gate |
| --- | --- | --- |
| Passwords, cookies, OIDC | Retain Microsoft ASP.NET Core Identity components and OIDC middleware | Already integrated; audit configuration and update cadence instead of replacing working protocol code |
| Passkeys | Retain .NET 10 Identity passkeys | Already has an account store. Framework supports primary passwordless authentication; it is not a general WebAuthn/second-factor implementation. [Microsoft passkey documentation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/passkeys/?view=aspnetcore-10.0) |
| Advanced WebAuthn | Evaluate `fido2-net-lib` only for a demonstrated framework gap | Avoid parallel passkey engines. [Project](https://github.com/passwordless-lib/fido2-net-lib) |
| Future Tuvima ID protocol server | Compare Keycloak with OpenIddict in a separate ADR | Keycloak supplies a full identity service; OpenIddict provides .NET protocol stacks, not a finished identity product. SQLite/Dapper integration, admin UX and operations still need design. [OpenIddict overview](https://documentation.openiddict.com/guides/getting-started/) |
| Certificates | Prefer established ACME operations; evaluate Certes for native integration | Certes is an ACME client, not a complete renewal/secret-storage service. Pin reviewed releases and validate maintenance/security posture before selection. [Certes](https://github.com/fszlin/certes) |
| Existing reverse proxy | Continue Caddy guidance | Automatic HTTPS reduces operator work. It remains a proxy, so it does not fulfill a literal no-proxy requirement. [Caddy automatic HTTPS](https://caddyserver.com/docs/automatic-https) |
| Authorization | Retain current typed permission/resource evaluators | No evidence that an external policy engine is needed; consistency and complete enforcement matter more than another service |

Do not commit to package versions from this planning document. At implementation, verify .NET 10 compatibility, license/rebranding requirements, security advisories, maintainer activity, release signatures/SBOM, supported deployment footprint and update responsibility. Open-source components reduce protocol implementation work; they do not transfer product security accountability.

## 7. Foundations for Tuvima ID, Social, and hosted connections

### 7.1 Separate product responsibilities

- **Library:** owns local accounts, profiles, grants, media, history and Personal Spaces. It decides access even when authentication was external.
- **Tuvima ID:** future optional OIDC provider owning its own credentials, sign-in consent and recovery. It need not know Library titles, paths, photos, listening history or social graph.
- **Tuvima Social:** future application with its own identity linkage, audience rules, moderation and export/deletion semantics. It receives only data a user deliberately shares.
- **Hosted connectivity:** future discovery/rendezvous/relay service with separate enrollment and consent. Signing in to ID must not silently publish a server address or activate relaying.

Persist a random installation ID and a separate rotatable installation key for future proof of possession. Keep local account/profile IDs distinct from external subjects. Standard OIDC keys identities by issuer and subject; retain an explicit provider-configuration binding for this installation without interpreting a display name/email as identity. Provider rename/issuer migration needs authenticated relinking or a reviewed migration, not string normalization.

Prefer pairwise subjects per product/client boundary for future ID, with explicit consent for any cross-product association. Pairwise IDs reduce correlation by relying parties; the ID operator still knows its own sign-in relationships. A shared sector identifier across every Tuvima product would weaken that separation. [OIDC Core privacy and subject identifiers](https://openid.net/specs/openid-connect-core-1_0-18.html).

Define future scope names and data categories in an ADR, but do not grant them today. Separate authentication scopes from Library authorization and Social publishing consent. Use audience-restricted tokens, short lifetime, revocation/rotation and explicit user-client consent. Do not make a global ID token a universal administrator credential. Keep native clients on appropriate browser/device authorization flows rather than collecting provider passwords.

### 7.2 Privacy and outage contracts

No mandatory cloud check for startup/local playback. Unlinking ID keeps local library state and requires another usable sign-in/recovery route. Provider logout, Library session logout and all-device revocation are different actions; document which are implemented. Define bounded behavior for disabled external identities rather than promising instantaneous federated revocation without a protocol to deliver it.

A future discovery service should hold the minimum necessary server/device reachability metadata, with opaque IDs, explicit retention and opt-out. A relay may protect content from the operator only if the design preserves encryption all the way to a user-controlled endpoint; ordinary TLS termination at a hosted relay does not satisfy that claim. Traffic timing and addresses remain metadata even with encrypted content. No “zero knowledge” claim without a precise threat model and evidence.

A future Social integration starts off. Sharing a reading status is different from sharing a book file, photo, location, or entire library; consent must express the exact action and audience. Reuse the event/outbox foundations, but authorize and minimize exports rather than mirroring internal events. Keep social pseudonyms separate from private household profiles. ActivityPub is a candidate for interoperability, not an identity provider or automatic privacy/E2EE solution; federated recipients may retain copies after deletion. [ActivityPub specification](https://www.w3.org/TR/activitypub/).

### 7.3 Future-service ADR deliverables

Specify OIDC reliance/linking, subject privacy, canonical origin, installation proof, discovery provider interface, relay transport boundary, consent/audience model, key rotation and account unlink/recovery. Include a fake provider/connection adapter and contract tests only when implementing these seams. Defer global account database, Social federation, contact import, billing and relay deployment until separately scoped. Do not introduce shared cross-product cookies or a mandatory global user ID now.

## 8. Technical work packages and sequence

| Package | Deliverables and main code seams | Dependencies / exit gate |
| --- | --- | --- |
| **WP1 — Audit and threat model** | Refresh the existing endpoint/permission ledger; inventory listeners, origins, middleware, config defaults, deployment templates, secrets and outbound data; reproduce G02–G06. Define revocation timing and support labels. Owners: security/backend plus deployment reviewer | First. Exit: every gap confirmed, disproved or explicitly bounded with evidence; threat model and acceptance matrix reviewed |
| **WP2 — Ownership and first-run state** | Setup contracts/session service/repository, first-party identity, account schema, installer/launcher/headless claim, Setup UI, lifecycle transitions and recovery; preserve stable identities during securing | WP1. Exit: host-claim proof, crash/concurrency/restore tests and credential-free desktop journey pass; no anonymous admin or reopened bootstrap |
| **WP3 — Authentication/session hardening** | Username/contact separation, capability-based login UI, canonical origin, password/passkey/recovery/step-up rules, cookie/circuit lifecycle, rate limits and durable key protection | WP1; coordinate account changes with WP2. Exit: local-to-remote replay denied; complete recovery and authenticator mutation matrix passes |
| **WP4 — Rights and resource review** | Current authority evaluators, endpoint metadata, catalogue/View filtering, applications/delegation, streams, events, caches and admin projection; readable effective-access summary | WP1; integrate WP2/WP3 context. Exit: full actor/resource matrix passes at request level and live-session level |
| **WP5 — Exposure policy and remote wizard** | Unified policy evaluator in Dashboard/Engine boundaries, strict provenance, remote readiness/status, settings mutations, minimal probe, supported deployment guidance | WP2–WP4. Exit: manual exposure remains protected, remote-off disconnects existing remote access, no automatic insecure downgrade |
| **WP6 — Connectivity delivery** | Preserve Tailscale/Caddy; direct-Kestrel certificate/deployment spike then production support; optional Cloudflare and self-hosted overlay evaluation with honest support labels | WP5 before enabling new exposure. Exit: Windows/Linux/container, IPv4/IPv6/CGNAT, renewal, off-network and media compatibility evidence |
| **WP7 — Future product contracts** | ADRs for optional ID, Social consent and connectivity interfaces; choose candidate stack evaluation criteria, no hosted product rollout | WP1 and canonical identity decisions in WP3. Exit: privacy data-flow and outage review; no mandatory cloud dependency |
| **WP8 — Integrated acceptance and documentation** | Automated security/regression suite, isolated browser/device matrix, provider certification, release/cutover/rollback guide, product wording and independent review | All delivered packages. Exit: critical findings resolved, tests recorded, recovery demonstrated, product owner walkthrough accepted |

Suggested delivery increments: **A:** WP1 plus narrowly scoped fixes to existing behavior; **B:** WP2–WP5 as an integrated secure-local-use release; **C:** certified direct HTTPS and additional providers; **D:** only the accepted future-product contracts. Independent code changes can be developed separately, but do not release a password-free owner path ahead of its exposure and session protections. This plan does not require multi-agent implementation.

Estimate only after WP1 resolves topology and session assumptions. Largest uncertainties are native/headless packaging, revocation across active Blazor/media sessions, provider certification and certificate operations. An inventory or UI rename is small; the integrated security lifecycle is not. Do not claim a calendar deadline from file counts.

## 9. Acceptance and security verification

Use fresh isolated databases/configuration and synthetic media under ignored `.tmp/`; never reset the working household database. Follow repository startup/cleanup rules before implementation verification, including stopping Engine/Dashboard processes before development builds. Keep evidence, clean compiled QA copies after acceptance, and never retain actual secrets in reports.

| Gate | Required observable result |
| --- | --- |
| A01 First ownership | Competing browsers, restart and process races produce exactly one claimed installation; only host-proof holder can claim |
| A02 Local-only use | No email/password required for the desktop local journey; unrelated LAN/WAN browser cannot obtain a session or list people |
| A03 Manual exposure | Test raw forwarding, trusted and untrusted proxy, forged forwarding headers, Docker NAT, null IP, IPv4/IPv6 and hairpin routes; disallowed ingress never reaches protected data |
| A04 Existing-session replay | Local session copied to remote ingress is denied, including image/reader/stream/hub requests; cookie device ID alone is never proof |
| A05 Disablement | Disable remote access, account, grant, device, app, credential and token separately; deny new requests and terminate active remote work within the accepted bound |
| A06 Setup lockout safety | Removing/locking the last admin, expired credentials, database restore and provider outage never reopen unclaimed setup |
| A07 Account upgrade | Local owner becomes secured without changing account/profile ownership, history, private media or grants; provisional session is replaced |
| A08 Access isolation | Full actor matrix against APIs, direct URLs, search/counts/artwork, queues, HLS, downloads, private collections/playlists, View derivatives and events |
| A09 Authentication abuse | Credential stuffing, lockout abuse, enumeration, CSRF/login CSRF, callback state/nonce replay, wrong issuer/audience and token leakage checks |
| A10 Recovery/mutation races | Concurrent code redemption, reset, unlink, last-authenticator removal and invitation acceptance remain atomic; deleted/revoked identities cannot regain access |
| A11 Provider certification | At least one mainstream OIDC provider and one self-hosted privacy candidate pass real linking/login/recovery; each advertised additional provider has its own record |
| A12 Passkey portability | Supported desktop/mobile/security-key journeys, wrong origin/RP, replay, missing user verification, recovery, domain change and lost device |
| A13 Direct HTTPS | Real off-network reachability, invalid/expired certificate rejection, renewal with active streams, restart, DNS change, firewall/CGNAT guidance and no exposed Engine |
| A14 Data minimization | Logs, traces, backups, browser storage, errors and outbound traffic contain no unnecessary credentials/media details; privacy notice matches observed flows |
| A15 Usability/accessibility | Friendly local entry, method availability reasons, keyboard/screen-reader setup and recovery, mobile layout, clear profile-sharing disclosure |
| A16 Recovery/rollback | Restore configuration/keys/database in isolation, recover through host console, revoke old sessions, and return to secured local mode without original-media loss |

Extend existing tests rather than building a duplicate test architecture: `AuthenticationEntryEndpointTests`, `AuthenticationPolicyTests`, `ProtectedSetupTests`, `RouteAuthorizationGuardrailTests`, `AccessAuthorityIntegrationTests`, `ViewResourceAuthorizationTests`, `ClientAuthorizationServiceFlowTests`, `DashboardAuthoritySessionTests`, `DashboardLoginAntiforgeryTests`, `DashboardFirstRunExperienceTests`, `ExternalAuthenticationConfigurationTests`, and `FirstPartyIdentityServiceTests`.

Start with focused tests, then required solution/build/static gates and meaningful real browser/device/provider verification. Add negative runtime tests: source-text assertions alone cannot establish security. Run dependency vulnerability review and an authorized scanner against the isolated deployment, with rate/resource bounds. No public scanning or router mutation during ordinary unit-test execution. Independent review should examine high-risk data flows and evidence, not just aggregate passing test counts.

## 10. Migration, recovery, and product decisions

Existing secured installs stay secured; never silently downgrade to local password-free mode. Reconcile `auth.mode`, remote-sign-in flags, network remote enablement, canonical origin and trusted-network settings in a versioned migration. Ambiguous exposure or origin configurations fail into a clearly explained secured-local state with host recovery. Do not rewrite external issuer/subject links automatically. Preserve account/profile IDs and Personal Space ownership; protect original media. Repository rules treat obsolete pre-beta state separately, so obtain a specific migration/cutover decision instead of quietly adding legacy authorization fallbacks or resetting data.

Back up identity database, configuration and necessary encryption/signing key material with access controls; ordinary backups may exclude `.secrets`, so restoration must explicitly account for it. Never use an old binary against a new incompatible identity schema as a casual rollback. Stop services and restore a matched isolated-tested snapshot or disable the new exposure feature while keeping the valid security state. Restore must revoke/revalidate appropriate sessions and check installation identity collisions when clones exist.

Recommended decisions for review:

| Decision | Proposed default | Alternative / consequence |
| --- | --- | --- |
| D1 Password-free first run | This computer only, host claimed | Password-free LAN convenience requires trusted-device pairing and more threat analysis; no anonymous first-LAN-user ownership |
| D2 Email requirement | Default email/password form, username alternative; email recovery optional | Email-only identity is simpler but conflicts with privacy/offline goals |
| D3 Remote prerequisite | Secured owner plus independent recovery; passkey/MFA for public admin | Password-only administration is easier but weaker; would require an explicit risk decision |
| D4 First supported remote route | Tailscale private path and existing HTTPS proxy | Direct HTTPS is a planned advanced route after its operational gates |
| D5 Privacy SSO candidate | Pocket ID for a small household pilot; authentik/Keycloak for broader evaluation | Avoid certifying many providers at once |
| D6 Local profile on household devices | Authenticated/pairing-approved device then granted profile selection | CIDR-only no-PIN convenience remains explicit legacy behavior to review, never an internet identity |
| D7 Cloudflare | Optional DNS; evaluated Tunnel deployment with terms/privacy disclosure | No default unrestricted media relay promise |
| D8 Future brand services | Optional OIDC ID, separate consent for Social and connectivity | Mandatory Tuvima account would change the local-first product promise |

These defaults make the proposal reviewable without requiring immediate answers. Product decisions must be resolved before their dependent implementation ships.

## 11. Source evidence and reviewer handoff

Repository paths below are relative to the repository root for portability in Claude or another checkout. Symbols identify the reviewed behavior even if line numbers move. Read the current source, since this is a dated baseline.

| Ref | Source / relevant entry point |
| --- | --- |
| R1 | `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs` — login GET/POST, `LoginPage`, `IsLocalClient` |
| R2 | `src/MediaEngine.Identity/FirstPartyIdentityService.cs` — bootstrap, password/PIN entry, `ValidateSessionAsync`, recovery and password validation |
| R3 | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs` — admission, `AllowsClient`, `IsConfiguredProvider`, `IsCanonicalOriginReady`; `src/MediaEngine.Domain/Configuration/AuthSettings.cs` |
| R4 | `src/MediaEngine.Web/Components/Pages/SetupPage.razor` — `ReloadAsync`; `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs`; `src/MediaEngine.Api/Services/SetupSessionService.cs` |
| R5 | `src/MediaEngine.Web/Program.cs` — middleware and authentication registration; `src/MediaEngine.Web/Services/Configuration/ForwardedHeaderConfiguration.cs` |
| R6 | `src/MediaEngine.Api/Security/TuvimaAuthentication.cs`; `src/MediaEngine.Api/Security/RequestAuthorityServices.cs` |
| R7 | `src/MediaEngine.Domain/Authorization/AuthorizationPrimitives.cs`; `src/MediaEngine.Domain/Authorization/ApplicationPermissionPresets.cs`; `docs/architecture/security.md` |
| R8 | `src/MediaEngine.Web/Services/Integration/DashboardCookieEvents.cs`; `src/MediaEngine.Web/Services/Integration/DashboardIdentityClient.cs` |
| R9 | `docs/guides/account-security.md`; `src/MediaEngine.Api/DependencyInjection/TuvimaStorageServiceCollectionExtensions.cs` |
| R10 | `src/MediaEngine.Api/Security/AccountPasskeyStore.cs`; passkey routes in R3 |
| R11 | `src/MediaEngine.Web/Services/Integration/ExternalAuthenticationRegistration.cs`; `src/MediaEngine.Api/Security/AuthenticationProviderConfigurationService.cs` |
| R12 | `src/MediaEngine.Api/Security/ExternalIdentityTransactionService.cs`; `src/MediaEngine.Identity/AccountExternalLoginService.cs` |
| R13 | `src/MediaEngine.Api/Services/Networking/RemoteAccessReadinessService.cs`; `src/MediaEngine.Api/Endpoints/NetworkEndpoints.cs`; `docs/architecture/network-and-remote-access.md`; `docs/guides/remote-access.md` |
| R14 | `engineering/plans/access-architecture-2026-09-08/plan.md`; `engineering/plans/access-architecture-2026-09-08/execution/status.md` — current acceptance section, not superseded checkpoint notes |
| R15 | `docs/guides/external-authentication.md`; `tests/MediaEngine.Web.Tests/ExternalAuthenticationConfigurationTests.cs`; `tests/MediaEngine.Api.Tests/AuthenticationEntryEndpointTests.cs` |

Use the companion [Claude review prompt](./access-identity-remote-access-claude-review-2026-10-08.md). Provide this plan and repository access; without code access, Claude should perform an architecture/product review and clearly mark source claims unverified. Request prioritized findings, exact affected section/source, credible failure scenario, proposed correction, and acceptance evidence. No implementation, publishing, credential changes or public exposure is requested by that handoff.

## Plain-English completion summary

The review plan is complete. Tuvima already has accounts, permissions, recovery, passkeys and external sign-in foundations. Its local-profile option is meant for trusted household use, but its wording and raw ID field are confusing. The proposed work makes starting on one computer effortless, requires proper protection before access expands, verifies permissions across every way media is accessed, and keeps future Tuvima ID, Social and hosted connections optional. The application has not been changed or newly exposed to the internet.
