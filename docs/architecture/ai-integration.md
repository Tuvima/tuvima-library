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
status: current
---

# Local AI Intelligence Layer

## In this page

Understand how local inference, model lifecycle, prompts, and hardware limits support ingestion and enrichment. The Engine owns execution; the Dashboard presents readiness and operational controls.

## Where this lives in the code

- `src/MediaEngine.AI`
- `src/MediaEngine.Api/DependencyInjection/TuvimaAiServiceCollectionExtensions.cs`
- `src/MediaEngine.AI/Configuration/AiResourceProfileCatalog.cs`
- `src/MediaEngine.AI/Configuration/AiModelCatalogDefaults.cs`
- `config/ai.json`

## Role in the System

Local AI is the engine room: it runs the models and keeps the machine fair, and **plugins are how AI is used**. The AI features that ship with the Library (Smart labelling, Type helper, Series order, Vibe tags, Description insights and Taste summary) are Official plugins, each off until an administrator approves what it uses and switches it on on **Settings > Plugins**. The committed configuration leaves the optional audio pack off. `ModelAutoDownloadService` skips downloads when no AI plugin that needs a text model is on and the audio pack is off; basic setup does not require downloading every model.

Inference runs on the local machine. Model downloads and configured metadata-provider requests still use the network; local inference is not a claim that the whole Engine never makes outbound requests.

---

## The Local AI engine room

