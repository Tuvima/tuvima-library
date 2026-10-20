sub init()
    m.serverGroup = m.top.findNode("serverGroup")
    m.pairingGroup = m.top.findNode("pairingGroup")
    m.libraryGroup = m.top.findNode("libraryGroup")
    m.searchGroup = m.top.findNode("searchGroup")
    m.keyboard = m.top.findNode("serverKeyboard")
    m.rows = m.top.findNode("libraryRows")
    m.video = m.top.findNode("video")
    m.nav = m.top.findNode("navButtons")
    m.profileGroup = m.top.findNode("profileGroup")
    m.profileList = m.top.findNode("profileList")
    m.photoGroup = m.top.findNode("photoGroup")
    m.photoViewer = m.top.findNode("photoViewer")
    m.photoIds = []
    m.photoIndex = 0
    m.photoCounter = 0
    m.inCollection = false
    m.inUniverse = false
    m.universePage = invalid
    m.universeName = ""
    m.status = m.top.findNode("status")
    m.api = m.top.findNode("api")
    m.pairingTimer = m.top.findNode("pairingTimer")
    m.heartbeatTimer = m.top.findNode("heartbeatTimer")
    m.registry = CreateObject("roRegistrySection", "tuvima.native.v1")
    m.pendingOperation = ""
    m.api.observeField("result", "onApiResult")
    m.api.observeField("error", "onApiError")
    m.api.observeField("tokenUpdate", "onTokenUpdate")
    m.top.findNode("connectButton").observeField("buttonSelected", "onConnect")
    m.top.findNode("homeButton").observeField("buttonSelected", "loadHome")
    m.top.findNode("watchButton").observeField("buttonSelected", "loadWatch")
    m.top.findNode("readButton").observeField("buttonSelected", "loadRead")
    m.top.findNode("listenButton").observeField("buttonSelected", "loadListen")
    m.top.findNode("searchButton").observeField("buttonSelected", "showSearch")
    m.top.findNode("profilesButton").observeField("buttonSelected", "loadProfiles")
    m.profileList.observeField("itemSelected", "onProfileSelected")
    m.top.findNode("submitSearchButton").observeField("buttonSelected", "submitSearch")
    m.rows.observeField("rowItemFocused", "onItemFocused")
    m.rows.observeField("rowItemSelected", "onItemSelected")
    m.video.observeField("state", "onVideoState")
    m.pairingTimer.observeField("fire", "pollPairing")
    m.heartbeatTimer.observeField("fire", "sendHeartbeat")

    server = m.registry.Read("server")
    token = m.registry.Read("access_token")
    refreshToken = m.registry.Read("refresh_token")
    if server = ""
        showServer()
    else
        m.api.serverOrigin = server
        m.api.accessToken = token
        m.api.refreshToken = refreshToken
        if token = "" then beginPairing() else loadHome()
    end if
end sub

sub showServer()
    m.serverGroup.visible = true
    m.pairingGroup.visible = false
    m.libraryGroup.visible = false
    m.keyboard.setFocus(true)
end sub

sub onConnect()
    server = m.keyboard.text
    while Right(server, 1) = "/": server = Left(server, Len(server) - 1): end while
    if Left(server, 7) <> "http://" and Left(server, 8) <> "https://"
        m.status.text = "Enter a complete HTTP or HTTPS Dashboard address."
        return
    end if
    m.registry.Write("server", server)
    m.registry.Flush()
    m.api.serverOrigin = server
    runApi("discover", {})
end sub

sub beginPairing()
    payload = {
        client_id: "tuvima-roku",
        client_name: "Library for Roku",
        client_version: "0.1.0",
        device_name: "Roku",
        device_class: "television",
        scope: "library.read artwork.read progress.read progress.write queue.read queue.write playback.read playback.write view.personal.read",
        capabilities: {
            schema_version: 1,
            containers: ["mp4", "mpegts"],
            video_codecs: ["h264", "hevc", "vp9"],
            audio_codecs: ["aac", "ac3", "eac3"],
            subtitle_formats: ["webvtt", "vtt"],
            protocols: ["https", "http-range", "hls"],
            max_width: 3840,
            max_height: 2160,
            max_audio_channels: 8,
            supports_hdr: true,
            supports_playback_speed: false,
            supports_offline_downloads: false
        }
    }
    runApi("pair", payload)
end sub

