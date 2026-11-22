' Find another on TV, the pure logic (spec find-another-on-tv, WP4).
'
' Everything here is plain data in, plain data out: no nodes, no `m`, no network. The window, the Audio & subtitles
' panel and the scene include this file and draw what it decides. A structure check (tests/build/roku-find-another.test.mjs)
' keeps it free of UI calls and keeps the wording strings below in place.

' ---- Constants and wording -------------------------------------------------------------------------------------

' Previews are read after one second at rest (D10); the window's Timer uses the same number.
function faDwellSeconds() as Float
    return 1.0
end function

' The Engine's confidence is the source's base score plus how much of the release name matches the file's own;
' a high one reads as the same release (the Dashboard uses the same 0.9).
function faSameReleaseConfidence() as Float
    return 0.9
end function

' A subtitle file that ends this far (minutes) from the film is amber; further is red.
function faAmberUpToMinutes() as Float
    return 15.0
end function

function faTextLooking() as String
    return "Looking for subtitles…"
end function

function faTextReading() as String
    return "Reading…"
end function

function faTextCouldNotRead() as String
    return "Could not read this one"
end function

function faTextFindSubtitles() as String
    return "Find subtitles…"
end function

function faTextWindowTitle() as String
    return "Find subtitles"
end function

function faTextUseThis() as String
    return "Use this"
end function

function faTextCancel() as String
    return "Cancel"
end function

function faTextBestMatch() as String
    return "Best match"
end function

function faTextInUse() as String
    return "In use"
end function

function faTextHearingImpaired() as String
    return "Hearing impaired"
end function

function faTextMadeForYourFile() as String
    return "Made for your file"
end function

function faTextDifferentRelease() as String
    return "A different release, may be out of step"
end function

function faTextFitsTheFilm() as String
    return "Fits the film"
end function

function faTextUntitled() as String
    return "Untitled result"
end function

function faTextUpdating() as String
    return "Updating…"
end function

function faTextApplyFailed() as String
    return "Could not use that subtitle. Try again from Find subtitles…"
end function

function faLanguageButtonText(language as String) as String
    return "Language: " + language
end function

' ---- Lyrics (spec find-another-on-tv D6, WP5): the same window with lyrics wording ---------------------------------

function faTextFindLyrics() as String
    return "Find lyrics"
end function

function faTextFindLyricsRow() as String
    return "Find lyrics…"
end function

function faTextLookingLyrics() as String
    return "Looking for lyrics…"
end function

function faTextUpdatingLyrics() as String
    return "Updating lyrics…"
end function

function faTextLyricsHeading() as String
    return "Lyrics"
end function

function faTextSynced() as String
    return "Synced"
end function

function faTextUnsynced() as String
    return "Unsynced"
end function

function faTextWordsOnly() as String
    return "Words only, not timed"
end function

function faTextFitsTheSong() as String
    return "Fits the song"
end function

function faTextNoLyrics() as String
    return "There are no lyrics for this song yet."
end function

function faTextLyricsNotLoaded() as String
    return "The lyrics could not be loaded."
end function

function faTextApplyLyricsFailed() as String
    return "Could not use those lyrics. Try again from Find lyrics…"
end function

' Lyrics are searched in the song's own language: only subtitles have a Language button and a language parameter.
function faHasLanguage(kind as String) as Boolean
    return kind <> "lyrics"
end function

function faWindowTitle(kind as String) as String
    if kind = "lyrics" then return faTextFindLyrics()
    return faTextWindowTitle()
end function

function faLoadingText(kind as String) as String
    if kind = "lyrics" then return faTextLookingLyrics()
    return faTextLooking()
end function

function faApplyingText(kind as String) as String
    if kind = "lyrics" then return faTextUpdatingLyrics()
    return faTextUpdating()
end function

function faFooterRowText(kind as String) as String
    if kind = "lyrics" then return faTextFindLyricsRow()
    return faTextFindSubtitles()
end function

' ---- Small readers over parsed JSON ----------------------------------------------------------------------------

function faStr(value as Dynamic) as String
    if value = invalid then return ""
    kind = Type(value)
    if kind = "roString" or kind = "String" then return value
    return ""
end function

function faIsTrue(value as Dynamic) as Boolean
    if value = invalid then return false
    kind = Type(value)
    if kind = "roBoolean" or kind = "Boolean" then return value = true
    return false
