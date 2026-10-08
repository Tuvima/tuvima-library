---
title: "Run from source"
description: "Run the Engine and Dashboard from a source checkout with the required .NET SDK and local configuration checks."
audience: "developer"
category: "installation"
product_area: "deployment"
status: current
---

# Run from source

Use a source checkout to develop or evaluate Tuvima Library. Allow 15–30 minutes, plus tool installation, package restore, and optional AI downloads.

## Check prerequisites

Install Git and a stable .NET 10 SDK. `global.json` requests `10.0.100` with `latestFeature` roll-forward, so a later stable .NET 10 feature band can be selected.

```powershell
git --version
dotnet --version
```

Review `nuget.config` before restore. This checkout uses nuget.org. Some local development setups map `Tuvima.Wikidata*` to the sibling feed at `C:\Users\shaya\OneDrive\Documents\Source\Repos\tuvima-wikidata\artifacts`. If restore cannot find those packages, check your checkout's feed before changing package versions.

## Get the code

```powershell
git clone https://github.com/Tuvima/tuvima_library.git
cd tuvima_library
dotnet restore MediaEngine.slnx
```

## Choose safe local paths

Before starting, read `config/core.json` and `config/libraries.json`. The committed files can contain development-machine paths. Set your own data root, managed library root, approved storage locations, and sources.

Keep originals read-only when evaluating. Put provider keys in ignored `config/secrets/` files. Local AI weights and native runtimes use separate storage; see [shared AI storage](../guides/shared-ai-storage.md).

## Start both apps

1. Stop any existing Tuvima Engine or Dashboard development processes.
2. From the repository root, start the Engine:

   ```powershell
   dotnet run --project src/MediaEngine.Api
   ```

3. Wait for `Now listening on: http://localhost:61495`.
4. In another terminal at the same repository root, start the Dashboard:

   ```powershell
   dotnet run --project src/MediaEngine.Web
   ```

5. Open `http://localhost:5016/setup` and create the administrator. Store the recovery codes safely.

Launch profiles point both apps at `../../config`. If you override `TUVIMA_CONFIG_DIR`, use the same resolved directory for both apps. Set `TUVIMA_ENGINE_URL` before launching the Dashboard if the Engine address differs.

Press `Ctrl+C` in both terminals to stop. Do not expose the Engine to other devices.

## Next steps

- [Set up development tools and verification](../tutorials/dev-setup.md).
- [Add your first library](../tutorials/first-library.md).
- [Resolve startup problems](../guides/troubleshooting.md).