sub pollPairing()
    if m.retryAssetId <> invalid
        assetId = m.retryAssetId
        m.retryAssetId = invalid
        m.pairingTimer.control = "stop"
        runApi("manifest", { assetId: assetId })
        return
    end if
    if m.deviceCode = invalid then return
    runApi("token", {
        grant_type: "urn:ietf:params:oauth:grant-type:device_code",
        client_id: "tuvima-roku",
        device_code: m.deviceCode
    })
end sub

sub clearBrowseState()
    ' Leaving a collection or a Universe's Series list for good; Back from either must never stay armed.
    m.inCollection = false
    m.inUniverse = false
    m.universePage = invalid
    m.universeName = ""
end sub

sub loadHome()
    clearBrowseState()
    m.homePage = invalid
    m.continuePage = invalid
    m.collectionsPage = invalid
    m.photosPage = invalid
    runApi("home", {})
end sub
sub loadWatch(): clearBrowseState(): runApi("browse", { lane: "watch" }): end sub
sub loadRead(): clearBrowseState(): runApi("browse", { lane: "read" }): end sub
sub loadListen(): clearBrowseState(): runApi("browse", { lane: "listen" }): end sub

sub showSearch()
    m.libraryGroup.visible = false
    m.searchGroup.visible = true
    m.top.findNode("searchKeyboard").setFocus(true)
end sub

sub submitSearch()
    query = m.top.findNode("searchKeyboard").text
    if query <> "" then runApi("search", { query: query })
end sub

sub runApi(operation as String, payload as Object)
    if m.api.state = "RUN" then return
    m.pendingOperation = operation
    m.api.operation = operation
    m.api.payload = payload
    m.api.control = "RUN"
end sub

sub onApiResult()
    result = m.api.result
    if result = invalid then return
    operation = result.operation
    value = result.value
    if operation = "discover"
        supported = false
        for each version in value.supported_api_versions: if version = "1" then supported = true
        end for
        if not supported then m.status.text = "This server does not support API v1.": return
        beginPairing()
    else if operation = "pair"
        m.deviceCode = value.device_code
        m.top.findNode("verificationUri").text = value.verification_uri
        m.top.findNode("userCode").text = value.user_code
        m.pairingTimer.duration = value.interval
        m.serverGroup.visible = false
        m.pairingGroup.visible = true
        m.pairingTimer.control = "start"
    else if operation = "token"
        m.pairingTimer.control = "stop"
        m.api.accessToken = value.access_token
        m.api.refreshToken = value.refresh_token
        m.registry.Write("access_token", value.access_token)
        m.registry.Write("refresh_token", value.refresh_token)
        m.registry.Flush()
        loadHome()
    else if operation = "home"
        ' Home is followed by Continue, Collections, and Photos; each one is optional and a failure only drops its row.
        m.homePage = value
        runApi("continue", {})
    else if operation = "continue"
        m.continuePage = value
        runApi("collections", {})
    else if operation = "collections"
        m.collectionsPage = value
        runApi("photos", {})
    else if operation = "photos"
        m.photosPage = value
        showHomeRows()
    else if operation = "browse"
        showLibrary(value)
    else if operation = "collectionItems"
        showCollectionItems(value)
    else if operation = "universe"
        showUniverse(value)
    else if operation = "photoFile"
        showPhoto(value)
    else if operation = "profiles"
        showProfiles(value)
    else if operation = "switchProfile"
        m.profileGroup.visible = false
        loadHome()
    else if operation = "search"
        showSearchResults(value)
    else if operation = "details"
        showDetails(value)
    else if operation = "manifest"
        playManifest(value)
    end if
end sub

sub onTokenUpdate()
    tokens = m.api.tokenUpdate
    if tokens = invalid then return
    m.registry.Write("access_token", tokens.accessToken)
    m.registry.Write("refresh_token", tokens.refreshToken)
    m.registry.Flush()
end sub

sub onApiError()
    error = m.api.error
    if error = invalid then return
    if error.operation = "token" and (error.code = "authorization_pending" or error.code = "slow_down")
        if error.interval <> invalid then m.pairingTimer.duration = error.interval
        return
    end if
    if error.status = 401
        m.registry.Delete("access_token")
        m.registry.Delete("refresh_token")
        m.registry.Flush()
    else if error.operation = "continue"
        runApi("collections", {})
        return
    else if error.operation = "collections"
        runApi("photos", {})
        return
    else if error.operation = "photos"
        if error.status = 403 then m.status.text = photoDeniedText()
        showHomeRows()
        return
    else if error.operation = "photoFile"
        if error.status = 403 then m.status.text = photoDeniedText() else m.status.text = error.description
        return
    else if error.operation = "switchProfile"
        if error.status = 428
            showPinDialog()
            return
        else if error.status = 403
            m.status.text = "That person is not available to this device."
            return
        else if error.status = 409
            m.status.text = "That person is already active on this device."
            return
        else if error.status = 429
            m.status.text = "Too many wrong PINs. Try again later."
            return
        end if
    end if
    m.status.text = error.description
