# Spec: Fast, hardware-aware playback start (the playback path)

> **Status:** Built, awaiting Local checks (batch.md, Playback path L1 to L12) · **Version:** 1 · **Last updated:** 2026-11-14 · **Related docs:** [Playing now and General tabs](playing-now-and-general-tabs.md) (shows what this spec decides) · [Video player controls and bookmarks](video-player-controls-and-bookmarks.md) · issue #1004 · Mockups: [playback-path.html](../design/mockups/playback-path.html)

## Product walkthrough (read this first)

Today, when you press Play on a movie that a browser cannot open as it is (most MKV files), the Engine builds a full streaming copy: it re-encodes the whole film in software at up to three qualities, one after another, and you wait for the first few segments of the smallest one. The result is a long "Preparing video…" and a processor running flat out, even on a PC with a very capable graphics card.

After this spec, Library picks the lightest way to play each title, in this order, and tells you which one it chose:

1. **Play directly.** The file already works in this player. Starts in about a second. No work for the Engine.
2. **Repackage.** The picture and sound are fine, only the box (the file type) is wrong, such as H.264 and AAC inside an MKV. The Engine copies them into a streamable form without re-encoding anything. Starts in a few seconds, almost no processor.
3. **Convert the sound only.** The picture is fine but the sound is not playable (DTS, TrueHD, FLAC in some players). The Engine copies the picture and converts only the audio. Starts in a few seconds.
4. **Encode the picture.** Only when the picture itself cannot be played (a codec or size the player cannot handle, or a slow connection). The Engine uses the graphics card when it works, the processor when it does not, builds one quality that suits this player, and starts playing after the first few segments.

What you see, in order (numbers match the mockup markers):

1. **A real reason while preparing.** "Preparing video…" becomes a title-card line such as "Getting this ready · converting sound (about 5 seconds)" or "Preparing picture · using your graphics card". If it has to use the processor it says why ("Graphics card unavailable: using processor").
2. **A hardware card on Settings > Playback > Quality.** It names what the Engine found ("NVIDIA RTX 5080 · encoding works" or "No graphics card encoding found: using processor"), has a **Test hardware** button, and shows the plain reason when a test fails.
3. **Choice of encoder.** The existing Hardware acceleration selector stays (Automatic, NVIDIA, Intel, VAAPI, Processor only). Automatic now means "try the graphics card, fall back by itself".
4. **Fewer, smaller copies.** Library builds one quality per start instead of three full qualities for every title, so there is less waiting, less disk used and less wasted processor. A second quality is only built when a connection needs it.
5. **The Engine says what it chose.** Every start writes one line to the Engine log ("Play: repackage, because MKV is not a browser container; video copied, audio copied") so slow starts can be explained afterwards. Playing now (the other spec) shows the same label.

What stays the same: your files are never changed, the player controls, bookmarks, subtitles, casting, and offline downloads (the "prepared for phone" encodes keep their own queue and settings, but share the same hardware choice).

## 1. Goal

A title should start in about a second when it can, within a few seconds when only the box or the sound must change, and only then re-encode the picture, on the graphics card first. Reason: Performance (start time and processor use), Reliability (a clear fallback and a stated reason) and Ease of use (no mystery waits).

## 2. Scope

**In scope**
- The decision ladder above, for every video player that uses the Engine's playback manifest (Dashboard player, native clients, cast), using what each player reports it can play.
- Repackaging and audio-only conversion paths (copy, not re-encode).
- Graphics-card detection fixed and testable; hardware first, processor fallback, reason shown and logged.
- One right-sized quality per start; extra quality only when the connection needs it.
- Per-start decision log line and a plain reason in the player's waiting state.
- Players measuring and reporting what they can decode (browsers first, then Android, Apple, Roku and TV web), learned limits per device, and showing both on the Devices page.
- A hardware card with a Test hardware button on Settings > Playback > Quality, and the same check as one row in the first-run Tools step.

