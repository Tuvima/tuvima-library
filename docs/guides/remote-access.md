---
title: "Secure Remote Access"
description: "Use Tailscale Serve or an HTTPS reverse proxy without exposing the Tuvima Engine."
audience: "administrator"
category: "guide"
product_area: "networking"
status: current
---

# Secure Remote Access

Reach Tuvima Library through a private HTTPS path while keeping sign-in required. Allow 15–30 minutes once your network tools are ready. Tuvima starts in **Local network only** mode; local use needs no router mapping.

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

For Docker, follow the [complete Tailscale deployment instructions](https://github.com/Tuvima/tuvima_library/blob/main/deploy/tailscale/README.md). Download the overlay and its configuration files together; the base Compose file alone is not enough. The auth key is supplied as a
deployment secret outside `/config` and `/backups`.

## Option 2: Caddy or another HTTPS reverse proxy

Run the proxy on the same host or a trusted adjacent container. A minimal Caddy
site is:

```text
tuvima.example.com {
    reverse_proxy 127.0.0.1:5017
}
```

Point a same-machine proxy at the **proxy port**, not the main Dashboard port.
Set `remote.proxy_port` (here `5017`) in `config/network.json`; the Dashboard
then also listens on it, treats everything arriving there as remote, and trusts
forwarded headers only there. A proxy aimed at port 5016 (the old instruction) would make every
visitor look like a person sitting at this computer to the Dashboard's local
checks. If you already had a proxy on port 5016, move it to the proxy port and restart the Dashboard; until you do, connections from a listed `trusted_proxies` address are treated as remote but forwarded headers are ignored, so the real visitor address and HTTPS scheme are not seen, and sign-in and setup are refused for them with a "use the proxy port" message. Add your hostname to `local.allowed_hostnames` unless it is already the
`remote.public_hostname`.

Then:

1. Point public or private DNS for the hostname at the proxy.
2. Allow the proxy to obtain and renew a trusted certificate.
3. Add the proxy's exact address under **Advanced → Reverse Proxy Trust**. For
   an isolated Docker proxy network, add its explicit CIDR instead. A proxy on
   this same computer needs no entry: loopback is already trusted on the proxy
   port, and listing `127.0.0.1` would make `localhost:5016` count as remote and
   be refused.
4. Restart the Dashboard so the proxy trust boundary is applied.
5. Select **HTTPS reverse proxy**, enter the HTTPS address, and choose
   **Save and verify**.
6. Choose **Anywhere** under **Who can connect** only after every Security Check is ready.

Tuvima ignores forwarded headers from untrusted peers. Never enable a framework
or hosting option that trusts forwarded headers from every address.

## Check account access

1. Open **Settings → Network** and set **Who can connect** to **Anywhere** only when you intend to use remote access. Remote sign-in always requires HTTPS.
2. Under **Settings → Users & Access → Authentication**, choose the sign-in methods you want to allow.
3. In **Users & Access → Users**, grant the intended account only the profiles, features, and libraries it should reach.
4. Test with that account from the remote device.

A working tunnel or proxy does not grant library access.

## Advanced port forwarding

Port forwarding, PCP, NAT-PMP, and UPnP live under **Advanced**. They are useful
only when you run your own local TLS reverse proxy. Configure that proxy's local
HTTPS port; Tuvima refuses to map its HTTP Dashboard listener directly.

Docker bridge deployments cannot use router discovery from inside the Tuvima
container because the visible gateway is Docker's bridge, not the household
router. Use Tailscale, configure the reverse proxy on the Docker host, or manage
the host router manually.

## Next steps

- [Manage accounts and recovery](account-security.md).
- [Back up before network changes](operations-and-recovery.md).
- [Troubleshoot connection problems](troubleshooting.md).
