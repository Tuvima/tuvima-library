---
paths:
  - "Directory.Packages.props"
  - "**/*.csproj"
  - "website/package.json"
  - "clients/**/build.gradle*"
  - "src/MediaEngine.Web/wwwroot/lib/**"
  - "src/MediaEngine.Web/wwwroot/vendor/**"
---

# New tools and libraries (license: AGPLv3)

Every new tool must be license-compatible; check before adding.

- **Safe:** MIT, Apache 2.0, BSD-2/3, LGPL 2.1/3, GPL 3/AGPL 3.
- **Ask Shy first:** GPL 2-only.
- **Block:** SSPL, Commons Clause, proprietary.

Pin .NET versions in `Directory.Packages.props`. Record approved additions in `docs/reference/attributions.md` and `THIRD-PARTY-NOTICES.md` as applicable. A plan that adds a tool lists it under "New tools needed" with its license.
