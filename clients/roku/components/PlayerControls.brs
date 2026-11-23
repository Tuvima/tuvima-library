' The Roku player's controls (spec native-apps WP6), included by MainScene so it shares the scene's `m`. The contract is
' clients/contracts/v1/player-controls.md (Televisions, Panels, Paused details, End card, tv-keys); the layout is the approved
' mock-up engineering/design/mockups/tv-player/tv-player-panels.html. What each key does and what each row says is decided in
' PlayerLogic.brs; this file keeps the state of one playing video (m.p), reads the remote and drives the overlay and the panels.
'
' Video (an episode, a film or a folder video) gets this overlay. Music and audiobooks keep Roku's own playback screen and the
' Lyrics panel (see the README's Player gaps). The profile's playback preferences are read-only for an app, so Autoplay and the
' skip seconds chosen here stay on this Roku, in the device registry.

sub initPlayer()
    m.overlay = m.top.findNode("overlay")
    m.sidePanel = m.top.findNode("sidePanel")
    m.overlayTimer = m.top.findNode("overlayTimer")
    m.p = invalid
    m.prefs = invalid
    m.pendingMeta = invalid
    m.showCache = { showId: "", seasons: [] }
    m.bmTarget = invalid
    m.overlayTimer.observeField("fire", "onOverlayTimer")
    m.sidePanel.observeField("selection", "onSideSelection")
    m.sidePanel.observeField("adjust", "onSideAdjust")
    m.sidePanel.observeField("leaveRequested", "onSideLeave")
    m.sidePanel.observeField("closeRequested", "onSideClose")
    m.panel.observeField("leaveRequested", "onSideLeave")
end sub

' ---- What is about to play ---------------------------------------------------------------------------------------

function emptyMeta() as Object
    return { workId: "", showId: "", subject: "", season: 0, episode: 0, epTitle: "", playTitle: "", year: 0, description: "" }
end function

' What a card knows about the item it plays, read when the person selects it (the manifest does not carry the work).
function metaFromItem(item as Object) as Object
    meta = emptyMeta()
    meta.workId = faStr(item.workId)
    meta.showId = faStr(item.showId)
    meta.subject = faStr(item.subject)
    meta.season = wireCount(item.seasonNo)
    meta.episode = wireCount(item.episodeNo)
    meta.epTitle = faStr(item.epTitle)
    meta.playTitle = faStr(item.playTitle)
    meta.year = wireCount(item.sortYear)
    meta.description = faStr(item.description)
    return meta
end function

' ---- Starting and ending ---------------------------------------------------------------------------------------

sub beginPlayer(manifest as Object, kind as String, meta as Object)
    title = meta.playTitle
    if title = "" then title = m.lastTitle
    m.p = {
        kind: kind,
        assetId: faStr(manifest.assetId),
        workId: meta.workId,
        showId: meta.showId,
        title: title,
        epTitle: meta.epTitle,
        season: meta.season,
        episode: meta.episode,
        year: meta.year,
        description: meta.description,
        manifest: manifest,
        prefs: m.prefs,
        device: readDeviceSettings(),
        seasons: [],
        seasonIndex: 0,
        preceding: invalid,
        following: invalid,
        bookmarks: [],
        bmTab: "mine",
        bmFailed: false,
        bmLoadError: "",
        keepTopRow: false,
        ticks: false,
        panel: "",
        panelView: "root",
        zone: "transport",
        index: 2,
        overlayVisible: true,
        endCard: false,
        endCancelled: false,
        endFocus: 1,
        endRemaining: -1
    }
    m.panel.visible = false
    m.sidePanel.visible = false
    m.overlay.visible = true
    m.top.setFocus(true)
    if m.prefs = invalid then runSide("playerPreferences", {})
    if kind = "episode" and meta.showId <> ""
        if m.showCache.showId = meta.showId then applyEpisodes(m.showCache.seasons) else runSide("showEpisodes", { id: meta.showId })
    end if
    if meta.workId <> "" then runSide("bookmarks", { workId: meta.workId })
    showOverlay()
end sub

sub endPlayerUi()
    m.p = invalid
    m.overlay.visible = false
    m.sidePanel.visible = false
    m.overlayTimer.control = "stop"
