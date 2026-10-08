---
title: "Secure Remote Access"
description: "Use Tailscale Serve or an HTTPS reverse proxy without exposing the Tuvima Engine."
audience: "administrator"
category: "guide"
product_area: "networking"
---
Tuvima starts in **Local network only** mode. You do not need to understand
ports, router protocols, DNS, or certificates for a normal local installation.

Remote access always requires both Tuvima sign-in and a verified secure path.
The Engine on port 61495 is internal and must not be published or proxied.

## Option 1: Tailscale Serve

Tailscale is the recommended private path. Install Tailscale separately on the
host, or use the supported Compose overlay in `deploy/tailscale`.

For a native installation, connect the host to its tailnet and run:

```text
tailscale serve --bg http://127.0.0.1:5016
tailscale serve status --json
```

Open **Settings → Network & Remote Access → Remote Access**, select
**Tailscale**, confirm that the private `https://…ts.net` address and Serve HTTPS
are detected, then enable remote access. Do not use Tailscale Funnel; this
deployment is tailnet-private.

For Docker, follow `deploy/tailscale/README.md`. The auth key is supplied as a
deployment secret outside `/config` and `/backups`.

## Option 2: Caddy or another HTTPS reverse proxy

Run the proxy on the same host or a trusted adjacent container. A minimal Caddy
site is:

```text
tuvima.example.com {
    reverse_proxy 127.0.0.1:5016
}
```

Then:

1. Point public or private DNS for the hostname at the proxy.
2. Allow the proxy to obtain and renew a trusted certificate.
3. Add the proxy's exact address under **Advanced → Reverse Proxy Trust**. For
   an isolated Docker proxy network, add its explicit CIDR instead.
4. Restart the Dashboard so the proxy trust boundary is applied.
5. Select **HTTPS reverse proxy**, enter the HTTPS address, and choose
   **Save and verify**.
6. Enable remote access only after every Security Check is ready.

Tuvima ignores forwarded headers from untrusted peers. Never enable a framework
or hosting option that trusts forwarded headers from every address.

## Advanced port forwarding

Port forwarding, PCP, NAT-PMP, and UPnP live under **Advanced**. They are useful
only when you run your own local TLS reverse proxy. Configure that proxy's local
HTTPS port; Tuvima refuses to map its HTTP Dashboard listener directly.

Docker bridge deployments cannot use router discovery from inside the Tuvima
container because the visible gateway is Docker's bridge, not the household
router. Use Tailscale, configure the reverse proxy on the Docker host, or manage
the host router manually.
