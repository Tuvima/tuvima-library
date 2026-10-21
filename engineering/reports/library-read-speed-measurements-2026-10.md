---
title: "Which library reads are slow or break on very large libraries"
summary: "Measured on a generated library of 4,000 works: which reads scanned the whole-library credit list, which sent one value per work to the data store, and what was changed."
audience: "product-owner, developer"
category: "report"
product_area: "engine"
---

# Which library reads are slow or break on very large libraries

October 21, 2026. Follow-up to the person credits and series search speed work (PR 364). Measured on the generated library of 4,000 works used by the performance tests, counting work done by the data store (thousands of SQLite instructions; the same on every run). Whole-library credit list: about 82,000 for one scan.

| Read | Problem found | Result |
|---|---|---|
| Person credits, series search, add-media search | Whole-library credit list (done in PR 364) | Fixed earlier: about 1,000 instead of 85,000 to 3,000,000 |
| Presence counts for a page of people | Read the whole-library credit list | Fixed: 20 busiest authors cost about 5,000 (was about 82,000 per call); values identical in the benchmark snapshot |
| People search (Search, People section) | Checked the whole credit list once per matching person; sent one value per visible file | Fixed: credits worked out only for matched people's files; files sent as one list |
| Series search in universal search | One value per visible work | Fixed: one list |
| New-items feed, detail composition reads | One value per visible work or file (fails past 32,766) | Fixed: one list |
| Role counts, catalogue-wide lists, system view groups | Intrinsically read every credit | Left as is: they answer a question about the whole library, so a full read is the honest cost |

Guardrails: `LibrarySizedIdListGuardrailTests` (no per-id binding of the allow-lists), `PeopleReadGuardrailTests` (work budget and no whole-library credit list in these reads), and behaviour tests with more than 32,766 ids.
