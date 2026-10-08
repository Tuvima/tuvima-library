---
title: "Build a Plugin"
description: "How developers create dynamic Tuvima Library plugins and submit them for the approved catalog."
audience: "developer"
category: "guide"
product_area: "plugins"
tags:
  - "plugins"
  - "development"
  - "dotnet"
---
Tuvima Library plugins are .NET class libraries loaded by the Engine from `{library_root}/.data/plugins`. A plugin ships as a folder containing a `plugin.json` manifest and the compiled assembly named by that manifest.

The current public extension surface lives in `src/MediaEngine.Plugins`.

## What a plugin can do today

The live contracts support:

- `ITuvimaPlugin` as the plugin entry point.
- `IPlaybackSegmentDetector` for playback markers such as commercial, intro, recap, and credits segments.
- `IUniverseLoreProvider` for approved supplemental universe lore such as characters, locations, factions, and relationships.
- `IPluginHealthCheck` for setup and dependency checks.
- `IPluginJob` for plugin-owned work.
- `IPluginSettingsSchemaProvider` for runtime settings metadata.
- Host-bound media, HTTP, storage, tool, and AI services on `IPluginExecutionContext`.

Do not depend on Web, API, Storage, Providers, Ingestion, or UI implementation types from a plugin. Use the services on the execution context for media reads, HTTP, files, processes, downloads, and AI. Those services are bound by the Engine to the plugin that is running; their methods deliberately do not accept a plugin id.

Plugins run as trusted managed code inside the Engine process. Permission checks protect host-provided operations and prevent one plugin from claiming another plugin's manifest. They are not an operating-system sandbox for hostile assemblies. Install only reviewed plugins from sources you trust.

## Create the project

Create a class library targeting the same .NET version as Tuvima Library.

```powershell
dotnet new classlib -n Tuvima.Plugin.Sample
dotnet add Tuvima.Plugin.Sample reference path\to\tuvima-library\src\MediaEngine.Plugins\MediaEngine.Plugins.csproj
```

For plugins published outside the main repo, reference a versioned package of `MediaEngine.Plugins` when one is available. During local development, a project reference is fine.

## Implement `ITuvimaPlugin`

```csharp
using System.Text.Json;
using MediaEngine.Plugins;

namespace Tuvima.Plugin.Sample;

public sealed class SamplePlugin : ITuvimaPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = "example.sample-plugin",
        Name = "Sample Plugin",
        Version = "0.1.0",
        MinimumTuvimaApiVersion = "1.0.0",
        Description = "Shows the minimum plugin shape.",
        Capabilities =
        [
            new PluginCapabilityDescriptor
            {
                Kind = "plugin-health-check",
                Name = "Sample health",
                Description = "Reports that the plugin loaded.",
            },
        ],
        Permissions = [],
        SupportedPlatforms = ["win-x64", "linux-x64", "osx-x64", "osx-arm64"],
        DefaultSettings = new Dictionary<string, JsonElement>
        {
            ["enabled_feature"] = JsonSerializer.SerializeToElement(true),
        },
    };

    public IReadOnlyList<IPluginCapability> CreateCapabilities() =>
    [
        new SampleHealthCheck(),
    ];
}
```

## Ship `plugin.json`

Dynamic plugins must include a manifest file named `plugin.json` in the plugin folder. The manifest overrides the compiled manifest metadata for display and settings defaults.

```json
{
  "id": "example.sample-plugin",
  "name": "Sample Plugin",
  "version": "0.1.0",
  "minimum_tuvima_api_version": "1.0.0",
  "description": "Shows the minimum plugin shape.",
  "entry_assembly": "Tuvima.Plugin.Sample.dll",
  "entry_type": "Tuvima.Plugin.Sample.SamplePlugin",
  "capabilities": [
    {
      "kind": "plugin-health-check",
      "name": "Sample health",
      "description": "Reports that the plugin loaded."
    }
  ],
  "permissions": [],
  "supported_platforms": ["win-x64", "linux-x64", "osx-x64", "osx-arm64"],
  "default_settings": {
    "enabled_feature": true
  },
  "settings_schema": {
    "properties": {
      "enabled_feature": {
        "type": "boolean",
        "title": "Enable Feature",
        "description": "Turns on the sample feature."
      }
    },
    "required": ["enabled_feature"]
  }
}
```

