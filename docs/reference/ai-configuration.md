---
title: "AI configuration reference"
description: "Look up local AI storage, model catalog, operational role and safety validation settings."
audience: administrator
category: reference
product_area: ai
status: current
---

# AI configuration reference

## In this page

Look up local AI storage, model catalog, operational role and safety validation settings.

## Where this lives in the code

- `config/ai.json`
- `src/MediaEngine.Storage/ConfigurationDirectoryLoader.cs`
- `src/MediaEngine.AI/Configuration/AiSettings.cs`
- `src/MediaEngine.AI/Configuration/AiResourceProfileCatalog.cs`
- `src/MediaEngine.AI/Configuration/AiModelCatalogDefaults.cs`

`config/ai.json` is validated at startup. Unsafe file names, insecure URLs, malformed checksums, out-of-range plugin budgets, and enabled roles with missing catalog entries fail fast.

- `models_directory`: managed root. Executable files resolve below explicit `llama/` or `whisper/` folders.
- `native_runtime_directory`: absolute shared native-runtime root, or `bundled` for an explicitly bundled deployment. Empty selects the OS local-application-data `Tuvima/AI Runtimes` location. `TUVIMA_AI_RUNTIME_DIR` overrides this value; changing storage paths requires restart. See [shared AI storage](../guides/shared-ai-storage.md).
- There is no "max at once" setting: the text model runs one request at a time and the Local AI engine room keeps one queue per engine (a person first, then ingestion, then the Library's background work, then plugins).
- `resource_profile`: `essential`, `standard`, or `advanced`; committed default `standard`. Hardware eligibility determines the effective profile. All four logical text roles share its artifact.
- `audio_pack_enabled`: optional Whisper Medium lifecycle; committed default `false`.
- `features`: individual text feature enable flags; all six committed flags are `false`.
- `minimum_free_disk_mb`: space retained after download.
- `plugin_budget`: how much Local AI each plugin may use. `requests_per_hour` (default `120`, 1 to 100000) and `busy_seconds_per_hour` (default `600`, 1 to 3600) are counted over the last rolling hour from the durable usage records, so they survive an Engine restart; a request over either limit is refused without running, and the plugin shows Needs attention with "Used its Local AI allowance for this hour". `max_wait_seconds` (default `120`, 1 to 3600) is how long a plugin request may wait for its turn behind other work before it times out. Settings > Local AI > Budget edits these three numbers; Settings > Local AI > Usage shows each plugin against them.
- `plugin_budget_overrides`: optional, keyed by plugin id; each entry may set any of the three fields above and the rest keep the defaults.
The runtime's `Models`, `ModelCatalog`, and `RoleRequirements` properties are code-owned and JSON-ignored. They are not supported artifact overrides in `config/ai.json`. The launch catalog contains three Qwen text resource profiles and the optional Whisper Medium pack; earlier embedding, function, multimodal, and alternative-ASR proposals are not selectable entries. See [the portfolio](../architecture/local-ai-model-portfolio.md) for sizes and ownership.

`configuration_ready` means the model definition is complete (it names a file and a download address). `runtime_ready` means the native AI runtime really loads on this machine (checked, cached, and checked again when tools are installed or refreshed). `validated` means the model file is on disk and verified. These are independent states read from this machine, never assumed; `GET /ai/status` reports ready only when the text model file is in place and the runtime loads, and the optional audio pack never decides it. The Dashboard shows disk size, memory, quantization, source, license, checksum, validation, and blocking reasons; unsupported entries expose no lifecycle action. API failures use Problem Details without raw exception messages.

Executable roles that resolve to the same managed file are one physical artifact. Their URL, size, and checksum must agree; download, verification, progress, readiness, and deletion are coordinated for every sharing role.
