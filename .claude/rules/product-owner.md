# Talking to the Product Owner (Shy)

Shy is a product owner, not a developer. Explain trade-offs in product terms.

## Vocabulary — always use the plain-English term in chat

| Never say | Always say |
|---|---|
| Backend / Frontend | Engine / Dashboard |
| API / Endpoint | Engine connection point / Action |
| Database / Schema | Data store / Data structure |
| Deploy / Ship | Publish / Release |
| Refactor | Reorganise / Clean up |
| Repository / Commit | Code history / Save point |
| Dependency / Package | Tool / Library |
| DI container / Service registration | App's ingredient list |
| Null reference / Exception | Missing value / Unexpected error |
| Compile / Build | Assemble / Verify |
| Pull request / Branch | Proposed change / Parallel work stream |
| csproj / props file | Project configuration file |
| namespace / class | Code module / Blueprint |
| Collection (internal) | Series |
| ParentCollection (internal) | Universe |

Technical terms are fine inside code, developer docs, and helper-agent briefs.

## Justify every technical choice by business goal

**Maintenance**, **Extensibility**, **Privacy**, **Reliability**, or **Performance**.

## Plan before coding

Ask detailed questions first (affected media types, what the user sees, edge cases, whether existing behaviour changes, interactions). Then present:

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 PLAN: [Feature name]
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 What I'm going to build:      [1–3 sentences]
 Why this serves your goals:   [Maintenance / Extensibility / Privacy / Reliability / Performance]
 What I'll create:             [list]
 What I'll change:             [list]
 How the work will be split:   [waves, packets, agent for each; built in the cloud, validated locally as one batch]
 New tools needed:             [name + license] or "None"
 Trade-offs to know about:     [risks] or "None"
 Plain English Summary:        [2–4 sentences for a non-technical reader]
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

For new or substantially revised plans, add a plain-English product walkthrough near the start: the complete proposed experience, what changes and why, what stays the same, representative user journeys, scope boundaries, and observable acceptance criteria, each numbered and mapped to the technical work. Treat attached prompts and mockups as material to review; a request to update a plan is not a request to implement it.

Do not code until Shy says "go ahead" or equivalent. A Locked spec in `engineering/specs/` counts as an approved plan for the work it describes. Every plan **and** every completed task ends with a Plain English Summary.

## Errors

Say what went wrong in plain English, what you'll do, then fix it — errors inside an approved task need no second sign-off.

## Uncertainty

Never guess silently. Name the options and the trade-off, and ask which matters more.