Required dynamic plugin fields:

- `id`
- `name`
- `version`
- `entry_assembly`
- `entry_type`

Use reverse-DNS style ids when possible, such as `com.example.sample-plugin`.

## Expose settings clearly

Admins configure plugins through typed fields in **Settings > Plugins**. They do not edit raw settings JSON or manifest JSON from the Dashboard. Put every admin-facing option in `default_settings`, then describe it with `settings_schema` in `plugin.json` or implement `IPluginSettingsSchemaProvider` when the schema has to be computed at runtime.

The Dashboard understands a small JSON-schema-style subset:

- `properties` with one object per setting key.
- `title` and `description` for field labels and help text.
- `type` values such as `boolean`, `string`, `integer`, `number`, `array`, and `object`.
- `enum` for select controls.
- `default`, `minimum`, `maximum`, `format: "password"`, `secret: true`, and `advanced: true`.

The Engine validates typed saves against the schema when one exists. Keep manifest metadata focused on packaging and capability declarations; keep user-adjustable behavior in settings.

## Package locally

Build the plugin, then copy the output and manifest into one folder:

```text
sample-plugin/
  plugin.json
  Tuvima.Plugin.Sample.dll
  Tuvima.Plugin.Sample.deps.json
  other-required-files.dll
```

Place the folder under:

```text
{library_root}/.data/plugins/sample-plugin/
```

Restart the Engine, open **Settings > Plugins**, and confirm the plugin loads without a setup warning.

## Use permissions honestly

Declare only what the plugin needs.

Common permissions:

- `media.read`: reads media file metadata or streams.
- `network.http`: makes outbound HTTP requests through the bound HTTP client.
- `process.execute`: runs a local tool through `IPluginToolRuntime`.
- `tool.download`: allows tool auto-install when a checksum-pinned download is declared.
- `ai.infer`: calls local AI through `IPluginAiClient`.
- `storage.plugin`: reads or writes inside the invocation's plugin working directory.

The Engine checks the live enabled state and the exact manifest permission again immediately before each host operation. Disabling a plugin therefore revokes an already-created context. Tool resolution accepts a declared tool id, and execution accepts only a resolution returned to that same context. Working and storage paths must remain inside the plugin directory.

If a plugin uses tools, declare each `tool_requirements` entry with the executable name, license, source URL, supported platforms, download URL, SHA-256, and relative executable path when auto-install is supported.

If a plugin uses local AI, declare `ai_permissions` with role, token limit, schedule, and resource class. The AI client checks these declarations before running inference.

## Universe lore plugins

Universe lore plugins supplement Wikidata; they do not replace it. The Engine gives an `IUniverseLoreProvider` a Wikidata-backed universe QID and settings, the plugin can propose external lore sources, and admins approve or reject those sources in Chronicle Explorer before any data is imported. Approved data is stored as supplemental plugin lore and can be requested by the graph API as an overlay.

Use this shape when a source has useful structured lore but should stay visibly separate from canonical Wikidata facts:

- `DiscoverSourcesAsync` proposes source candidates for admin review.
- `EnrichUniverseAsync` reads only approved sources.
- Extracted entities should include source URLs, confidence, evidence, and a stable external key.
- Article prose should not be copied into Tuvima. Store structured metadata, relationships, and links back to the source.

## Submit for the approved catalog

To be listed as an approved plugin, publish a GitHub release that includes:

- A versioned archive containing `plugin.json` and compiled files.
- A SHA-256 checksum for the archive.
- Source repository URL.
- Release URL.
- Minimum Tuvima API version.
- Capability list.
- Install notes and tool requirements.
- License information for plugin code and bundled or downloaded tools.

Maintainers can then add the release to `docs/reference/approved-plugins.json`.

Plain English summary: A plugin is a small .NET package with a manifest and clear settings metadata. Developers implement the plugin contracts, package the assembly and `plugin.json` together, test it from the library `.data/plugins` folder, and publish a checksummed GitHub release for catalog approval.
