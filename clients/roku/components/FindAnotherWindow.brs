' Find another (subtitles) window, spec find-another-on-tv section 4. The decisions live in FindAnotherLogic.brs;
' this file draws them and moves focus. Each Engine call runs in its own ApiTask so a late answer from an earlier
' search never blocks or overwrites a newer one.

sub init()
    m.list = m.top.findNode("resultList")
    m.itemName = m.top.findNode("itemName")
    m.languageButton = m.top.findNode("languageButton")
    m.loadingLabel = m.top.findNode("loadingLabel")
    m.listGroup = m.top.findNode("listGroup")
    m.paneTitle = m.top.findNode("paneTitle")
    m.paneText = m.top.findNode("paneText")
    m.footer = m.top.findNode("footer")
    m.useButton = m.top.findNode("useButton")
    m.cancelButton = m.top.findNode("cancelButton")
    m.messageGroup = m.top.findNode("messageGroup")
    m.messageLabel = m.top.findNode("messageLabel")
    m.messageCancel = m.top.findNode("messageCancel")
    m.languageGroup = m.top.findNode("languageGroup")
    m.languageList = m.top.findNode("languageList")
    m.dwellTimer = m.top.findNode("dwellTimer")
    m.dwellTimer.duration = faDwellSeconds()

    m.list.observeField("itemFocused", "onRowFocused")
    m.list.observeField("itemSelected", "onRowSelected")
    m.useButton.observeField("buttonSelected", "onUse")
    m.cancelButton.observeField("buttonSelected", "onCancel")
    m.messageCancel.observeField("buttonSelected", "onCancel")
    m.languageButton.observeField("buttonSelected", "onLanguageButton")
    m.languageList.observeField("itemSelected", "onLanguageChosen")
    m.dwellTimer.observeField("fire", "onDwell")

    m.tasks = {}
    m.counter = 0
    m.generation = 0
    m.items = []
    m.cache = faNewCache()
    m.state = "idle"
    m.fullOpen = false
    m.languageOpen = false
    m.languageShown = true
    m.language = "English"
    m.assetId = ""
    m.choices = []
end sub

' ---- Opening, searching, closing -------------------------------------------------------------------------------

sub onOpen()
    args = m.top.openWith
    if args = invalid then return
    m.assetId = faStr(args.assetId)
    m.itemName.text = faStr(args.title)
    m.language = faStr(args.language)
    if m.language = "" then m.language = "English"
    m.top.visible = true
    searchAgain()
end sub

sub searchAgain()
    m.generation = m.generation + 1
    m.dwellTimer.control = "stop"
    m.items = []
    m.cache = faNewCache()
    m.fullOpen = false
    m.languageOpen = false
    m.languageGroup.visible = false
    m.languageButton.text = faLanguageButtonText(m.language)
    m.languageButton.visible = true
    m.languageShown = true
    showLoading()
    runTask("alternatives", { assetId: m.assetId, language: m.language }, "")
end sub

sub closeWindow(applied as Boolean, item as Dynamic)
    ' Bumping the generation makes every answer still on its way be ignored.
    m.generation = m.generation + 1
    m.dwellTimer.control = "stop"
    m.tasks = {}
    m.state = "idle"
    m.languageOpen = false
    m.languageGroup.visible = false
    m.top.visible = false
    if applied and item <> invalid
        m.top.closed = { applied: true, provider: faStr(item.provider), sourceId: faStr(item.source_id), language: m.language }
    else
        m.top.closed = { applied: false }
    end if
end sub

' ---- Engine calls ----------------------------------------------------------------------------------------------

sub runTask(operation as String, payload as Object, key as String)
    m.counter = m.counter + 1
    id = "req" + m.counter.ToStr()
    task = CreateObject("roSGNode", "ApiTask")
    task.id = id
    task.serverOrigin = m.top.serverOrigin
    task.accessToken = m.top.accessToken
    task.refreshToken = m.top.refreshToken
    task.observeField("result", "onTaskResult")
    task.observeField("error", "onTaskError")
    task.observeField("tokenUpdate", "onTaskToken")
    ' Keeping the node here keeps the task alive until it answers.
    m.tasks[id] = { node: task, operation: operation, generation: m.generation, key: key }
    task.operation = operation
    task.payload = payload
    task.control = "RUN"