end sub

sub closePlayer()
    sendHeartbeat()
    m.video.control = "stop"
    m.video.visible = false
    m.panel.visible = false
    m.lyricsPanel.visible = false
    m.heartbeatTimer.control = "stop"
    endPlayerUi()
    m.rows.setFocus(true)
end sub

' After a panel closes (or something that needed the video focus ends): focus is the controls again.
sub focusPlayer()
    if m.p <> invalid
        m.p.panel = ""
        m.p.panelView = "root"
        m.top.setFocus(true)
        showOverlay()
    else
        m.video.setFocus(true)
    end if
end sub

sub applyEpisodes(seasons as Object)
    p = m.p
    if p = invalid then return
    m.showCache = { showId: p.showId, seasons: seasons }
    p.seasons = seasons
    neighbours = plNeighbours(seasons, p.assetId)
    p.preceding = neighbours.preceding
    p.following = neighbours.following
    p.seasonIndex = neighbours.season
    current = neighbours.current
    if current <> invalid
        if current.description <> "" then p.description = current.description
        if p.epTitle = "" then p.epTitle = current.title
        if p.season = 0 then p.season = current.season
        if p.episode = 0 then p.episode = current.number
        if p.workId = ""
            p.workId = current.workId
            if p.workId <> "" then runSide("bookmarks", { workId: p.workId })
        end if
    end if
    refreshPlayer()
end sub

' ---- Drawing -----------------------------------------------------------------------------------------------------

function currentTransport() as Object
    p = m.p
    skip = plSkipSeconds(p.prefs, p.device)
    playing = m.video.state = "playing"
    return plTransport(p.kind, skip, { playing: playing, canPrevious: p.preceding <> invalid, canNext: p.following <> invalid })
end function

function currentTools() as Object
    p = m.p
    return plTools(p.kind, { hasEpisodes: p.seasons.Count() > 0, canBookmark: p.workId <> "" })
end function

sub drawOverlay()
    p = m.p
    if p = invalid then return
    tools = currentTools()
    if p.zone = "tools" and p.index >= tools.Count() then p.index = tools.Count() - 1
    position = plNum(m.video.position)
    duration = plNum(m.video.duration)
    line = plDetailLine(p.kind, { season: p.season, episode: p.episode, title: p.epTitle, year: p.year, seconds: duration })
    panelOpen = p.panel <> ""
    area = 1920
    if panelOpen then area = 1220
    paused = m.video.state = "paused"
    showDescription = paused and not panelOpen and p.description <> "" and p.kind <> "film"
    ticks = []
    if p.ticks then ticks = plBookmarkTicks(plBookmarkSets(p.bookmarks).mine, duration)
    card = invalid
    if p.endCard
        nextTitle = ""
        if p.following <> invalid then nextTitle = p.following.title
        card = plEndCardView(nextTitle, p.endRemaining)
        card.focus = p.endFocus
    end if
    m.overlay.model = {
        areaWidth: area,
        title: p.title,
        line: line,
        description: p.description,
        showDescription: showDescription,
        compact: panelOpen,
        zone: p.zone,
        index: p.index,
        selectedTool: p.panel,
        transport: currentTransport(),
        tools: tools,
        ticks: ticks,
        endCard: card
    }
    m.overlay.progress = { position: position, duration: duration }
end sub

sub refreshPlayer()
    if m.p = invalid then return
    drawOverlay()
    if m.p.panel <> "" and m.p.panel <> "audio-subtitles" then drawPanel()
end sub

sub showOverlay()
    if m.p = invalid then return
    m.p.overlayVisible = true
    m.overlay.visible = true
    drawOverlay()
    m.overlayTimer.control = "stop"
    m.overlayTimer.control = "start"
end sub

sub hideOverlay()
    if m.p = invalid then return
    m.p.overlayVisible = false
    m.overlay.visible = false
end sub

' The overlay hides 5 seconds after the last key, but never while a panel, the Find window or the End card is open.
sub onOverlayTimer()
    p = m.p
    if p = invalid then return
    paused = m.video.state = "paused"
    if plOverlayMayHide(p.panel <> "", m.findWindow.visible, p.endCard, paused) then hideOverlay()
end sub

