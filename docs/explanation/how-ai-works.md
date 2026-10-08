---
title: "How the Local AI Works"
description: "Understand the local models, hardware tiers, and AI responsibilities inside Tuvima Library."
audience: "user"
category: "explanation"
product_area: "ai"
tags:
  - "ai"
  - "local-models"
  - "hardware"
---
Tuvima Library's AI is not a cloud feature with a monthly cost and a privacy disclaimer. It runs entirely on your machine, using your CPU and GPU, processing your files without any data ever leaving your home. This page explains what the AI does, how it's designed, and - equally important - what it doesn't do.

---

## Fully Local, Always

Every AI model in Tuvima Library runs on your hardware:

- No cloud APIs
- No subscriptions
- No data transmitted externally
- No usage tracking

Models download automatically on first startup (about 9 GB total for a standard setup). After that, the AI works offline indefinitely.

Download URLs are for retrieving model files only. They do not enable cloud inference, remote prompts, telemetry, or usage tracking.

This isn't just a privacy nicety - it's what makes the AI viable for enriching a large personal collection over time. Cloud AI APIs have rate limits, costs, and latency. Local inference has none of those constraints. The Engine can process your entire library continuously in the background without you ever seeing a bill.

---

## Model Roles

The AI layer uses multiple specialized roles rather than one model doing everything. Smaller models are faster and cheaper to run, so Tuvima starts with the lightest model that can reliably pass each role's validation gates.

| Model | Size | Role | What it handles |
|---|---|---|---|
| Qwen3 0.6B | ~639 MB | Quick tasks | Search query parsing, TL;DR summaries, on-demand requests |
| Qwen3 1.7B Q5_K_M | ~1.26 GB | Ingestion | Filename cleaning, Wikidata candidate matching, vibe tag generation |
| Qwen3 4B | ~2.5 GB | Scholar/CJK | Deep enrichment, long-context analysis, CJK/multilingual processing |
| Whisper Medium | ~1.5 GB | Audio | Timestamped transcription, language detection, subtitle synchronization |

The model catalog also tracks legacy Llama baselines, Gemma 4 candidates, Gemma 4 12B as a lab escalation model, and newer ASR candidates such as Distil-Whisper, Whisper turbo, Parakeet, and Qwen3-ASR.

The fast model handles anything that needs to be responsive. The quality model handles batch work during ingestion. The scholar model is available for harder enrichment, but hardware availability alone does not make it the default. Whisper is in its own category entirely because audio sync depends on reliable timestamps, not only transcription text.

---

## Model Lifecycle And Status

Settings > Local AI reports model state from the Engine instead of guessing:

- **Configured**: a role exists in `config/ai.json`.
- **Missing**: the configured file is not present in the local model directory.
- **Downloading**: the Engine has an active download and may report progress.
- **Downloaded / ready**: the file exists and can be loaded.
- **Loaded / active**: the Engine has the model in memory for inference.
- **Failed**: download or load failed; the UI shows the Engine error instead of marking the model ready.

Supported roles can be downloaded, cancelled, loaded, and unloaded from Local AI settings. Unsupported configured roles are shown as configured-only. Model deletion is not exposed because there is no public Engine endpoint for it.

## Feature Availability

AI feature flags save to `config/ai.json`, but a saved flag is not the same as active behavior. The Dashboard explains whether a feature is ready, disabled, blocked by a missing/downloading model, limited by hardware, blocked by the AI subsystem, or not connected to Engine behavior yet. Some changes require an Engine restart, scheduler reload, model reload, rescan, or future enrichment run before they affect existing items.

---

## Hardware Auto-Profiling

Not every machine has the same capabilities. On first startup, the Engine benchmarks your CPU and GPU and classifies your hardware into one of three tiers:

**High** (e.g., RTX 3060 or better)
- All AI features available
- Continuous background enrichment
- Scholar model available when validation requires it
- GPU acceleration via CUDA (NVIDIA) or Vulkan (AMD/Intel Arc)

**Medium** (e.g., i7, 16 GB RAM)
- All AI features available
- Scheduled enrichment (runs during quiet periods)
- GPU used if available; falls back to CPU

**Low** (e.g., i5, 8 GB RAM)
- All features available, but heavy features run overnight only
- Uses small-first defaults unless a background validation run promotes a larger role
- CPU only

GPU detection is automatic. CUDA is tried first for NVIDIA cards. Vulkan is used for AMD and Intel Arc. Integrated GPUs are intentionally left alone - the Engine uses those for video transcoding, so AI takes the discrete GPU and leaves integrated graphics to FFmpeg.

You can see your hardware profile and current tier at Settings -> Intelligence -> Models.

---

## Resource Awareness

