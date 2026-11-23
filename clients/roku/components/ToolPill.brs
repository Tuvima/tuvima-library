' One tool pill: 76 px high, 26 px text, the same size whether or not a panel is open.

sub init()
    m.ring = m.top.findNode("ring")
    m.fill = m.top.findNode("fill")
    m.text = m.top.findNode("text")
    font = CreateObject("roSGNode", "Font")
    font.uri = "font:SystemFontFile"
    font.size = 26
    m.text.font = font
end sub

sub onLook()
    width = m.top.pillWidth
    m.ring.width = width + 8
    m.ring.height = 84
    m.ring.translation = [-4, -4]
    m.fill.width = width
    m.fill.height = 76
    m.text.width = width
    m.text.height = 76
    m.text.text = m.top.label
    m.ring.visible = m.top.focused
    if m.top.selected
        m.fill.color = "#5A3FA0"
    else if m.top.focused
        m.fill.color = "#4A4B57"
    else
        m.fill.color = "#2F3039"
    end if
end sub
