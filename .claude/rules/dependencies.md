---
paths:
  - "Directory.Packages.props"
  - "**/*.csproj"
  - "clients/**/build.gradle*"
  - "src/MediaEngine.Web/wwwroot/lib/**"
  - "src/MediaEngine.Web/wwwroot/vendor/**"
---

# New tools and libraries (license: AGPLv3)

Every new tool must be license-compatible; check before adding.

- **Safe:** MIT, Apache 2.0, BSD-2/3, LGPL 2.1/3, GPL 3/AGPL 3.
- **Ask Shy first:** GPL 2-only.
- **Block:** SSPL, Commons Clause, proprietary.

Pin .NET versions in `Directory.Packages.props`.

## Credit every tool (Shy, 2026-10-22)

Every tool or library that ships to users, or that the Engine downloads and runs (FFmpeg, Tesseract, ExifTool and the like), gets clear credit in the same change:
1. A row in the README's "Built on open-source software" table (name, link, what it does in plain English).
2. A row in `docs/reference/attributions.md` with its licence.
3. Whatever its licence requires in `THIRD-PARTY-NOTICES.md` and alongside the copy: the copyright line, the licence text, and any NOTICE file (Apache-2.0); a downloaded helper program keeps its own licence and readme files next to it.
Removing a tool removes its credit in the same change. Build-only and test-only tools go in the attributions page only. A plan that adds a tool lists it under "New tools needed" with its license.

## Face models (spec view-people-search, checked 2026-11-19)

License check done before building People (WP6): YuNet face detection is MIT, SFace face matching is Apache 2.0 and ONNX Runtime is MIT, all AGPLv3-compatible. The research-only models used by other photo apps are not allowed. The models are downloaded once from the OpenCV Zoo into the data folder through `config/tool_downloads.json` (pinned SHA-256, github.com and its storage hosts only), never copied into the repository or build output. They sit behind one face-finding and one face-matching role so a model can be swapped without redoing the pages.
