sub init()
    m.ring = m.top.findNode("ring")
    m.release = m.top.findNode("release")
    m.meta = m.top.findNode("meta")
    m.check = m.top.findNode("check")
    m.timing = m.top.findNode("timing")
    m.chipBest = m.top.findNode("chipBest")
    m.chipBestBack = m.top.findNode("chipBestBack")
    m.chipUse = m.top.findNode("chipUse")
    m.chipUseBack = m.top.findNode("chipUseBack")
end sub

sub onContent()
    content = m.top.itemContent
    if content = invalid then return
    ' Reading a row fills in its timing after the row is drawn; follow that change on the node.
    content.observeField("timing", "onContent")
    m.release.text = content.release
    m.meta.text = content.meta
    m.check.text = content.check
    m.check.color = faToneColor(content.checkTone)
    m.timing.text = content.timing
    m.timing.color = faToneColor(content.timingTone)
    m.chipBest.text = content.chipBest
    m.chipBestBack.visible = content.chipBest <> ""
    m.chipUse.text = content.chipInUse
    m.chipUseBack.visible = content.chipInUse <> ""
end sub

sub onFocusChange()
    ' The white ring is the focus mark (the list's own focus drawing is off).
    m.ring.visible = m.top.itemHasFocus
end sub
