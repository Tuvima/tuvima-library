# Spec: <Feature name>

> **Status:** Draft · **Version:** 1 · **Last updated:** YYYY-MM-DD · **Related docs:** [Product overview](../product/overview.md) · <other links>

<!--
How to use this template
- Status values: Draft (being discussed) → Locked (approved; Claude Code may build it) → In progress (being built) → Built (done; move to specs/archive/).
- Only Shy changes Draft → Locked. Claude Code changes Locked → In progress → Built and never rewrites the body of a Locked spec; if reality differs, it stops and asks.
- Bump Version when the meaning changes; update Last updated on every edit.
- Write every section in plain language for a reader with no code access. Exact file paths and code patterns belong only in section 6 (Work packets).
- Delete these comments and any section marked (optional) that you don't need.
-->

## 1. Goal

One or two sentences: what changes for the person using Tuvima, and why it matters.

## 2. Scope

**In scope**
- …

**Out of scope**
- …

## 3. Decisions made

Numbered, so work packets and reviews can cite them (D1, D2, …). Each says what was decided and the reason.

- **D1.** …

## 4. UI behaviour

Describe the experience screen by screen, for each kind of user who sees it (administrator, household member, profile, phone vs desktop vs TV where it differs).

| Situation | What the person sees |
|---|---|
| Normal | … |
| Empty (nothing to show yet) | … |
| Loading | … |
| Error (Engine unreachable, not allowed, not found) | … |
| Partly ready (enrichment still running, provider down) | … |

## 5. Data / model changes

Plain language: what new information Tuvima stores or shows, where it comes from, what happens to existing libraries (pre-release: a reset/reingest is acceptable — say so if needed). "None" is a valid answer.

## 6. Work packets

Numbered in a sensible order. Claude Code groups them into waves by "Depends on" and runs at most two at once. Size decides who does it: **Mechanical** → mechanic agent (cheap, literal); **Standard** / **Complex** → builder agent.

### WP1 — <short name>
- **What it does:** …
- **Files likely touched:** `src/…`, `tests/…`
- **Depends on:** none
- **Size:** Mechanical | Standard | Complex
- **Done when:** an observable check (a test name passes, a screen shows X at phone width, a command outputs Y).

### WP2 — <short name>
- **What it does:** …
- **Files likely touched:** …
- **Depends on:** WP1
- **Size:** …
- **Done when:** …

## 7. Local checks

What the cloud cannot verify and Shy (or a local session) must try on SHY-DESKTOP once the work has merged: real media (`C:\Temp\real_import`), Windows-only behaviour, look and feel, devices, FFmpeg/OCR versions, live metadata providers. Each is a plain-English step with what should appear and what counts as a failure. Write "None" if the cloud can prove everything. The build session appends these to `engineering/validation/batch.md` and reports "Built, awaiting your Local checks" until they pass (`.claude/rules/cloud-first.md`).

- **L1.** …

## 8. Risks and open questions

- **Risks:** what could go wrong for users, and how we'd notice.
- **Open questions:** numbered (Q1, Q2, …), each with the options and a recommendation. A spec can't be Locked while questions remain open.

## 9. Gaps and drift (optional — for as-built or revised specs)

- **Built and matches the spec:** …
- **Built differently (what changed):** …
- **In the spec but not built yet:** …