end function

' A wire number as a Float, or invalid when it is missing or not a number.
function faNum(value as Dynamic) as Dynamic
    if value = invalid then return invalid
    kind = Type(value)
    if kind = "roInt" or kind = "roInteger" or kind = "Integer" or kind = "roFloat" or kind = "Float" or kind = "roDouble" or kind = "Double" or kind = "roLongInteger" or kind = "LongInteger"
        return value + 0.0
    end if
    return invalid
end function

function faCount(value as Dynamic) as Integer
    if value = invalid then return 0
    kind = Type(value)
    if kind = "roArray" or kind = "Array" then return value.Count()
    return 0
end function

function faPad2(value as Integer) as String
    text = value.ToStr()
    if Len(text) < 2 then text = "0" + text
    return text
end function

' Minutes and seconds ("3:42"), or hours when long ("1:05:09").
function faClock(seconds as Dynamic) as String
    total = Int(seconds + 0.5)
    if total < 0 then total = 0
    hours = total \ 3600
    minutes = (total - hours * 3600) \ 60
    secs = total mod 60
    if hours > 0 then return hours.ToStr() + ":" + faPad2(minutes) + ":" + faPad2(secs)
    return minutes.ToStr() + ":" + faPad2(secs)
end function

' ---- Sentences for each outcome of the search ------------------------------------------------------------------

' The Engine's own sentence is shown when it sent one; these stand in when it did not (or the call failed).
function faStatusSentence(status as String, message as String, kind = "subtitles" as String) as String
    if message <> "" then return message
    if kind = "lyrics" then return faLyricsStatusSentence(status)
    fallbacks = {
        found: "No other subtitles were found.",
        nonefound: "No other subtitles were found.",
        nosourceon: "No subtitle source is switched on. An administrator can switch one on in Settings.",
        notallowed: "This profile cannot change subtitles.",
        blocked: "Subtitles cannot be searched for this item.",
        unavailable: "The subtitle source could not be reached. Try again in a moment.",
        assetmissing: "This video could not be found.",
        unsupported: "Subtitles cannot be searched for this kind of item.",
        identityunverified: "This video has not been identified, so it cannot be searched for online.",
        sourcekeyrequired: "A subtitle source is switched on, but its key is missing or was refused.",
        limitreached: "The subtitle source's allowance is used up for now. Try again later.",
        languageunknown: "That language cannot be searched."
    }
    sentence = fallbacks[LCase(status)]
    if sentence <> invalid then return sentence
    return "Subtitles could not be searched right now."
end function

' The same plain sentences for lyrics.
function faLyricsStatusSentence(status as String) as String
    fallbacks = {
        found: "No other lyrics were found.",
        nonefound: "No other lyrics were found.",
        nosourceon: "No lyrics source is switched on. An administrator can switch one on in Settings.",
        notallowed: "This profile cannot change lyrics.",
        blocked: "Lyrics cannot be searched for this item.",
        unavailable: "The lyrics source could not be reached. Try again in a moment.",
        assetmissing: "This song could not be found.",
        unsupported: "Lyrics cannot be searched for this kind of item.",
        identityunverified: "This song has not been identified, so it cannot be searched for online.",
        sourcekeyrequired: "A lyrics source is switched on, but its key is missing or was refused.",
        limitreached: "The lyrics source's allowance is used up for now. Try again later."
    }
    sentence = fallbacks[LCase(status)]
    if sentence <> invalid then return sentence
    return "Lyrics could not be searched right now."
end function

' The search call itself failed (no answer from the Engine, or a refusal).
function faSentenceForHttpFailure(httpStatus as Integer, kind = "subtitles" as String) as String
    if httpStatus = 403 then return faStatusSentence("NotAllowed", "", kind)
    return faStatusSentence("Unavailable", "", kind)
end function

' The footer row "Find subtitles…" (or "Find lyrics…") shows only when the sources check says there is something to search.
function faSourcesReady(status as String) as Boolean
    return LCase(status) = "ready"
end function

' ---- Rows of the list ------------------------------------------------------------------------------------------

function faKey(item as Object) as String
    return faStr(item.provider) + "|" + faStr(item.source_id)
end function

