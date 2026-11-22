sub init()
    m.edge = m.top.findNode("edge")
    m.words = m.top.findNode("words")
end sub

sub onContent()
    content = m.top.itemContent
    if content = invalid then return
    ' The panel flips isCurrent on the content as the song moves on; follow that change on the node.
    content.observeField("isCurrent", "onCurrent")
    m.words.text = content.words
    onCurrent()
end sub

sub onCurrent()
    content = m.top.itemContent
    if content = invalid then return
    current = content.isCurrent = true
    m.edge.visible = current
    if current then m.words.color = "#FFFFFF" else m.words.color = "#9EA4BC"
end sub
