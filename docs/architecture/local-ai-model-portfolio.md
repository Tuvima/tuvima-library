---
title: "Local AI model portfolio"
description: "Tuvima Library separates a model artifact, its operational role, and the product feature using that role."
audience: "developer"
category: "architecture"
product_area: "ai"
---
Tuvima Library separates a model artifact, its operational role, and the product feature using that role. This prevents an embedding model from becoming a chat model and keeps experimental runtimes out of the production GGUF lifecycle.

| Role | Default candidate | Envelope | Promotion suite |
|---|---|---:|---|
| `text_fast` | Qwen3 0.6B Q8 | 1 GB / 4K | `text_instant` |
| `text_quality` | Qwen3 1.7B Q5 | 2 GB / 8K | `text_ingestion` |
| `text_scholar` | Qwen3 4B Q4 | 4 GB / 16K | `text_enrichment` |
| `text_cjk` | Qwen3 4B Q4 | 4 GB / 8K | `text_multilingual` |
| `embedding_search` | EmbeddingGemma 300M | 1.5 GB / 2K | `embedding_retrieval` |
| `function_routing` | FunctionGemma 270M | 1 GB / 4K | `function_routing` |
| `multimodal_analysis` | Gemma 4 E2B | 12 GB / 32K | `multimodal_analysis` |
| `audio_fast` | Whisper small | 768 MB | `audio_fast` |
| `audio_english` | Distil-Whisper large-v3 | 2 GB | `audio_english` |
| `audio_multilingual` | Whisper large-v3-turbo | 2 GB | `audio_multilingual` |
| `audio_translation` | Whisper medium | 2 GB | `audio_translation` |

The Qwen ladder is small-first. EmbeddingGemma is a separate vector capability. FunctionGemma is experimental and not a general dialogue model. Gemma 4 E2B is an experimental Safetensors text/image/audio candidate, not an LLamaSharp GGUF model. Turbo performs source-language transcription; Whisper medium remains the speech-to-English translation baseline.

Sources: [Qwen3 GGUF](https://huggingface.co/Qwen/Qwen3-0.6B-GGUF), [EmbeddingGemma](https://huggingface.co/google/embeddinggemma-300m), [FunctionGemma](https://huggingface.co/google/functiongemma-270m-it), [Gemma 4 E2B](https://huggingface.co/google/gemma-4-E2B), [Whisper](https://github.com/openai/whisper), and [Distil-Whisper](https://huggingface.co/distil-whisper/distil-large-v3).

`model_catalog` owns provenance, license, checksum, capabilities, compatibility, and gates. `operational_roles` owns workload envelopes. `role_requirements` owns objective promotion policy. The enum-backed `models` section remains the executable bridge for currently integrated LLamaSharp/Whisper roles. Automatically downloadable executable artifacts are SHA-256 pinned; gated Google artifacts have no automatic download until an administrator accepts their terms and installs a verified artifact.