' The view model of one list row. `read` is the item as read (or the list's own item when nothing has been read).
function faRowModel(item as Object, index as Integer, showLanguage as Boolean, languageName as String, kind = "subtitles" as String) as Object
    if kind = "lyrics" then return faLyricsRowModel(item, index)
    release = faStr(item.release_name)
    if release = "" then release = faTextUntitled()

    meta = ""
    if showLanguage then meta = languageName
    if faIsTrue(item.is_hearing_impaired)
        if meta <> "" then meta = meta + " · "
        meta = meta + faTextHearingImpaired()
    end if

    check = faTextDifferentRelease()
    checkTone = "warn"
    confidence = faNum(item.confidence)
    if confidence <> invalid and confidence >= faSameReleaseConfidence()
        check = faTextMadeForYourFile()
        checkTone = "ok"
    end if

    timing = ""
    timingTone = "ok"
    difference = faNum(item.length_difference_seconds)
    if faStr(item.length_check) <> "" and difference <> invalid
        if faStr(item.length_check) = "Fits"
            timing = faTextFitsTheFilm()
        else
            minutes = faWholeMinutes(difference)
            if difference < 0
                timing = "Ends " + minutes.ToStr() + " minutes short of the film"
            else
                timing = "Ends " + minutes.ToStr() + " minutes past the film"
            end if
            if Abs(difference) / 60.0 <= faAmberUpToMinutes() then timingTone = "warn" else timingTone = "bad"
        end if
    end if

    chipBest = ""
    if index = 0 then chipBest = faTextBestMatch()
    chipInUse = ""
    if faIsTrue(item.is_current) then chipInUse = faTextInUse()

    return {
        rowKey: faKey(item),
        release: release,
        meta: meta,
        check: check,
        checkTone: checkTone,
        timing: timing,
        timingTone: timingTone,
        chipBest: chipBest,
        chipInUse: chipInUse
    }
end function

' A lyrics row: the song's title, then artist, album and length; "Synced" or "Words only, not timed"; once read,
' "Fits the song" or how many seconds longer or shorter (amber up to 15 seconds off, red beyond).
function faLyricsRowModel(item as Object, index as Integer) as Object
    name = faStr(item.title)
    if name = "" then name = faStr(item.release_name)
    if name = "" then name = faTextUntitled()

    meta = ""
    if faStr(item.artist) <> "" then meta = faStr(item.artist)
    if faStr(item.album) <> ""
        if meta <> "" then meta = meta + " · "
        meta = meta + faStr(item.album)
    end if
    length = faNum(item.length_seconds)
    if length <> invalid and length > 0
        if meta <> "" then meta = meta + " · "
        meta = meta + faClock(length)
    end if

    check = faTextWordsOnly()
    checkTone = "warn"
    if faIsTrue(item.is_timed)
        check = faTextSynced()
        checkTone = "ok"
    end if

    timing = ""
    timingTone = "ok"
    difference = faNum(item.length_difference_seconds)
    if faStr(item.length_check) <> "" and difference <> invalid
        if faStr(item.length_check) = "Fits"
            timing = faTextFitsTheSong()
        else
            seconds = Int(Abs(difference) + 0.5)
            if seconds < 1 then seconds = 1
            unit = " seconds"
            if seconds = 1 then unit = " second"
            if difference > 0 then timing = seconds.ToStr() + unit + " longer" else timing = seconds.ToStr() + unit + " shorter"
            if Abs(difference) <= 15.0 then timingTone = "warn" else timingTone = "bad"
        end if
    end if

    chipBest = ""
    if index = 0 then chipBest = faTextBestMatch()
    chipInUse = ""
    if faIsTrue(item.is_current) then chipInUse = faTextInUse()

    return {
        rowKey: faKey(item),
        release: name,
        meta: meta,
        check: check,
        checkTone: checkTone,
        timing: timing,
        timingTone: timingTone,
        chipBest: chipBest,
        chipInUse: chipInUse
    }
end function

function faWholeMinutes(differenceSeconds as Dynamic) as Integer
    minutes = Int(Abs(differenceSeconds) / 60.0 + 0.5)
    if minutes < 1 then minutes = 1
    return minutes
end function

' Whether the list mixes languages (then each row names its own).
function faMixedLanguages(items as Object) as Boolean
    first = ""
    for each item in items
        language = LCase(faStr(item.language))
        if first = "" then first = language
        if language <> first then return true
    end for
    return false
