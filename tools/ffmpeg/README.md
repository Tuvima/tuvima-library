# Packaged FFmpeg

Windows installers bundle the FFmpeg and FFprobe executables downloaded by
`tools/Download-FFmpeg.ps1`. The executables themselves are intentionally
gitignored because of their size.

- Upstream: gyan.dev builds, published as GitHub releases at GyanD/codexffmpeg (permanent release assets; BtbN's dated autobuilds are pruned, which broke the earlier pin)
- Release: `9.0.2`
- Archive: `ffmpeg-9.0.2-essentials_build.zip` (GPLv3)
- Archive SHA-256: `60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA`
- FFmpeg SHA-256: `3256173F3F8BFFD7DF12227C68ADF68025EDB1832273A9530688A7BB1ED8EDEC`
- FFprobe SHA-256: `F0D36ECBBDD3BCFAC3EFA078C96C7271C2E68B3810595552AC3B7F17E9A65C52`

Containers use static GPL builds from BtbN's rolling `latest` release
(`ffmpeg-n9.0-latest-linux64-gpl-9.0.tar.xz` and the `linuxarm64` equivalent).
Dated autobuild releases are pruned upstream, so the container build is not
checksum-pinned; the Windows download below still is.

The Windows download script validates all three hashes. `build-installer.bat` validates
the two executable hashes and required HLS codecs/muxers before publishing.
`LICENSE.txt` contains the GPLv3 license shipped by the upstream bundle.
