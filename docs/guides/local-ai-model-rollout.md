---
title: "Roll out a local AI model"
description: "Steps for adding and rolling out a new local AI model, from the catalog entry to the release gates."
audience: "administrator"
category: "guide"
product_area: "ai"
---
1. Add the official source, license, artifact type, size, capabilities, compatibility, and gates to `model_catalog`. Never add an automatic URL for a gated artifact.
2. Pin the exact executable artifact's SHA-256.
3. Bind it to a disabled operational role with memory, context, output, temperature, and concurrency limits.
4. Evaluate recorded fixtures first. Live hardware benchmarking and model execution are separately deny-by-default and need explicit opt-in.
5. Run the named suite on representative target hardware and store the serialized report.
6. Mark configuration, runtime, and validation readiness only when independently proven. Enable through the API so capability gates cannot be bypassed.
7. Roll out to one feature, observe latency/memory/errors, then expand. Roll back by disabling the role or restoring the prior catalog key.

Text suites measure task pass, JSON validity, and unsupported claims. Embedding fixtures measure retrieval relevance. Function routing requires an allowed function and schema-valid arguments. Multimodal fixtures reject ungrounded details. Audio suites use role-specific language data, WER, and timestamp drift.

Evaluation inputs, outputs, reports, and model files remain local unless an administrator deliberately exports them. Fixtures must not contain secrets or unnecessary personal media. Internal logs retain diagnostic context while the Dashboard receives stable, non-sensitive Problem Details.
