---
title: "Understand local AI"
description: "Learn what local AI can assist with, how model readiness works, and why an enabled setting does not guarantee an active feature."
audience: user
category: explanation
product_area: ai
status: early-access
---

# Understand local AI

Learn how local AI assists Tuvima Library in about four minutes. Check readiness in Settings before relying on a feature, because models, settings, and connected processing are separate requirements.

## Keep inference on your server

[Local AI](../reference/glossary.md#local-ai) runs model files on the Engine host through local runtimes. It is not a cloud prompt service and does not need a cloud inference subscription.

Downloading models contacts an external host. Metadata providers and the Places map can also use the network; local inference does not make the whole application offline.

## Check what is ready

1. Open **Settings > Local AI** with the required access.
2. Inspect the configured profile, model inventory, and feature readiness.
3. Download a supported missing model when needed.
4. Load it or follow the displayed action for its state.
5. Check the feature's own status before expecting processing to begin.

**Configured** means a role is selected. **Missing**, **Downloading**, **Downloaded/ready**, **Loaded/active**, and **Failed** describe different model states. A file being present does not prove a feature is connected or enabled.

Supported controls include download, cancel, load, and unload where endpoints exist. Unsupported roles remain configured-only. Model deletion is not exposed as a working public action.

## Choose a resource profile

The selected resource profile supplies one text model to the text roles. It does not download every profile's model as a mandatory bundle.

| Profile or pack | Model | Approximate file size |
| --- | --- | ---: |
| Essential | Qwen3 0.6B Q8 | 639 MB |
| Standard | Qwen3 1.7B Q5_K_M | 1,260 MB |
| Advanced | Qwen3 4B Q4_K_M | 2,500 MB |
| Optional audio pack | Whisper Medium | 1,500 MB |

These are model-file sizes, not total memory or installation requirements. The repository configuration selects Standard and disables the optional audio pack. Your installed configuration may differ.

## Understand assistance and limits

Local AI is the engine room: it runs the models and keeps the machine fair. The things it helps with are plugins that ship with the Library: filename cleanup (Smart labelling), ambiguous media types (Type helper), series order, vibe tags, description analysis and a short taste summary, plus audio language detection with the optional audio pack. Each is off until an administrator approves what it uses and switches it on at Settings > Plugins. They share one queue, where a person comes first, then ingestion, then the Library's own background work, then plugins, and each plugin has an hourly allowance. What the AI suggests is marked "Suggested by Local AI" on the item, ranks below every provider, and never replaces a value a person or provider set.

Every AI plugin is off by default. Switching one on is not proof that processing has run on existing items; check the plugin's card and Settings > Local AI > Usage.

Changes may require a model reload, Engine restart, scheduler reload, rescan, or a later enrichment job. Follow the status shown for that feature rather than assuming every change acts immediately.

## Distinguish facts from descriptions

Genres are sourced categories such as mystery or biography. Vibes describe mood or texture, such as cozy, tense, or atmospheric. AI-generated descriptions are assistance, not evidence of factual identity.

The Priority Cascade chooses factual metadata using locks, configured priorities, Wikidata authority, and confidence rules. AI does not override that process.

## Account for your hardware

Available memory, CPU, GPU support, and competing work affect speed. Resource controls can defer background inference when the system is busy. Larger models are not automatically better choices for every host.

Inspect the hardware profile, benchmark controls, and resource settings on the Runtime page under Settings > Local AI. Do not infer that every feature is available merely because the machine has a discrete GPU.

<details>
<summary>Technical details</summary>

`config/ai.json` selects `resource_profile`, `audio_pack_enabled`, concurrency, timeout, CPU reservation, free-disk threshold, idle unloading, and the default plugin allowance. Which features run, their schedules and the vibe words are plugin settings. The code-owned `AiResourceProfileCatalog` defines Essential, Standard, Advanced, and the optional Whisper pack. Text fast, quality, scholar, and CJK roles clone the selected text model with role-specific context/token limits.

The launch catalog records artifact URLs, SHA-256 hashes, memory envelopes, runtime compatibility, and validation requirements. It does not make an arbitrary model URL a supported runtime. Gated artifacts require deliberate license acceptance and verified installation when supported by policy.

LLamaSharp handles text inference; Whisper.net handles supported audio tasks. Grammar-constrained output helps produce expected structured formats, but it does not guarantee factual accuracy or eliminate runtime failures. Services must still validate results and handle errors.

AI responsibilities are the six AI plugins and supported Whisper language detection. Description insights can extract themes, mood, setting, pace, a short summary, and character names after provider descriptions arrive, on the plugin's own schedule; a plugin that is off or a model that is not ready does not run merely because a schedule exists.

Supported GPU acceleration depends on the installed backend and hardware. Runtime/resource policies govern scheduling and transcoding competition. Evaluation fixtures, outputs, promotion reports, and model state stay local unless deliberately exported. Hardware benchmarking and live evaluation follow their explicit opt-in controls.

See [AI architecture](../architecture/ai-integration.md) for implementation details. The profile and readiness description above follows the current code-owned launch catalog.

</details>

## Next steps

- [Understand privacy and network calls](privacy-local-first.md)
- [Understand metadata source selection](how-scoring-works.md)
- [Check current AI availability](../product/status.md)
- [Set language preferences](../guides/language-setup.md)
