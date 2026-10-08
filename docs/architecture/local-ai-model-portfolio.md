---
title: "Local AI model portfolio"
description: "Understand the local AI model catalog, role boundaries, runtime compatibility and promotion gates."
audience: developer
category: architecture
product_area: ai
status: current
---

# Local AI model portfolio

## In this page

Understand the local AI model catalog, role boundaries, runtime compatibility and promotion gates.

## Where this lives in the code

- `config/ai.json`
- `src/MediaEngine.AI/Configuration/AiResourceProfileCatalog.cs`
- `src/MediaEngine.AI/Configuration/AiModelCatalogDefaults.cs`
- `src/MediaEngine.AI/Configuration/AiSettings.cs`
- `src/MediaEngine.AI`

Tuvima Library separates a model artifact, its operational role, and the product feature using that role. This prevents an embedding model from becoming a chat model and keeps experimental runtimes out of the production GGUF lifecycle.

| Resource profile / pack | Supported artifact | Declared download | Memory envelope | Role use |
|---|---|---:|---:|---|
| Essential | Qwen3 0.6B Q8_0 | 639 MB | 1,024 MB | All four logical text roles |
| Standard | Qwen3 1.7B Q5_K_M | 1,260 MB | 2,048 MB | All four logical text roles |
| Advanced | Qwen3 4B Q4_K_M | 2,500 MB | 4,096 MB | All four logical text roles |
| Optional audio pack | Whisper Medium | 1,500 MB | 2,048 MB | `audio` only |

`AiResourceProfileCatalog` owns the selected artifact and clones its definition into `text_fast`, `text_quality`, `text_scholar`, and `text_cjk`. The roles have different context/output budgets while sharing the same file, checksum, and download. The committed profile is Standard; hardware eligibility can lower the effective profile. The audio pack is disabled in the committed configuration.

`AiModelCatalogDefaults` contains only these supported launch entries. EmbeddingGemma, FunctionGemma, Gemma multimodal, alternative Whisper models, and separate embedding/function/multimodal roles from earlier portfolio designs are not current selectable catalog entries. Such experiments require explicit implementation and validation before promotion.

Sources of runtime truth are `src/MediaEngine.AI/Configuration/AiResourceProfileCatalog.cs`, `AiModelCatalogDefaults.cs`, and `AiSettings.cs`. Model provenance URLs, checksums, capabilities, and validation objectives are recorded there.

The code-owned catalog records provenance, license, checksum, capabilities, compatibility, and gates. Code-owned role requirements record validation objectives. `Models`, `ModelCatalog`, and `RoleRequirements` are JSON-ignored runtime properties; `config/ai.json` selects the resource profile and optional pack rather than redefining them. Automatically downloadable executable artifacts are SHA-256 pinned.
