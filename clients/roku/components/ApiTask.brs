sub init()
    m.top.functionName = "execute"
end sub

sub execute()
    m.top.error = invalid
    m.top.result = invalid
    payload = m.top.payload
    if payload = invalid then payload = {}

    if m.top.operation = "discover"
        response = request("GET", "/.well-known/tuvima", invalid, false)
    else if m.top.operation = "pair"
        response = request("POST", "/api/v1/oauth/device_authorization", payload, false)
    else if m.top.operation = "token"
        response = request("POST", "/api/v1/oauth/token", payload, false)
    else if m.top.operation = "home"
        response = request("GET", "/api/v1/display/home", invalid, true)
    else if m.top.operation = "browse"
        response = request("GET", "/api/v1/display/browse?lane=" + encode(payload.lane), invalid, true)
    else if m.top.operation = "search"
        response = request("GET", "/api/v1/display/search?q=" + encode(payload.query), invalid, true)
    else if m.top.operation = "details"
        response = request("GET", "/api/v1/details/" + encode(payload.entityType) + "/" + encode(payload.id), invalid, true)
    else if m.top.operation = "continue"
        response = request("GET", "/api/v1/display/continue", invalid, true)
    else if m.top.operation = "collections"
        response = request("GET", "/api/v1/collections?limit=24", invalid, true)
    else if m.top.operation = "collectionItems"
        response = request("GET", "/api/v1/collections/" + encode(payload.id) + "/items?limit=100", invalid, true)
    else if m.top.operation = "universe"
        ' Only a Wikidata QID (Q + digits) is ever sent; anything else is treated as a Universe the profile cannot see.
        if isQid(payload.qid)
            response = request("GET", "/api/v1/universe/" + encode(payload.qid), invalid, true)
        else
            response = { ok: false, status: 404, body: FormatJson({ error: "not_found", error_description: "That Universe could not be found." }) }
        end if
    else if m.top.operation = "photos"
        response = request("GET", "/api/v1/view/timeline?limit=60", invalid, true)
    else if m.top.operation = "photoFile"
        ' Poster cannot send an Authorization header, so the preview JPEG is fetched here with the bearer token and saved to tmp:/.
        m.downloadTarget = payload.target
        response = request("GET", "/api/v1/view/items/" + encode(payload.id) + "/preview?size=1280", invalid, true)
        m.downloadTarget = invalid
        if response.ok then response.body = FormatJson({ path: payload.target })
    else if m.top.operation = "profiles"
        response = request("GET", "/api/v1/devices/current/profiles", invalid, true)
    else if m.top.operation = "switchProfile"
        response = request("POST", "/api/v1/devices/current/switch-profile", payload, true)
    else if m.top.operation = "manifest"
        response = request("GET", "/api/v1/playback/" + encode(payload.assetId) + "/manifest?connectionPath=local", invalid, true)
    else if m.top.operation = "heartbeat"
        response = request("POST", "/api/v1/player/heartbeat", payload, true)
    else if m.top.operation = "playbackPreferences"
        response = request("GET", "/api/v1/devices/current/playback-preferences", invalid, true)
    else if m.top.operation = "textTracks"
        response = request("GET", textTracksPath(payload.assetId), invalid, true)
    else if m.top.operation = "textTrackSources"
        ' payload.kind is "lyrics" for the Lyrics panel; everything else is subtitles.
        response = request("GET", textTracksPath(payload.assetId) + "/sources?kind=" + textKind(payload), invalid, true)
    else if m.top.operation = "alternatives"
        response = request("GET", textTracksPath(payload.assetId) + "/alternatives?kind=" + textKind(payload) + languageQuery(payload), invalid, true)
    else if m.top.operation = "alternativePreview"
        response = request("GET", textTracksPath(payload.assetId) + "/alternatives/preview?kind=" + textKind(payload) + "&provider=" + encode(payload.provider) + "&sourceId=" + encode(payload.sourceId) + languageQuery(payload), invalid, true)
    else if m.top.operation = "alternativeChoose"
        choice = { kind: textKind(payload), provider: payload.provider, source_id: payload.sourceId }
        ' Lyrics are in the song's own language, so only subtitles name one.
        if textKind(payload) = "subtitles" then choice.language = payload.language
        response = request("POST", textTracksPath(payload.assetId) + "/alternatives/choose", choice, true)
    else if m.top.operation = "lyricsText"
        ' The lyrics file is plain text; it is handed back inside a small object so every answer stays JSON.
        response = request("GET", textTracksPath(payload.assetId) + "/" + encode(payload.trackId), invalid, true)
        if response.ok then response.body = FormatJson({ text: response.body })
    else if m.top.operation = "playerPreferences"
        ' The profile's saved playback preferences (read-only for an app): skip seconds and Autoplay next episode.
        response = request("GET", "/api/v1/devices/current/playback-preferences", invalid, true)
    else if m.top.operation = "showEpisodes"
        ' The show's owned episodes by Season, for the Episodes panel and Previous and Next.
        response = request("GET", "/api/v1/details/tvShow/" + encode(payload.id), invalid, true)
    else if m.top.operation = "bookmarks"
        response = request("GET", bookmarksPath(payload.workId), invalid, true)
    else if m.top.operation = "bookmarkCreate"
        response = request("POST", bookmarksPath(payload.workId), payload.body, true)
    else if m.top.operation = "bookmarkUpdate"
        response = request("PUT", "/api/v1/player/audiobooks/bookmarks/" + encode(payload.id), payload.body, true)
    else if m.top.operation = "bookmarkDelete"
        response = request("DELETE", "/api/v1/player/audiobooks/bookmarks/" + encode(payload.id), invalid, true)
    else if m.top.operation = "textTrackFile"
        ' The player cannot send a bearer header for a sidecar file, so the WebVTT is fetched here and saved to tmp:/.
        m.downloadTarget = payload.target
        response = request("GET", textTracksPath(payload.assetId) + "/" + encode(payload.trackId), invalid, true)
        m.downloadTarget = invalid
        if response.ok then response.body = FormatJson({ path: payload.target })
    else
        response = { ok: false, status: 0, body: "Unknown API task operation." }
    end if

    parsed = invalid
    if response.body <> invalid and response.body <> "" then parsed = ParseJson(response.body)
    if response.ok
        if parsed = invalid then parsed = {}
        if m.top.operation = "switchProfile" and parsed.access_token <> invalid
            ' The switch revokes the old tokens at once, so adopt the new pair before anything else runs.
            m.top.accessToken = parsed.access_token
            m.top.refreshToken = parsed.refresh_token
            m.top.tokenUpdate = { accessToken: parsed.access_token, refreshToken: parsed.refresh_token }
        end if
        m.top.result = { operation: m.top.operation, value: parsed }
    else
        code = "request_failed"
        description = "Library returned HTTP " + response.status.ToStr()
        interval = invalid
        if parsed <> invalid
            if parsed.error <> invalid then code = parsed.error
            if parsed.error_description <> invalid then description = parsed.error_description
            if parsed.interval <> invalid then interval = parsed.interval
        end if
        m.top.error = { operation: m.top.operation, status: response.status, code: code, description: description, interval: interval }
    end if
