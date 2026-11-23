' Audio & subtitles: the rows are made by faPanelRows (FindAnotherLogic.brs); this file puts them in the shared right-hand panel.
' Audio and subtitle rows go in the middle with a check on the one in use; "Find subtitles…" is the fixed footer row.

sub init()
    m.inner = m.top.findNode("inner")
    m.origin = []
    m.inner.observeField("selection", "onInnerSelection")
    m.inner.observeField("closeRequested", "onInnerClose")
    m.inner.observeField("leaveRequested", "onInnerLeave")
end sub

sub onOpen()
    args = m.top.openWith
    if args = invalid then return
    body = []
    footer = []
    m.origin = []
    focus = -1
    firstChoice = -1
    for each row in args.rows
        if row.kind = "find"
            footer.Push({ kind: "value", title: row.title, second: "", value: "find" })
        else if row.kind = "header"
            body.Push({ kind: "header", title: row.title })
            m.origin.Push(row)
        else
            if firstChoice < 0 then firstChoice = body.Count()
            if focus < 0 and row.on = true then focus = body.Count()
            body.Push({ kind: "plain", title: row.title, second: "", check: row.on = true, value: row.value })
            m.origin.Push(row)
        end if
    end for
    if focus < 0 then focus = firstChoice
    if focus < 0 then focus = 0
    m.top.visible = true
    m.inner.model = { title: "Audio & subtitles", back: false, rows: body, footer: footer, focus: focus }
    m.inner.focusNow = true
end sub

sub onFocusNow()
    m.inner.focusNow = true
end sub

sub onInnerSelection()
    selection = m.inner.selection
    if selection = invalid then return
    if selection.index < 0
        ' The footer row.
        m.top.selection = { kind: "find", value: "" }
    else if selection.index < m.origin.Count()
        row = m.origin[selection.index]
        m.top.selection = { kind: row.kind, value: row.value }
    end if
end sub

sub onInnerClose()
    m.top.closeRequested = true
end sub

sub onInnerLeave()
    m.top.leaveRequested = true
end sub

function onKeyEvent(key as String, press as Boolean) as Boolean
    if not m.top.visible then return false
    if not press then return true
    if key = "back"
        m.top.closeRequested = true
        return true
    end if
    ' The panel is modal: the rows handle their own keys; every other key is kept from the player.
    return true
end function
