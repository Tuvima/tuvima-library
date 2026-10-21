---
title: "Build a Plugin"
description: "How developers create dynamic Tuvima Library plugins and submit them to the plugin directory."
audience: "developer"
category: "guide"
product_area: "plugins"
tags:
  - "plugins"
  - "development"
  - "dotnet"
status: current
---

# Build a Plugin

## In this page

Create a local .NET plugin, package its manifest, and load it through the Engine. The guide also explains the trust boundary and how to submit a plugin to the plugin directory.

## Where this lives in the code

- `src/MediaEngine.Plugins/PluginContracts.cs`
- `src/MediaEngine.Plugins/UniverseLoreContracts.cs`
- `src/MediaEngine.Plugins/PluginManifest.cs`
- `src/MediaEngine.Api/Services/Plugins/PluginCatalog.cs`

Tuvima Library plugins are .NET class libraries loaded by the Engine from `{library_root}/.data/plugins`. A plugin ships as a folder containing a `plugin.json` manifest and the compiled assembly named by that manifest.

The current public extension surface lives in `src/MediaEngine.Plugins`.

## What a plugin can do today

The live contracts support:

- `ITuvimaPlugin` as the plugin entry point.
- `IPlaybackSegmentDetector` for playback markers such as commercial, intro, recap, and credits segments.
- `IUniverseLoreProvider` for approved supplemental universe lore such as characters, locations, factions, and relationships.
- `IPluginHealthCheck` for setup and dependency checks.
- `IPluginScheduledJob`, `IPluginEventSubscriber` and `IPluginBackgroundService` for recurring jobs, Library events and always-on work, with `Records` on the context for the plugin's own stored records (see "Host services" below).
- `IPluginSettingsSchemaProvider` for runtime settings metadata.
- Host-bound media, HTTP, storage, tool, and AI services on `IPluginExecutionContext`.

Do not depend on Web, API, Storage, Providers, Ingestion, or UI implementation types from a plugin. Use the services on the execution context for media reads, HTTP, files, processes, downloads, and AI. Those services are bound by the Engine to the plugin that is running; their methods deliberately do not accept a plugin id.

## Trust model

> **Caution:** Plugins run as trusted managed code inside the Engine process. Permission checks protect host-provided operations and prevent one plugin from claiming another plugin's manifest. They are not an operating-system sandbox for hostile assemblies. Install only reviewed plugins from sources you trust.

## Plugin quick start

1. Create the class library with the repository's .NET SDK, as shown below.
2. Reference `src/MediaEngine.Plugins/MediaEngine.Plugins.csproj` from your local checkout.
3. Add the `SamplePlugin` and `SampleHealthCheck` implementations below. The entry type needs a public parameterless constructor because the loader uses `Activator.CreateInstance`.
4. Save the JSON example as `plugin.json` beside the project file.
5. Run `dotnet build Tuvima.Plugin.Sample -c Release` from the folder containing the sample project. Keep the host's plugin-contract assembly version aligned with the project reference.
6. Copy the sample assembly, its required dependencies, and `plugin.json` into `{library_root}/.data/plugins/sample-plugin/`.
7. Restart the Engine, open **Settings > Plugins**, and enable the plugin. New plugin settings default to disabled. Confirm its health result reads `ok` before adding real capabilities.

The loader searches recursively for `plugin.json`. If `library_root` is empty, it uses `.data/plugins` relative to the Engine's working directory. It reads `entry_assembly` relative to the manifest folder and resolves the fully qualified `entry_type` from that assembly. Keep assemblies inside the plugin folder.

## Create the project

Create a class library targeting the same .NET version as Tuvima Library.

```powershell
dotnet new classlib -n Tuvima.Plugin.Sample -f net10.0
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
        Kind = PluginKind.Official,
        MinimumLibraryVersion = "1.0.0",
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
        SupportedPlatforms = [], // empty means every platform
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

The health-check capability used above is a second class in the same project:

```csharp
using MediaEngine.Plugins;

namespace Tuvima.Plugin.Sample;

public sealed class SampleHealthCheck : IPluginHealthCheck
{
    public string Kind => PluginCapabilityKinds.HealthCheckKind;