**Out of scope**
- Showing and stopping encodes (the [Playing now spec](playing-now-and-general-tabs.md)).
- Changing offline "prepared for phone" encodes beyond sharing the hardware choice.
- Music and audiobook playback (no picture work).
- Native client rewrites (they keep their own capability profiles; this spec only makes the Engine's answer smarter).
- HDR tone-mapping on the graphics card (stays as it works today; see risks).

## 3. Decisions made

Drafted by Claude from reading the code and Shy's notes of 14 Nov 2026; every decision below is approved.

- **D1.** The ladder (Direct, Repackage, Convert sound, Encode picture) is decided by the Engine for each start, from the file's stored technical facts and what the player reports it can play. The Dashboard never guesses.
- **D2.** The first rung that works wins. A rung is never skipped to "play it safe" by encoding.
- **D3.** Repackage and Convert sound use copy for everything that already plays. They never re-encode the picture.
- **D4.** Encode picture builds one quality chosen from the player's screen height and the connection (for example 1080p for a TV on the home network), not the full 1080/720/480 set. A second quality is added only when a remote or slow connection asks for it (the current "adaptive remote delivery" rule stays).
- **D5.** Hardware first, processor fallback, both stated. Automatic tries the graphics card, and on a start-up failure of the graphics encoder it restarts that start on the processor at once rather than after the whole film (today the retry only happens after a full-length attempt fails).
- **D6.** Detection is tested with a picture size the graphics card accepts, and the actual error text is kept and shown. (Likely cause of "none": see section 5. To be confirmed on your PC.)
- **D7.** Every start logs one line with the rung, the reason, the encoder and where it ran. The same words feed the Playing now label.
- **D8.** The waiting state in the player always carries a real reason and never says only "Preparing video…". Phone and desktop are the same.
- **D9.** The limit "Maximum active encodes" applies at once when changed, not after the Engine restarts (today the number is read once at start-up; to be confirmed).
- **D11.** **Hardware is checked in first-run setup, lightly.** The existing Tools step gets one more row, "Graphics hardware", filled in automatically once FFmpeg is ready: "NVIDIA RTX 5080 · encoding works", or "No graphics encoding found · Library will use the processor", or "Found, but the test failed: <plain reason>". It never blocks Continue and asks nothing. Reason: Reliability (a too-old tool is caught on day one, not as a mystery slow first movie) and Ease of use (the right default without a question). The same check re-runs when FFmpeg is updated and when the Engine notices a different graphics card name at start-up; Test hardware on the Playback page runs it on demand. Setup is the one place a phone may differ, so this row is part of setup only.
- **D12.** **Players are measured, not assumed.** Today a paired device sends a fixed list when it pairs (the Android and TV web apps carry hard-coded lists) and the Dashboard in a browser, which is not paired, gets a fixed "Web" list; nothing is measured. New: every player checks what it can actually decode and reports it at connect and after each app or browser update (the Engine already accepts updates: `PUT /devices/current/capabilities`). How each measures: **browsers** ask the browser (media capability checks that say supported, smooth and power-efficient, which also reveals hardware decoding); **Android** asks the system's codec list (hardware decoders, highest size and level, HDR, audio passthrough); **Apple** asks whether hardware decoding of HEVC, AV1 and HDR is available; **Roku and TV web** use the platform's own check. The report holds only codecs, containers, sizes, HDR and audio channels, never names or identifiers beyond the device record that already exists. Version 2 of the capabilities record adds "hardware decode" flags per codec; older apps keep working with their fixed lists.
- **D13.** **Learn from failures.** If a player starts a title in a rung and then reports it could not play it, the Engine remembers that for this device and this kind of file and uses the next rung from then on (shown to the administrator on the device's page as "Learned: cannot play HEVC 10-bit"). An administrator can clear what was learned.
- **D14.** **The administrator sees it.** Settings > Security & Devices > Devices shows each device's measured abilities in one line ("Plays H.264, HEVC and AV1 in hardware · HDR · up to 4K") and any learned limits. The server's own hardware stays on the Playback page (D11 above).
- **D15.** **The same playback options exist in every app, from one list.** The Engine owns a short list of video playback options and serves it with their saved values; every client (Dashboard, Android phone and TV, Apple, Roku, TV web) draws that list in its own style under "Video quality and devices" in its playback settings. A new client gets the options by reading the list, not by being rebuilt. Two layers: **Per profile** (follow the person to every device, per the standing per-profile rule): *Video quality* (Automatic, Highest, Balanced, Data saver), *Quality on mobile data* (same four choices), *Convert only when needed* (on) and *Allow HDR* (on if the device reports it). **Per device** (this screen only): *Maximum resolution* (Automatic or a cap), *Use hardware decoding* (Automatic, On, Off, for when a device's decoder misbehaves), *Surround sound* (Automatic, Pass through, Stereo only), plus a read-only *What this device can play* summary and a *Check again* button that re-measures (D12). Everything is optional and defaults to Automatic, so nobody has to touch it.
- **D16.** **Client checklist.** Every client build, now and later, must: (1) report measured abilities at connect and after updates (D12); (2) show the same Video quality and devices list with the same wording; (3) show the same player waiting reasons (D8) and failure sentence; (4) play by the manifest the Engine returns and report a failure so it can be learned (D13); (5) on a TV, make the list fully remote-friendly (one row per setting, large type, no hover). The checklist is copied into each client build brief, so Android, Apple, Roku and TV web are built to it as they are built out.
- **D10.** No change to what is stored about your media: this spec adds no tables; the decision is made per start and logged.

## 4. UI behaviour

| Where | State | What shows |
|---|---|---|
| Player waiting state | Direct play | No waiting state; starts. |
| | Repackaging | "Getting this ready · about a few seconds". |
| | Converting sound | "Getting this ready · converting sound". |
| | Encoding picture | "Preparing picture · using your graphics card" (or "using processor: graphics card unavailable") and a real percent once known. |
| | Waiting for a slot | "Waiting for another title to finish preparing" with a Cancel that goes back. |
| | Failed | One sentence of what failed and a Try again, plus "Show details" for the Engine's text. |
| Settings > Playback > Quality | Card found and works | "NVIDIA RTX 5080 · encoding works" with Test hardware. |
| | Found but test failed | The reason in plain words ("The driver is too old for this card") and the fallback in use. |
| | None found | "No graphics encoding found. Library uses the processor." |
| Phone | All of the above | Same lines, 44 px buttons; nothing left out. |

## 5. Data and model changes

- **No new tables.** One new field on the playback answer the Engine already sends: which rung was chosen, the reason, the encoder and where it runs. Wire types, Contracts tests and fixtures follow.
- **Config:** `adaptive_hls.renditions` stays but is used as the pool a start picks from (one quality) rather than "build all". New settings: none required; the existing `hardware_acceleration` keeps its values.
- **Findings from reading the code** (to be proved by each packet's first test; not reproduced yet):
  1. The browser profile lists MP4, WebM and a few audio containers only. An MKV, even H.264 and AAC, is therefore never "direct", and there is no repackage rung, so it goes straight to full re-encode.
  2. The streaming copy builds 480p first (so you can start), then 720p and 1080p, each re-encoding the entire film one after another. That is the 1.7 GB package and the whole-film software encode seen.
  3. Hardware detection runs a one-frame test at 64 by 64 pixels. NVIDIA's encoder rejects pictures smaller than roughly 145 by 49, so the test fails on every NVIDIA card and the Engine concludes "no hardware". This is a strong candidate for the RTX 5080 report. Newer 50-series cards may also need a newer FFmpeg build than the one the Engine fetched; that would need your Engine log and tool version to confirm.
  4. The retry on the processor happens only after a hardware encode fails to the end of the film.
  5. The "Maximum active encodes" number is read once when the Engine starts.
  6. The player always requests the adaptive stream when the viewer picks an audio track the browser cannot switch; that stays, but should use rungs 2 and 3 where it can.

## 6. Work packets

Built in the cloud. Waves go one after another; at most two packets at once.

- **WP1 — Decision ladder (Complex).** One decision function from stored technical facts plus the player's reported capabilities to a rung, with the reason; log line; manifest field. Files likely: `Services/Playback/PlaybackCapabilitiesService.cs`, `Contracts/Playback/PlaybackDtos.cs` (to be confirmed). Depends on: none. Done when: table-driven tests cover MP4 H.264/AAC, MKV H.264/AAC, MKV HEVC/DTS, 4K HEVC, MP4 with AC3, a slow remote connection, and a player that reports HEVC support.
- **WP2 — Repackage and sound-only conversion (Complex).** Copy-based streaming copy for rungs 2 and 3, including multiple audio tracks and subtitles as today. Files likely: `Services/Playback/AdaptiveHlsService.cs` (to be confirmed). Depends on: WP1. Done when: a generated MKV H.264/AAC file starts in under 5 seconds with no video re-encode (checked from the Engine's process list in the test), and the DTS-audio file keeps its picture untouched.
- **WP3 — Hardware detection and fallback (Complex).** Fix the probe (size and real error text), start-up failure falls back at once, per-start encoder choice, quality picked by device and connection (D4). Files likely: `src/MediaEngine.Processors/FFmpegService.cs`, `AdaptiveHlsService.cs`. Depends on: WP1. Done when: tests with a fake FFmpeg that fails the graphics encoder show an immediate processor restart and a reason; probe unit tests use an accepted size.
- **WP4 — Player waiting states (Standard).** Real reasons and percent in the Dashboard player, same on phone; failed state with Try again. Files likely: `Components/Playback/…` (to be confirmed). Depends on: WP1. Done when: component tests show every row of the waiting-state table.
- **WP5 — Playback Quality card and the setup row (Standard).** Also adds the "Graphics hardware" row to the setup Tools step (`Components/Setup/SetupToolsStage.razor`, to be confirmed) and the automatic re-check after an FFmpeg update. Hardware card, Test hardware action (Engine connection point), plain failure reasons; Maximum active encodes applies immediately (D9). Depends on: WP3. Done when: Dashboard tests cover found, failed and none; limit change test passes without restart.
- **WP6b — Video quality and devices options (Standard).** Per-profile fields in the profile preference store (Contracts DTO, Engine cleaning, wire fixtures) and per-device fields on the device record; the Engine serves the list with values; the Engine applies them when choosing a rung and a quality (D4). Dashboard page under Settings > Playback (profile) and on the Devices page (device). The native screens are follow-ups that use D16. Depends on: WP6. Done when: tests show Data saver on mobile data picks a lower quality, Stereo only forces audio conversion, and the list has the same entries for every client.
- **WP6 — Measured player abilities (Complex).** Capabilities record version 2 in Contracts (hardware decode flags) with tests and fixtures; Dashboard player measures with the browser's own checks and reports once per session; TV web measures the same way; the Engine uses a report over the fixed list (WP1 already takes capabilities); learned-failure memory per device and file kind; Devices page line. Native Android, Apple and Roku measuring is a follow-up per client (each is outside this solution under `clients/`). Depends on: WP1. Done when: tests show a report that lacks HEVC makes an HEVC title use convert or encode rungs, and a reported failure moves the next start down a rung.
- **WP7 — Docs (Mechanical).** Public docs: how Library chooses how to play a file; Playback settings page. Depends on: WP6.

## 7. Local checks (SHY-DESKTOP, real media)

- **L9.** Run first-run setup on a fresh data folder: the Tools step shows the Graphics hardware row after FFmpeg finishes, says the right thing for the RTX 5080, never blocks Continue, and changes to "using processor" when FFmpeg is the old build.
- **L10.** Chrome, Edge, Safari and a phone browser each report different abilities on the Devices page; an H.264/AAC MKV direct-plays where the browser reports it can and repackages where not; an old browser missing HEVC gets a converted title. Needs real browsers and devices.
- **L11.** On an Android phone and TV: abilities show on the Devices page; a deliberately unplayable file is learned and the next start works.
- **L12.** On each client as it is built out (phone, TV, Roku, TV web): the Video quality and devices list shows the same rows and values as the Dashboard, a change on one device appears on the others for the per-profile rows, and Check again updates What this device can play. Needs real devices.
- **L1.** An H.264/AAC MKV starts in a few seconds with the processor barely used; the Engine log says Repackage.
- **L2.** An HEVC/DTS file starts and the log says convert sound or encode picture, whichever the player needs; picture quality unchanged for the sound-only case.
- **L3.** A 4K file plays; the log names the encoder and where it ran.
- **L4.** Settings > Playback > Quality names the RTX 5080; Test hardware passes. If it fails, the reason is readable. Needs the real computer and its driver and FFmpeg version.
- **L5.** Turn Hardware acceleration to Processor only and play the same file: it works and says why.
- **L6.** Unplug or break the graphics encoder (rename the tool's encoder, or use a machine without a card): playback still starts on the processor within seconds and says so.
- **L7.** On a real phone and over Wi-Fi away from home: the same waiting lines, nothing missing.
- **L8.** The disk used by streaming copies after playing ten titles is far below the old 1 to 2 GB per title.

## 8. Risks and open questions

Risks: copying instead of re-encoding relies on the player accepting the original encoding, so wrong "can play" answers would produce a stalled player; the first packet therefore ships with a safe auto-fallback to the next rung. HDR 4K needs tone-mapping that still runs on the processor, so it stays heavy even when the card encodes. NVIDIA 50-series cards may need a newer FFmpeg than the one currently fetched; if so the fix is a tool update, which touches the Tools page. Cast and native clients use their own capability profiles, which this spec does not change.

None open.


## Answers recorded (Shy, 14 Nov 2026)

Shy answered every open question with the recommended option (questions 90 to 110): General tabs for Library activity and Playing now; this spec owns both tabs with a pointer in Library activity; build the playback path first; auto-stop after 2 minutes (paused counts as watching for 10); ask once before stopping something being watched; player bugs #1000, #1002 and #1003 stay separate fixes; direct-play sessions are listed; tab names General, Notifications, Library activity, Playing now; People and Device filters; Libraries keeps its one-line status; the playback path is its own spec; MKV H.264/AAC direct-plays when the player reports it can and is otherwise repackaged; one quality per start; a hardware card with Test hardware and a setup row; players are measured and failures learned; native apps follow as per-app work; the specs are locked and built playback path first.

## Plain English summary

Library will start a title by the lightest method that works: play the file as it is, or copy it into a streamable form, or convert only the sound, and encode the picture only if it must, using your graphics card first and falling back by itself with a clear reason. It builds one quality for the player instead of three for the whole film, so titles start in seconds and the processor stays quiet. Settings gets a card that says what graphics hardware was found and a button to test it, and the Engine log says why each title was played the way it was.
