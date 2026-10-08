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

`config/ai.json` is validated at startup. Unsafe file names, insecure URLs, malformed checksums, invalid concurrency, and enabled roles with missing catalog entries fail fast.

- `models_directory`: managed root. Executable files resolve below explicit `llama/` or `whisper/` folders.
- `native_runtime_directory`: absolute shared native-runtime root, or `bundled` for an explicitly bundled deployment. Empty selects the OS local-application-data `Tuvima/AI Runtimes` location. `TUVIMA_AI_RUNTIME_DIR` overrides this value; changing storage paths requires restart. See [shared AI storage](../guides/shared-ai-storage.md).
- `max_concurrent_inferences`: must be `1`. The single-resident local runtime serializes inference for every logical role.
- `resource_profile`: `essential`, `standard`, or `advanced`; committed default `standard`. Hardware eligibility determines the effective profile. All four logical text roles share its artifact.
- `audio_pack_enabled`: optional Whisper Medium lifecycle; committed default `false`.
- `features`: individual text feature enable flags; all six committed flags are `false`.
- `minimum_free_disk_mb`: space retained after download.
The runtime's `Models`, `ModelCatalog`, and `RoleRequirements` properties are code-owned and JSON-ignored. They are not supported artifact overrides in `config/ai.json`. The launch catalog contains three Qwen text resource profiles and the optional Whisper Medium pack; earlier embedding, function, multimodal, and alternative-ASR proposals are not selectable entries. See [the portfolio](../architecture/local-ai-model-portfolio.md) for sizes and ownership.

`configuration_ready` means artifact metadata and terms are settled. `runtime_ready` means a backend is integrated. `validated` means the named suite passed. These are independent states. The Dashboard shows disk size, memory, quantization, source, license, checksum, validation, and blocking reasons; unsupported entries expose no lifecycle action. API failures use Problem Details without raw exception messages.

Executable roles that resolve to the same managed file are one physical artifact. Their URL, size, and checksum must agree; download, verification, progress, readiness, and deletion are coordinated for every sharing role.
