# 0012 — Live TV is not built, and nothing in the plugin platform closes the door

**Date:** 2026-10-21 (Shy; spec `engineering/specs/developer-tools.md` D-I, WP22) · **Status:** Accepted

## What
Live TV is not part of the Library today and no work is scheduled. The plugin platform is built so it can be added later without changing the platform:

- **Channel and guide sources already exist as JSON plugins.** A plugin can declare a `channel_sources` entry in M3U or XMLTV format (`docs/guides/building-a-plugin.md`). On a schedule the Library downloads the list from the one address the administrator approved and keeps the latest result as the plugin's own record in the `channels` collection: for a channel list, each channel's id, name, logo address, group and stream address; for a guide, each programme's channel, title, start and stop. No tuner, no code and no screen is needed for this, and a test (`LiveTvReadiness_…` in `DeclarativePluginTests`) pins the record shape so a later Live TV reads what plugins write today.
- **Tuner backends and richer guide sources fit the existing kinds.** A separate-program plugin (kind `service`) can talk to an HDHomeRun or similar tuner and answer lookups; a plugin can show its own page with the plugin page types. Neither needs a platform change.
- **What belongs to the Library, not to plugins, when the time comes:** a channel library type (so channels are browsed, searched and permissioned like other media and obey profile limits), and a live-stream path into the player (no file to seek, a moving window, recording rules). Those need their own spec and are deliberately not started here.

## Why
Shy, 2026-10-19: Live TV does not have to be built, it only has to remain possible. No media server makes Live TV a plugin, because it needs seams that belong to the Library. Keeping the plugin side ready costs almost nothing and avoids a later breaking change (**Extensibility**); building the Library side now would be a large feature with nobody asking for it yet (**Maintenance**).

## Trade-offs accepted
- Channel lists are kept as plugin records, not as Library items, so nothing about them shows in browse, search or the player until Live TV is specified.
- The record shape is now a small promise: changing the field names later means changing the test and handling records already kept.

## Revisit if
Shy asks for Live TV, or a plugin author needs more than lists and guides (for example recording or tuner control) before the Library side exists.
