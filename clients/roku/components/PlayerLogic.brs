' Roku player logic (spec native-apps WP6). Plain functions with no screen, no `m` and no network, so they can be read and
' checked on their own: the transport and tool rows, the lines of text the player shows, the key map of the television
' layout, the overlay rule, the end card, and the rows of the Episodes, Bookmarks and Playback settings panels.
' Every fixed word comes from the Televisions section and the Panels table of clients/contracts/v1/player-controls.md; the
' layout is the approved mock-up engineering/design/mockups/tv-player/tv-player-panels.html. The reference for the same
' decisions is clients/tv-web/player-logic.js.

' ---- Words -----------------------------------------------------------------------------------------------------

function plWords() as Object
    return {
        episodes: "Episodes",
        audioSubtitles: "Audio & subtitles",
        bookmarks: "Bookmarks",
        playbackSettings: "Playback settings",
        close: "Close",
        play: "Play",
        pause: "Pause",
        season: "Season",
        autoplay: "Autoplay next episode",
        upNext: "Up next",
        playing: "Playing",
        paused: "Paused",
        bookmarkAt: "Bookmark at {0}",
        mine: "Mine",
        globalName: "Global",
        showOnTimeline: "Show on timeline",
        edit: "Edit",
        remove: "Delete",
        keep: "Keep",
        name: "Name",
        save: "Save",
        cancel: "Cancel",
        couldNotSave: "Couldn’t save · Try again",
        noGlobal: "No Global bookmarks on this title yet",
        noMine: "No bookmarks of yours yet",
        selectBookmark: "Select a bookmark to jump to that moment.",
        skipBack: "Skip back",
        skipForward: "Skip forward",
        playbackInformation: "Playback information",
        resetPreferences: "Reset playback preferences",
        endHeading: "Up next",
        endCountdown: "Starts in {0} seconds",
        nextEpisode: "Next episode",
        playNow: "Play now"
    }
end function

function plSep() as String
    return " · "
end function

' The overlay hides after 5 seconds with no input; the End card counts down from 10.
function plOverlaySeconds() as Float
    return 5.0
end function

function plEndCardSeconds() as Integer
    return 10
end function

' ---- Small readers over parsed JSON --------------------------------------------------------------------------------

' True only for a real Boolean true: a missing key, or any other type, is false (comparing a missing value to true is an error).
function plIsTrue(value as Dynamic) as Boolean
    if value = invalid then return false
    kind = Type(value)
    if kind = "roBoolean" or kind = "Boolean" then return value = true
    return false
end function

function plStr(value as Dynamic) as String
    if value = invalid then return ""
    kind = Type(value)
    if kind = "roString" or kind = "String" then return value
    return ""
end function

' A wire number as a Float; the fallback when it is missing or not a number.
function plNum(value as Dynamic, fallback = 0.0 as Float) as Dynamic
    if value = invalid then return fallback
    kind = Type(value)
    if kind = "roInt" or kind = "roInteger" or kind = "Integer" or kind = "roFloat" or kind = "Float" or kind = "roDouble" or kind = "Double" or kind = "roLongInteger" or kind = "LongInteger"
        return value + 0.0
    end if
    return fallback
end function

function plList(value as Dynamic) as Object
    if value = invalid then return []
    kind = Type(value)
    if kind = "roArray" or kind = "Array" then return value
    return []
end function

function plFill(template as String, a as String) as String
    return template.Replace("{0}", a)
end function

' ---- Time and length ---------------------------------------------------------------------------------------------

function plTwo(value as Integer) as String
    text = value.ToStr()
    if Len(text) < 2 then text = "0" + text
    return text
end function

' "7:42", or "1:05:09" from an hour.
function plFormatTime(seconds as Dynamic) as String
    total = Int(plNum(seconds))
    if total < 0 then total = 0
    hours = total \ 3600
    minutes = (total - hours * 3600) \ 60
    secs = total mod 60
    if hours > 0 then return hours.ToStr() + ":" + plTwo(minutes) + ":" + plTwo(secs)
    return minutes.ToStr() + ":" + plTwo(secs)
end function

' The time left at the right of the timeline, "-14:59"; nothing while the length is not known.
function plTimeLeft(position as Dynamic, duration as Dynamic) as String
    length = plNum(duration)
    if length <= 0 then return ""
    remaining = length - plNum(position)
    if remaining < 0 then remaining = 0
    return "-" + plFormatTime(remaining)
end function

