' Draws the player's controls from a model (see PlayerLogic.brs). Nothing here decides anything: the words, the order and which
' button is dimmed come from the model, and where focus is comes from MainScene.

sub init()
    m.titleLabel = m.top.findNode("titleLabel")
    m.lineLabel = m.top.findNode("lineLabel")
    m.descLabel = m.top.findNode("descLabel")
    m.playedLabel = m.top.findNode("playedLabel")
    m.leftLabel = m.top.findNode("leftLabel")
    m.barRing = m.top.findNode("barRing")
    m.barTrack = m.top.findNode("barTrack")
    m.barFill = m.top.findNode("barFill")
    m.barKnob = m.top.findNode("barKnob")
    m.tickGroup = m.top.findNode("tickGroup")
    m.endCard = m.top.findNode("endCard")
    m.buttons = []
    for i = 0 to 4
        m.buttons.Push(m.top.findNode("tb" + i.ToStr()))
    end for
    m.pills = []
    for i = 0 to 3
        m.pills.Push(m.top.findNode("tool" + i.ToStr()))
    end for
    ' A pool of tick marks for "Show on timeline".
    m.ticks = []
    for i = 0 to 39
        tick = CreateObject("roSGNode", "Rectangle")
        tick.width = 4
        tick.height = 24
        tick.color = "#FF9F1C"
        tick.visible = false
        m.tickGroup.appendChild(tick)
        m.ticks.Push(tick)
    end for
    setFont(m.titleLabel, 44, true)
    setFont(m.lineLabel, 30, false)
    setFont(m.descLabel, 26, false)
    setFont(m.playedLabel, 26, false)
    setFont(m.leftLabel, 26, false)
    setFont(m.top.findNode("endHeading"), 34, true)
    setFont(m.top.findNode("endCountdown"), 26, false)
    setFont(m.top.findNode("endTitle"), 30, true)
    setFont(m.top.findNode("endKind"), 24, false)
    setFont(m.top.findNode("cancelLabel"), 28, true)
    setFont(m.top.findNode("playNowLabel"), 28, true)
    m.barX = 220
    m.barWidth = 1480
    m.fraction = 0.0
    m.areaWidth = 1920
end sub

sub setFont(label as Object, size as Integer, bold as Boolean)
    font = CreateObject("roSGNode", "Font")
    if bold then font.uri = "font:BoldSystemFontFile" else font.uri = "font:SystemFontFile"
    font.size = size
    label.font = font
end sub

' A tool pill's width from its words (26 px text), with 28 px either side.
function pillWidthFor(text as String) as Integer
    return 56 + Len(text) * 14
end function

sub onModel()
    model = m.top.model
    if model = invalid then return
    area = model.areaWidth
    m.areaWidth = area

    ' While a panel is open only the transport and the tool row stay (mock-up pictures 3 to 6): no title, no timeline.
    compact = model.compact = true
    for each node in [m.titleLabel, m.lineLabel, m.playedLabel, m.leftLabel, m.barTrack, m.barFill, m.barKnob, m.tickGroup]
        node.visible = not compact
    end for

    ' Title and the one line; with a description (paused episodes and lessons) they move up to make room.
    showDescription = model.showDescription = true
    m.titleLabel.text = model.title
    m.lineLabel.text = model.line
    m.descLabel.visible = showDescription
    m.descLabel.text = model.description
    if showDescription
        m.titleLabel.translation = [96, 566]
        m.lineLabel.translation = [96, 624]
    else
        m.titleLabel.translation = [96, 632]
        m.lineLabel.translation = [96, 690]
    end if
    m.titleLabel.width = area - 192
    m.lineLabel.width = area - 192

    ' The timeline spans the area the controls have (narrower while a panel is open).
    rightX = area - 96 - 110
    m.leftLabel.translation = [rightX, 752]
    m.barWidth = rightX - 14 - m.barX
    m.barTrack.width = m.barWidth
    m.barRing.width = m.barWidth + 8
    m.barRing.visible = model.zone = "timeline" and model.endCard = invalid and not compact

    ' The transport row, centred on one line.
    focusTransport = model.zone = "transport" and model.endCard = invalid
    for i = 0 to 4
        button = m.buttons[i]
        if i = 2 then button.diameter = 136 else button.diameter = 112
        button.spec = model.transport[i]
        button.translation = [area / 2 + (i - 2) * 140, 864]
        button.focused = focusTransport and model.index = i
    end for

    ' The tool row: only the tools this media and app support; the others take no space.
    tools = model.tools
    total = 0
    for each tool in tools
        total = total + pillWidthFor(tool.label)
    end for
    if tools.Count() > 1 then total = total + 24 * (tools.Count() - 1)
    x = area / 2 - total / 2
    focusTools = model.zone = "tools" and model.endCard = invalid
    for i = 0 to 3
        pill = m.pills[i]
        if i < tools.Count()
            width = pillWidthFor(tools[i].label)
            pill.pillWidth = width
            pill.label = tools[i].label
            pill.selected = tools[i].key = model.selectedTool
            pill.focused = focusTools and model.index = i
            pill.translation = [x, 948]
            pill.visible = true
            x = x + width + 24
        else
            pill.visible = false
        end if
    end for

    drawTicks(model.ticks)
    drawEndCard(model.endCard)
    moveBar()
end sub

sub drawTicks(ticks as Dynamic)
    count = 0
    if ticks <> invalid then count = ticks.Count()
    for i = 0 to m.ticks.Count() - 1
        tick = m.ticks[i]
        if i < count
            tick.translation = [m.barX + m.barWidth * ticks[i] - 2, 760]
            tick.visible = true
        else
            tick.visible = false
        end if
    end for
end sub

sub drawEndCard(card as Dynamic)
    if card = invalid
        m.endCard.visible = false
        return
    end if
    m.endCard.visible = true
    m.top.findNode("endHeading").text = card.heading
    m.top.findNode("endCountdown").text = card.countdown
    m.top.findNode("endTitle").text = card.title
    m.top.findNode("endKind").text = card.kind
    m.top.findNode("cancelLabel").text = card.cancel
    m.top.findNode("playNowLabel").text = card.playNow
    ' Focus lands on Play now (1); Left reaches Cancel (0).
    m.top.findNode("cancelRing").visible = card.focus = 0
    m.top.findNode("playNowRing").visible = card.focus = 1
end sub

sub onProgress()
    progress = m.top.progress
    if progress = invalid then return
    position = progress.position
    duration = progress.duration
    m.playedLabel.text = plFormatTime(position)
    m.leftLabel.text = plTimeLeft(position, duration)
    m.fraction = 0.0
    if duration > 0 then m.fraction = position / duration
    if m.fraction < 0 then m.fraction = 0.0
    if m.fraction > 1 then m.fraction = 1.0
    moveBar()
end sub

sub moveBar()
    filled = m.barWidth * m.fraction
    m.barFill.width = filled
    m.barKnob.translation = [m.barX + filled - 13, 759]
end sub