end sub

function photoDeniedText() as String
    return "Photo access was not allowed for this device. Re-pair and tick photos and videos to enable it"
end function

sub showLibrary(page as Object)
    ' Every fresh list starts outside a collection / Universe; the callers that are one set their flag after this returns.
    m.inCollection = false
    m.inUniverse = false
    m.serverGroup.visible = false
    m.pairingGroup.visible = false
    m.libraryGroup.visible = true
    m.searchGroup.visible = false
    m.profileGroup.visible = false
    root = CreateObject("roSGNode", "ContentNode")
    for each shelf in page.shelves
        row = root.CreateChild("ContentNode")
        row.title = shelf.title
        for each card in shelf.items
            item = row.CreateChild("ContentNode")
            item.title = card.title
            item.description = card.description
            artwork = card.artwork
            if artwork <> invalid
                if artwork.coverLargeUrl <> invalid then item.HDPosterUrl = absoluteMediaUrl(artwork.coverLargeUrl)
                if item.HDPosterUrl = "" and artwork.squareLargeUrl <> invalid then item.HDPosterUrl = absoluteMediaUrl(artwork.squareLargeUrl)
                if item.HDPosterUrl = "" and artwork.backgroundLargeUrl <> invalid then item.HDPosterUrl = absoluteMediaUrl(artwork.backgroundLargeUrl)
            end if
            assetId = ""
            if card.assetId <> invalid then assetId = card.assetId
            for each action in card.actions
                if action.assetId <> invalid then assetId = action.assetId: exit for
            end for
            kind = ""
            if card.kind <> invalid then kind = card.kind
            mediaKind = ""
            if card.mediaKind <> invalid then mediaKind = card.mediaKind
            item.AddFields({ kind: kind, mediaKind: mediaKind, assetId: assetId, entityId: card.id, entityType: detailEntityType(card.mediaType), mediaType: card.mediaType, httpHeaders: { Authorization: "Bearer " + m.api.accessToken } })
        end for
    end for
    m.rows.content = root
    m.rows.setFocus(true)
end sub

sub onItemFocused()
    location = m.rows.rowItemFocused
    if location = invalid or m.rows.content = invalid then return
    item = m.rows.content.getChild(location[0]).getChild(location[1])
    m.top.findNode("detailTitle").text = item.title
    m.top.findNode("detailText").text = item.description
end sub

sub onItemSelected()
    location = m.rows.rowItemSelected
    item = m.rows.content.getChild(location[0]).getChild(location[1])
    if item.kind = "collection"
        ' A plain collection opened from Home: Back returns Home, not to a Universe.
        m.universePage = invalid
        m.collectionName = item.title
        runApi("collectionItems", { id: item.entityId })
    else if item.kind = "universe"
        m.universeName = item.title
        runApi("universe", { qid: item.entityId })
    else if item.kind = "universeSeries"
        ' m.universePage stays set so Back from the items returns to the Series list.
        m.collectionName = item.title
        runApi("collectionItems", { id: item.entityId })
    else if item.kind = "photo"
        openPhoto(item)
    else if item.assetId <> ""
        runApi("manifest", { assetId: item.assetId })
    else if item.entityId <> ""
        runApi("details", { entityType: item.entityType, id: item.entityId })
    end if
end sub