end function

function faToneColor(tone as String) as String
    if tone = "ok" then return "#7DDC9B"
    if tone = "bad" then return "#FF8A80"
    return "#FFCC80"
end function

' ---- Read once per window, and when to read --------------------------------------------------------------------

' The read-once cache: one entry per row key, "reading", "read" (with the item as read) or "failed".
function faNewCache() as Object
    return { entries: {} }
end function

function faCacheState(cache as Object, key as String) as String
    entry = cache.entries[key]
    if entry = invalid then return "none"
    return entry.state
end function

function faCacheItem(cache as Object, key as String) as Dynamic
    entry = cache.entries[key]
    if entry = invalid then return invalid
    return entry.item
end function

sub faMarkReading(cache as Object, key as String)
    cache.entries[key] = { state: "reading", item: invalid }
end sub

sub faStoreRead(cache as Object, key as String, item as Object)
    cache.entries[key] = { state: "read", item: item }
end sub

sub faMarkFailed(cache as Object, key as String)
    cache.entries[key] = { state: "failed", item: invalid }
end sub

' Rows that arrive with their words (the best match) are already read.
sub faSeedCache(cache as Object, items as Object)
    for each item in items
        if faCount(item.preview_lines) > 0 then faStoreRead(cache, faKey(item), item)
    end for
end sub

' The dwell decision. Nothing is ever read twice in one window; the best match is read at once; any other
' row is read only after focus has rested on it for the dwell (`rested` is true when the Timer fired).
function faShouldRead(cache as Object, key as String, isBest as Boolean, rested as Boolean) as Boolean
    if faCacheState(cache, key) <> "none" then return false
    if isBest then return true
    return rested
end function

' The item to draw for a row: as read when it has been read, else the list's own.
function faEffectiveItem(cache as Object, item as Object) as Object
    read = faCacheItem(cache, faKey(item))
    if read = invalid then return item
    return read
end function

' A preview answer keeps what only the list knows about the row.
function faMergeRead(listItem as Object, fresh as Object) as Object
    merged = {}
    merged.Append(fresh)
    merged.is_current = faIsTrue(listItem.is_current)
    return merged
end function

' ---- The pane --------------------------------------------------------------------------------------------------

function faPaneLineLimit(full as Boolean, kind = "subtitles" as String) as Integer
    if kind = "lyrics" then return 6
    if full then return 20
    return 3
end function

' What the right-hand pane shows for the focused row: kind "words", "reading", "failed" or "empty".
' Lyrics show six lines of words with no times; subtitles show the time each line appears.
function faPaneModel(item as Object, state as String, full as Boolean, kind = "subtitles" as String) as Object
    title = faStr(item.release_name)
    if kind = "lyrics" and faStr(item.title) <> "" then title = faStr(item.title)
    if title = "" then title = faTextUntitled()
    if state = "reading" then return { title: title, kind: "reading", body: faTextReading() }
    if state = "failed" then return { title: title, kind: "failed", body: faTextCouldNotRead() }
    lines = item.preview_lines
    if faCount(lines) = 0 then return { title: title, kind: "empty", body: "" }
    limit = faPaneLineLimit(full, kind)
    body = ""
    shown = 0
    for each line in lines
        if shown >= limit then exit for
        text = faStr(line.text)
        at = faNum(line.at_seconds)
        if at <> invalid and kind <> "lyrics" then text = faClock(at) + "   " + text
        if body <> "" then body = body + Chr(10) + Chr(10)
        body = body + text
        shown = shown + 1
    end for
    return { title: title, kind: "words", body: body }
end function

' ---- Keys ------------------------------------------------------------------------------------------------------

' Where focus goes for an arrow key, by zone: "list", "pane", "use", "cancel" or "language".
' Empty means the key is not a focus move here (the control itself handles it, or nothing happens).
function faMove(zone as String, key as String, atFirstRow as Boolean, languageShown = true as Boolean) as String
    if zone = "list"
        if key = "right" then return "pane"
        if key = "up" and atFirstRow and languageShown then return "language"
    else if zone = "pane"
        if key = "right" then return "use"
        if key = "left" then return "list"
    else if zone = "use"
        if key = "left" then return "pane"
        if key = "up" then return "list"
    else if zone = "cancel"
        if key = "up" then return "list"
    else if zone = "language"
        if key = "down" then return "list"
    end if
    return ""
