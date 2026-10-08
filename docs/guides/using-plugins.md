---
title: "Use Plugins"
description: "How administrators discover, install, enable, configure, and remove Tuvima Library plugins."
audience: "user"
category: "guide"
product_area: "plugins"
tags:
  - "plugins"
  - "settings"
  - "admin"
---
Plugins extend Tuvima Library with optional behavior such as playback segment detection. The current plugin system is admin-only and intentionally conservative: Tuvima can list built-in and dynamic plugins, enable or disable them, edit settings, check health, show recent jobs, and read an approved plugin catalog from GitHub. One-click marketplace install and update flows are not live yet.

## Where plugins live

Open **Settings > Plugins** in the Dashboard.

From that page you can:

- Enable or disable a plugin.
- Edit simple settings or raw settings JSON.
- Edit a dynamic plugin manifest JSON.
- Run plugin health checks.
- View recent plugin jobs.
- Delete a dynamic plugin folder and saved configuration.
- Refresh the approved plugin catalog from GitHub.

Built-in plugins are compiled into Tuvima Library. They can be disabled, but they cannot be deleted or edited as files.

## Install an approved plugin

Approved third-party plugins are listed from the GitHub-backed approved catalog. The catalog is a discovery and trust list, not an installer.

1. Open **Settings > Plugins > Approved catalog**.
2. Refresh the GitHub catalog.
3. Open the plugin release link.
4. Download the versioned plugin archive.
5. Verify the published SHA-256 checksum when the catalog provides one.
6. Extract the archive into your library data folder under:

```text
{library_root}/.data/plugins/{plugin-folder}/
```

The extracted folder must contain `plugin.json` beside the plugin assembly named by the manifest.

7. Restart the Engine or reload the plugin page.
8. Enable the plugin in **Settings > Plugins**.
9. Run **Jobs & health > Check health** before relying on scheduled work.

## Configure a plugin

Select a plugin in **Settings > Plugins**.

- Use **Settings** for simple boolean, number, and text values.
- Use **JSON** when the plugin exposes nested settings.
- Use **Manifest** only for dynamic plugins when you need to inspect or repair plugin metadata.
- Use **Jobs & health** after changing tool paths, AI settings, or scheduled batch settings.

If a plugin needs an external tool such as FFmpeg, the health check explains whether Tuvima found the tool on `PATH`, found a cached copy, used a configured path, or could not resolve it.

## Remove a plugin

Built-in plugins can only be disabled. Dynamic plugins can be removed from **Danger > Delete plugin**. Deleting a dynamic plugin removes its plugin folder and saved plugin configuration.

## Safety model

Only install plugins from sources you trust. A plugin is compiled .NET code loaded by the Engine process. The approved catalog helps administrators find reviewed releases, but it does not make arbitrary third-party code risk-free.

Plugin manifests should declare:

- The plugin id, name, version, entry assembly, and entry type.
- Capabilities the plugin provides.
- Permissions it needs, such as `media.read`, `process.execute`, or `tool.download`.
- Tool requirements and checksums when auto-download is supported.
- AI permissions when the plugin calls local AI.

Plain English summary: Plugins are optional add-ons managed from Settings. Today, users can discover approved plugins from GitHub and install them manually by placing a verified plugin folder under the library data directory, then enabling and checking it in the app.