' Once a second, from the video's position: the timeline, and the End card in the last seconds of an episode.
sub playerTick()
    p = m.p
    if p = invalid then return
    position = plNum(m.video.position)
    duration = plNum(m.video.duration)
    if p.following <> invalid and duration > 0 and position > 0
        remaining = duration - position
        if remaining <= plEndCardSeconds() and not p.endCancelled
            p.endRemaining = -1
            if plAutoplayOn(p.prefs, p.device) then p.endRemaining = Int(remaining + 0.99)
            if not p.endCard
                p.endCard = true
                p.endFocus = 1
                if p.panel <> "" then closeAnyPanel()
            end if
            p.overlayVisible = true
            m.overlay.visible = true
            drawOverlay()
            return
        else if p.endCard and remaining > plEndCardSeconds() + 1
            p.endCard = false
            drawOverlay()
        end if
    end if
    if p.overlayVisible then m.overlay.progress = { position: position, duration: duration }
end sub

' The Video node's state: Play reads Pause while playing, and a paused video keeps its details up.
sub playerState()
    p = m.p
    if p = invalid then return
    state = m.video.state
    if state = "paused"
        showOverlay()
    else if state = "playing"
        drawOverlay()
        m.overlayTimer.control = "stop"
        m.overlayTimer.control = "start"
    end if
end sub

' The video ended. Returns true when the player carries on (the next episode, or the End card waiting with autoplay off).
function playerFinished() as Boolean
    p = m.p
    if p = invalid then return false
    if p.following = invalid or p.endCancelled then return false
    if plAutoplayOn(p.prefs, p.device)
        playEpisode(p.following)
    else
        p.endCard = true
        p.endRemaining = -1
        p.overlayVisible = true
        m.overlay.visible = true
        drawOverlay()
    end if
    return true
end function

' ---- Keys (the contract's tv-keys table) ---------------------------------------------------------------------------

function playerKey(key as String) as Boolean
    p = m.p
    state = { overlayVisible: p.overlayVisible, panelOpen: p.panel <> "", panelView: p.panelView, endCard: p.endCard, zone: p.zone }
    effect = plKeyEffect(state, key)
    if effect = "none" then return false
    if effect = "show-overlay"
        showOverlay()
    else if effect = "hide-overlay"
        hideOverlay()
    else if effect = "close-panel"
        closeAnyPanel()
    else if effect = "panel-root"
        p.panelView = "root"
        drawPanel()
    else if effect = "close-player"
        closePlayer()
    else if effect = "cancel-end-card"
        cancelEndCard()
    else if effect = "skip-back" or effect = "skip-forward"
        skipBy(effect)
        showOverlay()
    else if effect = "toggle-play"
        togglePlay()
        showOverlay()
    else if effect = "options"
        if p.panel = "" then openPanel("audio-subtitles")
    else if effect = "select"
        activate()
    else
        moveFocus(effect)
    end if
    return true
end function

sub moveFocus(direction as String)
    p = m.p
    if p.endCard
        if direction = "left" then p.endFocus = 0
        if direction = "right" then p.endFocus = 1
        drawOverlay()
        return
    end if
    moved = plMoveFocus(p.zone, p.index, direction, currentTransport(), currentTools().Count(), p.panel <> "")
    if moved.enterPanel
        focusPanel()
        return
    end if
    p.zone = moved.zone
    p.index = moved.index
    showOverlay()
end sub

sub activate()
    p = m.p
    if p.endCard
        if p.endFocus = 1 then playEpisode(p.following) else cancelEndCard()
        return
    end if
    if p.zone = "tools"
        tools = currentTools()
        if p.index < tools.Count() then openPanel(tools[p.index].key)
        return
    end if
    if p.zone = "transport"
        spec = currentTransport()[p.index]
        if plIsTrue(spec.dimmed) then return
        if spec.key = "skip-back" or spec.key = "skip-forward"
            skipBy(spec.key)
        else if spec.key = "previous"
            playEpisode(p.preceding)
        else if spec.key = "next"
            playEpisode(p.following)
        else
            togglePlay()
        end if
    else
        togglePlay()
    end if
    showOverlay()
end sub

