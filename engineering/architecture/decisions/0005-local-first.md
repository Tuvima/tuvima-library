# 0005 — Local-first; no Tuvima-hosted account

**Date:** 2026-02-27 (from the first public project structure) · **Status:** Accepted

## What
The Engine, Dashboard, catalogue and optional AI run on the user's own machine. There is no Tuvima account, subscription or cloud service. Outside requests happen only for configured metadata providers, downloads and the Places map (`docs/explanation/privacy-local-first.md`).

## Why
- **Privacy:** what you own and watch stays on your hardware.
- **Reliability:** the library keeps working without Tuvima's servers, because there are none.

## Trade-offs accepted
No cloud sync between homes; remote access is the user's job (Tailscale or reverse-proxy guides); local AI needs local hardware and model files.
