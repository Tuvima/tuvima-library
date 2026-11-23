' One row of the right-hand panel. The decisions (which rows exist and what they say) are made in PlayerLogic.brs; this file
' draws a row from its content. Kinds: plain, episode, bookmark, value, season, segment, add, switch, action, note, header.

sub init()
    m.ring = m.top.findNode("ring")
    m.fill = m.top.findNode("fill")
    m.segLeft = m.top.findNode("segLeft")
    m.segRight = m.top.findNode("segRight")
    m.titleLabel = m.top.findNode("titleLabel")
    m.secondLabel = m.top.findNode("secondLabel")
    m.valueLabel = m.top.findNode("valueLabel")
    m.centerLabel = m.top.findNode("centerLabel")
    m.segLeftLabel = m.top.findNode("segLeftLabel")
    m.segRightLabel = m.top.findNode("segRightLabel")
    m.chipBack = m.top.findNode("chipBack")
    m.chipLabel = m.top.findNode("chipLabel")
    m.checkMark = m.top.findNode("checkMark")
    m.switchTrack = m.top.findNode("switchTrack")
    m.switchKnob = m.top.findNode("switchKnob")
    setRowFont(m.titleLabel, 28, true)
    setRowFont(m.secondLabel, 24, false)
    setRowFont(m.valueLabel, 26, false)
    setRowFont(m.centerLabel, 28, true)
    setRowFont(m.segLeftLabel, 26, true)
    setRowFont(m.segRightLabel, 26, true)
    setRowFont(m.chipLabel, 24, false)
end sub

sub setRowFont(label as Object, size as Integer, bold as Boolean)
    font = CreateObject("roSGNode", "Font")
    if bold then font.uri = "font:BoldSystemFontFile" else font.uri = "font:SystemFontFile"
    font.size = size
    label.font = font
end sub

sub onContent()
    content = m.top.itemContent
    if content = invalid then return
    kind = content.kind
    m.titleLabel.visible = false
    m.secondLabel.visible = false
    m.valueLabel.visible = false
    m.centerLabel.visible = false
    m.segLeft.visible = false
    m.segRight.visible = false
    m.segLeftLabel.visible = false
    m.segRightLabel.visible = false
    m.chipBack.visible = false
    m.chipLabel.visible = false
    m.checkMark.visible = false
    m.switchTrack.visible = false
    m.switchKnob.visible = false
    m.titleLabel.color = "#F5F7FB"
    m.titleLabel.translation = [24, 8]
    m.titleLabel.height = 44
    m.titleLabel.width = 456
    m.titleLabel.wrap = false
    m.secondLabel.translation = [24, 52]
    m.fill.color = "#14151A"
    if kind = "header"
        ' A small heading ("Audio", "Subtitles"): not a choice.
        m.titleLabel.visible = true
        m.titleLabel.text = content.title
        m.titleLabel.color = "#94A3B8"
        m.titleLabel.translation = [24, 24]
        m.titleLabel.width = 600
    else if kind = "note"
        m.titleLabel.visible = true
        m.titleLabel.text = content.title
        m.titleLabel.color = "#94A3B8"
        m.titleLabel.translation = [24, 6]
        m.titleLabel.width = 600
        m.titleLabel.height = 80
        m.titleLabel.wrap = true
        m.titleLabel.numLines = 2
    else if kind = "segment"
        ' Mine | Global: Left and Right change the choice; the chosen half is purple.
        m.segLeftLabel.visible = true
        m.segRightLabel.visible = true
        m.segLeftLabel.text = content.title
        m.segRightLabel.text = content.second
        showChoice()
    else if kind = "add"
        m.fill.color = "#8852FC"
        m.centerLabel.visible = true
        m.centerLabel.text = "+  " + content.title
    else if kind = "action"
        m.centerLabel.visible = true
        m.centerLabel.text = content.title
    else if kind = "switch"
        m.titleLabel.visible = true
        m.titleLabel.text = content.title
        m.titleLabel.height = 76
        m.titleLabel.width = 480
        m.switchTrack.visible = true
        m.switchKnob.visible = true
        showSwitch(content.on = true)
    else if kind = "value"
        m.titleLabel.visible = true
        m.titleLabel.text = content.title
        m.titleLabel.height = 76
        m.titleLabel.width = 320
        m.valueLabel.visible = true
        m.valueLabel.text = content.second + "  ›"
    else if kind = "season"
        ' "‹  Season 1  ›" and the count at the right: Left and Right change the Season.
        m.titleLabel.visible = true
        m.titleLabel.text = "‹  " + content.title + "  ›"
        m.titleLabel.height = 76
        m.titleLabel.width = 330
        m.valueLabel.visible = true
        m.valueLabel.text = content.second
    else
        ' plain, episode, bookmark: the title, the line under it, and a chip or a check at the right.
        m.titleLabel.visible = true
        m.titleLabel.text = content.title
        m.secondLabel.visible = content.second <> ""
        m.secondLabel.text = content.second
        if content.second = "" then m.titleLabel.height = 76
        if content.chip <> invalid and content.chip <> ""
            m.chipBack.visible = true
            m.chipLabel.visible = true
            m.chipLabel.text = content.chip
        end if
        if content.check = true then m.checkMark.visible = true
    end if
    onFocusChange()
end sub

sub showChoice()
    content = m.top.itemContent
    if content = invalid then return
    isGlobal = content.value = "global"
    m.segLeft.visible = not isGlobal
    m.segRight.visible = isGlobal
    if isGlobal
        m.segLeftLabel.color = "#B6C2D6"
        m.segRightLabel.color = "#FFFFFF"
    else
        m.segLeftLabel.color = "#FFFFFF"
        m.segRightLabel.color = "#B6C2D6"
    end if
end sub

sub showSwitch(isOn as Boolean)
    if isOn
        m.switchTrack.color = "#8852FC"
        m.switchKnob.translation = [584, 27]
    else
        m.switchTrack.color = "#3A3B45"
        m.switchKnob.translation = [544, 27]
    end if
end sub

' The white ring and a lighter fill are the focus mark (the list's own focus drawing is off); a header or a note is never focused.
sub onFocusChange()
    content = m.top.itemContent
    if content = invalid then return
    kind = content.kind
    if kind = "header" or kind = "note"
        m.ring.visible = false
        return
    end if
    focused = m.top.itemHasFocus
    m.ring.visible = focused
    if kind = "add"
        m.fill.color = "#8852FC"
    else if focused
        m.fill.color = "#272830"
    else
        m.fill.color = "#14151A"
    end if
end sub