sub showHomeRows()
    page = { shelves: [] }
    if m.continuePage <> invalid and m.continuePage.shelves <> invalid
        for each shelf in m.continuePage.shelves
            if shelf.items <> invalid and shelf.items.Count() > 0 then page.shelves.Push(shelf)
        end for
    end if
    if m.homePage <> invalid and m.homePage.shelves <> invalid
        for each shelf in m.homePage.shelves
            ' Home carries its own cross-media continue shelf; the dedicated Continue row above replaces it.
            if shelf.key <> "continue" then page.shelves.Push(shelf)
        end for
    end if
    if m.collectionsPage <> invalid and m.collectionsPage.items <> invalid and m.collectionsPage.items.Count() > 0
        shelf = { title: "Collections", items: [] }
        for each entry in m.collectionsPage.items
            cover = invalid
            if entry.cover_artwork_url <> invalid then cover = entry.cover_artwork_url
            ' A Universe entry opens its Series list; the QID rides in the tile id so the selection handler can read it.
            tileKind = "collection"
            tileId = entry.id
            qid = universeQidFor(entry)
            if qid <> ""
                tileKind = "universe"
                tileId = qid
            end if
            shelf.items.Push({
                id: tileId,
                assetId: invalid,
                kind: tileKind,
                mediaType: "collection",
                title: entry.name,
                description: entry.item_count.ToStr() + " items",
                artwork: { coverLargeUrl: cover },
                actions: []
            })
        end for
        page.shelves.Push(shelf)
    end if
    if m.photosPage <> invalid and m.photosPage.months <> invalid
        shelf = { title: "Photos", items: [] }
        for each month in m.photosPage.months
            for each photo in month.items
                title = "Photo"
                if photo.title <> invalid then title = photo.title
                shelf.items.Push({
                    id: photo.id,
                    assetId: invalid,
                    kind: "photo",
                    mediaKind: photo.media_kind,
                    mediaType: "photo",
                    title: title,
                    description: "",
                    artwork: { coverLargeUrl: photo.thumbnail_url },
                    actions: []
                })
            end for
        end for
        if shelf.items.Count() > 0 then page.shelves.Push(shelf)
    end if
    showLibrary(page)
end sub

sub showCollectionItems(response as Object)
    shelf = { title: m.collectionName, items: [] }
    for each entry in response.items
        creator = ""
        if entry.creator <> invalid then creator = entry.creator
        shelf.items.Push({
            id: entry.work_id,
            assetId: invalid,
            mediaType: entry.media_type,
            title: entry.title,
            description: creator,
            artwork: { coverLargeUrl: entry.cover_url },
            actions: []
        })
    end for
    showLibrary({ shelves: [shelf] })
    m.inCollection = true
end sub

function isQid(value as Dynamic) as Boolean
    if value = invalid then return false
    if Type(value) <> "roString" and Type(value) <> "String" then return false
    matcher = CreateObject("roRegex", "^Q[0-9]+$", "")
    return matcher.IsMatch(value)
end function

' The Universe QID of a Collections entry: universe_qid, else a QID-shaped system_key; empty when the entry is not a Universe.
function universeQidFor(entry as Object) as String
    if entry.group_type = invalid then return ""
    if LCase(entry.group_type) <> "universe" then return ""
    if isQid(entry.universe_qid) then return entry.universe_qid
    if isQid(entry.system_key) then return entry.system_key
    return ""
end function

function wireCount(value as Dynamic) as Integer
    if value = invalid then return 0
    kind = Type(value)
    if kind = "roInt" or kind = "roInteger" or kind = "Integer" or kind = "roFloat" or kind = "Float" or kind = "roDouble" or kind = "Double" or kind = "roLongInteger" or kind = "LongInteger"
        return Int(value)
    end if
    return 0
end function

function itemCountText(count as Integer) as String
    if count = 1 then return "1 item"
    return count.ToStr() + " items"
end function

' The short line under a Series tile: its run count, or the Also count when the run is empty (never "0 items"); empty text means skip the Series.
function seriesCountLine(itemCount as Integer, alsoCount as Integer) as String
    if itemCount > 0 then return itemCountText(itemCount)
    if alsoCount > 0 then return "Also in this world · " + itemCountText(alsoCount)
    return ""
end function

function firstCardCover(source as Dynamic) as Dynamic
    if source = invalid then return invalid
    if source.items = invalid or source.items.Count() = 0 then return invalid
    artwork = source.items[0].artwork
    if artwork = invalid then return invalid
    if artwork.coverLargeUrl <> invalid then return artwork.coverLargeUrl
    if artwork.coverUrl <> invalid then return artwork.coverUrl
    return invalid
end function

sub showUniverse(response as Object)
    m.universePage = response
    showUniverseSeries(response)
end sub

