# P1 â€” Theme, icons and layout atoms

The first-party theme provider emits Dashboard and Reader theme values, while
shared native components own typography, spacing, layout and icons. Existing
palette inputs and original `tuvima.tokens.css` values are preserved. Framework
CSS reads and class targets were migrated to first-party names. See the
[consolidated inventory and preservation hashes](./index.md).

`AppMaterialIcon` renders the captured SVG fragments inline, preserving the 24-unit
view box, 1.25/1.5/2.25rem size choices, color mapping and presentation attributes.
At the observed 14px root size, the medium glyph is 21px; hard-coded 24px rendering
would not match that original theme geometry. Strong text retains weight 700.
`AppMaterialIconPaths.cs` is generated from the pinned path snapshot using
`scripts/icons/generate-material-icons.py`; the original 387 referenced icons and
required native-control defaults are represented by 394 pinned members. Filled,
Outlined and Uncategorized families are tested against the exact snapshot and
every source reference. License attribution is retained in the repository notices.

Native typography/layout owners include AppText, AppStack, AppGrid/AppItem,
AppSpacer, AppContainer, AppDivider, AppLink, AppImage, AppAvatar and AppSkeleton.
Calls retain first-party compatibility parameters while output uses native DOM.
Existing shared artwork and playback-specific glyph owners remain authoritative.

The integrated migration removes all 1,085 original opening framework component
tags and 1,957 Material constant references. This all-phase branch does not claim
that intermediate provider removal followed six separately merged phase commits.
Native structure tests cover icon/alert/avatar contracts; deterministic catalog,
reference and dependency guards cover source removal. Representative browser
review verified native icon/control treatment and corrected sizing differences.
Every-layout computed theme equality across the complete visual matrix was not
established; that limitation is distinct from the exact preserved source values.

## Plain-English completion summary

Icons, colors, text and layout now use shared first-party components. The original
token values and vendor content remain preserved. Representative screenshot review
supports the resulting appearance; it does not establish every requested state.