end function

' The same for the sentence screens (nothing found, a source problem): focus starts on Cancel, up reaches Language,
' down returns. `languageShown` is false when the sentence is about something a language cannot fix.
function faMoveInMessage(zone as String, key as String, languageShown as Boolean) as String
    if not languageShown then return ""
    if zone = "message" and key = "up" then return "language"
    if zone = "language" and key = "down" then return "message"
    return ""
end function

' The language choice is no use when the sentence is about a switch, a key, an item that cannot be matched or a profile that may not search.
function faLanguageRelevant(status as String, kind = "subtitles" as String) as Boolean
    if not faHasLanguage(kind) then return false
    lower = LCase(status)
    if lower = "nosourceon" or lower = "sourcekeyrequired" or lower = "blocked" or lower = "notallowed" then return false
    if lower = "identityunverified" or lower = "unsupported" or lower = "assetmissing" then return false
    return true
end function

' What Back closes first: the language list, then the full words, then the window.
function faBackAction(languageListOpen as Boolean, fullWordsOpen as Boolean) as String
    if languageListOpen then return "closeLanguages"
    if fullWordsOpen then return "closeFullWords"
    return "closeWindow"
end function

' ---- Languages -------------------------------------------------------------------------------------------------

function faCommonLanguages() as Object
    return [
        "English", "Spanish", "French", "German", "Italian", "Portuguese", "Dutch", "Swedish", "Norwegian", "Danish",
        "Finnish", "Polish", "Czech", "Hungarian", "Greek", "Turkish", "Russian", "Ukrainian", "Hebrew", "Arabic",
        "Hindi", "Japanese", "Korean", "Chinese", "Thai", "Vietnamese", "Indonesian"
    ]
end function

' The short list the Language button opens: the profile's own language first, then the common ones.
function faLanguageChoices(current as String) as Object
    choices = []
    if current <> "" then choices.Push(current)
    for each name in faCommonLanguages()
        if LCase(name) <> LCase(current) then choices.Push(name)
    end for
    return choices
end function

' A language name for a track's code (two- or three-letter); the code itself when it is not a common one.
function faLanguageName(code as String) as String
    lower = LCase(code)
    names = {
        en: "English", eng: "English", es: "Spanish", spa: "Spanish", fr: "French", fre: "French", fra: "French",
        de: "German", ger: "German", deu: "German", it: "Italian", ita: "Italian", pt: "Portuguese", por: "Portuguese",
        nl: "Dutch", dut: "Dutch", nld: "Dutch", sv: "Swedish", swe: "Swedish", no: "Norwegian", nor: "Norwegian",
        da: "Danish", dan: "Danish", fi: "Finnish", fin: "Finnish", pl: "Polish", pol: "Polish", cs: "Czech", cze: "Czech", ces: "Czech",
        hu: "Hungarian", hun: "Hungarian", el: "Greek", gre: "Greek", ell: "Greek", tr: "Turkish", tur: "Turkish",
        ru: "Russian", rus: "Russian", uk: "Ukrainian", ukr: "Ukrainian", he: "Hebrew", heb: "Hebrew", ar: "Arabic", ara: "Arabic",
        hi: "Hindi", hin: "Hindi", ja: "Japanese", jpn: "Japanese", ko: "Korean", kor: "Korean", zh: "Chinese", chi: "Chinese", zho: "Chinese",
        th: "Thai", tha: "Thai", vi: "Vietnamese", vie: "Vietnamese", id: "Indonesian", ind: "Indonesian"
    }
    name = names[lower]
    if name <> invalid then return name
    if lower = "" or lower = "und" then return "Unknown language"
    return UCase(code)
end function

' ---- The Audio & subtitles panel -------------------------------------------------------------------------------

