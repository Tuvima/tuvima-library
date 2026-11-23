' The right-hand panel (spec native-apps WP6). It draws a model made in PlayerLogic.brs and reports what the person did:
' Select on a row (selection), Left or Right on a Season or Mine | Global row (adjust), Left to go back to the controls
' (leaveRequested) and Select on Close (closeRequested). Back is not handled here: it goes up to the player, which closes the
' panel first (a nested view goes back to its root first).
'
' Focus has three places: the header's Close, the body list and the footer row. A row that is only a heading or a note is
' never focused; focus steps over it in the direction of travel.

sub init()
    m.title = m.top.findNode("panelTitle")
    m.closeRing = m.top.findNode("closeRing")
    m.closeFill = m.top.findNode("closeFill")
    m.closeLabel = m.top.findNode("closeLabel")
    m.bodyList = m.top.findNode("bodyList")
    m.footerList = m.top.findNode("footerList")
    m.footerLine = m.top.findNode("footerLine")
    m.bodyRows = []
    m.footerRows = []
    m.zone = "body"
    m.lastBody = 0
    titleFont = CreateObject("roSGNode", "Font")
    titleFont.uri = "font:BoldSystemFontFile"
    titleFont.size = 34
    m.title.font = titleFont
    closeFont = CreateObject("roSGNode", "Font")
    closeFont.uri = "font:SystemFontFile"
    closeFont.size = 26
    m.closeLabel.font = closeFont
    m.bodyList.observeField("itemFocused", "onBodyFocused")
    m.bodyList.observeField("itemSelected", "onBodySelected")
    m.footerList.observeField("itemSelected", "onFooterSelected")
end sub

function textOf(value as Dynamic) as String
    if value = invalid then return ""
    kind = Type(value)
    if kind = "roString" or kind = "String" then return value
    return ""
end function

function isFocusable(row as Object) as Boolean
    return row.kind <> "header" and row.kind <> "note"
end function

' One row as the ContentNode the PanelRow reads; every field is always there so a row never reads a missing one.
function rowNode(parent as Object, row as Object) as Object
    entry = parent.CreateChild("ContentNode")
    entry.title = textOf(row.title)
    entry.AddFields({
        kind: textOf(row.kind),
        second: textOf(row.second),
        chip: textOf(row.chip),
        check: row.check = true,
        on: row.on = true,
        value: textOf(row.value),
        adjustable: row.adjustable = true
    })
    return entry
end function

sub onModel()
    model = m.top.model
    if model = invalid then return
    hadFocus = m.top.isInFocusChain()
    zone = m.zone
    heading = textOf(model.title)
    if model.back = true then heading = "‹  " + heading
    m.title.text = heading
    m.bodyRows = model.rows
    m.footerRows = model.footer
    bodyRoot = CreateObject("roSGNode", "ContentNode")
    for each row in m.bodyRows
        rowNode(bodyRoot, row)
    end for
    m.bodyList.content = bodyRoot
    footerRoot = CreateObject("roSGNode", "ContentNode")
    for each row in m.footerRows
        rowNode(footerRoot, row)
    end for
    m.footerList.content = footerRoot
    m.footerList.visible = m.footerRows.Count() > 0
    m.footerLine.visible = m.footerRows.Count() > 0
    m.lastBody = 0
    if model.focus <> invalid and model.focus >= 0 and model.focus < m.bodyRows.Count() then m.lastBody = model.focus
    m.bodyList.jumpToItem = m.lastBody
    if hadFocus
        if zone = "footer" and m.footerRows.Count() > 0
            goFooter()
        else if zone = "header"
            goHeader()
        else
            goBody()
        end if
    end if
end sub

sub onFocusNow()
    goBody()
end sub

sub goBody()
    m.zone = "body"
    m.closeRing.visible = false
    m.bodyList.setFocus(true)
    m.bodyList.jumpToItem = m.lastBody
end sub

sub goFooter()
    if m.footerRows.Count() = 0 then return
    m.zone = "footer"
    m.closeRing.visible = false
    m.footerList.setFocus(true)
    m.footerList.jumpToItem = 0
end sub

sub goHeader()
    m.zone = "header"
    m.closeRing.visible = true
    m.top.setFocus(true)
end sub

' Section headings and notes are not choices: focus steps over them in the direction of travel, then on to the header (going
' up) or the footer (going down) when there is nothing more in that direction.
sub onBodyFocused()
    index = m.bodyList.itemFocused
    if index < 0 or index >= m.bodyRows.Count() then return
    if isFocusable(m.bodyRows[index])
        m.lastBody = index
        return
    end if
    direction = 1
    if index < m.lastBody then direction = -1
    target = index
    while target >= 0 and target < m.bodyRows.Count()
        if isFocusable(m.bodyRows[target]) then exit while
        target = target + direction
    end while
    if target >= 0 and target < m.bodyRows.Count()
        m.lastBody = target
        m.bodyList.jumpToItem = target
    else if direction < 0
        m.bodyList.jumpToItem = m.lastBody
        goHeader()
    else if m.footerRows.Count() > 0
        m.bodyList.jumpToItem = m.lastBody
        goFooter()
    else
        m.bodyList.jumpToItem = m.lastBody
    end if
end sub

sub onBodySelected()
    index = m.bodyList.itemSelected
    if index < 0 or index >= m.bodyRows.Count() then return
    row = m.bodyRows[index]
    if not isFocusable(row) then return
    if row.adjustable = true
        ' Select on Season or Mine | Global moves on to the next choice.
        m.top.adjust = { direction: 1, value: textOf(row.value), kind: textOf(row.kind), viaSelect: true }
        return
    end if
    m.top.selection = { kind: textOf(row.kind), value: textOf(row.value), index: index, row: row }
end sub

sub onFooterSelected()
    index = m.footerList.itemSelected
    if index < 0 or index >= m.footerRows.Count() then return
    row = m.footerRows[index]
    m.top.selection = { kind: textOf(row.kind), value: textOf(row.value), index: -1, row: row }
end sub

function focusedRow() as Dynamic
    if m.zone = "footer"
        if m.footerRows.Count() > 0 then return m.footerRows[0]
        return invalid
    end if
    index = m.bodyList.itemFocused
    if index >= 0 and index < m.bodyRows.Count() then return m.bodyRows[index]
    return invalid
end function

function onKeyEvent(key as String, press as Boolean) as Boolean
    if not m.top.visible then return false
    if not press then return true
    if key = "back" then return false
    if m.zone = "header"
        if key = "OK"
            m.top.closeRequested = true
        else if key = "down"
            goBody()
        else if key = "left"
            m.top.leaveRequested = true
        end if
        return true
    end if
    if key = "left" or key = "right"
        row = focusedRow()
        if row <> invalid and row.adjustable = true
            direction = 1
            if key = "left" then direction = -1
            m.top.adjust = { direction: direction, value: textOf(row.value), kind: textOf(row.kind) }
        else if key = "left"
            m.top.leaveRequested = true
        end if
        return true
    end if
    if key = "up" and m.zone = "footer"
        goBody()
    else if key = "down" and m.zone = "body" and m.footerRows.Count() > 0
        goFooter()
    else if key = "options"
        row = focusedRow()
        if row <> invalid then m.top.selection = { kind: "options", value: textOf(row.value), index: m.lastBody, row: row }
    end if
    ' The panel is modal: every other key is kept from the player.
    return true
end function
