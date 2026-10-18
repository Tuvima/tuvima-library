---
title: "Secure Remote Access"
description: "Set up a secure outside address for Tuvima with Tailscale Serve or an HTTPS reverse proxy, without exposing the Tuvima Engine."
audience: "administrator"
category: "guide"
product_area: "networking"
status: current
---

# Secure Remote Access

Tuvima is open from the start: any device that can reach the server can use it, and Tuvima sign-in is always required. At home you need no setup, and phone and TV apps can pair straight away. This page is for reaching Tuvima from outside your home. Allow 15–30 minutes once your network tools are ready.

**Keep your router's ports closed.** Closed ports are what keep the internet out. The Dashboard serves plain http, so if you forward its port to the internet, sign-ins and pairing codes cross the internet unencrypted. For access from outside, use Tailscale or an HTTPS reverse proxy as described below, and never forward the Dashboard port directly.

**Settings → Network → Remote Access** (**How the internet reaches Tuvima**) is the optional place to choose that secure outside address. It no longer unlocks anything; it records the address and checks it with **Test remote access**. Apps use the address away from home.

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
**Tailscale**, and confirm that the private `https://…ts.net` address and Serve
HTTPS are detected. Choose **Test remote access** to check it. Do not use
Tailscale Funnel; this deployment is tailnet-private.

For Docker, follow the [complete Tailscale deployment instructions](https://github.com/Tuvima/tuvima-library/blob/main/deploy/tailscale/README.md). Download the overlay and its configuration files together; the base Compose file alone is not enough. The auth key is supplied as a
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
then also listens on it and trusts forwarded headers (the visitor's address and
the HTTPS scheme) only there. A proxy aimed at port 5016 would hide the real
visitor address and scheme from the Dashboard, because forwarded headers are
ignored on the main port. If you already had a proxy on port 5016, move it to the
proxy port and restart the Dashboard. Add your hostname to `local.allowed_hostnames` unless it is already the
`remote.public_hostname`.

Then:

1. Point public or private DNS for the hostname at the proxy.
2. Allow the proxy to obtain and renew a trusted certificate.
3. Add the proxy's exact address under **Advanced → Reverse Proxy Trust**. For
   an isolated Docker proxy network, add its explicit CIDR instead. A proxy on
   this same computer needs no entry: loopback is already trusted on the proxy
   port.
4. Restart the Dashboard so the proxy trust boundary is applied.
5. Select **HTTPS reverse proxy**, enter the HTTPS address, and choose
   **Save and verify**, then **Test remote access**.

Tuvima ignores forwarded headers from untrusted peers. Never enable a framework
or hosting option that trusts forwarded headers from every address.

## Check account access

1. Under **Settings → Users & Access → Authentication**, choose the sign-in methods you want to allow.
2. In **Users & Access → Users**, grant the intended account only the profiles, features, and libraries it should reach.
3. If you started Tuvima on your computer without a password, add a password or passkey before you use an outside address. That no-password account works from any device that reaches the server, with administrator rights (see [Account security](account-security.md)).
4. Test with that account from the remote device.

A working tunnel or proxy does not grant library access.

## Let phone and TV apps connect

Phone and TV apps are always allowed; there is no switch to turn them on. Open **Settings → Administration → Devices**:

1. **Address to type in the app** shows your home address (this server's address on the adapter chosen in **Settings → Network**, with the Dashboard port) with **Copy** and **Test** buttons. Type it into the app when it asks for your server.
2. When you have chosen a way in from outside (Tailscale or an HTTPS address), the panel also shows the outside address, labelled "Apps use this away from home". The Android app learns it when it pairs and when it starts, tries your home address first, and switches to the outside address on its own when you are away. If you later remove the outside address, re-pair the phone so it forgets it. The Apple, Roku and TV web apps do not switch addresses yet.
3. **Paired devices** lists each paired phone or TV with who it belongs to and when it was last seen. **Revoke** signs one out straight away; it has to be paired again. A server administrator sees every device, and a household administrator sees only their own. The same page lists everyone who is signed in under **Signed-in sessions**, with a **Sign out** for each device.

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