' The Universe's Series list (Series only on Roku): one tile per shelf in the Engine's order.
sub showUniverseSeries(page as Object)
    title = m.universeName
    if page.name <> invalid and page.name <> "" then title = page.name
    row = { title: title, items: [] }
    if page.shelves <> invalid
        for each entry in page.shelves
            itemCount = wireCount(entry.item_count)
            alsoCount = wireCount(entry.also_in_this_world_count)
            also = entry.also_in_this_world
            if alsoCount = 0 and also <> invalid and also.items <> invalid then alsoCount = also.items.Count()
            line = seriesCountLine(itemCount, alsoCount)
            if line <> "" and entry.collection_id <> invalid
                name = "Series"
                if entry.shelf <> invalid and entry.shelf.title <> invalid and entry.shelf.title <> "" then name = entry.shelf.title
                cover = invalid
                if itemCount > 0 then cover = firstCardCover(entry.shelf)
                if cover = invalid then cover = firstCardCover(also)
                row.items.Push({
                    id: entry.collection_id,
                    assetId: invalid,
                    kind: "universeSeries",
                    mediaType: "collection",
                    title: name,
                    description: line,
                    artwork: { coverLargeUrl: cover },
                    actions: []
                })
            end if
        end for
    end if
    if row.items.Count() = 0
        m.universePage = invalid
        m.status.text = "This Universe has no Series to show yet."
        return
    end if
    showLibrary({ shelves: [row] })
    m.inUniverse = true
end sub

sub openPhoto(item as Object)
    if item.mediaKind = "video"
        m.status.text = "Videos from your photo library are not played on Roku."
        return
    end if
    ' Collect the photo ids in row order so left and right can step through them.
    m.photoIds = []
    m.photoIndex = 0
    row = m.rows.content.getChild(m.rows.rowItemSelected[0])
    for i = 0 to row.getChildCount() - 1
        entry = row.getChild(i)
        if entry.mediaKind <> "video"
            if entry.entityId = item.entityId then m.photoIndex = m.photoIds.Count()
            m.photoIds.Push(entry.entityId)
        end if
    end for
    loadPhoto()
end sub

sub loadPhoto()
    m.photoCounter = (m.photoCounter + 1) mod 4
    runApi("photoFile", { id: m.photoIds[m.photoIndex], target: "tmp:/photo" + m.photoCounter.ToStr() + ".jpg" })
end sub

sub showPhoto(value as Object)
    m.photoViewer.uri = value.path
    m.photoGroup.visible = true
    m.top.setFocus(true)
end sub

sub closePhoto()
    m.photoGroup.visible = false
    m.rows.setFocus(true)
end sub

sub loadProfiles()
    runApi("profiles", {})
end sub

sub showProfiles(value as Object)
    root = CreateObject("roSGNode", "ContentNode")
    for each person in value.profiles
        entry = root.CreateChild("ContentNode")
        label = person.display_name
        if person.requires_pin then label = label + "  (PIN)"
        if person.is_active then label = label + "  - current"
        entry.title = label
        entry.AddFields({ profileId: person.profile_id })
    end for
    m.profileList.content = root
    m.libraryGroup.visible = false
    m.profileGroup.visible = true
    m.profileList.setFocus(true)
end sub

sub onProfileSelected()
    entry = m.profileList.content.getChild(m.profileList.itemSelected)
    m.pendingProfileId = entry.profileId
    runApi("switchProfile", { profile_id: entry.profileId })
end sub

sub showPinDialog()
    dialog = CreateObject("roSGNode", "PinDialog")
    dialog.title = "Enter PIN"
    dialog.buttons = ["OK", "Cancel"]
    dialog.observeField("buttonSelected", "onPinButton")
    m.pinDialog = dialog
    m.top.dialog = dialog
end sub

sub onPinButton()
    pin = m.pinDialog.pin
    index = m.pinDialog.buttonSelected
    m.pinDialog.close = true
    if index = 0 and pin <> "" then runApi("switchProfile", { profile_id: m.pendingProfileId, pin: pin })
end sub

sub showSearchResults(response as Object)
    page = { shelves: [] }
    for each section in response.sections
        shelf = { title: section.title, items: [] }
        for each result in section.results
            shelf.items.Push({
                id: result.id,
                assetId: invalid,
                mediaType: result.mediaType,
                title: result.title,
                description: result.description,
                artwork: { coverLargeUrl: result.artworkUrl },
                actions: []
            })
        end for
        page.shelves.Push(shelf)
    end for
    showLibrary(page)
end sub

sub showDetails(detail as Object)
    m.top.findNode("detailTitle").text = detail.title
    m.top.findNode("detailText").text = detail.description
    if detail.primaryActions <> invalid
        for each action in detail.primaryActions
            if action.assetId <> invalid
                runApi("manifest", { assetId: action.assetId })
                return
            end if
        end for
    end if