end sub

sub onTaskToken(event as Object)
    tokens = event.getData()
    if tokens = invalid then return
    m.top.accessToken = tokens.accessToken
    m.top.refreshToken = tokens.refreshToken
    m.top.tokenUpdate = tokens
end sub

sub onTaskResult(event as Object)
    id = event.getNode()
    info = m.tasks[id]
    if info = invalid then return
    m.tasks.Delete(id)
    if info.generation <> m.generation or not m.top.visible then return
    result = event.getData()
    if result = invalid then return
    if info.operation = "alternatives"
        onAlternatives(result.value)
    else if info.operation = "alternativePreview"
        onPreview(info.key, result.value)
    end if
end sub

sub onTaskError(event as Object)
    id = event.getNode()
    info = m.tasks[id]
    if info = invalid then return
    m.tasks.Delete(id)
    if info.generation <> m.generation or not m.top.visible then return
    error = event.getData()
    if error = invalid then return
    if info.operation = "alternatives"
        showMessage(faSentenceForHttpFailure(error.status), "Unavailable")
    else if info.operation = "alternativePreview"
        ' Read once: a row that failed is not retried in this window; it can still be used.
        faMarkFailed(m.cache, info.key)
        refreshRowByKey(info.key)
    end if
end sub

sub onAlternatives(value as Object)
    status = faStr(value.status)
    items = value.items
    if status = "Found" and faCount(items) > 0
        showList(items)
    else
        showMessage(faStatusSentence(status, faStr(value.message)), status)
    end if
end sub

sub onPreview(key as String, value as Object)
    index = indexOfKey(key)
    if index < 0 then return
    if faStr(value.status) = "Found" and value.item <> invalid
        faStoreRead(m.cache, key, faMergeRead(m.items[index], value.item))
    else
        faMarkFailed(m.cache, key)
    end if
    refreshRowByKey(key)
end sub

' ---- Drawing the states ----------------------------------------------------------------------------------------

sub showLoading()
    m.state = "loading"
    m.listGroup.visible = false
    m.messageLabel.visible = false
    m.loadingLabel.text = faTextLooking()
    m.loadingLabel.visible = true
    m.messageCancel.translation = [96, 950]
    m.messageGroup.visible = true
    m.messageCancel.setFocus(true)
end sub

sub showMessage(sentence as String, status as String)
    m.state = "message"
    m.listGroup.visible = false
    m.loadingLabel.visible = false
    m.messageLabel.text = sentence
    m.messageLabel.visible = true
    m.messageCancel.translation = [830, 700]
    m.messageGroup.visible = true
    m.languageShown = faLanguageRelevant(status)
    m.languageButton.visible = m.languageShown
    m.messageCancel.setFocus(true)
end sub

sub showList(items as Object)
    m.state = "list"
    m.items = items
    faSeedCache(m.cache, items)
    root = CreateObject("roSGNode", "ContentNode")
    for i = 0 to items.Count() - 1
        child = root.CreateChild("ContentNode")
        child.AddFields(rowModelFor(i))
    end for
    m.loadingLabel.visible = false
    m.messageGroup.visible = false
    m.listGroup.visible = true
    m.list.content = root
    m.list.jumpToItem = 0
    m.list.setFocus(true)
    focusRow(0)
end sub

function rowModelFor(index as Integer) as Object
    item = m.items[index]
    effective = faEffectiveItem(m.cache, item)
    return faRowModel(effective, index, faMixedLanguages(m.items), faLanguageName(faStr(item.language)))
end function

function indexOfKey(key as String) as Integer
    for i = 0 to m.items.Count() - 1
        if faKey(m.items[i]) = key then return i
    end for
    return -1
end function

sub refreshRowByKey(key as String)
    index = indexOfKey(key)
    if index < 0 then return
    child = m.list.content.getChild(index)
    if child <> invalid then child.setFields(rowModelFor(index))
    if m.list.itemFocused = index then showPane(index)
end sub

sub showPane(index as Integer)
    if index < 0 or index >= m.items.Count() then return
    item = m.items[index]
    key = faKey(item)
    model = faPaneModel(faEffectiveItem(m.cache, item), faCacheState(m.cache, key), m.fullOpen)
    m.paneTitle.text = model.title
    m.paneText.text = model.body
end sub

