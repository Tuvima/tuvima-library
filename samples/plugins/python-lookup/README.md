# Python lookup (sample plugin)

A plugin that is its own program, written in Python with only the standard library. Copy this folder into
`{library_root}/.data/plugins/python-lookup/`, then switch it on from Settings > Plugins. The Library starts
`python3 server.py`, checks it is healthy every few seconds, restarts it if it stops, and gives it a key that opens the
plugin door only as this plugin. See `docs/guides/building-a-plugin.md` ("Plugins that are separate programs") and the
published contract `docs/reference/plugin-host-v1.openapi.json`.