end sub

function request(method as String, path as String, body as Dynamic, authenticated as Boolean) as Object
    response = performRequest(method, path, body, authenticated)
    if authenticated and response.status = 401 and m.top.refreshToken <> ""
        refresh = performRequest("POST", "/api/v1/oauth/token", {
            grant_type: "refresh_token",
            client_id: "tuvima-roku",
            refresh_token: m.top.refreshToken
        }, false)
        if refresh.ok
            tokens = ParseJson(refresh.body)
            if tokens <> invalid
                m.top.accessToken = tokens.access_token
                m.top.refreshToken = tokens.refresh_token
                m.top.tokenUpdate = { accessToken: tokens.access_token, refreshToken: tokens.refresh_token }
                response = performRequest(method, path, body, true)
            end if
        end if
    end if
    return response
end function

function performRequest(method as String, path as String, body as Dynamic, authenticated as Boolean) as Object
    transfer = CreateObject("roUrlTransfer")
    transfer.SetCertificatesFile("common:/certs/ca-bundle.crt")
    transfer.InitClientCertificates()
    transfer.SetUrl(absoluteUrl(path))
    transfer.AddHeader("Accept", "application/json")
    if authenticated and m.top.accessToken <> ""
        transfer.AddHeader("Authorization", "Bearer " + m.top.accessToken)
    end if
    if method = "POST"
        transfer.AddHeader("Content-Type", "application/json")
        text = transfer.PostFromString(FormatJson(body))
    else if method = "PUT"
        transfer.AddHeader("Content-Type", "application/json")
        transfer.SetRequest("PUT")
        text = transfer.PostFromString(FormatJson(body))
    else if method = "DELETE"
        transfer.SetRequest("DELETE")
        text = transfer.GetToString()
    else
        if m.downloadTarget <> invalid
            transfer.GetToFile(m.downloadTarget)
            text = ""
        else
            text = transfer.GetToString()
        end if
    end if
    status = transfer.GetResponseCode()
    return { ok: status >= 200 and status < 300, status: status, body: text }
end function

function absoluteUrl(path as String) as String
    if Left(path, 7) = "http://" or Left(path, 8) = "https://" then return path
    if Left(path, 1) <> "/" then path = "/" + path
    return m.top.serverOrigin + path
end function

function isQid(value as Dynamic) as Boolean
    if value = invalid then return false
    if Type(value) <> "roString" and Type(value) <> "String" then return false
    matcher = CreateObject("roRegex", "^Q[0-9]+$", "")
    return matcher.IsMatch(value)
end function

' "lyrics" or "subtitles" (the default): which Find another list a call is about.
function textKind(payload as Object) as String
    if payload.kind <> invalid and payload.kind = "lyrics" then return "lyrics"
    return "subtitles"
end function

' Subtitles are searched in a language; lyrics are not.
function languageQuery(payload as Object) as String
    if textKind(payload) = "lyrics" then return ""
    return "&language=" + encode(payload.language)
end function

' The Engine's bookmark routes for one work (videos and audiobooks); the Engine decides which bookmarks the profile sees.
function bookmarksPath(workId as String) as String
    return "/api/v1/player/audiobooks/" + encode(workId) + "/bookmarks"
end function

function textTracksPath(assetId as String) as String
    return "/api/v1/stream/" + encode(assetId) + "/text-tracks"
end function

function encode(value as String) as String
    transfer = CreateObject("roUrlTransfer")
    return transfer.Escape(value)
end function
