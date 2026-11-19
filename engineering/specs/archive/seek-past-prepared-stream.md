# Spec: Jump anywhere in a film that is being converted

> **Status:** Built (awaiting Local checks) · **Version:** 1 · **Last updated:** 2026-11-19 · **Related docs:** [Playback path](playback-path.md) · [Playing now and General tabs](playing-now-and-general-tabs.md) · issue #1053 (Shy chose "restart at the jump point" on 2026-11-19)

## Product walkthrough (read this first)

When a film cannot be played as it is (for example HEVC video with DTS sound), the Engine converts it while you watch. Today the converted copy is built from the start of the film, so while it is still being built the player's timeline only reaches the part that is ready. A jump past that point lands at the edge and stays there. Resuming a film from the middle has the same problem until the whole film is converted.

After this spec, a jump (or a resume) to a point that is not prepared yet starts a second conversion at that point. The film shows its full length from the first second, the jump lands within a second or two of where you aimed after a short wait, and the conversion at the old point is dropped so the processor is not used twice. Films that need no conversion are unaffected.

## 1. Goal

Seeking and resuming anywhere in a converted film lands where aimed, without waiting for the whole film to be converted first.

## 2. Scope

**In scope**
- The stream the Engine builds for a film that needs conversion (encode-picture and the lighter rungs that use a prepared stream).
- The Dashboard player's timeline, progress saving and resume for such a stream.

**Out of scope**
- Direct play and repackaged streams that already start anywhere.
- Native clients (Android, Apple, Roku, TV web): they follow once the Engine half exists.
- Offline (download) conversions.

## 3. Decisions made

- **D1.** (Shy, 2026-11-19) A jump past the prepared part restarts the conversion at that point. Rejected: preparing the whole film first (long waits and heavy processor use) and leaving it as is.
- **D2.** The film's full length is known from the file, so the timeline shows it from the start. Reason: no shrinking or growing bar.
- **D3.** Only one conversion runs per viewer and film. A jump drops the old one (Stop in Playing now uses the same path), so the Streaming limit is never exceeded by jumping.
- **D4.** A conversion that starts at a point is a separate cached copy keyed by its start point rounded down to a segment. Jumping back into an already prepared part reuses it.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Jump inside the prepared part | Lands at once, as today |
| Jump past it | The picture waits (the normal Preparing wording) for a few seconds, then plays from the jump point; Playing now shows one "Preparing" row for the film |
| Resume from the middle | Same as a jump: starts at the saved point after a short wait, not at the start or the edge |
| Engine busy or conversion fails | The normal could-not-be-prepared sentence with Try again; the player keeps the old position |
| Phone, tablet, TV | Same behaviour |

## 5. Data / model changes

The cached copy's key gains a start point. Pre-release: existing cached copies are simply rebuilt.

## 6. Work packets

### WP1 — Engine: conversion that starts at a point
- **What it does:** the stream plan gets a start second; the video, sound and caption conversions begin there (`-ss` before the input, segment times kept continuous with the full-length timeline); the playlist is written full length so the player sees the whole film; the cache key and Playing now row carry the start.
- **Files likely touched:** `src/MediaEngine.Api/Services/Playback/AdaptiveHlsService.cs`, `PlaybackCapabilitiesService.cs`, `src/MediaEngine.Contracts/Playback/`, matching tests.
- **Depends on:** none
- **Size:** Complex
- **Done when:** a test film converted from 600 s has a playlist covering the whole film whose first listed segment starts at 600 s, and its first picture decodes at 600 s plus or minus one second.

### WP2 — Engine: restart on a far jump
- **What it does:** a manifest or segment request past the prepared part starts the second conversion and stops the first (same cancel path as Stop).
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** two jumps in a row leave one running conversion and the Streaming limit is respected.

### WP3 — Dashboard: timeline, saving and resume
- **What it does:** the player asks for the manifest at a start point on a far jump or resume, and keeps every displayed time, saved progress and bookmark in film time.
- **Files likely touched:** `src/MediaEngine.Web/Services/Playback/PlaybackSessionController*.cs`, `Components/Player/VideoPlaybackHost.razor`.
- **Depends on:** WP1, WP2
- **Size:** Complex
- **Done when:** component tests show a jump to 6000 s of a 6700 s film asks the Engine for a start at 6000 s and shows 6000 s on the timeline, and progress saved while playing is in film time.

## 7. Local checks

- **L1.** On SHY-DESKTOP play I Am Number Four (HEVC, DTS): jump to the middle and near the end several times; each lands within two seconds of the aim after a short wait. Resume from the middle starts at the saved point.
- **L2.** While jumping repeatedly, Playing now shows one encode for the film and the processor is not doubled.

## 8. Risks and open questions

- **Risks:** time offset mistakes would save wrong resume points (guarded by WP3's done-when test); segment boundaries that do not line up after `-ss` could show a brief glitch at the start of a restart.
- **Open questions:** Q1. Should a jump wait for the old conversion to finish its current segment before it is dropped? Answered: no, it is dropped at once (built that way).

## 9. Build notes (2026-11-19)

- A converted stream's own clock begins at zero at its start point (the Engine does not stamp film time into the segments). The Engine answers `streamStartSeconds` and the player adds it back, so every time shown, saved or bookmarked is film time. WP1's "first listed segment starts at 600 s" is therefore met as "the stream begins at 600 s of the film" (the `-ss` start and `streamStartSeconds`), not as a playlist that lists 600 s as its first time. The film's full length still comes from the file, so the timeline shows it from the first second.
- One conversion per viewer (profile and device) and film: a new start stops that viewer's earlier one for the same film. Cast sessions have no owner and are left alone.
- Known limits: a copied (not converted) picture starts at the keyframe before the point, so it can begin up to a few seconds early; casting to a TV from a restarted stream resumes from the stream's own clock; the double-tap skip on a phone moves within what is ready and does not restart.

