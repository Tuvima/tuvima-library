---
title: "Local AI Intelligence Layer"
description: "Deep technical documentation for model orchestration, prompts, hardware tiers, and AI service boundaries."
audience: "developer"
category: "architecture"
product_area: "ai"
tags:
  - "ai"
  - "architecture"
  - "models"
---
## Role in the System

AI is a core function of Tuvima Library, not an optional add-on. It replaces brittle heuristic and regex code that previously handled filename cleaning, media type disambiguation, and metadata scoring. The Engine requires AI models to be present and will not begin ingestion until they have been downloaded.

All inference runs entirely on the local machine. No cloud account, no subscription, no data leaves the NAS.

---

## Project Structure

All AI implementations live in `MediaEngine.AI`. This project sits alongside `MediaEngine.Providers` in the dependency chain - it references `MediaEngine.Domain` (for contracts) and `MediaEngine.Storage` (for configuration).

NuGet dependencies:
- `LLamaSharp` + `LLamaSharp.Backend.Cpu` (both MIT) - .NET native llama.cpp binding with GBNF grammar constraint support
- `Whisper.net` + `Whisper.net.Runtime` (both MIT) - .NET native whisper.cpp binding for speech-to-text and language detection

---

## Model Roles

Model roles are small-first functional slots. Only one selected model is loaded into memory at a time, enforced by a `SemaphoreSlim`. Models auto-unload after a configurable idle timeout. On first run, `ModelAutoDownloadService` downloads the selected models to the `/models` Docker volume (environment variable: `TUVIMA_MODELS_DIR`).

| Role | Default model | Memory footprint | Use |
|---|---|---|---|
| `text_fast` | Qwen3 0.6B Q8 | ~639 MB | On-demand tasks: search intent parsing, TL;DR, recommendation explanations |
| `text_quality` | Qwen3 1.7B Q5_K_M | ~1.26 GB | Batch tasks: ingestion manifest analysis, vibe tagging, QID disambiguation |
| `text_scholar` | Qwen3 4B Q4_K_M | ~2.5 GB | Hard enrichment, relationship extraction, and long-context analysis |
| `text_cjk` | Qwen3 4B Q4_K_M | ~2.5 GB | Chinese, Japanese, Korean, and broader multilingual analysis |
| `audio` | Whisper Medium | ~1.5 GB | Timestamped transcription, language detection, audiobook sync, subtitle sync |

Model roles are configurable in `config/ai.json`. Each selected role points at a `model_catalog` entry, and each role has `role_requirements` that define required capabilities and promotion gates. Hardware availability can make a model usable, but does not by itself promote a larger model.

### Model Catalog

The catalog tracks current, candidate, experimental, and escalation models across the local spectrum:

| Catalog group | Intended use | Promotion rule |
|---|---|---|
| Qwen3 0.6B / 1.7B / 4B | Primary text ladder | Use the smallest model that passes JSON, accuracy, latency, and hallucination gates |
| Llama 3.2 1B / 3B | Legacy baselines | Keep for regression comparison until Qwen3 passes the same fixtures |
| Gemma 4 E2B / E4B | Multimodal and long-context candidates | Validate only when Qwen3 is insufficient or multimodal input becomes a product requirement |
| Gemma 4 12B | Lab escalation | Never default unless smaller models fail documented gates |
| Whisper Medium / Distil-Whisper / Whisper turbo | Whisper-compatible ASR candidates | Keep sync-grade timestamp semantics and compare WER, drift, and runtime |
| Parakeet / Qwen3-ASR | Experimental ASR | Do not promote until a local runtime adapter exists and sync fixtures pass |

---

## Structured Output

All LLM calls use GBNF grammar constraints - llama.cpp forces the model to produce valid JSON at the token level. This is model-agnostic and works with Llama, Mistral, Phi, Gemma, and Qwen models. JSON schema validation and retry logic serve as a safety net over the grammar constraint.

---

## Validation Gates

Tuvima promotes models by role requirements, not by hardware tier alone. A larger model is selected only when the smaller candidate fails a documented gate.

| Suite | Role | Required proof |
|---|---|---|
| `text_instant` | `text_fast` | Warm response target under 1.5s, valid JSON at least 99%, no UI-blocking model load |
| `text_ingestion` | `text_quality` | Valid JSON at least 99%, pass rate at least 94% on filename, media type, QID, and vibe fixtures |
| `text_enrichment` | `text_scholar` | Pass rate at least 95% on Wikipedia-backed description, people, theme, and relationship extraction |
| `text_multilingual` | `text_cjk` | CJK fixtures preserve canonical names and return schema-valid multilingual output |
| `audio_sync` | `audio` | WER at or below 12%, segment drift at or below 250 ms, reliable language detection, and recoverable long-file chunking |

