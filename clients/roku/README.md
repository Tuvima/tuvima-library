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