sub togglePlay()
    if m.video.state = "playing"
        m.video.control = "pause"
    else if m.video.state = "paused"
        m.video.control = "resume"
    end if
end sub

' Skip back and Skip forward by the profile's seconds (10 and 30 by default); Rewind and Fast-forward do the same.
sub skipBy(direction as String)
    p = m.p
    skip = plSkipSeconds(p.prefs, p.device)
    target = plNum(m.video.position)
    if direction = "skip-back" then target = target - skip.back else target = target + skip.forward
    duration = plNum(m.video.duration)
    if duration > 1 and target > duration - 1 then target = duration - 1
    if target < 0 then target = 0
    m.video.seek = target
end sub

sub cancelEndCard()
    p = m.p
    p.endCancelled = true
    p.endCard = false
    ' A video that already ended (autoplay off) has nothing left to show.
    if m.video.state = "finished" then closePlayer() else drawOverlay()
end sub

' Previous, Next, a row of the Episodes panel and Play now: start another episode of the show.
sub playEpisode(item as Dynamic)
    p = m.p
    if p = invalid or item = invalid then return
    ' The position reached in this episode is saved before another one starts.
    sendHeartbeat()
    meta = emptyMeta()
    meta.workId = item.workId
    meta.showId = p.showId
    meta.subject = "TvEpisode"
    meta.season = item.season
    meta.episode = item.number
    meta.epTitle = item.title
    meta.playTitle = p.title
    meta.description = item.description
    m.pendingMeta = meta
    m.lastTitle = p.title
    p.endCard = false
    closeAnyPanel()
    runSide("manifest", { assetId: item.assetId })
end sub

' ---- Panels ------------------------------------------------------------------------------------------------------

sub openPanel(name as String)
    p = m.p
    if p.zone = "timeline" then p.zone = "tools"
    p.panel = name
    p.panelView = "root"
    if name = "audio-subtitles"
        m.sidePanel.visible = false
        drawOverlay()
        openAudioSubtitles()
        return
    end if
    m.panel.visible = false
    drawPanel()
    m.sidePanel.visible = true
    m.sidePanel.focusNow = true
    drawOverlay()
end sub

' Closes whichever panel is open and gives focus back to the controls.
sub closeAnyPanel()
    p = m.p
    if p = invalid then return
    if p.panel = "audio-subtitles"
        closePanel(true)
    else
        m.sidePanel.visible = false
        focusPlayer()
    end if
end sub

sub focusPanel()
    if m.p.panel = "audio-subtitles"
        m.panel.focusNow = true
    else
        m.sidePanel.focusNow = true
    end if
end sub

' Left from a panel: focus returns to the tool that opened it, which stays highlighted.
sub onSideLeave()
    p = m.p
    if p = invalid then return
    tools = currentTools()
    for i = 0 to tools.Count() - 1
        if tools[i].key = p.panel then p.index = i
    end for
    p.zone = "tools"
    m.top.setFocus(true)
    drawOverlay()
end sub

sub onSideClose()
    closeAnyPanel()
end sub

sub drawPanel()
    p = m.p
    if p = invalid then return
    model = invalid
    skip = plSkipSeconds(p.prefs, p.device)
    if p.panel = "episodes"
        nextId = ""
        if p.following <> invalid then nextId = p.following.assetId
        model = plEpisodesModel(p.seasons, { assetId: p.assetId, playing: m.video.state = "playing", position: m.video.position, duration: m.video.duration, nextAssetId: nextId, seasonIndex: p.seasonIndex, autoplay: plAutoplayOn(p.prefs, p.device) })
        if plIsTrue(p.keepTopRow) then model.focus = 0
    else if p.panel = "bookmarks"
        model = plBookmarksModel({ items: p.bookmarks, choice: p.bmTab, position: m.video.position, ticks: p.ticks, failed: p.bmFailed, loadError: p.bmLoadError })
        if plIsTrue(p.keepTopRow) then model.focus = 0
    else if p.panel = "playback-settings"
        if p.panelView = "skip-back" or p.panelView = "skip-forward"
            model = plSkipChoicesModel(p.panelView, skip)
        else if p.panelView = "info"
            model = plInfoModel(p.manifest)
        else
            model = plSettingsModel(skip)
        end if
    end if
    p.keepTopRow = false
    if model <> invalid then m.sidePanel.model = model
