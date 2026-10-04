# Disposable player verification media

Use the isolated production-setup workflow in [Home fixture instructions](../home-media-cards/README.md), with a new `fixture-player-*` root. Stop both fixture apps after normal setup, then run the Home fixture seed using the actual profile ID. Do not use a configured user catalogue or reset storage.

Before restarting the apps, run:

```powershell
& $qaPython scripts/visual-qa/player-update/enrich-fixture.py --root tools/reports/home-media-cards-visual/fixture-player-2026-10-04-final
```

`$qaPython` is the bundled Python runtime returned by `load_workspace_dependencies`. FFmpeg/ffprobe must already be present in `tools/ffmpeg`. The helper validates the exact disposable root and marker, refuses a repeated extension, creates actual 180-second local music/audio/video files, embeds three source-authored chapters in the audiobook and chaptered movie, adds three owned music tracks, and creates local LRC/VTT text tracks. Technical inspection facts are derived from ffprobe and stored under each real content hash, as ingestion does. A separate unchaptered movie exercises the no-context case. The existing TV fixture includes an owned next episode and an unowned provider-only episode for exclusion checks.

Artwork is the existing synthetic, bounded fixture art, not the reference screenshots' covers. The audio is silence and the video is a test pattern; they verify transport and layout, not audio/video quality. Caption/lyric words are fixture data. File durations, chapters and stream metadata are actual file facts.

TV fixtures with an explicit episode-still URL also receive a typed `EpisodeStill` record referencing the same synthetic bounded renditions. Their Background records remain available, and the missing-still episode receives no invented still. This allows the video panel's explicit-still contract to be exercised separately from generic artwork fallback.

For lyric version and static-text checks, run `add-lyric-variants.py --root <same marked root>` while both apps are stopped. It requires the completed player marker and adds two non-preferred local versions to Opening Signals: an 18-line timed LRC and ordinary text. It is idempotent and preserves the existing preferred track, media and progress. The longer version makes manual scrolling and Back to current line observable; both versions support checking temporary selection separately from the explicit saved preference.

For long-title layout checks, run `add-layout-case.py --root <same marked root>` while both apps are stopped. It gives the fourth owned song a deliberately long title and updates its existing album track entry to match. The first three songs, identities, media, progress and artwork stay unchanged. This idempotent case checks whether artwork yields space before identity text or controls are clipped.

Use normal local sign-in and UI playback actions. Keep private `.qa-auth.json`, browser cookies, database, media and keys inside the ignored marked fixture. Do not print credentials or copy signed URLs into evidence. Runtime captures belong outside Git; record actual CSS viewport, DPR, state, reference ID, intentional differences and limitations. Do not count mockups or prior captures as a passing implementation check.

Only remove a fixture after stopping its apps, resolving its path under the intended task directory, and verifying the exact marker. This helper does not delete fixture data.
