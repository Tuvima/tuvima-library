---
title: "AI configuration reference"
description: "Reference for config/ai.json, the validated settings file that controls Local AI models and features."
audience: "administrator"
category: "reference"
product_area: "ai"
---
`config/ai.json` is validated at startup. Unsafe file names, insecure URLs, malformed checksums, invalid concurrency, and enabled roles with missing catalog entries fail fast.

- `models_directory`: managed root. Executable files resolve below explicit `llama/` or `whisper/` folders.
- `native_runtime_directory`: absolute shared native-runtime root, or `bundled` for an explicitly bundled deployment. Empty selects the OS local-application-data `Tuvima/AI Runtimes` location. `TUVIMA_AI_RUNTIME_DIR` overrides this value; changing storage paths requires restart. See [shared AI storage](../guides/shared-ai-storage.md).
- `max_concurrent_inferences`: must be `1`. The current single-resident local runtime enforces one inference at a time for every role, so each operational role also uses `max_concurrency: 1`.
- `minimum_free_disk_mb`: space retained after download.
- `models`: the five executable lifecycle definitions. File, HTTPS URL, SHA-256, context, output, temperature, GPU layers, and threads are runtime inputs.
- `model_catalog`: portfolio metadata. Capabilities, compatibility, and readiness are machine-readable gates.
- `operational_roles`: runtime-neutral text, audio, embedding, function, and multimodal bindings with bounded memory/context/output/concurrency.
- `role_requirements`: required capabilities, candidate order, experimental policy, and evaluation suite.

`configuration_ready` means artifact metadata and terms are settled. `runtime_ready` means a backend is integrated. `validated` means the named suite passed. These are independent states. The Dashboard shows disk size, memory, quantization, source, license, checksum, validation, and blocking reasons; unsupported entries expose no lifecycle action. API failures use Problem Details without raw exception messages.

Executable roles that resolve to the same managed file are one physical artifact. Their URL, size, and checksum must agree; download, verification, progress, readiness, and deletion are coordinated for every sharing role.
