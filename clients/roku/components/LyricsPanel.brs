' The Lyrics side panel (spec find-another-on-tv D6, WP5). The decisions live in FindAnotherLogic.brs
' (faLyricsPanelModel, faActiveLyricIndex); this file draws them. The current synced line has a purple edge and
' bright words, and the list keeps it in the middle as the song plays.

sub init()
    m.list = m.top.findNode("lyricsList")
    m.note = m.top.findNode("noteLabel")
    m.findButton = m.top.findNode("findButton")
    m.chipBack = m.top.findNode("chipBack")
    m.chipLabel = m.top.findNode("chipLabel")
    m.title = m.top.findNode("panelTitle")
    m.current = -1
    m.findButton.observeField("buttonSelected", "onFind")
end sub

sub onModel()
    model = m.top.model
    if model = invalid then return
    m.title.text = model.heading
    m.chipLabel.text = model.chip
    m.chipBack.visible = model.chip <> ""
    m.current = -1
    rows = model.rows
    if rows.Count() > 0
        root = CreateObject("roSGNode", "ContentNode")
        for each words in rows
            entry = root.CreateChild("ContentNode")
            entry.AddFields({ words: words, isCurrent: false })
        end for
        m.list.content = root
        m.list.visible = true
        m.note.visible = false
    else
        m.list.content = invalid
        m.list.visible = false
        m.note.text = model.note
        m.note.visible = true
    end if
    m.findButton.visible = model.showFind
    m.top.visible = true
    if model.showFind
        m.findButton.setFocus(true)
    else
        m.list.setFocus(true)
    end if
end sub

' Moves the highlight from the old current line to the new one and scrolls the list to it.
sub onActive()
    index = m.top.activeIndex
    if index = m.current then return
    content = m.list.content
    if content = invalid then return
    if m.current >= 0
        old = content.getChild(m.current)
        if old <> invalid then old.isCurrent = false
    end if
    m.current = index
    if index >= 0
        entry = content.getChild(index)
        if entry <> invalid then entry.isCurrent = true
        m.list.jumpToItem = index
    end if
end sub

sub onFind()
    m.top.selection = "find"
end sub

function onKeyEvent(key as String, press as Boolean) as Boolean
    if not m.top.visible then return false
    if not press then return true
    if key = "back"
        m.top.closeRequested = true
        return true
    end if
    ' The panel is modal: the buttons and the list handle their own keys; every other key is kept from the player.
    return true
end function
