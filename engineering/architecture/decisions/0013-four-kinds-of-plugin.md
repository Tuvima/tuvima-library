# 0013 — Plugins come in four kinds, and nothing runs until it is approved

**Date:** 2026-10-21 (Shy; spec `engineering/specs/developer-tools.md` D-A, D-C, D-G, D-H, D-J) · **Status:** Accepted

## What
A plugin is one of four kinds, set by `kind` in its `plugin.json`:

- **`official`**: compiled .NET code that runs inside the Engine with the Engine's trust. Shipped Local AI features are of this kind. An official plugin installed from the directory must be signed.
- **`declarative`**: JSON only. The Library supplies all behaviour (AI steps, channel and guide lists, notification channels, metadata sources), so a bad file is refused with a sentence and a plugin cannot do more than the format allows.
- **`service`**: a separate program the Library starts, supervises (health checks, restarts, memory watch) and reaches only through a local, key-protected door (`/plugin-host/v1`).
- **`sandboxed`**: a WebAssembly module (Wasmtime, Apache-2.0 with LLVM exception) with no file system, clock, random or environment, only `library.log` and `library.http_get`, and limits on memory, time and work.

Every kind goes through the same rules: the manifest declares permissions and each web address it contacts; an administrator approves a plain-words statement of exactly that (recorded with its hash, asked again if it changes); the web connection refuses any undeclared host; secrets live in the config folder and are read only through the plugin's own context. Trust levels (Official, Verified, Community, Private) are shown on the card and the approval, directory installs are checked against a signature, checksum and size limits, and a blocklist can switch a harmful plugin off. Providers, the Local AI features, notification channels and backups are all expressed through these kinds, so the Library has no name-keyed special cases for them.

## Why
Shy wanted outside software and new data sources to be easy to add without making the Library unsafe or fragile. Giving each level of power its own kind lets a person choose the least that does the job: most additions are JSON with no code at all (**Maintenance**, **Privacy**), code that needs to run on this machine can be isolated as a separate program or a sandbox (**Reliability**), and compiled in-process code is kept for what ships with the Library (**Performance**, **Extensibility**).

## Trade-offs accepted
- An `official` plugin has the Engine's full trust; the approval is honesty about that, not protection. A hand-installed in-process plugin is as risky as any program the person runs.
- Four kinds is more to explain than one. The Plugins page shows the kind on every card so a person can tell them apart.
- Backup destinations for S3 and WebDAV are built into the Library instead of being plugins, because their address is chosen by the administrator and a plugin must declare its addresses up front; the same approval is kept as a host confirmation (see the WP26 note in the spec).

## Revisit if
A plugin type is needed whose address is chosen by the administrator at approval, a fifth kind of isolation is wanted, or the Library gains a safe way to run unsigned compiled plugins.
