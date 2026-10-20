---
title: "Plugin directory format"
description: "Reference for the GitHub-hosted plugin directory list the Library reads, how it is saved and checked, and how to submit a plugin."
audience: "developer"
category: "reference"
product_area: "plugins"
tags:
  - "plugins"
  - "directory"
  - "github"
status: current
---

# Plugin directory format

## In this page

Reference for the list of available plugins that the Library reads from GitHub: where it lives, the format of the file, how the Library saves and checks it, and what the trust labels mean.

## Where this lives in the code

- `src/MediaEngine.Api/Services/Plugins/PluginDirectoryService.cs` (saved copy, checks, switch, install preview)
- `src/MediaEngine.Api/Services/Plugins/PluginDirectoryParser.cs` (reads the file)
- `src/MediaEngine.Api/Endpoints/PluginEndpoints.cs` (`/plugins/directory`)
- `docs/reference/approved-plugins.json` (the list itself)

## How the Library uses the list

**Settings > Plugins > Available plugins** shows the plugin directory. The Library keeps a saved copy of the list in its data folder (`plugin-directory/directory.json`) and shows that copy. It goes to GitHub only:

- when an administrator opens Available plugins and the last check was more than 24 hours ago (or there is no saved copy yet), or
- when an administrator presses **Check now**.

There is no background schedule. The request is a plain `GET` of the list's address with the Library's standard User-Agent; nothing about the person, the Library or the server is sent. A check that fails never replaces the saved copy: the page keeps showing it with the date it is from.

The switch on the section turns the lookup off (`plugin_catalog.enabled` in `config/core.json`). With the switch off no request is made at all; the page still shows the saved copy, if there is one, and says the directory is switched off.

The address is configured in `config/core.json`:

```json
{
  "plugin_catalog": {
    "enabled": true,
    "approved_plugins_url": "https://raw.githubusercontent.com/Tuvima/tuvima-library/main/docs/reference/approved-plugins.json"
  }
}
```

The Library only accepts HTTPS addresses on `raw.githubusercontent.com` or `github.com`. The directory is read-only: reading the list never downloads or installs plugin code.

## List format (version 2)

```json
{
  "schema_version": "2.0",
  "updated_at": "2026-10-20T00:00:00Z",
  "plugins": [
    {
      "id": "example.sample-plugin",
      "name": "Sample Plugin",
      "author": "Example",
      "description": "Short user-facing summary.",
      "version": "0.1.0",
      "kind": "declarative",
      "trust_level": "community",
      "permissions": ["network.http"],
      "network": [
        { "host": "api.example.com", "sends": "Searches for titles by name" }
      ],
      "capabilities": ["metadata-source"],
      "minimum_library_version": "1.0.0",
      "maximum_library_version": "1.9.99",
      "supported_platforms": ["windows", "linux"],
      "repository_url": "https://github.com/example/tuvima-sample-plugin",
      "release_url": "https://github.com/example/tuvima-sample-plugin/releases/tag/v0.1.0",
      "package_url": "https://github.com/example/tuvima-sample-plugin/releases/download/v0.1.0/sample-plugin.zip",
      "sha256": "64-character lowercase hex checksum",
      "added_at": "2026-10-20T00:00:00Z"
    }
  ]
}
```

Top level:

| Field | Meaning |
|---|---|
| `schema_version` | The format version. This page describes `2.x`; a file with another major version is refused and the saved copy stays. |
| `updated_at` | When the list was last changed (UTC). |
| `plugins` | The listed plugins. |

Each plugin:

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Unique plugin id, with no spaces (the same `id` as in the plugin's manifest). |
| `name` | yes | Name shown to people. |
| `kind` | yes | How it runs: `declarative` (JSON only), `service` (a separate program), `sandboxed` (WebAssembly) or `official` (ships with the Library). |
| `author`, `description`, `version` | no | Shown on the entry. |
| `trust_level` | no | `official`, `verified` or `community`. Missing or unknown counts as `community` (not verified). |
| `permissions` | no | The permission ids the plugin declares, for example `network.http` or `media.read`. The Library words them for people ("Connects to the internet"). |
| `network` | no | Where the plugin sends data and what it sends there: a list of `{ "host", "sends" }` (optionally `max_requests_per_minute`), written exactly as in the plugin's manifest (see [Building a plugin](../guides/building-a-plugin.md#say-where-the-plugin-connects)). When present, the approval step shows one "Contacts host: what it sends" line per entry instead of "Connects to the internet", and the Engine returns the list as `network`. A list that gives `network` entries must also give `network.http` in `permissions`, and each entry must be a valid host with a plain sentence; an entry that breaks this is left out and counted, never shown as a smaller list. A plugin that has `network.http` but no `network` list (older lists) is still shown, described only as connecting to the internet. |
| `capabilities` | no | Short names of what the plugin provides. |
| `minimum_library_version`, `maximum_library_version` | no | The Library versions it works with. The minimum defaults to `1.0.0`; no maximum means no upper limit. |
| `supported_platforms` | no | Runtime ids (`win-x64`) or OS families (`windows`, `linux`, `macos`). Empty means every platform. |
| `repository_url`, `release_url`, `package_url` | no | HTTPS addresses only; anything else is ignored. |
| `sha256` | no | Checksum of the package: 64 lowercase hex characters, otherwise ignored. |
| `added_at` | no | When the plugin was added to the list (UTC). |

The Library reads the list defensively. An entry that cannot be understood (not an object, no `id` or `name`, an unknown or missing `kind`, or an `id` already used) is left out and counted; the page says how many entries could not be read. One bad entry never hides the rest of the list.

## Trust labels

The label on each entry comes from `trust_level`:

- **Official**: built by Tuvima and shipped with the Library.
- **Verified**: Tuvima checked the publisher and reviewed what the plugin asks for.
- **Not verified**: listed, but Tuvima has not checked it. The entry shows the warning "Tuvima hasn't checked this plugin. Plugins run inside the Library with its full access." beside it, and the same words appear in the approval step before an install.

Only Tuvima can set `verified`: a submission that claims it is treated as a request, and the mark is added to the list after the review.

## What is new

The Library remembers which plugin ids it has already shown. A listed plugin that was not in the list the last time Available plugins was opened counts as new, and the page shows a quiet "N new plugins available" line. Opening the section marks the current list as seen.

## Install

The list carries the package address and checksum so the Library can install a plugin, but this version only reads the list. An **Install** button appears on an entry only when the Engine reports `install_supported: true`; it opens the same approval step as switching a plugin on, built from what the list declares, before anything is downloaded. Until installing exists the button is not drawn.

## Submit your plugin

See [Submit your plugin](../guides/building-a-plugin.md#submit-your-plugin).

Plain English summary: Tuvima keeps a public list of plugins on GitHub. The Library saves a copy, checks it at most once a day or when an administrator asks, and labels every entry as Official, Verified or Not verified. Nothing is installed by reading the list.