' The panel's rows, top to bottom: a heading and a row per audio track when there is more than one, the Subtitles heading,
' Off, one row per subtitle track, then "Find subtitles…" when something can be searched.
' audio: [{ name, index }]; subtitles: [{ id, language, hearingImpaired }]
function faPanelRows(audio as Object, activeAudio as Integer, subtitles as Object, activeSubtitleId as String, showFind as Boolean) as Object
    rows = []
    if faCount(audio) > 1
        rows.Push({ kind: "header", title: "AUDIO", value: "" })
        for each track in audio
            title = track.name
            if track.index = activeAudio then title = title + "  · On"
            rows.Push({ kind: "audio", title: title, value: track.index.ToStr() })
        end for
    end if
    rows.Push({ kind: "header", title: "SUBTITLES", value: "" })
    offTitle = "Off"
    if activeSubtitleId = "" then offTitle = offTitle + "  · On"
    rows.Push({ kind: "off", title: offTitle, value: "" })
    for each track in subtitles
        title = faLanguageName(track.language)
        if track.hearingImpaired then title = title + " · SDH"
        if track.id = activeSubtitleId then title = title + "  · On"
        rows.Push({ kind: "subtitle", title: title, value: track.id })
    end for
    if showFind then rows.Push({ kind: "find", title: faTextFindSubtitles(), value: "" })
    return rows
end function

' The next row that can be chosen, moving in `direction` (1 or -1) from `from`; -1 when there is none.
function faNextSelectable(rows as Object, from as Integer, direction as Integer) as Integer
    index = from
    while index >= 0 and index < rows.Count()
        if rows[index].kind <> "header" then return index
        index = index + direction
    end while
    return -1
end function

' The text-track list answer reduced to the subtitle tracks: [{ id, language, hearingImpaired, preferred }].
function faSubtitleTracks(value as Dynamic) as Object
    tracks = []
    if faCount(value) = 0 then return tracks
    for each entry in value
        if LCase(faStr(entry.kind)) = "subtitles" and faStr(entry.id) <> ""
            tracks.Push({
                id: faStr(entry.id),
                language: faStr(entry.language),
                hearingImpaired: faIsTrue(entry.isHearingImpaired),
                preferred: faIsTrue(entry.isPreferred)
            })
        end if
    end for
    return tracks
end function

' After "Use this": the track to turn on is the one the Engine named, else the one it marks preferred; "" when there is none.
function faTrackToEnable(tracks as Object, namedId as String) as String
    if namedId <> ""
        for each track in tracks
            if LCase(track.id) = LCase(namedId) then return track.id
        end for
    end if
    for each track in tracks
        if track.preferred then return track.id
    end for
    return ""
end function

function faTrackById(tracks as Object, id as String) as Dynamic
    for each track in tracks
        if LCase(track.id) = LCase(id) then return track
    end for
    return invalid
end function

' ---- The Lyrics panel (spec find-another-on-tv WP5) --------------------------------------------------------------

' Whether a playback manifest's media type is music: the Lyrics panel is offered there only.
function faIsMusic(mediaType as String) as Boolean
    value = LCase(mediaType)
    if value = "" then return false
    if Instr(1, value, "audiobook") > 0 then return false
    if Instr(1, value, "music") > 0 or Instr(1, value, "song") > 0 or Instr(1, value, "track") > 0 then return true
    return false
end function

' Which of the asset's text tracks is its lyrics: the one the Engine marks preferred, else the first lyrics track; "" when none.
function faPickLyricsTrackId(tracks as Dynamic) as String
    first = ""
    if faCount(tracks) = 0 then return first
    for each entry in tracks
        if LCase(faStr(entry.kind)) = "lyrics" and faStr(entry.id) <> ""
            if faIsTrue(entry.isPreferred) then return faStr(entry.id)
            if first = "" then first = faStr(entry.id)
        end if
    end for
    return first
end function

function faIsDigits(value as String) as Boolean
    if Len(value) = 0 then return false
    for i = 1 to Len(value)
        code = Asc(Mid(value, i, 1))
        if code < 48 or code > 57 then return false
    end for
    return true
end function

' "mm:ss", "mm:ss.xx" or "mm:ss:xx" as seconds; invalid when the text is not a time (a tag such as "ar:Name").
function faStampSeconds(inside as String) as Dynamic
    colon = Instr(1, inside, ":")
    if colon < 2 then return invalid
    minutes = Left(inside, colon - 1)
    rest = Mid(inside, colon + 1)
    if not faIsDigits(minutes) then return invalid
    fraction = "0"
    seconds = rest
    separator = Instr(1, rest, ".")
    if separator = 0 then separator = Instr(1, rest, ":")
    if separator > 0
        seconds = Left(rest, separator - 1)
        fraction = Mid(rest, separator + 1)
        if not faIsDigits(fraction) then return invalid
    end if
    if Len(seconds) <> 2 or not faIsDigits(seconds) then return invalid
    return Val(minutes) * 60.0 + Val(seconds) + Val("0." + fraction)