The built-in benchmark suite definitions are exposed through `/ai/benchmark/suites`. Actual promotion still requires running the fixtures on the target machine and recording the result before changing the selected catalog key.

---

## Whisper and ASR Replacement Policy

Whisper is older, but it remains the default sync provider because the current .NET integration already returns timestamped segments. For Tuvima, transcription quality alone is not enough; audiobook and subtitle workflows need stable timing.

Replacement candidates are split into two groups:

- Whisper-compatible candidates such as Distil-Whisper large-v3 and Whisper large-v3-turbo can be evaluated first because they preserve the current whisper.cpp/Whisper.net style of integration.
- Parakeet and Qwen3-ASR are experimental until the Engine has a local ASR adapter for their runtime and their word or segment timestamps pass `audio_sync` fixtures.

Gemma 4 audio is not a Whisper replacement for sync in the current architecture. It can be tested later for transcript extraction or audio question answering, but it does not become sync-grade unless it exposes timestamped alignment output that passes the same gates.

---

## Features

### Ingestion (automatic, runs during file processing)

| Feature | Model | What it does |
|---|---|---|
| Smart Labeling | text_quality | Cleans raw filenames into structured title/author/year/series fields. Replaces `TitleNormalizer.cs`. |
| Media Type Classification | text_quality | Classifies ambiguous file formats (MP3, MP4, M4A) when heuristic signals are insufficient. Replaces AudioProcessor/VideoProcessor disambiguation heuristics. |
| Batch Manifest Builder | text_quality | Analyses an entire folder of files as a group before retail API calls, inferring series, author, and format context. Reduces retail API calls by 80-95% for bulk imports. Supersedes `IngestionHintCache`. |
| Audio Language Detection | audio | Detects the spoken language of audio files using Whisper. |

### Alignment (automatic / on-demand)

| Feature | Model | What it does |
|---|---|---|
| QID Disambiguation | text_quality | When the Reconciliation API returns multiple Wikidata candidates, picks the best match using semantic reasoning over title, description, and existing metadata. |
| Series Alignment | text_quality | Infers correct reading/watching order within a series when Wikidata series position data is absent or inconsistent. Background service (3 AM daily). |
| Watching Order | text_fast | Generates a recommended cross-media consumption order for a Collection (read the book before watching the film, etc.). On-demand. |

### Enrichment (background / on-demand)

| Feature | Model | What it does |
|---|---|---|
| Vibe Tags | text_quality | Generates mood and atmosphere tags (3-5 per work) using per-category controlled vocabularies defined in `config/ai.json`. 25-30 tags per media type, organised by dimension: pacing, mood, atmosphere, tone, intensity. Vibes describe *how something feels*, not *what it is* - genre handles categorisation (from Wikidata/retail), vibes handle the emotional/atmospheric space. Background service (4 AM daily). |
| TL;DR | text_fast | Generates a 2-3 sentence plain-language summary of a work's description. On-demand. |
| Cover Art Validation | text_fast | Verifies that a downloaded cover image matches the expected work (catches mismatched covers from retail providers). |
| Audio Similarity | - | Chromaprint-based acoustic fingerprinting to detect duplicate audio files across formats. No LLM required. |

### Syncing (scheduled / on-demand)

| Feature | Models | What it does |
|---|---|---|
| Immersive Bake | audio + text_quality | Generates a synchronized audiobook/ebook experience - aligns audio timestamps to text positions. |
| Subtitle Sync | audio | Corrects subtitle timing drift using Whisper-generated transcription as ground truth. |
| Cross-Media Scene Mapping | text_quality | Maps equivalent scenes across book, film, and audiobook editions of the same work. |

### Personalization (background / on-demand)

| Feature | Model | What it does |
|---|---|---|
| Local Taste Profiling | text_quality | Builds a per-profile preference model from reading/watching history. Background service (Sunday 5 AM). |
| "Why" Factor | text_fast | Explains in plain language why a specific item was recommended to a user. |

### Discovery (user input)

| Feature | Model | What it does |
|---|---|---|
| Intent Search | text_fast | Translates a natural language search query ("something scary set in space") into structured filter parameters for the library query engine. |

### Advanced (manual)