' A length under an hour reads "22 min", from an hour "1h 52m"; nothing when it is not known.
function plLengthText(seconds as Dynamic) as String
    total = plNum(seconds)
    if total <= 0 then return ""
    minutes = Int(total / 60)
    if minutes < 1 then minutes = 1
    if minutes < 60 then return minutes.ToStr() + " min"
    return (minutes \ 60).ToStr() + "h " + (minutes mod 60).ToStr() + "m"
end function

' ---- What is playing -----------------------------------------------------------------------------------------------

' "episode", "lesson" (a folder video), "film", "music" or "audiobook".
function plKindOf(mediaType as String, showId as String, subject as String) as String
    value = LCase(mediaType)
    if Instr(1, value, "audiobook") > 0 then return "audiobook"
    if Instr(1, value, "music") > 0 or Instr(1, value, "song") > 0 or Instr(1, value, "track") > 0 then return "music"
    if showId <> "" or LCase(subject) = "tvepisode" or Instr(1, value, "episode") > 0 or value = "tv" then return "episode"
    if value = "videos" or value = "video" then return "lesson"
    return "film"
end function

' The player draws its own overlay for video; music and audiobooks keep Roku's own playback screen.
function plHasOverlay(kind as String) as Boolean
    return kind = "episode" or kind = "film" or kind = "lesson"
end function

' "S1 E1 · The First Wish · 22 min" (episode), "Ball handling · 12 min" (folder video), "2019 · 1h 52m" (film).
' info: { season, episode, title, year, seconds }
function plDetailLine(kind as String, info as Object) as String
    parts = []
    length = plLengthText(info.seconds)
    if kind = "episode"
        season = Int(plNum(info.season))
        episode = Int(plNum(info.episode))
        code = ""
        if season > 0 and episode > 0
            code = "S" + season.ToStr() + " E" + episode.ToStr()
        else if season > 0
            code = "S" + season.ToStr()
        else if episode > 0
            code = "E" + episode.ToStr()
        end if
        if code <> "" then parts.Push(code)
        if plStr(info.title) <> "" then parts.Push(plStr(info.title))
    else if kind = "lesson"
        if plStr(info.title) <> "" then parts.Push(plStr(info.title))
    else
        year = Int(plNum(info.year))
        if year > 0 then parts.Push(year.ToStr())
    end if
    if length <> "" then parts.Push(length)
    line = ""
    for each part in parts
        if line <> "" then line = line + plSep()
        line = line + part
    end for
    return line
end function

' ---- Preferences ------------------------------------------------------------------------------------------------------

function plWholeSeconds(value as Dynamic, fallback as Integer) as Integer
    n = Int(plNum(value))
    if n > 0 and n <= 600 then return n
    return fallback
end function

' The seconds the skip buttons, Rewind and Fast-forward use: a choice made on this Roku (Playback settings), else the
' profile's saved pair from GET /api/v1/devices/current/playback-preferences, else 10 back and 30 forward.
' prefs and device may be invalid.
function plSkipSeconds(prefs as Dynamic, device as Dynamic) as Object
    back = 10
    forward = 30
    if prefs <> invalid
        back = plWholeSeconds(prefs.skipBackSeconds, back)
        forward = plWholeSeconds(prefs.skipForwardSeconds, forward)
    end if
    if device <> invalid
        back = plWholeSeconds(device.skipBack, back)
        forward = plWholeSeconds(device.skipForward, forward)
    end if
    return { back: back, forward: forward }
end function

' Autoplay next episode: this Roku's own choice, else the profile's preference, else on.
function plAutoplayOn(prefs as Dynamic, device as Dynamic) as Boolean
    if device <> invalid and device.autoplay <> invalid then return plIsTrue(device.autoplay)
    if prefs <> invalid and prefs.autoplayNextEpisode <> invalid then return plIsTrue(prefs.autoplayNextEpisode)
    return true
end function

function plSkipBackChoices() as Object
    return [5, 10, 15, 30]
end function

function plSkipForwardChoices() as Object
    return [10, 30, 60, 90]
end function

' ---- Transport and tools -----------------------------------------------------------------------------------------

