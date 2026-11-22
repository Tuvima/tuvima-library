# Contributing to the Library apps

This folder holds the apps that run on televisions, phones, tablets and cars
(`tv-web`, `android`, `roku`, `apple`) and the rules they share (`contracts`).
Spec: `engineering/specs/native-apps.md`.

## Licence

The code in `clients/` is open source under the **Mozilla Public License 2.0**
(MPL 2.0). Each folder has its own `LICENSE`. The rest of the repository stays
under the AGPLv3 (see the root `LICENSE`).

In plain words: anyone may use, change and ship these apps, including through an
app store. If you change one of these files and ship it, you share your change
to that file under the same licence. Your own new files are yours.

## Sending a change

Add a sign-off line to every commit (`git commit -s`), which adds
`Signed-off-by: Your Name <you@example.com>`. It says you wrote the change or have
the right to send it under this licence (the Developer Certificate of Origin,
<https://developercertificate.org>). No other agreement is needed.

## Adding a library

Check its licence first. Safe: MIT, Apache 2.0, BSD, LGPL, GPL 3, AGPL 3, MPL 2.0.
Ask first: GPL 2 only. Not allowed: SSPL, Commons Clause, proprietary. Credit it in
`THIRD-PARTY-NOTICES.md` at the repository root.

## Libraries the apps use today (checked 2026-11-22)

| App | Library | Licence |
|---|---|---|
| tv-web | hls.js 1.7.3 (bundled, licence beside it) | Apache 2.0 |
| android (core, mobile, tv) | AndroidX activity, core, lifecycle, work, concurrent-futures | Apache 2.0 |
| android (mobile, tv) | Jetpack Compose and Material 3 | Apache 2.0 |
| android (mobile, tv) | AndroidX Media3 (ExoPlayer, HLS, session, UI) | Apache 2.0 |
| android (core) | kotlinx.coroutines | Apache 2.0 |
| apple | none (Apple's own frameworks only) | n/a |
| roku | none (Roku's own SceneGraph) | n/a |

No library here conflicts with the AGPLv3 repository or with MPL 2.0.
