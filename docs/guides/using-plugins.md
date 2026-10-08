---
title: "Use Plugins"
description: "How administrators discover, install, enable, configure, and remove Tuvima Library plugins."
audience: "administrator"
category: "guide"
product_area: "plugins"
tags:
  - "plugins"
  - "settings"
  - "admin"
status: current
---

# Use Plugins

Add optional tools to Tuvima Library with [plugins](../reference/glossary.md#plugin). Allow a few minutes for setup, plus time to get and check the files. Only admins can manage plugins.

You can list built-in and dynamic plugins, enable or disable them, edit settings, check health, and view recent jobs. The approved catalog comes from GitHub. One-click install and update flows are not ready.

## Open plugin settings

Open **Settings > Plugins**. From there you can change settings, run health checks, view jobs, and refresh the approved catalog.

Built-in plugins ship as part of Tuvima. You can disable them, but cannot delete them or edit their files. Dynamic plugins have their own folder and manifest.

## Install an approved plugin

The approved catalog is a list of reviewed releases, not an installer.

1. Open **Settings > Plugins > Approved catalog**.
2. Refresh the GitHub catalog.
3. Open the plugin release link.
4. Download the versioned archive.
5. Check its published SHA-256 checksum when one is supplied.
6. Extract it into the library data folder:

```text
{library_root}/.data/plugins/{plugin-folder}/
```

The folder must contain `plugin.json` beside the assembly named in that file.

7. Restart the Engine to load the new assembly.
8. Enable the plugin in **Settings > Plugins**.
9. Run **Jobs & health > Check health** before relying on its scheduled work.

## Change settings

Select a plugin in **Settings > Plugins**.

- Use **Settings** for simple on/off, number, and text values.
- Use **JSON** for nested settings.
- Use **Manifest** to inspect or repair a dynamic plugin's manifest JSON.
- Use **Jobs & health** after changing tool paths, AI settings, or job settings.

If a plugin needs a tool such as FFmpeg, its health check tells you how that tool was found. It may use `PATH`, a cached copy, or a path you set. If none works, the check reports that failure.

## Remove a plugin

Built-in plugins can only be disabled. To remove a dynamic plugin, choose **Danger > Delete plugin**. This deletes its folder and saved setup.

## Choose trusted code

A plugin is compiled .NET code loaded into the Engine process. It is not sandboxed. Install only code from a source you trust. The approved catalog helps you find reviewed releases, but does not remove all risks from outside code.

<details>
<summary>Technical details</summary>

A manifest should name the plugin ID, name, version, entry assembly, and entry type. It should state its features and needed rights, such as `media.read`, `process.execute`, or `tool.download`.

It should also list required tools and checksums when tool downloads are supported. If it calls local AI, it must declare those rights too.

</details>

## Next steps

- [Check the approved plugin catalog](../reference/plugin-catalog.md)
- [Build a plugin](building-a-plugin.md)
- [Understand privacy and external services](../explanation/privacy-local-first.md)