' The five buttons of the TV episode and film tables, in that order. A button that cannot act is dimmed in place.
' ctx: { playing, canPrevious, canNext }
function plTransport(kind as String, skip as Object, ctx as Object) as Object
    playLabel = plWords().play
    if plIsTrue(ctx.playing) then playLabel = plWords().pause
    previousLabel = "Previous"
    nextLabel = "Next"
    if kind = "episode"
        previousLabel = "Previous episode"
        nextLabel = "Next episode"
    end if
    return [
        { key: "previous", label: previousLabel, dimmed: not plIsTrue(ctx.canPrevious), seconds: 0 },
        { key: "skip-back", label: "Skip back " + skip.back.ToStr() + " s", dimmed: false, seconds: skip.back },
        { key: "play-pause", label: playLabel, dimmed: false, seconds: 0 },
        { key: "skip-forward", label: "Skip forward " + skip.forward.ToStr() + " s", dimmed: false, seconds: skip.forward },
        { key: "next", label: nextLabel, dimmed: not plIsTrue(ctx.canNext), seconds: 0 }
    ]
end function

' The tool row in the Dashboard's order, without Volume, Fullscreen, Close and Picture in picture (a television owns them).
' Episodes only when the show lists its episodes; Bookmarks only when the item supports them. Playback speed is not a row
' anywhere: Roku's player cannot change speed. caps: { hasEpisodes, canBookmark }
function plTools(kind as String, caps as Object) as Object
    words = plWords()
    tools = []
    if kind = "episode" and plIsTrue(caps.hasEpisodes) then tools.Push({ key: "episodes", label: words.episodes })
    tools.Push({ key: "audio-subtitles", label: words.audioSubtitles })
    if plIsTrue(caps.canBookmark) then tools.Push({ key: "bookmarks", label: words.bookmarks })
    tools.Push({ key: "playback-settings", label: words.playbackSettings })
    return tools
end function

' ---- Keys (the contract's tv-keys table) --------------------------------------------------------------------------

' What a key does while a video plays on the overlay. state: { overlayVisible, panelOpen, panelView, endCard, zone }.
' zone is "timeline", "transport" or "tools". Returns the effect the player carries out:
'   show-overlay, hide-overlay, close-panel, panel-root, close-player, cancel-end-card, skip-back, skip-forward, toggle-play,
'   options, left, right, up, down, select, none
function plKeyEffect(state as Object, key as String) as String
    if plIsTrue(state.endCard)
        if key = "back" then return "cancel-end-card"
        if key = "left" or key = "right" then return key
        if key = "OK" then return "select"
    end if
    if key = "back"
        if plIsTrue(state.panelOpen)
            if state.panelView <> "root" then return "panel-root"
            return "close-panel"
        end if
        if plIsTrue(state.overlayVisible) then return "hide-overlay"
        return "close-player"
    end if
    if key = "rev" then return "skip-back"
    if key = "fwd" then return "skip-forward"
    if key = "play" then return "toggle-play"
    if key = "options" then return "options"
    if not plIsTrue(state.overlayVisible) then return "show-overlay"
    if (key = "left" or key = "right") and state.zone = "timeline"
        if key = "left" then return "skip-back"
        return "skip-forward"
    end if
    if key = "OK" then return "select"
    if key = "left" or key = "right" or key = "up" or key = "down" then return key
    return "none"
end function

' The overlay hides 5 seconds after the last key, but never while a panel, the Find window or the End card is open, nor while
' the video is paused (the paused details stay up).
function plOverlayMayHide(panelOpen as Boolean, finderOpen as Boolean, endCard as Boolean, paused as Boolean) as Boolean
    return not panelOpen and not finderOpen and not endCard and not paused
end function

' Where focus goes on an arrow key. transport: [{ dimmed }]; toolCount: how many tools. Focus steps over a dimmed button.
' Returns { zone, index, enterPanel }.
function plMoveFocus(zone as String, index as Integer, key as String, transport as Object, toolCount as Integer, panelOpen as Boolean) as Object
    result = { zone: zone, index: index, enterPanel: false }
    if zone = "timeline"
        if key = "down"
            result.zone = "transport"
            result.index = 2
        end if
    else if zone = "transport"
        if key = "up"
            ' The timeline is not drawn while a panel is open.
            if not panelOpen then result.zone = "timeline"
        else if key = "down"
            if toolCount > 0
                result.zone = "tools"
                result.index = 0
                if index < toolCount then result.index = index
            end if
        else
            delta = 1
            if key = "left" then delta = -1
            target = index + delta
            while target >= 0 and target < transport.Count()
                if not plIsTrue(transport[target].dimmed) then
                    result.index = target
                    exit while
                end if
                target = target + delta
            end while
        end if
    else if zone = "tools"
        if key = "up"
            result.zone = "transport"
            result.index = 2
        else if key = "left"
            if index > 0 then result.index = index - 1
        else if key = "right"
            if index < toolCount - 1
                result.index = index + 1
            else if panelOpen
                result.enterPanel = true
            end if
        end if
    end if
    return result