| Feature | Model | What it does |
|---|---|---|
| User-Assisted URL Paste | text_quality | Extracts structured metadata from a pasted URL (publisher page, Wikipedia article, Goodreads link) when automated providers fail. |

---

## Relationship to the Priority Cascade

AI does not replace the Priority Cascade Engine. Wikidata remains the sole authority for canonical data. AI improves the quality of inputs to the cascade:

| AI feature | Cascade interaction |
|---|---|
| SmartLabeler | Cleans filenames before they are parsed into claims - better raw input for Tier D scoring |
| MediaTypeAdvisor | Emits a high-confidence `media_type` claim that enters the cascade as any other claim |
| QidDisambiguator | Accelerates Tier C resolution when multiple Wikidata candidates are returned |
| BatchManifestBuilder | Reduces the number of API calls, but does not change claim weights or cascade logic |

The cascade evaluates all claims - including those produced by AI features - using the same tier system.

---

## Genre vs Vibe - Discovery Model

Genres and vibes serve different purposes and come from different sources. Together they power the discovery and smart playlist features.

| Layer | Source | Example | Answers |
|---|---|---|---|
| **Genre** | Wikidata (P136) / retail providers | Science Fiction, Mystery, Biography | "What is it?" - categorical |
| **Vibe** | AI (VibeTagger, text_quality model) | atmospheric, slow-burn, haunting | "How does it feel?" - emotional/atmospheric |
| **Taste Profile** | AI (TasteProfileBackgroundService) | User prefers cerebral + atmospheric sci-fi | "What do I like?" - personalised |
| **Intent Search** | AI (text_fast model) | "something scary set in space" -> genre:horror + genre:sci-fi + vibe:tense | "What am I in the mood for?" - natural language |

**Intent Search** is the bridge - it translates natural language into a structured query that combines genres, vibes, media types, and other metadata. The **"Why" Factor** then explains recommendations in plain language.

### Vibe vocabulary design principles

- Vibes must not overlap with genres. "Science Fiction" is a genre; "cerebral" and "futuristic-feeling" are vibes.
- Vibes are organised by dimension: **pacing** (page-turner, slow-burn), **mood** (dark, uplifting), **atmosphere** (atmospheric, gritty), **tone** (satirical, whimsical), **intensity** (epic, intimate).
- Cross-media tags (atmospheric, dark, intimate, haunting, raw, gentle) appear across most media types because those vibes are universal. Media-specific tags stay where they make sense (visually-stunning for film, driving for music, noir for comics).
- 25-30 tags per media type. The LLM selects 3-5 per work, so the larger vocabulary gives range without diluting results.
- Vocabularies are editable by the user in Settings > Intelligence > Vibe Vocabulary.

---

## API Endpoints

| Method | Route | Purpose |
|---|---|---|
| GET | `/ai/status` | Model load status, memory usage |
| GET | `/ai/models` | List configured models with download status |
| POST | `/ai/models/{role}/download` | Trigger model download |
| POST | `/ai/models/{role}/load` | Load a model into memory |
| POST | `/ai/models/{role}/unload` | Unload a model |
| GET | `/ai/config` | Current AI configuration |
| POST | `/ai/enrich/tldr/{entityId}` | Generate TL;DR for a work |
| POST | `/ai/enrich/vibes/{entityId}` | Generate vibe tags for a work |
| POST | `/ai/enrich/search/intent` | Parse a natural language search query |
| POST | `/ai/enrich/extract-url` | Extract metadata from a pasted URL |

---

## Background Services

| Service | Schedule | Purpose |
|---|---|---|
| `ModelAutoDownloadService` | Startup | Downloads missing models to `/models` volume |
| `VibeBatchService` | Daily at 4 AM | Processes vibe tag queue for all un-tagged works |
| `SeriesAlignmentBackgroundService` | Daily at 3 AM | Resolves series order for works without Wikidata series position |
| `TasteProfileBackgroundService` | Weekly, Sunday at 5 AM | Rebuilds per-profile taste models from history |

---

## Configuration

All AI settings live in `config/ai.json`:

- Model definitions per role (path, quantization, context window)
- Per-feature enable flags
- Per-category vibe vocabularies (Books, Movies, Music, Comics)
- Scheduling parameters for background services
- Idle unload timeout

## Related

- [How the Local AI Works](../explanation/how-ai-works.md)
- [How to Set Up Language Preferences](../guides/language-setup.md)
- [Hydration Pipeline, Provider Architecture and Enrichment Strategy](hydration-and-providers.md)
