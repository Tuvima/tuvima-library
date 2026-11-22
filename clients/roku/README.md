# Tuvima for Roku

This is a native SceneGraph/BrightScript client. Sideload the `clients/roku`
directory as a development application, or package it through the Roku
Developer Dashboard.

The client implements server entry, device pairing, browse rows, search-ready
API plumbing, details metadata, HLS/direct playback, captions supplied by the
manifest, heartbeats, and resume. Roku does not provide a Keychain-equivalent;
the refresh token is stored in the device registry with the minimum consumer
scopes and remains protected by server-side rotation, replay revocation, and
device revocation.

Public publication remains blocked until Roku confirms that Tuvima's
browser-approved device flow satisfies the current on-device authentication
criterion. Store artwork and localization assets must also be supplied before
certification packaging.

## Home rows, photos, and profiles

Home shows Continue first, then the home shelves, then a Collections row and a
Photos row. Select a collection to browse its items (Back returns to Home).
A Collections entry that is a Universe (its `group_type` is `universe` and it
carries a Wikidata QID in `universe_qid`, or a QID-shaped `system_key`) opens
that Universe's Series list (`GET /api/v1/universe/{qid}`): one tile per Series
in the Engine's order, showing the item count, or "Also in this world" with its
count when the Series has no numbered run of its own (never "0 items"). Select a
Series to see its items; Back returns to the Series list, and Back from the list
returns to Home. Roku shows Series only: no cast, adaptations or totals. A
Universe the profile cannot see shows the usual error text.

Up and down move between rows and the top buttons; left and right move inside a
row. Photos open in a full-screen viewer: left and right step through the row,
Back closes it. The preview JPEG is fetched by the app with the bearer token
and saved to `tmp:/`, because a Roku `Poster` cannot send headers. A `403` on a
photo call shows that photo access was not allowed; re-pair and tick photos and
videos. Photo videos are not played, and Roku does not download for offline use.

The Profiles button lists the people this paired app may switch to
(`GET /api/v1/devices/current/profiles`) and switches with
`POST /api/v1/devices/current/switch-profile`. A `428` opens a PIN dialog, `403`,
`409` and `429` show a plain message. The app requests the
`view.personal.read` scope at pairing; the person must still tick the photos
and videos option.

## Player

The player follows the shared contract in
[`clients/contracts/v1/player-controls.md`](../contracts/v1/player-controls.md)
(transport order and names, skip defaults of 10 seconds back and 30 forward, tool
order, panel names, paused details, end card and the television key map). Roku
plays through its native video node, and has only part of the rest: it has no
skip buttons or Previous/Next row, no Episodes panel, no Bookmarks (Everyone |
Mine), no Playback settings, no chapters, no paused details and no end card. The
gaps are one follow-up thread for Roku: skips, Episodes, Bookmarks and the end
card, laid out as the contract's first definition of the television side panels
(Unified player v2, D29 and section 9).

### Audio & subtitles and Find another

While a video plays, the remote's Options (`*`) key opens a small Audio & subtitles
panel on the right (`AudioSubtitlesPanel`): Audio rows when the video has more
than one track, then Subtitles (Off, then each subtitle track the Engine has),
then the footer row **Find subtitles…**. The footer row is shown only when
`GET /api/v1/stream/{asset}/text-tracks/sources?kind=subtitles` answers `Ready`.
A subtitle track is fetched with the bearer token, saved to `tmp:/`, and the same
stream is started again at the same position with it attached.

**Find subtitles…** opens the full-screen Find another window
(`FindAnotherWindow`, spec `find-another-on-tv`): the list on the left, the first
lines of the focused result on the right, **Use this** and **Cancel** at the
foot and one **Language** button (the profile's `subtitleLanguage`, with a short
list of languages to switch to; no typing). The best match is read at once; any
other row is read only after focus has rested on it for one second (a `Timer`
restarted on every focus change), once per window. Select on a row opens its
words in full and moves to **Use this**; Back closes the language list, then the
full words, then the window. **Use this** posts `alternatives/choose`, reloads the
text tracks and switches the new one on.

All wording, the dwell rule, the read-once cache and the key map are in
`components/FindAnotherLogic.brs`, which has no screen, `m` or network calls.
`tests/build/roku-find-another.test.mjs` checks the component structure in CI.

Not yet checked on a device (no Roku is available to the build): the Options key
reaching the scene while the video node has focus, a `tmp:/` WebVTT as an external
subtitle track, and the screen layout. The Engine's preview returns three lines per
result, so "full words" shows three lines today (the window takes up to 20 when the
Engine sends them). The Lyrics panel is a separate packet.