end function

' Word stamps (<mm:ss.xx>) inside a line are dropped; the words stay.
function faStripWordStamps(value as String) as String
    result = ""
    rest = value
    while true
        startAt = Instr(1, rest, "<")
        if startAt = 0 then exit while
        endAt = Instr(startAt, rest, ">")
        if endAt = 0 then exit while
        if faStampSeconds(Mid(rest, startAt + 1, endAt - startAt - 1)) <> invalid
            result = result + Left(rest, startAt - 1)
            rest = Mid(rest, endAt + 1)
        else
            result = result + Left(rest, startAt)
            rest = Mid(rest, startAt + 1)
        end if
    end while
    return result + rest
end function

' One lyrics line: the time tags it starts with ([mm:ss.xx], more than one means the line is sung again) and its words.
function faParseLyricsLine(rawLine as String) as Object
    stamps = []
    rest = rawLine.Trim()
    while Left(rest, 1) = "["
        closeAt = Instr(1, rest, "]")
        if closeAt = 0 then exit while
        stamp = faStampSeconds(Mid(rest, 2, closeAt - 2))
        if stamp = invalid then exit while
        stamps.Push(stamp)
        rest = Mid(rest, closeAt + 1)
    end while
    return { stamps: stamps, text: faStripWordStamps(rest).Trim() }
end function

' Time order, lines with the same time keeping the file's order.
function faSortLyrics(lines as Object) as Object
    sorted = []
    for each line in lines
        sorted.Push(line)
        j = sorted.Count() - 1
        while j > 0
            if sorted[j - 1].at <= line.at then exit while
            sorted[j] = sorted[j - 1]
            j = j - 1
        end while
        sorted[j] = line
    end for
    return sorted
end function

' Reads a lyrics file the Engine serves as text, as the Dashboard does: timed lines make it Synced; a file with no timed line
' is plain words (Unsynced, tag lines such as [ar:Name] left out). Returns { synced, lines: [{ at, text }] }; at is invalid when unsynced.
function faParseLyrics(content as String) as Object
    timed = []
    plain = []
    text = content.Replace(Chr(13), "")
    for each rawLine in text.Split(Chr(10))
        entry = faParseLyricsLine(rawLine)
        if entry.stamps.Count() > 0
            for each at in entry.stamps
                timed.Push({ at: at, text: entry.text })
            end for
        else if entry.text <> ""
            isTag = Left(entry.text, 1) = "[" and Right(entry.text, 1) = "]"
            if not isTag then plain.Push({ at: invalid, text: entry.text })
        end if
    end for
    if timed.Count() > 0 then return { synced: true, lines: faSortLyrics(timed) }
    return { synced: false, lines: plain }
end function

' The current synced line: the last timed line at or before this song position; -1 before the first line or when unsynced.
function faActiveLyricIndex(lines as Object, seconds as Float) as Integer
    found = -1
    index = 0
    for each line in lines
        if line.at = invalid then return -1
        if line.at > seconds then exit for
        found = index
        index = index + 1
    end for
    return found
end function

function faLyricsChipText(synced as Boolean) as String
    if synced then return faTextSynced()
    return faTextUnsynced()
end function

' What the Lyrics panel draws. status is "loading", "ready", "none" or "failed"; updating swaps the loading words for
' "Updating lyrics…" after Use this. rows are the lines' words; note is the sentence shown instead of lines.
function faLyricsPanelModel(parsed as Dynamic, status as String, updating as Boolean) as Object
    model = { heading: faTextLyricsHeading(), chip: "", rows: [], note: "" }
    if status = "ready" and parsed <> invalid
        model.chip = faLyricsChipText(parsed.synced)
        for each line in parsed.lines
            words = faStr(line.text)
            if words = "" then words = "♪"
            model.rows.Push(words)
        end for
    else if status = "loading"
        if updating then model.note = faTextUpdatingLyrics() else model.note = faTextLookingLyrics()
    else if status = "failed"
        model.note = faTextLyricsNotLoaded()
    else
        model.note = faTextNoLyrics()
    end if
    return model
end function