    public Task<PluginHealthResult> GetHealthAsync(
        IPluginExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PluginHealthResult
        {
            Status = "ok",
            Message = "Sample plugin loaded.",
        });
    }
}
```

## Ship `plugin.json`

Dynamic plugins must include a manifest file named `plugin.json` in the plugin folder. The manifest overrides the compiled manifest metadata for display and settings defaults.

```json
{
  "id": "example.sample-plugin",
  "name": "Sample Plugin",
  "version": "0.1.0",
  "kind": "official",
  "minimum_library_version": "1.0.0",
  "maximum_library_version": null,
  "description": "Shows the minimum plugin shape.",
  "entry_assembly": "Tuvima.Plugin.Sample.dll",
  "entry_type": "Tuvima.Plugin.Sample.SamplePlugin",
  "capabilities": [
    {
      "kind": "plugin-health-check",
      "name": "Sample health",
      "description": "Reports that the plugin loaded.",
      "fields": []
    }
  ],
  "permissions": [],
  "supported_platforms": [],
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

### Kind, versions and platforms

The Library checks these fields before a plugin can be switched on:

- `kind` is one of `official` (an in-process .NET plugin, today's contract), `declarative` (JSON only), `service` (a separate program) or `sandboxed` (WebAssembly). It defaults to `official`. All four kinds run in this version of the Library; see [Sandboxed plugins (WebAssembly)](#sandboxed-plugins-webassembly) for the WebAssembly kind.
- `minimum_library_version` (default `1.0.0`) and the optional `maximum_library_version` (inclusive; `null` or absent for no upper limit) are `major.minor.patch` versions. Any `-prerelease` or `+build` suffix on the Library version is ignored when comparing. A malformed version is a load error.
- `supported_platforms` lists runtime identifiers for an exact match (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-x64`, `osx-arm64`, and the other `x86`/`x64`/`arm`/`arm64` combinations) or an operating-system family (`windows`, `linux`, `macos`) that matches any architecture. An empty list means every platform. An unknown value is a load error that names the value.
- Each capability may list the `fields` it supplies (for example `series_position`); the list defaults to empty.

The trust level shown in Settings > Plugins is decided by the Library, never by the manifest: built-in plugins are `official`, and anything loaded from the plugins folder is `private` (sideloaded), even if its manifest says `"kind": "official"`. Verified and community levels arrive with the plugin directory and signed releases.

## JSON-only plugins

A plugin of kind `declarative` has no code at all: a `plugin.json` in its own folder under the plugins folder, with a `declarative` section. The Library does the work, so what the plugin can do is exactly what its manifest says and the administrator approved. The section may hold any of three lists, and a file that breaks a rule is refused with a sentence saying what to change (it shows on the plugin's card as the problem).

```json
{
  "id": "com.example.channels",
  "name": "Example channels",
  "kind": "declarative",
  "permissions": ["network.http", "scheduled-work", "plugin-records", "ai.infer"],
  "network": [{ "host": "lists.example.com", "sends": "Nothing; it only downloads the list" }],
  "ai_permissions": [{ "role": "text_fast", "max_tokens": 200, "schedule": "on-demand", "resource_class": "ai" }],
  "declarative": {
    "channel_sources": [
      { "key": "main", "name": "Main channels", "format": "m3u", "url": "https://lists.example.com/a.m3u", "refresh": "6h" }
    ],
    "ai_steps": [
      { "key": "summarise", "name": "Summarise", "role": "text_fast", "prompt": "Summarise this in one sentence: {text}", "max_tokens": 120 }
    ]
  }
}
```

- **`channel_sources`** fetch a channel list (`m3u`) or programme guide (`xmltv`) from an `https://` address on a schedule (`refresh` is a cron text or an interval of at least five minutes; the default is every six hours) and keep the latest result as the plugin's own record in the `channels` collection. The address must be listed under `network`, and the plugin must ask for `network.http`, `scheduled-work` and `plugin-records`. Each source shows in the plugin's jobs as `channels-{key}`.
- **`ai_steps`** send a prompt to Local AI as the plugin. `{name}` placeholders are filled from the inputs given when the step is run (`POST /plugins/{pluginId}/ai-steps/{stepKey}/run` with `{ "inputs": { "text": "..." } }`). The plugin must ask for `ai.infer` and list the step's `role` under `ai_permissions`; the answer comes back as `status` (`succeeded`, `refused`, `ai_unavailable`, `busy`, `over_allowance`, `timed_out`, `invalid_answer` or `failed`) so a caller can carry on without AI. Add `json_grammar` to require a JSON answer.
- **`metadata_sources`** hold a provider in the same format as the files in `config/providers/` (it needs a `name`, a JSON `adapter_type` such as `config_driven` or `text_track`, and `https://` endpoints). They are checked and listed now; the Library starts using them when providers move under Plugins.

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

Restart the Engine, open **Settings > Plugins**, enable the plugin, and confirm it loads without a setup warning. The loader enforces `kind`, `minimum_library_version`, `maximum_library_version` and `supported_platforms`: a plugin outside its supported range, on an unsupported platform or of a kind this version cannot run is refused when you try to switch it on (the Engine answers `409 Conflict` with the reason), and one that was saved as enabled before the Library changed is treated as off, with the reason shown in Settings > Plugins. These checks are not an isolation guarantee: an `official` plugin still runs inside the Engine with the Engine's trust. Test the release against the actual host version and platform.

## Use permissions honestly

Declare only what the plugin needs.

Common permissions:

- `media.read`: reads media file metadata or streams.
- `network.http`: makes outbound HTTP requests through the bound HTTP client.
- `process.execute`: runs a local tool through `IPluginToolRuntime`.
- `tool.download`: allows tool auto-install when a checksum-pinned download is declared.
- `ai.infer`: calls local AI through `IPluginAiClient`.
- `storage.plugin`: reads or writes inside the invocation's plugin working directory.
- `secrets.plugin`: reads the keys and logins the plugin declared in `secrets` (see below).
- `plugin-records`: keeps the plugin's own records in the Library's data store (`context.Records`).
- `scheduled-work`: runs the plugin's own recurring jobs (`IPluginScheduledJob`), listed in Scheduled tasks.
- `library-events`: is told when things happen in the Library (`IPluginEventSubscriber`); covers every event except `playback.*`.
- `watch-activity`: is told what people watch, read and listen to; needed for the `playback.*` events.
- `background-work`: runs continuously in the background while switched on (`IPluginBackgroundService`).
- `plugin-pages`: adds pages and menu entries to the Library, drawn in the Library's own page types from the plugin's records (see Pages and menu entries).

The Engine checks the live enabled state and the exact manifest permission again immediately before each host operation. Disabling a plugin therefore revokes an already-created context. Tool resolution accepts a declared tool id, and execution accepts only a resolution returned to that same context. Working and storage paths must remain inside the plugin directory.

### Say where the plugin connects

A plugin that declares `network.http` must also list every place it sends data in `network`. Each entry has:

- `host` (required): an exact host such as `api.example.com`, or one leading wildcard label such as `*.fandom.com`. No `https://`, path, port or bare `*`.
- `sends` (required): one plain sentence, 1 to 200 characters, saying what is sent there, for example "Searches for character and place names". Write it for a person deciding whether to switch the plugin on.
- `max_requests_per_minute` (optional): a whole number from 1 to 600 asking for a rate cap for that host.

```json
"permissions": ["network.http"],
"network": [
  { "host": "www.wikidata.org", "sends": "Wikidata item IDs of Universes already in your Library", "max_requests_per_minute": 60 },
  { "host": "*.fandom.com", "sends": "Requests for the page list of a wiki you approved", "max_requests_per_minute": 60 }
]
```

The Library refuses to load a manifest that declares `network.http` with no destinations, lists destinations without `network.http`, repeats a host, or has a malformed host, and says why in Settings > Plugins. The destinations are part of what a person approves: the approval step and the plugin's card show one "Contacts host: what it sends" line per entry, so adding or changing a destination (a host or its `sends` sentence) asks for approval again. The plugin's web connection (`context.Http`) enforces this list: a request, or a redirect, to a host that is not listed is refused with a plain message and shows as the plugin's last problem. `*.example.com` covers subdomains only, never `example.com` itself. Each listed host also has a requests-a-minute limit (`max_requests_per_minute`, or 60 a minute when you leave it out); a request that would have to wait more than 30 seconds for its turn is refused with "Hit its limit of N requests a minute to host", and the plugin shows Needs attention while that keeps happening. Redirects are followed only for GET and HEAD requests, at most five times, and never from `https` to `http`; a login header is not carried to a different host.

If a plugin uses tools, declare each `tool_requirements` entry with the executable name, license, source URL, supported platforms, download URL, SHA-256, and relative executable path when auto-install is supported.

If a plugin needs a key or login, declare each one in `secrets` (`name`, `label`, `description`) and request the `secrets.plugin` permission. An administrator enters the values through the `PUT /plugins/{pluginId}/secrets/{name}` action; the plugin reads one with `await context.Secrets.GetAsync("name")`, which returns null when the name was not declared or has no value. Plugins cannot write or list secrets, never see another plugin's secrets, and must not log what they read. Secrets are kept in the config folder (`config/secrets/plugins/{plugin-id}.json`), so they survive a data store reset and are left out of backups.

```json
"permissions": ["secrets.plugin"],
"secrets": [
  { "name": "api_token", "label": "API token", "description": "Create one in the service's account settings." }
]
```

If a plugin uses local AI, declare `ai_permissions` with role, token limit, schedule, and resource class. The AI client checks these declarations before running inference.

## Pages and menu entries

A plugin adds pages by listing them under `pages` in `plugin.json`. It needs the `plugin-pages` and `plugin-records` permissions. The plugin never ships screens: it picks one of the Library's own page types and supplies data, and the Dashboard draws the page like any built-in page. The data is the plugin's own records (`context.Records`), so a plugin updates a page by writing records.

```json
"pages": [
  {
    "key": "channels",
    "title": "Channels",
    "type": "list",
    "audience": "everyone",
    "menu": { "label": "Channels" },
    "collection": "channels",
    "fields": { "title": "name", "subtitle": "network" },
    "columns": [ { "key": "number", "label": "Number" } ]
  }
]
```

| Type | Shows | Notes |
|---|---|---|
| `list` | one row per record, with the declared `columns` | works on every device |
| `grid` | one tile per record (title, subtitle, picture) | works on every device |
| `guide` | a timeline: one row per `group` (a channel), programmes placed by `starts_at` and `ends_at` | not offered on phones; records without a valid time range are skipped |
| `form` | the declared `form.fields` (`text`, `number`, `yes_no`) and a submit button | each submission is saved as a record named `submission-...` in the collection `form-<key>` (always; a form cannot write elsewhere), for the plugin to read. A form keeps at most 1,000 submissions |
| `detail` | the labels and values of one record (`record`, default `main`) | works on every device |

- `fields` maps the page's slots (`title`, `subtitle`, `image`, `group`, `starts_at`, `ends_at`) to names in the record JSON; the defaults are `title`, `subtitle`, `image_url`, `group`, `starts_at`, `ends_at`.
- `menu.label` adds a menu entry (up to 40 characters). A page without `menu` is reachable only by its address, `/plugin/<plugin id>/<page key>`.
- `audience` is `administrators` (the default), `adults` (every profile except child profiles) or `everyone`. A page a person may not see answers exactly like a page that does not exist.
- Pictures show only from `https` addresses on a host the plugin listed in `network`.
- Who sees each page is part of the approval: the approval lists every page with its audience, so widening an audience asks the administrator again.
- A page shows up to 500 records. A plugin that is switched off, not approved, or without the permissions shows nothing.

## Host services

A plugin that declares the permission can use these services. Each is bounded by limits the Library enforces; none is a setting. A plugin that goes over a limit is stopped or refused and its card in **Settings > Plugins** shows Needs attention (or Broken for always-on work that stopped for good) with the reason.

**Its own records** (`plugin-records`). `context.Records` stores JSON documents by collection and key (`GetAsync`, `PutAsync`, `DeleteAsync`, `ListAsync`). Records belong to the plugin: it cannot name another plugin and cannot read another's records. Names are 1 to 128 printable characters. Limits: 64 KB for a value, 10,000 records and 16 MB in total. A write over a limit throws `PluginLimitExceededException` with a plain message and the card says "Reached its storage limit (16 MB)". Records survive an Engine restart.

**Recurring jobs** (`scheduled-work`). Return `IPluginScheduledJob` capabilities: a `Key` (lower-case letters, digits, `-` and `_`), a `DisplayName`, a `DefaultSchedule` (`PluginSchedule.Every(...)`, at least five minutes, or `PluginSchedule.OnCron("0 3 * * *")`) and `RunAsync`. Every run is a run record, listed in **System > Tools and requirements > Scheduled tasks** as "Plugin name: Job name". An administrator can override a job's schedule by hand in the plugin's configuration file (`{library_root}/.data/plugin-config/{plugin-id}.json`) with a `schedules` object that maps the job key to a cron expression or an interval such as `30m`, `2h` or `1d`; there is no screen for it. A job never overlaps itself, at most two plugin jobs run at once across the Engine, and a run longer than ten minutes is cancelled and recorded as failed ("Stopped: ran longer than 10 minutes"). After three of those in a row the job pauses and the card says so until the plugin is switched off and on.

**Library events** (`library-events`, plus `watch-activity` for `playback.*`). Return an `IPluginEventSubscriber` with the `EventTypes` it wants and `HandleAsync`. The events are the same ones webhooks use (the list in `ApplicationEventRegistry`), delivered with the same envelope JSON in `PluginEvent.PayloadJson`. A type the Library does not have is a load error. A new subscriber starts at the newest event, with no backlog, and carries on where it stopped after a restart. Each event is tried once with a 30-second limit; five failures in a row pause the subscription ("Stopped receiving events after 5 failures") until the plugin is switched off and on.

**Always-on work** (`background-work`). Return an `IPluginBackgroundService`; `RunAsync` should run until its token is cancelled. The Library starts it when the plugin is switched on and approved and stops it (cancel, then ten seconds' grace) when the plugin is switched off, loses its approval, or the Engine stops. If it crashes or returns early it restarts after 5 seconds, then 30 seconds, 2 minutes and 10 minutes; the fifth crash inside an hour stops it for good and the card shows Broken ("Stopped after crashing 5 times in an hour") until the plugin is switched off and on. The context passed to `RunAsync` stays usable for the whole run and is cleaned up when the run ends.

## Plugins that are separate programs

A plugin can be its own program in any language (Python, Go, Node, a compiled binary). Set `"kind": "service"` and describe how to run it in a `service` section. The Library starts the program when the plugin is switched on, checks it is healthy every few seconds, restarts it if it stops, and stops it when the plugin is switched off. A working Python example is in [`samples/plugins/python-lookup`](https://github.com/Tuvima/tuvima-library/tree/main/samples/plugins/python-lookup).

```json
{
  "id": "example.python-lookup",
  "name": "Python lookup",
  "version": "1.0.0",
  "kind": "service",
  "permissions": ["process.execute", "plugin-records"],
  "service": {
    "command": "python3",
    "arguments": ["server.py"],
    "health_path": "/health",
    "lookup_path": "/lookup",
    "max_memory_mb": 256,
    "startup_seconds": 20
  }
}
```

- **`command`** is one program name found on the machine, or a path inside the plugin's own folder, with `arguments` listed separately. There is no shell. It needs the `process.execute` permission, and the approval shows the exact command. To connect to a program that is already running instead, give an `address` (a program on this machine needs no extra permission; one on another machine needs `network.http` and the host listed in `network`).
- **What your program receives**: `TUVIMA_PLUGIN_PORT` (listen on `127.0.0.1` at this port), `TUVIMA_PLUGIN_KEY` (this plugin's own key), `TUVIMA_LIBRARY_URL` (where the Library is) and `TUVIMA_PLUGIN_ID`.
- **What it must answer**: `GET {health_path}` with a 200 when healthy, and `GET {lookup_path}?title=...` with JSON. The Library sends the key it gave your program in the `X-Tuvima-Plugin-Key` header; check it so only the Library can ask.
- **What it may ask the Library**: the plugin door, published as an OpenAPI description in [`plugin-host-v1.openapi.json`](../reference/plugin-host-v1.openapi.json). Send your key in `X-Tuvima-Plugin-Key`. The Library answers only for what the plugin was approved to do (a call outside the permissions is refused: a 403, or for Local AI a plain `refused` status), and only programs on the same machine.
- **Limits and restarts**: a program that exits, stops answering, or grows past `max_memory_mb` is restarted after a growing pause (5 seconds, 30 seconds, 2 minutes, 10 minutes). The fifth crash within an hour stops it and the plugin shows Broken until it is switched off and on. The key changes on every start and ends when the program stops.
- **What it is not**: the program runs with the same access to this machine as the Library, so the approval says "only turn on plugins you trust". The Library controls what the program may ask of the Library and gives it a small environment (no Engine settings or keys), not what it does on its own.
- **Where it runs**: the memory limit is checked by the Library for the main program process; hard operating-system limits and per-system builds are the author's choice (ship a `supported_platforms` list and one folder per system, or run the program in a container and use `address`).

## Sandboxed plugins (WebAssembly)

A plugin can be a WebAssembly module (written in Rust, C, Go, AssemblyScript or anything that compiles to WebAssembly). Set `"kind": "sandboxed"`, ask for `scheduled-work`, and describe the module in a `sandbox` section. The Library runs the module inside a sandbox on every platform it supports (Windows, Linux and the container image), with hard limits and no way out except the Library's own plugin services.

```json
{
  "id": "acme.reading-list",
  "name": "Reading list",
  "version": "1.0.0",
  "kind": "sandboxed",
  "permissions": ["scheduled-work", "plugin-records", "network.http"],
  "network": [{ "host": "api.example.com", "sends": "The titles on your shelf" }],
  "sandbox": {
    "module": "plugin.wasm",
    "jobs": [{ "key": "refresh", "name": "Refresh the reading list", "schedule": "6h" }],
    "max_memory_mb": 64,
    "max_seconds": 10
  }
}
```

The module must export `memory`, `alloc(length) -> address`, and one function per job (default name `run`, or the job's `export`) that takes `(address, length)` and returns an `i64`. The Library writes a JSON input into the module's memory (`plugin_id`, `job`, `settings`) and calls the function. The function returns `(result_address << 32) | result_length`, or `0` for no result. The result is UTF-8 text, at most 1 MB. If it is JSON with a `records` list of `{ "collection", "key", "json" }`, the Library saves them as the plugin's own records (needs `plugin-records`). The last run, with its log lines, is kept as the record `sandbox/<job key>`.

The module can import exactly two functions from the module name `library`: `log(address, length)` writes a line to the run's log, and `http_get(address, length) -> i64` fetches a web address and returns the answer the same packed way (it calls `alloc` to place it). `http_get` goes through the same checked network access as every other plugin: it needs `network.http`, only reaches hosts listed under `network`, and answers `0` when refused. There is no file system, no clock, no random numbers from the machine, no programs and no environment; a module that imports anything else does not start.

Limits, per run: memory (`max_memory_mb`, 1 to 512, default 64), time by the clock (`max_seconds`, 1 to 60, default 10) and work (`max_steps`, counted in WebAssembly instructions, default 1,000,000,000). A run that goes over is stopped and the Scheduled tasks entry shows a plain message such as "The plugin ran for more than 10 seconds and was stopped." Nothing carries over from one run to the next; keep state in records. The module file must be inside the plugin's folder and at most 16 MB. A `.wat` text file of up to 512 KB is also accepted, which is handy for small modules and tests. Only two modules run at the same time across the whole Library.

## Universe lore plugins

Universe lore plugins supplement Wikidata; they do not replace it. The Engine gives an `IUniverseLoreProvider` a Wikidata-backed universe QID and settings, the plugin can propose external lore sources, and admins approve or reject those sources in Chronicle Explorer before any data is imported. Approved data is stored as supplemental plugin lore and can be requested by the graph API as an overlay.

Use this shape when a source has useful structured lore but should stay visibly separate from canonical Wikidata facts:

- `DiscoverSourcesAsync` proposes source candidates for admin review.
- `EnrichUniverseAsync` reads only approved sources.
- Extracted entities should include source URLs, confidence, evidence, and a stable external key.
- Article prose should not be copied into Tuvima. Store structured metadata, relationships, and links back to the source.

## Submit your plugin

Plugins reach the Library's list of available plugins (**Settings > Plugins > Available plugins**) through a short, public review.

1. Publish a GitHub release that includes:
   - A versioned archive containing `plugin.json` and the plugin's files.
   - A SHA-256 checksum for the archive.
   - Your source repository URL and the release URL.
   - The Library versions it works with (`minimum_library_version`, and `maximum_library_version` if there is a limit).
   - The permissions it declares and the capabilities it provides.
   - License information for the plugin and for any bundled or downloaded tools.
2. Propose a change to the list file, [`docs/reference/approved-plugins.json`](../reference/approved-plugins.json), on GitHub. Add one entry in the [plugin directory format](../reference/plugin-catalog.md): `id`, `name`, `author`, `description`, `version`, `kind`, `permissions`, `capabilities`, `minimum_library_version`, `repository_url`, `release_url`, `package_url`, `sha256` and `added_at`. Use a real archive checksum, not the illustrative placeholder.
3. Tuvima reviews the proposed change. It is listed as **Not verified**: Tuvima has not checked the plugin, and the entry says so beside it.
4. The **Verified** mark is added only after Tuvima has checked the plugin and what it asks for. You cannot set it yourself; an entry that claims it is treated as a request until the review is done. Plugins built by Tuvima (**Official**) ship in the main repository and are not listed here.

The list is read-only: being listed does not install anything, and listing does not sandbox the plugin's code.

Plain English summary: A plugin is a small .NET package with a manifest and clear settings metadata. Developers implement the plugin contracts, package the assembly and `plugin.json` together, test it from the library `.data/plugins` folder, and publish a checksummed GitHub release and propose it for the plugin directory.
