' One round transport button: a disc, a plain white icon and, for the two skips, the seconds inside the circle.
' Previous, Skip back, Play, Skip forward, Next. Dimmed means 35 percent opacity, never hidden.

sub init()
    m.ring = m.top.findNode("ring")
    m.disc = m.top.findNode("disc")
    m.icon = m.top.findNode("icon")
    m.seconds = m.top.findNode("seconds")
    font = CreateObject("roSGNode", "Font")
    font.uri = "font:BoldSystemFontFile"
    font.size = 28
    m.seconds.font = font
end sub

sub onSpec()
    spec = m.top.spec
    if spec = invalid then return
    size = m.top.diameter
    ringSize = size + 16
    m.ring.width = ringSize
    m.ring.height = ringSize
    m.ring.translation = [-ringSize / 2, -ringSize / 2]
    m.disc.width = size
    m.disc.height = size
    m.disc.translation = [-size / 2, -size / 2]
    isSkip = spec.key = "skip-back" or spec.key = "skip-forward"
    iconSize = size * 0.46
    if isSkip then iconSize = size * 0.78
    m.icon.width = iconSize
    m.icon.height = iconSize
    m.icon.translation = [-iconSize / 2, -iconSize / 2]
    name = spec.key
    if spec.key = "play-pause"
        name = "play"
        if spec.label = "Pause" then name = "pause"
    end if
    m.icon.uri = "pkg:/images/" + name + ".png"
    m.icon.blendColor = "#FFFFFF"
    m.seconds.visible = isSkip
    if isSkip
        m.seconds.text = spec.seconds.ToStr()
        m.seconds.width = size
        m.seconds.height = size
        m.seconds.translation = [-size / 2, -size / 2 + 6]
        m.icon.blendColor = "#8E96A8"
    end if
    if plIsTrue(spec.dimmed) then m.top.opacity = 0.35 else m.top.opacity = 1.0
    onFocus()
end sub

sub onFocus()
    spec = m.top.spec
    if spec = invalid then return
    focused = m.top.focused
    m.ring.visible = focused
    if spec.key = "play-pause"
        if focused then m.disc.blendColor = "#A57BFF" else m.disc.blendColor = "#8852FC"
    else
        if focused then m.disc.blendColor = "#4A4B57" else m.disc.blendColor = "#2F3039"
    end if
end sub