end function

' ---- End card ----------------------------------------------------------------------------------------------------

' The end card's rows. remaining is -1 while autoplay is off: the card stays and has no countdown.
function plEndCardView(nextTitle as String, remaining as Integer) as Object
    words = plWords()
    countdown = ""
    if remaining >= 0 then countdown = plFill(words.endCountdown, remaining.ToStr())
    return { heading: words.endHeading, countdown: countdown, title: nextTitle, kind: words.nextEpisode, cancel: words.cancel, playNow: words.playNow }
end function

' ---- Episodes ----------------------------------------------------------------------------------------------------

' The Seasons of a show's details answer (owned episodes only): [{ key, title, items: [{ assetId, workId, season, number,
' title, description, duration, positionSeconds, durationSeconds, state }] }].
function plEpisodeSeasons(detail as Dynamic) as Object
    seasons = []
    if detail = invalid then return seasons
    placement = detail.sequencePlacement
    if placement = invalid then return seasons
    index = 0
    for each group in plList(placement.groups)
        items = []
        for each item in plList(group.items)
            context = item.episodeContext
            if context <> invalid and plStr(context.episodeAssetId) <> "" and (item.isOwned = invalid or plIsTrue(item.isOwned))
                items.Push({
                    assetId: plStr(context.episodeAssetId),
                    workId: plStr(context.episodeWorkId),
                    season: Int(plNum(context.seasonNumber)),
                    number: Int(plNum(context.episodeNumber)),
                    title: plStr(item.title),
                    description: plStr(item.description),
                    duration: plStr(item.duration),
                    positionSeconds: plNum(context.positionSeconds),
                    durationSeconds: plNum(context.durationSeconds),
                    state: plStr(context.state)
                })
            end if
        end for
        if items.Count() > 0
            title = plStr(group.title)
            if title = "" then title = plWords().season + " " + items[0].season.ToStr()
            seasons.Push({ key: plStr(group.key), title: title, items: items })
        end if
        index = index + 1
    end for
    return seasons
end function

function plFlatten(seasons as Object) as Object
    all = []
    for each season in seasons
        for each item in season.items
            all.Push(item)
        end for
    end for
    return all
end function

' The episode before and after the one in play, across Seasons: { preceding, following, season, current }, each invalid where none.
function plNeighbours(seasons as Object, assetId as String) as Object
    all = plFlatten(seasons)
    result = { preceding: invalid, following: invalid, season: 0, current: invalid }
    for i = 0 to all.Count() - 1
        if LCase(all[i].assetId) = LCase(assetId)
            result.current = all[i]
            if i > 0 then result.preceding = all[i - 1]
            if i < all.Count() - 1 then result.following = all[i + 1]
        end if
    end for
    for s = 0 to seasons.Count() - 1
        for each item in seasons[s].items
            if LCase(item.assetId) = LCase(assetId) then result.season = s
        end for
    end for
    return result
end function

' "20 episodes" (a single one reads in the singular).
function plCountLabel(count as Integer) as String
    if count = 1 then return "1 episode"
    return count.ToStr() + " episodes"
end function

' The Episodes panel's model: the Season row (Left and Right change the Season), one row per episode, and the Autoplay switch.
' The current row reads "Playing · 2:23 of 22:41" (or "Paused"); another left part-way reads "Paused · 4:10 of 22:00"; the next
' one carries the "Up next" chip. ctx: { assetId, playing, position, duration, nextAssetId, seasonIndex, autoplay }
function plEpisodesModel(seasons as Object, ctx as Object) as Object
    words = plWords()
    rows = []
    focus = 1
    seasonIndex = ctx.seasonIndex
    if seasonIndex < 0 or seasonIndex >= seasons.Count() then seasonIndex = 0
    if seasons.Count() = 0 then return { title: words.episodes, back: false, rows: rows, footer: [], focus: 0 }
    season = seasons[seasonIndex]
    rows.Push({ kind: "season", title: season.title, second: plCountLabel(season.items.Count()), value: seasonIndex.ToStr(), adjustable: true })
    for each item in season.items
        current = LCase(item.assetId) = LCase(ctx.assetId)
        title = item.title
        if item.number > 0 then title = item.number.ToStr() + plSep() + item.title
        second = ""
        if current
            state = words.paused
            if plIsTrue(ctx.playing) then state = words.playing
            second = state + plSep() + plFormatTime(ctx.position) + " of " + plFormatTime(ctx.duration)
            focus = rows.Count()
        else if item.state = "InProgress" and item.positionSeconds > 0 and item.durationSeconds > 0
            second = words.paused + plSep() + plFormatTime(item.positionSeconds) + " of " + plFormatTime(item.durationSeconds)
        else
            second = item.duration
            if item.description <> "" then
                if second <> "" then second = second + plSep()
                second = second + item.description
            end if
        end if
        chip = ""
        if not current and ctx.nextAssetId <> "" and LCase(ctx.nextAssetId) = LCase(item.assetId) then chip = words.upNext
        rows.Push({ kind: "episode", title: title, second: second, chip: chip, value: item.assetId })
    end for
    footer = [{ kind: "switch", title: words.autoplay, on: plIsTrue(ctx.autoplay), value: "autoplay" }]
    return { title: words.episodes, back: false, rows: rows, footer: footer, focus: focus }
end function

' ---- Bookmarks ---------------------------------------------------------------------------------------------------

function plBookmarkAtLabel(position as Dynamic) as String
    return plFill(plWords().bookmarkAt, plFormatTime(position))
end function

' Mine | Global. The Engine decides who sees what: Mine is isYours and Global is inEveryone, as in the Dashboard. The choice shows
' only when a Global set exists; Mine is first and the default. Returns { showChoice, mine, everyone }.
function plBookmarkSets(items as Object) as Object
    mine = []
    everyone = []
    for each item in plList(items)
        if plIsTrue(item.isYours) then mine.Push(item)
        if plIsTrue(item.inEveryone) then everyone.Push(item)
    end for
    return { showChoice: everyone.Count() > 0, mine: mine, everyone: everyone }
end function

' The rows of one tab in time order: the time, then the name or note (the maker's name on Global).
function plBookmarkRows(items as Object, whichTab as String) as Object
    sorted = []
    for each item in plList(items)
        sorted.Push(item)
    end for
    for i = 1 to sorted.Count() - 1
        entry = sorted[i]
        j = i - 1
        while j >= 0 and plNum(sorted[j].positionSeconds) > plNum(entry.positionSeconds)
            sorted[j + 1] = sorted[j]
            j = j - 1
        end while
        sorted[j + 1] = entry
    end for
    rows = []
    for each item in sorted
        words = plStr(item.label)
        if words = "" then words = plStr(item.note)
        if words = "" then words = plBookmarkAtLabel(item.positionSeconds)
        if whichTab = "global" and plStr(item.ownerName) <> "" then words = words + plSep() + plStr(item.ownerName)
        rows.Push({
            kind: "bookmark",
            title: plFormatTime(item.positionSeconds),
            second: words,
            value: plStr(item.id),
            seconds: plNum(item.positionSeconds),
            name: plStr(item.label),
            note: plStr(item.note),
            canChange: whichTab <> "global" and plIsTrue(item.isYours) and not plIsTrue(item.isGlobal)
        })
    end for
    return rows
end function

function plBookmarkEmpty(whichTab as String) as String
    if whichTab = "global" then return plWords().noGlobal
    return plWords().noMine
end function

' The Bookmarks panel's model. A television has no note field and no search box (a remote cannot type): Mine | Global,
' "Bookmark at {0}", the rows, and Show on timeline. Select on a row jumps to it; Options on a row of Mine reaches Edit and Delete.
' ctx: { items, choice, position, ticks, failed, loadError }
function plBookmarksModel(ctx as Object) as Object
    words = plWords()
    sets = plBookmarkSets(ctx.items)
    chosenTab = ctx.choice
    if not sets.showChoice then chosenTab = "mine"
    rows = []
    if sets.showChoice
        rows.Push({ kind: "segment", title: words.mine + plSep() + sets.mine.Count().ToStr(), second: words.globalName + plSep() + sets.everyone.Count().ToStr(), value: chosenTab, adjustable: true })
    end if
    rows.Push({ kind: "add", title: plBookmarkAtLabel(ctx.position), value: "add" })
    if plIsTrue(ctx.failed) then rows.Push({ kind: "note", title: words.couldNotSave })
    chosen = sets.mine
    if chosenTab = "global" then chosen = sets.everyone
    list = plBookmarkRows(chosen, chosenTab)
    if plStr(ctx.loadError) <> ""
        ' The list could not be read: say so, never the empty state.
        rows.Push({ kind: "note", title: plStr(ctx.loadError) })
    else if list.Count() = 0
        rows.Push({ kind: "note", title: plBookmarkEmpty(chosenTab) })
    else
        for each row in list
            rows.Push(row)
        end for
        rows.Push({ kind: "note", title: words.selectBookmark })
    end if
    footer = [{ kind: "switch", title: words.showOnTimeline, on: plIsTrue(ctx.ticks), value: "ticks" }]
    focus = 0
    for i = 0 to rows.Count() - 1
        if rows[i].kind = "add" then focus = i
    end for
    return { title: words.bookmarks, back: false, rows: rows, footer: footer, focus: focus, choice: chosenTab }
end function

' The body of "Bookmark at {0}": saved at the moment it was pressed, at once, with no note.
function plBookmarkRequest(assetId as String, position as Dynamic, duration as Dynamic) as Object
    body = { assetId: assetId, positionSeconds: plRound1(position), isGlobal: false }
    if plNum(duration) > 0 then body.durationSeconds = plRound1(duration)
    return body
end function

function plRound1(value as Dynamic) as Float
    return Int(plNum(value) * 10 + 0.5) / 10.0
end function

' Where each bookmark sits on the timeline, as a fraction of its length; one past the end or with no length is left out.
function plBookmarkTicks(items as Object, duration as Dynamic) as Object
    ticks = []
    length = plNum(duration)
    if length <= 0 then return ticks
    for each item in plList(items)
        fraction = plNum(item.positionSeconds) / length
        if fraction >= 0 and fraction <= 1 then ticks.Push(fraction)
    end for
    return ticks
end function

' ---- Playback settings --------------------------------------------------------------------------------------------

' The root of Playback settings: Skip back, Skip forward and Playback information, then the footer Reset playback preferences.
' There is no Playback speed row (Roku's player cannot change speed) and no Picture in picture (the television is full screen).
function plSettingsModel(skip as Object) as Object
    words = plWords()
    rows = [
        { kind: "value", title: words.skipBack, second: skip.back.ToStr() + " s", value: "skip-back" },
        { kind: "value", title: words.skipForward, second: skip.forward.ToStr() + " s", value: "skip-forward" },
        { kind: "value", title: words.playbackInformation, second: "", value: "info" }
    ]
    footer = [{ kind: "action", title: words.resetPreferences, value: "reset" }]
    return { title: words.playbackSettings, back: false, rows: rows, footer: footer, focus: 0 }
end function

' Skip back or Skip forward: the profile's choices with a check on the one in use. direction is "skip-back" or "skip-forward".
function plSkipChoicesModel(direction as String, skip as Object) as Object
    words = plWords()
    rows = []
    title = words.skipBack
    choices = plSkipBackChoices()
    current = skip.back
    if direction = "skip-forward"
        title = words.skipForward
        choices = plSkipForwardChoices()
        current = skip.forward
    end if
    focus = 0
    for i = 0 to choices.Count() - 1
        rows.Push({ kind: "plain", title: choices[i].ToStr() + " s", check: choices[i] = current, value: choices[i].ToStr() })
        if choices[i] = current then focus = i
    end for
    return { title: title, back: true, rows: rows, footer: [], focus: focus }
end function

' Playback information: read-only lines from the manifest. Returns the panel model.
function plInfoModel(manifest as Object) as Object
    rows = []
    technical = manifest.technical
    if technical <> invalid
        video = ""
        if plNum(technical.height) > 0 then video = Int(plNum(technical.height)).ToStr() + "p"
        if plStr(technical.videoCodec) <> ""
            if video <> "" then video = video + plSep()
            video = video + UCase(plStr(technical.videoCodec))
        end if
        if video <> "" then rows.Push({ kind: "plain", title: "Video", second: video, check: false, value: "" })
        audio = UCase(plStr(technical.audioCodec))
        if audio <> "" then rows.Push({ kind: "plain", title: "Audio", second: audio, check: false, value: "" })
    end if
    delivery = "Direct"
    if manifest.playbackPath <> invalid
        rung = plStr(manifest.playbackPath.rung)
        if rung <> "" and rung <> "direct" then delivery = "Converted"
    end if
    rows.Push({ kind: "plain", title: "Delivery", second: delivery, check: false, value: "" })
    connection = "Local"
    if manifest.connection <> invalid and plStr(manifest.connection.path) = "remote" then connection = "Remote"
    rows.Push({ kind: "plain", title: "Connection", second: connection, check: false, value: "" })
    return { title: plWords().playbackInformation, back: true, rows: rows, footer: [], focus: 0 }
end function
