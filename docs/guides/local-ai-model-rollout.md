---
title: "Roll out a local AI model"
description: "Choose a supported local text profile, check its readiness, and enable optional audio only when needed."
audience: "administrator"
category: "guide"
product_area: "ai"
status: current
---

# Roll out a local AI model

Choose a supported model profile in Tuvima Library and check it before enabling dependent features. Setup takes a few minutes; downloads and hardware checks may take longer.

The launch catalogue contains Qwen3 text models and one optional Whisper audio model. These artifacts are defined by the application. Server configuration selects a profile; it cannot add arbitrary models.

## Choose a text profile

1. Open **Settings → Local AI → Models & Runtime**.
2. Choose **Use Essential**, **Use Standard**, or **Use Advanced**.
3. Check the effective profile shown for this machine.
4. Download the selected text model if needed.

| Profile | Shared text model | Download size |
| --- | --- | --- |
| Essential | Qwen3 0.6B Q8 | 639 MB |
| Standard, the configured default | Qwen3 1.7B Q5 | 1,260 MB |
| Advanced | Qwen3 4B Q4 | 2,500 MB |

All text roles share the chosen model artifact. They do not download four separate text models. A machine with less than 8 GB available memory uses Essential. Advanced requires a successful qualifying benchmark; otherwise the effective profile falls back to Standard.

Use the [shared AI storage guide](shared-ai-storage.md) for weights and native runtimes. `TUVIMA_MODELS_DIR` selects model storage; `TUVIMA_AI_RUNTIME_DIR` selects native runtime storage. Container deployments use their bundled runtime.

## Check readiness before enabling features

1. Check the model card's file, checksum, and any blocking reasons.
2. Confirm the model is downloaded and the native runtime is available.
3. Use the offered load and benchmark controls when needed.
4. Check feature readiness before enabling a dependent feature.

Saved feature flags do not override missing dependencies. A downloaded file alone does not prove that a feature is ready.

## Enable audio only when needed

The **Optional Whisper feature pack** uses Whisper Medium, a separate 1,500 MB download. It starts disabled and is not downloaded during basic setup. Enable it only when you need audio transcription or alignment.

## Expand gradually

1. Enable one dependent feature.
2. Observe latency, memory use, errors, and output quality.
3. Expand only after those results are acceptable.
4. Disable the feature or return to the prior supported profile if quality or stability regresses.

Keep secrets and unnecessary personal media out of test samples. Model files and local evaluation reports remain on your server unless you export them.

<details>
<summary>Technical details: configuration and catalogue changes</summary>

`config/ai.json` selects `resource_profile` (`essential`, `standard`, or `advanced`) and `audio_pack_enabled`. The resource profile creates the text-role definitions from one artifact; hardware readiness determines the effective profile.

`AiResourceProfileCatalog` and `AiModelCatalogDefaults` own the supported artifacts, hashes, sizes, runtime requirements, and validation gates. Extending that catalogue requires a reviewed application change, not an administrator adding entries to JSON. Keep a new artifact disabled until its runtime and quality checks pass. Evaluation tools require explicit opt-in before live hardware benchmarking or model execution.

</details>

## Next steps

- [Manage shared AI storage](shared-ai-storage.md).
- [Understand AI behavior](../explanation/how-ai-works.md).
- [Troubleshoot unavailable AI](troubleshooting.md).
