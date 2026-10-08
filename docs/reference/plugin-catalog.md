---
title: "Approved Plugin Catalog"
description: "Reference for the GitHub-hosted approved plugin list consumed by the Engine."
audience: "developer"
category: "reference"
product_area: "plugins"
tags:
  - "plugins"
  - "catalog"
  - "github"
status: current
---

# Approved Plugin Catalog

## In this page

Reference for the GitHub-hosted approved plugin list consumed by the Engine.

## Where this lives in the code

- `src/MediaEngine.Api/Services/Plugins/ApprovedPluginCatalogService.cs`
- `src/MediaEngine.Api/Endpoints/PluginEndpoints.cs`
- `docs/reference/approved-plugins.json`

The Engine can read a GitHub-hosted JSON list of approved plugins through:

```http
GET /plugins/approved
```

The source URL is configured in `config/core.json`:

```json
{
  "plugin_catalog": {
    "enabled": true,
    "approved_plugins_url": "https://raw.githubusercontent.com/Tuvima/tuvima_library/main/docs/reference/approved-plugins.json"
  }
}
```

The Engine only accepts HTTPS URLs hosted by `raw.githubusercontent.com` or `github.com`. The catalog is read-only. It does not install plugin code.

## Catalog shape

```json
{
  "schema_version": "1.0",
  "source_url": "https://raw.githubusercontent.com/Tuvima/tuvima_library/main/docs/reference/approved-plugins.json",
  "last_updated": "2026-05-15T00:00:00Z",
  "status": "ok",
  "plugins": [
    {
      "id": "example.sample-plugin",
      "name": "Sample Plugin",
      "version": "0.1.0",
      "description": "Short user-facing summary.",
      "author": "Example",
      "status": "approved",
      "repository_url": "https://github.com/example/tuvima-sample-plugin",
      "release_url": "https://github.com/example/tuvima-sample-plugin/releases/tag/v0.1.0",
      "package_url": "https://github.com/example/tuvima-sample-plugin/releases/download/v0.1.0/sample-plugin.zip",
      "sha256": "64-character lowercase hex checksum",
      "minimum_tuvima_api_version": "1.0.0",
      "capabilities": ["playback-segment-detector"],
      "install_notes": "Extract the archive under {library_root}/.data/plugins/sample-plugin."
    }
  ]
}
```

## Status values

Catalog-level status:

- `ok`: fetched and parsed successfully.
- `disabled`: disabled by local config.
- `invalid_source`: configured URL is not an allowed GitHub URL.
- `unavailable`: GitHub could not be reached or returned an error.

Plugin-level status:

- `approved`: current approved release.
- `deprecated`: still listed for existing users but not recommended for new installs.
- `blocked`: no longer approved; do not install.

Plain English summary: Tuvima reads a GitHub JSON file to show admins which plugins are approved. The list helps discovery and review, but installing the plugin is still a manual action.
