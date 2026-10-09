---
title: "Network and Remote Access"
description: "How Tuvima separates desired network configuration from observed connectivity and chooses remote playback delivery."
audience: "developer"
category: "architecture"
product_area: "networking"
status: current
---

# Network and Remote Access

## In this page

How Tuvima separates desired network configuration from observed connectivity and chooses remote playback delivery.

## Where this lives in the code

- `src/MediaEngine.Api/Services/Networking`
- `src/MediaEngine.Api/DependencyInjection/TuvimaNetworkServiceCollectionExtensions.cs`
- `deploy`

Tuvima treats networking as an appliance-style workflow. Administrators use
**Settings → Network & Remote Access** for a health overview, LAN settings,
remote access, network-aware streaming, and advanced diagnostics. A new install
uses the same panels in `/setup/network`; there is no second onboarding model.
The default is local-network-only and requires no router, DNS, TLS, or tunnel
knowledge.

## Source of truth

Desired state lives in `config/network.json` and is validated against
`config/schemas/network.schema.json`. `who_can_connect` (`this_computer`, `home_network` or `anywhere`) is the single door rule that replaces the old remote on/off switch and the sign-in remote toggles: the Dashboard's exposure policy refuses any request from farther away than it allows, before routing, authentication or static files. Schema 2.0 includes the Dashboard port,
binding, local discovery, remote mode, advanced router automation, custom HTTPS
address, exact/CIDR trusted proxies, and remote-streaming policy. Supported
remote modes are `tailscale`, `custom`, and the advanced `direct-only` mode.
The removed `secure-provider` placeholder is rejected.

`native_app_access.enabled` (default `false`) switches the Dashboard's paired-app
door on or off. It can only be saved as `true` while `who_can_connect` is `anywhere`,
and turning remote access off or resetting network settings turns it off too.
The Dashboard re-reads the file when it changes and treats a missing or
unreadable file as off.

Observed state stays in memory: usable interfaces, topology, current
Tuvima-owned router mapping, public address, latest tests, measured bandwidth,
and provider state. The API never writes those observations into configuration.

## Supported secure edge

The Dashboard is the only supported user-facing service. The Engine stays on
loopback or an internal application network. TLS terminates at one of these
external edges:

- Tailscale Serve for private tailnet HTTPS.
- An administrator-managed HTTPS reverse proxy such as Caddy, nginx, or
  Traefik.

Tuvima authentication remains mandatory. Tailnet membership and forwarded
identity headers do not replace the Dashboard sign-in boundary. Remote access
cannot be enabled until an administrator exists, localhost bypass is off, and
the selected HTTPS/tunnel route passes a Tuvima challenge probe.

## Connectivity lifecycle

- Local discovery announces `_tuvima._tcp.local` when enabled.
- Router setup is off by default and appears only under Advanced settings.
- Advanced automatic setup tries PCP, NAT-PMP, then UPnP IGD, targeting an
  explicitly configured local TLS reverse-proxy listener rather than the
  Dashboard HTTP port.
- Lease mappings are renewed every 15 minutes and after a safe port change.
- Disabling router automation or a clean Engine shutdown removes only the
  mapping that Tuvima created.
- A failed automatic setup produces a dynamic manual TCP forwarding rule.
- CGNAT evidence changes the guidance instead of repeatedly recommending a
  router rule that is unlikely to work.
- Docker bridge mode is classified as `unsupported-topology`. Inside the
  container, the visible gateway belongs to Docker's bridge (commonly associated
  with `docker0`) rather than the physical LAN router. No PCP, NAT-PMP, or SSDP
  traffic is sent in that topology.

Mapping observations use explicit states: `not-attempted`,
`unsupported-topology`, `protocol-unavailable`, `router-refused`, `active`, and
`expired`. Protocol timeouts and malformed responses remain reason codes rather
than being presented as proof that a router refused the request.

External reachability and upload measurements remain **unknown** when no trusted
external measurement target exists. The UI does not substitute sample success,
public-IP, or bandwidth values.

## Tailscale deployment boundary

Tailscale is an external deployment provider, not an embedded VPN. Native hosts
are observed with `tailscale status --json` and `tailscale serve status --json`.
The Docker preset uses the official Tailscale sidecar, private Serve HTTPS, and
a separate persistent state volume. Enrollment keys are deployment secrets
outside Tuvima configuration and backups. The generic
`IRemoteConnectivityProvider` boundary remains available for Headscale or a
different tunnel provider.

## Playback context

Playback manifests and heartbeats carry connection path, provider, approximate
bandwidth, latency, device, profile, and optional room identity. These facts are
used only for delivery selection and diagnostics—not authentication.

For remote playback, the existing playback manifest can prefer its existing HLS
delivery path when source resolution or calculated bitrate exceeds the configured
quality/bandwidth budget. Local compatible clients retain direct delivery. This
does not introduce a second transcoding engine.

## Security boundary

Every network settings and diagnostics endpoint requires the Administrator role.
Reachability never grants authentication. Forwarded host, scheme, and
client-address headers are accepted only from exact proxy IP addresses in
`trusted_proxies` or explicit CIDR entries in `trusted_proxy_networks`, with one
forwarding hop allowed. The middleware runs before HSTS, redirection,
authentication, and URL generation. Forwarded headers are honoured only on the
optional proxy port (`remote.proxy_port`); on the main Dashboard port they are
ignored. One `IngressClassifier` decides whether a request is from this
computer, the home network, or remote (anything on the proxy port is remote),
and requests classified as remote are rejected before authentication when the
effective scheme is not HTTPS. A `Host` allow-list (`local.allowed_hostnames`
plus the machine, server and public names) refuses unknown host names to block
DNS rebinding.