Nothing talks to the text model or the text reader directly. Every caller (the Library's own AI features, the hardware benchmark, plugins and the scanned-document reader) asks `IAiEngineRoom` (`src/MediaEngine.AI/EngineRoom/`) for an **ability**; the implementation, `LocalAiEngineRoom` (`src/MediaEngine.Api/Services/LocalAi/`), picks the engine. `LocalAiEngineRoomGuardrailTests` fails the build if any other source file uses the inference services or `IOcrService.RecognizeAsync`.

| Ability | Engine | Text model role | Notes |
|---|---|---|---|
| Write text (quick) | Text model | `text_fast` | |
| Write text (careful) | Text model | `text_quality` | |
| Write text (long) | Text model | `text_scholar` | |
| Pull out structured facts | Text model | `text_quality` | Grammar required |
| Classify | Text model | `text_quality` | Grammar required |
| Read text from images | Tesseract | none | A tool: works with Local AI off |
| Transcribe speech, Find similar, Describe image | none yet | none | Recorded for later; always answer "Not available yet" |

Any text ability may carry a grammar. `text_cjk` and the audio role get no ability yet.

**One queue per engine.** The text model has one slot. Waiting requests are served in caller-kind order (a person using the app, then ingestion, then the Library's background work, then plugins), first come first served within a kind. A person never waits behind background work: their arrival cancels running Library background and plugin work on that engine (ingestion is never cancelled). Ingestion is never turned away by the machine rules, only ordered. Library background work is **deferred** at once, writing nothing, when first-run setup is unfinished, someone used the app recently, resources are tight or another background request is running; the batch services then skip the run and try again later. A plugin instead **waits** (up to `plugin_budget.max_wait_seconds`) and then times out. Text reading (Tesseract) has no slot limit today, so its throughput is unchanged.

**Plugin allowance.** A plugin that has made `requests_per_hour` requests, or run for `busy_seconds_per_hour`, in the last rolling hour is refused without running, and its health reports degraded with "Used its Local AI allowance for this hour" (shown as Needs attention). The count is read from the durable `ai_usage_records` table, so a restart does not reset it.

**Usage records.** Every request that reaches the engine room, whatever its outcome, writes one `ai_usage_records` row (who, which ability, which engine, how long it waited and ran, how it ended). Deferred requests write none. A failed record write is logged as a warning and never fails the AI request.

**Readiness is read from the machine.** A text ability is available when Local AI is on (for plugins), the native runtime loads (`AiRuntimeStatus`, cached and re-checked on tool install or refresh) and the text model file is on disk. `GET /ai/status` reports ready only on those last two; the optional audio pack never decides it. Reading text from images needs text recognition switched on and Tesseract installed, and nothing else.

---

## Project Structure

All AI implementations live in `MediaEngine.AI`. This project sits alongside `MediaEngine.Providers` in the dependency chain - it references `MediaEngine.Domain` and `MediaEngine.Contracts`, without depending on `MediaEngine.Storage`.

NuGet dependencies:
- `LLamaSharp` + `LLamaSharp.Backend.Cpu` (both MIT) - .NET native llama.cpp binding with GBNF grammar constraint support
- `Whisper.net` + `Whisper.net.Runtime` (both MIT) - .NET native whisper.cpp binding for speech-to-text and language detection

---

## Model Roles

The machine selects one text resource profile. `AiResourceProfileCatalog.CreateDefinitions` clones that one artifact into `text_fast`, `text_quality`, `text_scholar`, and `text_cjk` with role-specific context and output limits. Those logical roles are not four separate text downloads. A single-resident runtime serializes inference (the engine room's one text slot), and idle models unload after the configured timeout.

| Resource profile | Selected text artifact | Declared download size | Catalog memory envelope |
|---|---|---:|---:|
| Essential | Qwen3 0.6B Q8_0 | 639 MB | 1,024 MB |
| Standard | Qwen3 1.7B Q5_K_M | 1,260 MB | 2,048 MB |
| Advanced | Qwen3 4B Q4_K_M | 2,500 MB | 4,096 MB |

The committed `resource_profile` is `standard`. `AiSettings.EffectiveResourceProfile` chooses Essential below 8 GB available RAM and falls back from Advanced to Standard when the hardware profile is ineligible. A download size is not a total RAM requirement.

Whisper Medium is a separate optional audio pack: 1,500 MB declared download and a 2,048 MB catalog memory envelope. `audio_pack_enabled` defaults to false. The supported launch catalog contains these three Qwen artifacts and Whisper Medium only.

### Model catalog ownership

`config/ai.json` selects `resource_profile`, `audio_pack_enabled` and the engine-room limits (concurrency, timeouts, the default hourly plugin allowance); which features run, their schedules and the words Vibe tags may use belong to the plugins and are saved as plugin settings. Code owns artifact URLs, SHA-256 values, role definitions, catalog metadata, and role requirements. `AiSettings.Models`, `ModelCatalog`, and `RoleRequirements` are JSON-ignored runtime state, not editable artifact definitions in the configuration file. See [the model portfolio](local-ai-model-portfolio.md).

Older design candidates such as Llama baselines, Gemma, Distil-Whisper, Whisper turbo, Parakeet, and Qwen3-ASR are not entries in the supported launch catalog. Evaluating them requires a deliberate runtime/catalog change and validation; they are not selectable setup options.

## Structured Output

A call may carry a GBNF grammar, and llama.cpp then forces the model to produce valid JSON at the token level. The abilities that pull out structured facts or classify require a grammar; writing text does not. Grammar constraints are model-agnostic and work with Llama, Mistral, Phi, Gemma, and Qwen models. JSON schema validation and retry logic serve as a safety net over the grammar constraint.

---

## Validation Gates

Role suites define validation objectives. They do not automatically select a larger model: resource-profile selection and effective hardware limits determine the current artifact. The figures below are design acceptance targets, not a statement that every suite has passed on every host.

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

AI features are plugins. Each is an Official plugin that is **off until an administrator approves it** (the approval lists "Uses Local AI") and each asks the engine room for an ability; it never names a model. A plugin gets its own hourly allowance (requests, busy seconds, longest wait); going over it shows Needs attention on **Settings > Local AI > Usage**.

| Plugin | Ability | What it does |
|---|---|---|
| Smart labelling | text_quality | Cleans raw file and folder names into title, author, year, series and episode suggestions when files are added. With it off, plain filename rules run. |
| Type helper | text_quality | Decides the media type of a file when the usual signals are unclear. With it off, the unclear file goes to Needs Review. |
| Series order | text_quality | Infers reading or watching order within a Series when Wikidata position data is missing or inconsistent. Runs on the plugin's schedule. |
| Vibe tags | text_quality | Gives each work 3 to 5 mood and atmosphere tags from a controlled word list (the plugin's settings). Vibes describe how something feels; genre, from Wikidata and retail providers, says what it is. Runs on the plugin's schedule. |
| Description insights | text_quality | Reads a work's description for themes, moods, content warnings and a short summary. Runs on the plugin's schedule. |
| Taste summary | text_quality | Writes a short plain-language summary of a profile's taste. It also asks to read what people watch, so it is off by default; with it off the profile page keeps its numbers and shows a plain sentence. |

Results are recorded as AI-sourced values under the plugin's own key. They never replace a value a provider or a person set, they rank below providers in the Priority Cascade, and the item's Details tab shows them under **Suggested by Local AI** with an AI mark. Switching a plugin off removes what it still owns and leaves anything a person or provider has since set. Vibes and themes are also browse filters.

Other services that use the same engine room: document text reading (Tesseract, which works with Local AI off), the hardware benchmark, and audio language detection with the optional audio pack. Ideas such as cross-media scene mapping, assisted URL extraction, intent search and audio-text sync are not built; the speech-sync placeholder and the image-AI placeholder plugin were removed until a feature needs them.

---

## Relationship to the Priority Cascade

AI does not replace the Priority Cascade Engine. Wikidata remains the sole authority for canonical data. AI improves the quality of inputs to the cascade:

| AI feature | Cascade interaction |
|---|---|
| Smart labelling | Cleans filenames before they are parsed into claims - better raw input for Tier D scoring |
| Type helper | Emits a high-confidence `media_type` claim that enters the cascade as any other claim |
| Series order | Fills in order only when Wikidata has no series position; it never overrides one |
| Vibe tags, Description insights | Add labelled suggestions that rank below every provider |

The cascade evaluates all claims - including those produced by AI features - using the same tier system, and an AI claim never outranks a provider's.

---

## Genre vs Vibe - Discovery Model

Genres and vibes serve different purposes and come from different sources. They describe separate metadata inputs for discovery and personalization; their presence does not make every proposed smart mix a current product feature.

| Layer | Source | Example | Answers |
|---|---|---|---|
| **Genre** | Wikidata (P136) / retail providers | Science Fiction, Mystery, Biography | "What is it?" - categorical |
| **Vibe** | AI (VibeTagger, text_quality model) | atmospheric, slow-burn, haunting | "How does it feel?" - emotional/atmospheric |
| **Taste Profile** | AI design (`TasteProfiler`) | User prefers cerebral + atmospheric sci-fi | "What do I like?" - personalised |
| **Intent Search** | AI (text_fast model) | "something scary set in space" -> genre:horror + genre:sci-fi + vibe:tense | "What am I in the mood for?" - natural language |

**Intent Search** is the bridge - it translates natural language into a structured query that combines genres, vibes, media types, and other metadata. The **"Why" Factor** then explains recommendations in plain language.

### Vibe vocabulary design principles

- Vibes must not overlap with genres. "Science Fiction" is a genre; "cerebral" and "futuristic-feeling" are vibes.
- Vibes are organised by dimension: **pacing** (page-turner, slow-burn), **mood** (dark, uplifting), **atmosphere** (atmospheric, gritty), **tone** (satirical, whimsical), **intensity** (epic, intimate).
- Cross-media tags (atmospheric, dark, intimate, haunting, raw, gentle) appear across most media types because those vibes are universal. Media-specific tags stay where they make sense (visually-stunning for film, driving for music, noir for comics).
- 25-30 tags per media type. The LLM selects 3-5 per work, so the larger vocabulary gives range without diluting results.
- The word list is a setting of the Vibe tags plugin (Settings > Plugins); an empty list falls back to the built-in words so tagging never runs without a vocabulary.

---

## API Endpoints

Model, status, configuration and usage routes live in `AiEndpoints.cs`. Refer to [the endpoint reference](../reference/api-endpoints.md) for the current route surface.

| Method | Route | Purpose |
|---|---|---|
| GET | `/ai/status` | Model load status, memory usage |
| GET | `/ai/models` | List configured models with download status |
| POST | `/ai/models/{role}/download` | Trigger model download |
| POST | `/ai/models/{role}/load` | Load a model into memory |
| POST | `/ai/models/{role}/unload` | Unload a model |
| GET / PUT | `/ai/config` | Read or change the AI settings, including the default plugin allowance |
| GET | `/ai/profile`, `/ai/resources` | Hardware profile and current resource use |
| POST | `/ai/benchmark` | Run the hardware benchmark |
| GET | `/ai/usage` | Who used which ability in the last 7 days, and each AI plugin's standing this hour |
| GET | `/ai/enrichment/progress` | Progress of AI-assisted enrichment |

Plugins reach Local AI only through the plugin door (`PluginAiGateway`), which checks the plugin's declared AI permission and its allowance first.

---

## Background Services

| Service | Schedule | Purpose |
|---|---|---|
| `ModelAutoDownloadService` | Startup | Downloads the selected text artifact only when an AI plugin that needs it is on; downloads Whisper only when the audio pack is enabled |
| Series order, Vibe tags and Description insights | Their own plugin schedules | Each plugin's scheduled job runs one pass of its runner; the schedule is a plugin setting and each run is recorded in the run history |

---

## Configuration

Engine-room settings live in `config/ai.json`:

- One selected text resource profile and an optional audio-pack flag; artifact definitions remain code-owned
- Concurrency, timeout, CPU reservation, free-disk and idle-unload limits
- The default hourly plugin allowance (`plugin_budget`) and per-plugin overrides

Which features run, their schedules and the Vibe tags word list are not here: they are plugin settings, saved under `.data/plugin-config/{plugin id}.json` in the library root.

## Related

- [How the Local AI Works](../explanation/how-ai-works.md)
- [How to Set Up Language Preferences](../guides/language-setup.md)
- [Hydration Pipeline, Provider Architecture and Enrichment Strategy](hydration-and-providers.md)