end sub

sub playManifest(manifest as Object)
    path = invalid
    format = "mp4"
    if manifest.recommendedDelivery = "hls" and manifest.hlsStatus = "ready"
        path = manifest.hlsUrl
        format = "hls"
    else if manifest.recommendedDelivery = "direct-stream"
        path = manifest.directStreamUrl
    else if manifest.hlsStatus = "preparing"
        m.status.text = "Preparing adaptive playback…"
        m.retryAssetId = manifest.assetId
        m.pairingTimer.duration = 2
        m.pairingTimer.control = "start"
        return
    end if
    if path = invalid then m.status.text = "No compatible playback delivery is available.": return
    content = CreateObject("roSGNode", "ContentNode")
    content.url = absoluteMediaUrl(path)
    content.streamFormat = format
    content.HttpHeaders = { Authorization: "Bearer " + m.api.accessToken }
    m.video.content = content
    m.video.visible = true
    m.video.setFocus(true)
    if manifest.resume <> invalid then m.video.seek = manifest.resume.positionSeconds
    m.activeAssetId = manifest.assetId
    m.video.control = "play"
    m.heartbeatTimer.control = "start"
end sub

sub onVideoState()
    if m.video.state = "finished" or m.video.state = "error"
        m.heartbeatTimer.control = "stop"
        m.video.visible = false
        m.libraryGroup.visible = true
        m.rows.setFocus(true)
    end if
end sub

sub sendHeartbeat()
    if m.activeAssetId = invalid then return
    runApi("heartbeat", {
        assetId: m.activeAssetId,
        isPlaying: m.video.state = "playing",
        positionSeconds: m.video.position,
        durationSeconds: m.video.duration
    })
end sub

sub onLaunchContent()
    if m.top.launchContentId <> "" and m.api.accessToken <> ""
        runApi("manifest", { assetId: m.top.launchContentId })
    end if
end sub

function absoluteMediaUrl(path as String) as String
    if Left(path, 7) = "http://" or Left(path, 8) = "https://" then return path
    if Left(path, 1) <> "/" then path = "/" + path
    return m.api.serverOrigin + path
end function

function detailEntityType(mediaType as Dynamic) as String
    if mediaType = invalid then return "work"
    value = LCase(mediaType)
    if Instr(1, value, "movie") > 0 then return "movie"
    if Instr(1, value, "episode") > 0 then return "tvEpisode"
    if Instr(1, value, "tv") > 0 then return "tvShow"
    if Instr(1, value, "audiobook") > 0 then return "audiobook"
    if Instr(1, value, "book") > 0 then return "book"
    if Instr(1, value, "comic") > 0 then return "comicIssue"
    if Instr(1, value, "music") > 0 then return "musicAlbum"
    return "work"
end function

function onKeyEvent(key as String, press as Boolean) as Boolean
    if not press then return false
    if m.photoGroup.visible
        if key = "left" and m.photoIndex > 0
            m.photoIndex = m.photoIndex - 1
            loadPhoto()
        else if key = "right" and m.photoIndex < m.photoIds.Count() - 1
            m.photoIndex = m.photoIndex + 1
            loadPhoto()
        else if key = "back"
            closePhoto()
        end if
        return true
    end if
    if key = "back" and m.profileGroup.visible
        m.profileGroup.visible = false
        m.libraryGroup.visible = true
        m.rows.setFocus(true)
        return true
    end if
    if key = "back" and m.inCollection and m.libraryGroup.visible
        ' Items opened from a Universe's Series list go back to that list; any other collection goes Home.
        if m.universePage <> invalid then showUniverseSeries(m.universePage) else loadHome()
        return true
    end if
    if key = "back" and m.inUniverse and m.libraryGroup.visible
        loadHome()
        return true
    end if
    if m.libraryGroup.visible and not m.video.visible
        ' Rows and the top navigation are separate focus owners: up from the first row reaches the buttons, down returns to the rows.
        if key = "up" and m.rows.isInFocusChain() and m.rows.rowItemFocused <> invalid and m.rows.rowItemFocused[0] = 0
            m.top.findNode("homeButton").setFocus(true)
            return true
        else if key = "down" and m.nav.isInFocusChain()
            m.rows.setFocus(true)
            return true
        end if
    end if
    if key = "back" and m.video.visible
        m.video.control = "stop"
        m.video.visible = false
        m.rows.setFocus(true)
        return true
    end if
    return false
end function
