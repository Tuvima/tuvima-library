# Tailscale private HTTPS preset

This optional Compose overlay runs the official Tailscale image beside Tuvima.
It does not add VPN code to the Tuvima image and it does not publish the Engine.
Tailscale Serve terminates private tailnet HTTPS and proxies only the Dashboard
at `127.0.0.1:5017` (the Dashboard's proxy port, set by `TUVIMA_PROXY_PORT`) in the shared network namespace. Visitors arriving on that port are always treated as remote. Funnel is explicitly off.

## Configure

1. Create a reusable, tagged Tailscale auth key with the minimum grants needed
   for this node.
2. Store the key in a root-readable deployment secret file outside the Tuvima
   `/config` and `/backups` mounts. Do not add it to this repository or an `.env`
   file.
3. Set `TAILSCALE_AUTH_KEY_FILE` to that file's absolute path.
4. Set `TUVIMA_TAILSCALE_URL` to the node's expected MagicDNS HTTPS address,
   such as `https://tuvima-library.example.ts.net`.
5. Start the base deployment plus the overlay:

   ```sh
   docker compose -f docker-compose.yml -f deploy/tailscale/docker-compose.tailscale.yml up -d
   ```

6. In Tuvima, open **Settings → Network & Remote Access → Remote Access**,
   select **Tailscale**, verify that Serve HTTPS is detected, and then enable
   remote access.

The Tailscale node state lives in its own Docker volume. The enrollment key is
mounted as a Docker secret and is never placed in Tuvima configuration, exports,
or backup archives.

Headscale and other tunnel providers can use the same remote-connectivity
provider boundary. They are not enabled by this preset.