' ---- Reading rows: the best at once, any other after one second at rest ----------------------------------------

sub onRowFocused()
    index = m.list.itemFocused
    if index < 0 or m.state <> "list" then return
    m.fullOpen = false
    focusRow(index)
end sub

sub focusRow(index as Integer)
    m.dwellTimer.control = "stop"
    showPane(index)
    key = faKey(m.items[index])
    if faShouldRead(m.cache, key, index = 0, false)
        startRead(index)
    else if faCacheState(m.cache, key) = "none"
        ' Restarting the Timer on every focus change is what makes it "at rest": fast scrolling never lets it fire.
        m.dwellTimer.control = "start"
    end if
end sub

sub onDwell()
    index = m.list.itemFocused
    if index < 0 or m.state <> "list" then return
    if faShouldRead(m.cache, faKey(m.items[index]), index = 0, true) then startRead(index)
end sub

sub startRead(index as Integer)
    item = m.items[index]
    key = faKey(item)
    faMarkReading(m.cache, key)
    showPane(index)
    runTask("alternativePreview", { assetId: m.assetId, provider: faStr(item.provider), sourceId: faStr(item.source_id), language: m.language }, key)
end sub

' ---- Buttons and rows ------------------------------------------------------------------------------------------

sub onRowSelected()
    index = m.list.itemSelected
    if index < 0 or index >= m.items.Count() then return
    ' Select reads the row at once if it has not been read, opens its words in full and moves to Use this.
    if faShouldRead(m.cache, faKey(m.items[index]), true, true) then startRead(index)
    m.fullOpen = true
    showPane(index)
    focusZone("use")
end sub

sub onUse()
    index = m.list.itemFocused
    if index < 0 or index >= m.items.Count() then return
    closeWindow(true, m.items[index])
end sub

sub onCancel()
    closeWindow(false, invalid)
end sub

sub onLanguageButton()
    m.choices = faLanguageChoices(m.language)
    root = CreateObject("roSGNode", "ContentNode")
    for each name in m.choices
        entry = root.CreateChild("ContentNode")
        entry.title = name
    end for
    m.languageList.content = root
    m.languageList.jumpToItem = 0
    m.languageGroup.visible = true
    m.languageOpen = true
    m.languageList.setFocus(true)
end sub

sub onLanguageChosen()
    index = m.languageList.itemSelected
    if index < 0 or index >= m.choices.Count() then return
    m.language = m.choices[index]
    searchAgain()
end sub

' ---- Keys ------------------------------------------------------------------------------------------------------

function currentZone() as String
    if m.languageOpen then return "languageList"
    if m.languageButton.hasFocus() then return "language"
    if m.state = "message" and m.messageCancel.hasFocus() then return "message"
    if m.list.hasFocus() then return "list"
    if m.paneText.hasFocus() then return "pane"
    if m.useButton.hasFocus() then return "use"
    if m.cancelButton.hasFocus() then return "cancel"
    return ""
end function

sub focusZone(zone as String)
    if zone = "list"
        m.list.setFocus(true)
    else if zone = "pane"
        m.paneText.setFocus(true)
    else if zone = "use"
        m.footer.focusButton = 0
        m.footer.setFocus(true)
    else if zone = "language"
        m.languageButton.setFocus(true)
    else if zone = "message"
        m.messageCancel.setFocus(true)
    end if
end sub

function onKeyEvent(key as String, press as Boolean) as Boolean
    ' While open, this window owns every key so nothing reaches the player or the library behind it.
    if not m.top.visible then return false
    if not press then return true
    if key = "back"
        action = faBackAction(m.languageOpen, m.fullOpen)
        if action = "closeLanguages"
            m.languageOpen = false
            m.languageGroup.visible = false
            m.languageButton.setFocus(true)
        else if action = "closeFullWords"
            m.fullOpen = false
            showPane(m.list.itemFocused)
            focusZone("list")
        else
            closeWindow(false, invalid)
        end if
        return true
    end if
    if m.languageOpen then return true
    zone = currentZone()
    target = ""
    if m.state = "list"
        target = faMove(zone, key, m.list.itemFocused = 0)
    else if m.state = "message"
        target = faMoveInMessage(zone, key, m.languageShown)
    end if
    if target <> "" then focusZone(target)
    return true
end function