end sub

sub onSideSelection()
    selection = m.sidePanel.selection
    p = m.p
    if selection = invalid or p = invalid then return
    kind = selection.kind
    if kind = "options"
        if p.panel = "bookmarks" and selection.row.kind = "bookmark" and plIsTrue(selection.row.canChange) then bookmarkMenu(selection.row)
        return
    end if
    if p.panel = "episodes"
        if kind = "episode"
            chooseEpisode(selection.value)
        else if kind = "switch"
            p.device.autoplay = not plAutoplayOn(p.prefs, p.device)
            m.registry.Write("autoplay", boolFlag(p.device.autoplay))
            m.registry.Flush()
            drawPanel()
        end if
    else if p.panel = "bookmarks"
        if kind = "add"
            p.bmFailed = false
            runSide("bookmarkCreate", { workId: p.workId, body: plBookmarkRequest(p.assetId, m.video.position, m.video.duration) })
        else if kind = "bookmark"
            m.video.seek = selection.row.seconds
        else if kind = "switch"
            p.ticks = not p.ticks
            drawPanel()
            drawOverlay()
        end if
    else if p.panel = "playback-settings"
        if p.panelView = "root"
            if kind = "value"
                p.panelView = selection.value
                drawPanel()
            else if kind = "action"
                resetDeviceSettings()
            end if
        else if kind = "plain"
            chooseSkip(selection.value)
        end if
    end if
end sub

function boolFlag(value as Boolean) as String
    if value then return "1"
    return "0"
end function

' Left and Right on the Season row or on Mine | Global.
sub onSideAdjust()
    adjust = m.sidePanel.adjust
    p = m.p
    if adjust = invalid or p = invalid then return
    if adjust.kind = "season"
        count = p.seasons.Count()
        if count > 1
            p.seasonIndex = p.seasonIndex + adjust.direction
            if p.seasonIndex < 0 then p.seasonIndex = count - 1
            if p.seasonIndex >= count then p.seasonIndex = 0
        end if
    else if adjust.kind = "segment"
        if adjust.direction < 0 then p.bmTab = "mine" else p.bmTab = "global"
        if plIsTrue(adjust.viaSelect)
            if adjust.value = "global" then p.bmTab = "mine" else p.bmTab = "global"
        end if
    end if
    p.keepTopRow = true
    drawPanel()
end sub

sub chooseEpisode(assetId as String)
    p = m.p
    if LCase(assetId) = LCase(p.assetId)
        closeAnyPanel()
        return
    end if
    for each item in plFlatten(p.seasons)
        if LCase(item.assetId) = LCase(assetId) then playEpisode(item)
    end for
end sub

' ---- Playback settings: this Roku's own choices -----------------------------------------------------------------

function readDeviceSettings() as Object
    device = {}
    back = m.registry.Read("skip_back")
    if back <> "" then device.skipBack = back.ToInt()
    forward = m.registry.Read("skip_forward")
    if forward <> "" then device.skipForward = forward.ToInt()
    autoplay = m.registry.Read("autoplay")
    if autoplay <> "" then device.autoplay = autoplay = "1"
    return device
end function

sub chooseSkip(value as String)
    p = m.p
    seconds = value.ToInt()
    if seconds <= 0 then return
    if p.panelView = "skip-back"
        p.device.skipBack = seconds
        m.registry.Write("skip_back", value)
    else
        p.device.skipForward = seconds
        m.registry.Write("skip_forward", value)
    end if
    m.registry.Flush()
    p.panelView = "root"
    drawPanel()
    drawOverlay()
end sub

' "Reset playback preferences": this Roku's own choices go and the profile's saved ones apply again.
sub resetDeviceSettings()
    p = m.p
    m.registry.Delete("skip_back")
    m.registry.Delete("skip_forward")
    m.registry.Delete("autoplay")
    m.registry.Flush()
    p.device = {}
    drawPanel()
    drawOverlay()
end sub

' ---- Bookmarks: Edit (rename with Roku's keyboard) and Delete -------------------------------------------------------