The AI doesn't just run blindly whenever it wants. It monitors the system actively:

- **CPU load**: If CPU usage is high (e.g., another application is working hard), AI enrichment pauses
- **RAM pressure**: If available RAM drops below a threshold, AI backs off
- **Active transcoding**: If FFmpeg is encoding a video, the AI defers heavy models until the encode completes

This means Tuvima Library coexists gracefully with the rest of your system. You won't notice the AI working in the background unless you go looking for it. And if you're doing something CPU-intensive, the library waits.

---

## Constrained Output with GBNF Grammars

One of the practical problems with using language models in a production system is that they can produce unpredictable output. Ask a model to return JSON and it might return JSON with an explanation attached, or malformed JSON, or JSON with hallucinated fields.

Tuvima Library solves this with **GBNF grammar constraints**. Every AI call that needs structured output (a JSON response, a list, a classification) uses a grammar that constrains the model to produce only valid output in exactly the format expected. The model cannot produce anything outside the grammar.

This eliminates parsing failures. The Engine never has to defensively handle malformed AI output because the model is literally incapable of producing it.

---

## What the AI Does and Doesn't Do

This is the most important section to understand.

### What the AI does

The AI is a **quality improvement layer** for matching and classification:

- **SmartLabeler**: Cleans up messy filenames before matching. "Frank.Herbert.Dune.Part.1.EPUB.rip" becomes "Dune Part 1 by Frank Herbert" - a much better search query.
- **QidDisambiguator**: When Wikidata returns multiple candidates for the same title, the AI picks the right one by comparing descriptions and properties against the file's metadata.
- **MediaTypeAdvisor**: Classifies ambiguous formats (MP3 could be music or audiobook; MP4 could be a film or a TV episode).
- **VibeTagger**: Generates 25-30 vibe tags per item based on descriptions and themes.
- **DescriptionIntelligenceService**: Extracts structured vocabulary from descriptions - themes, mood, setting, pace, TL;DR, character names.
- **IntentSearchParser**: Translates natural language search queries ("something scary set in space with a cozy feel") into structured search filters.
- **Whisper transcription**: Reads audio content, detects languages, and syncs subtitles.

### What the AI doesn't do

The AI **does not replace the Priority Cascade**. It doesn't determine canonical values for title, author, genre, series, or any other factual metadata. That's Wikidata's job.

This distinction matters. The AI can make matching better - help the Engine find the right Wikidata match for an item, help it classify an ambiguous file. But once the match is found, Wikidata's structured data flows through the Priority Cascade to determine what the canonical values are. The AI is an input to the matching process, not the authority for the output.

---

## Genre vs Vibes

These are two related but distinct concepts worth understanding clearly.

**Genres** come from Wikidata and retail providers. They describe *what something is* in a categorical sense: science fiction, biography, mystery, documentary. Genres are relatively stable, well-defined, and sourced from authoritative data.

**Vibes** are AI-generated and describe *how something feels*. Each media type has a vocabulary of 25-30 vibe tags:
- For books: atmospheric, cerebral, cozy, haunting, propulsive, intimate, sweeping, spare
- For films: kinetic, contemplative, tense, warm, bleak, playful, operatic
- And so on

Vibes aren't categorical - they're emotional texture. A book can be both "atmospheric" and "propulsive". A film can be "kinetic" and "haunting".

**Intent Search** combines both. When you type "something scary set in space", the Engine parses "scary" as a vibe signal (maps to "tense", "haunting") and "space" as a genre/setting signal. The search returns items that match both dimensions - not just genre:science-fiction, but specifically the items in your library that feel scary.

---

## Description Intelligence

This runs as a background batch service on a 15-minute schedule. It doesn't run inline during ingestion - that would slow everything down. Instead, after a file is ingested and its descriptions have been fetched from providers, the Description Intelligence service processes those descriptions asynchronously.

What it extracts:
- Themes (philosophical or narrative - "mortality", "power and corruption")
- Mood (emotional register - "melancholic", "hopeful")
- Setting (time and place - "near-future", "rural England")
- Pace (narrative speed - "slow burn", "relentless")
- TL;DR (a one-sentence summary)
- Character names (for cross-linking with the Universe Graph)

This data feeds the vibe tagging, the Intent Search index, and the Universe Graph's character detection.

---

For technical details about the AI model architecture, hardware benchmark thresholds, GBNF grammar definitions, the feature policy matrix by hardware tier, and the full list of 16 AI features across 7 categories - see the [architecture deep-dive](../architecture/ai-integration.md).

## Related

- [Local AI Intelligence Layer](../architecture/ai-integration.md)
- [How to Set Up Language Preferences](../guides/language-setup.md)
- [How Two-Stage Enrichment Works](how-hydration-works.md)
