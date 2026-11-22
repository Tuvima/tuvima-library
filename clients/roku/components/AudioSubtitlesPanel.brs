sub init()
    m.list = m.top.findNode("panelList")
    m.rows = []
    m.lastIndex = -1
    m.list.observeField("itemFocused", "onRowFocused")
    m.list.observeField("itemSelected", "onRowSelected")
end sub

sub onOpen()
    args = m.top.openWith
    if args = invalid then return
    m.rows = args.rows
    root = CreateObject("roSGNode", "ContentNode")
    for each row in m.rows
        entry = root.CreateChild("ContentNode")
        entry.title = row.title
    end for
    m.lastIndex = -1
    m.list.content = root
    m.top.visible = true
    first = faNextSelectable(m.rows, 0, 1)
    if first < 0 then first = 0
    m.list.jumpToItem = first
    m.lastIndex = first
    m.list.setFocus(true)
end sub

' Section headings are not choices: focus steps over them in the direction of travel.
sub onRowFocused()
    index = m.list.itemFocused
    if index < 0 or index >= m.rows.Count() then return
    if m.rows[index].kind = "header"
        direction = 1
        if m.lastIndex >= 0 and index < m.lastIndex then direction = -1
        target = faNextSelectable(m.rows, index, direction)
        if target < 0 then target = m.lastIndex
        if target < 0 then return
        m.list.jumpToItem = target
        m.lastIndex = target
    else
        m.lastIndex = index
    end if
end sub

sub onRowSelected()
    index = m.list.itemSelected
    if index < 0 or index >= m.rows.Count() then return
    row = m.rows[index]
    if row.kind = "header" then return
    m.top.selection = { kind: row.kind, value: row.value }
end sub

function onKeyEvent(key as String, press as Boolean) as Boolean
    if not m.top.visible then return false
    if not press then return true
    if key = "back"
        m.top.closeRequested = true
        return true
    end if
    ' The panel is modal: arrows and Select are handled by the list; every other key is kept from the player.
    return true
end function