sub bookmarkMenu(row as Object)
    words = plWords()
    m.bmTarget = row
    dialog = CreateObject("roSGNode", "Dialog")
    dialog.title = row.title + plSep() + row.second
    dialog.buttons = [words.edit, words.remove]
    dialog.observeField("buttonSelected", "onBookmarkMenu")
    m.menuDialog = dialog
    m.top.dialog = dialog
end sub

sub onBookmarkMenu()
    index = m.menuDialog.buttonSelected
    m.menuDialog.close = true
    if m.bmTarget = invalid then return
    if index = 0 then editBookmark() else confirmDelete()
end sub

sub editBookmark()
    words = plWords()
    dialog = CreateObject("roSGNode", "KeyboardDialog")
    dialog.title = words.name
    dialog.text = m.bmTarget.name
    dialog.buttons = [words.save, words.cancel]
    dialog.observeField("buttonSelected", "onBookmarkRename")
    m.renameDialog = dialog
    m.top.dialog = dialog
end sub

sub onBookmarkRename()
    dialog = m.renameDialog
    index = dialog.buttonSelected
    text = dialog.text
    dialog.close = true
    if index = 0 and m.p <> invalid and m.bmTarget <> invalid
        m.p.bmFailed = false
        runSide("bookmarkUpdate", { id: m.bmTarget.value, body: { label: text, note: m.bmTarget.note } })
    end if
end sub

' Delete asks once: Delete or Keep.
sub confirmDelete()
    words = plWords()
    dialog = CreateObject("roSGNode", "Dialog")
    dialog.title = words.remove + plSep() + m.bmTarget.title
    dialog.buttons = [words.remove, words.keep]
    dialog.observeField("buttonSelected", "onBookmarkDelete")
    m.deleteDialog = dialog
    m.top.dialog = dialog
end sub

sub onBookmarkDelete()
    index = m.deleteDialog.buttonSelected
    m.deleteDialog.close = true
    if index = 0 and m.p <> invalid and m.bmTarget <> invalid
        m.p.bmFailed = false
        runSide("bookmarkDelete", { id: m.bmTarget.value })
    end if
end sub

' ---- Answers from the Engine (side tasks) -------------------------------------------------------------------------

' Returns true when the answer belonged to the player's controls.
function handlePlayerResult(operation as String, value as Dynamic, payload as Dynamic) as Boolean
    p = m.p
    if operation = "manifest"
        playManifest(value)
        return true
    end if
    if operation = "playerPreferences"
        m.prefs = value
        if p <> invalid
            p.prefs = value
            refreshPlayer()
        end if
        return true
    end if
    if p = invalid then return operation = "showEpisodes" or operation = "bookmarks" or operation = "bookmarkCreate" or operation = "bookmarkUpdate" or operation = "bookmarkDelete"
    if operation = "showEpisodes"
        if payload <> invalid and payload.id = p.showId then applyEpisodes(plEpisodeSeasons(value))
        return true
    else if operation = "bookmarks"
        p.bookmarks = plList(value)
        p.bmLoadError = ""
        refreshPlayer()
        return true
    else if operation = "bookmarkCreate" or operation = "bookmarkUpdate" or operation = "bookmarkDelete"
        ' The Engine decides which bookmarks are the profile's own, so the list is read again.
        runSide("bookmarks", { workId: p.workId })
        return true
    end if
    return false
end function

function handlePlayerError(operation as String, error as Dynamic) as Boolean
    p = m.p
    if operation = "manifest"
        message = "Playback could not start."
        if error <> invalid and faStr(error.description) <> "" then message = error.description
        m.status.text = message
        return true
    end if
    if operation = "playerPreferences" or operation = "showEpisodes"
        ' Nothing known: the defaults stand and the Episodes tool stays absent.
        return true
    end if
    if operation = "bookmarks"
        if p <> invalid
            ' An error line, never the empty state: the list could not be read.
            p.bookmarks = []
            p.bmLoadError = "Bookmarks could not be read."
            if error <> invalid and faStr(error.description) <> "" then p.bmLoadError = error.description
        end if
        refreshPlayer()
        return true
    end if
    if operation = "bookmarkCreate" or operation = "bookmarkUpdate" or operation = "bookmarkDelete"
        if p <> invalid then p.bmFailed = true
        refreshPlayer()
        return true
    end if
    return false
end function
