package com.tuvima.library.mobile

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tuvima.library.core.PhoneDockTab
import com.tuvima.library.core.PhoneMenuItem
import com.tuvima.library.core.PhoneSection
import com.tuvima.library.core.PhoneShellLogic
import com.tuvima.library.core.PhoneTopBarAction

// The phone shell's pieces, as the Dashboard draws them on a phone: the top bar, the section chips, the floating dock and the menu.
// Dark only, existing tokens, 44 dp controls, icon buttons bare (no border), no toasts.

/** A bare icon button: a 44 dp target, no border and no fill. */
@Composable
internal fun BareIconButton(label: String, onClick: () -> Unit, content: @Composable () -> Unit) {
    Box(
        Modifier
            .size(TuvimaTokens.tapTarget)
            .clip(CircleShape)
            .clickable(onClickLabel = label, role = Role.Button, onClick = onClick)
            .semantics { contentDescription = label },
        contentAlignment = Alignment.Center,
    ) { content() }
}

/**
 * The top bar: the menu button, the logo, then on the right Downloads, My List and the avatar. Downloads is always shown (this is the
 * app, where downloads work). The avatar names the profile by its first letter.
 */
@Composable
internal fun PhoneTopBar(
    profileName: String?,
    onMenu: () -> Unit,
    onDownloads: () -> Unit,
    onMyList: () -> Unit,
    onAccount: () -> Unit,
) {
    Row(
        Modifier
            .fillMaxWidth()
            .background(TuvimaTokens.bgShell)
            .statusBarsPadding()
            .height(56.dp)
            .padding(horizontal = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        PhoneShellLogic.topBarActions().forEach { action ->
            when (action) {
                PhoneTopBarAction.Menu -> {
                    BareIconButton(PhoneShellLogic.MENU_TITLE, onMenu) { HamburgerGlyph(TuvimaTokens.textPrimary) }
                    Text(
                        "Tuvima",
                        color = TuvimaTokens.accentPrimaryHover,
                        fontWeight = FontWeight.Bold,
                        fontSize = 20.sp,
                        modifier = Modifier.padding(start = 4.dp),
                    )
                    Spacer(Modifier.weight(1f))
                }
                PhoneTopBarAction.Downloads ->
                    BareIconButton(action.label, onDownloads) { Text("⇩", color = TuvimaTokens.textPrimary, fontSize = 22.sp) }
                PhoneTopBarAction.MyList ->
                    BareIconButton(action.label, onMyList) { BookmarkGlyph(TuvimaTokens.textPrimary) }
                PhoneTopBarAction.Account ->
                    BareIconButton(action.label, onAccount) {
                        Box(
                            Modifier.size(32.dp).clip(CircleShape).background(TuvimaTokens.accentPrimary),
                            contentAlignment = Alignment.Center,
                        ) {
                            Text(
                                PhoneShellLogic.avatarInitial(profileName),
                                color = TuvimaTokens.textOnAccent,
                                fontWeight = FontWeight.Bold,
                                fontSize = 15.sp,
                            )
                        }
                    }
            }
        }
    }
}

/** The section chips under the top bar: Read, Watch, Listen, View, Collections, scrolling sideways. [selected] is null off a section. */
@Composable
internal fun PhoneSectionChips(sections: List<PhoneSection>, selected: PhoneSection?, onPick: (PhoneSection) -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState())
            .padding(horizontal = 12.dp, vertical = 4.dp)
            .semantics { contentDescription = PhoneShellLogic.SECTIONS_LABEL },
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        sections.forEach { section ->
            FilterChip(
                selected = selected == section,
                onClick = { onPick(section) },
                label = { Text(section.label) },
            )
        }
    }
}

/** The floating dock: Home, Search, My List. 48 dp tall; the current tab is lit, and no tab is lit off the three (a section, the menu pages). */
@Composable
internal fun PhoneDock(selected: PhoneDockTab?, onPick: (PhoneDockTab) -> Unit) {
    Box(
        Modifier.fillMaxWidth().navigationBarsPadding().padding(horizontal = 24.dp, vertical = 8.dp),
        contentAlignment = Alignment.Center,
    ) {
        Surface(
            shape = RoundedCornerShape(TuvimaTokens.radiusRound),
            color = TuvimaTokens.bgSurfaceElevated,
            shadowElevation = 8.dp,
        ) {
            Row(Modifier.padding(4.dp), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                PhoneShellLogic.dockTabs().forEach { tab ->
                    val lit = tab == selected
                    val tint = if (lit) TuvimaTokens.textPrimary else TuvimaTokens.textMuted
                    Row(
                        Modifier
                            .heightIn(min = TuvimaTokens.touchArea)
                            .widthIn(min = 88.dp)
                            .clip(RoundedCornerShape(TuvimaTokens.radiusRound))
                            .background(if (lit) TuvimaTokens.accentPrimary.copy(alpha = 0.28f) else TuvimaTokens.bgSurfaceElevated)
                            .clickable(onClickLabel = tab.label, role = Role.Tab) { onPick(tab) }
                            .padding(horizontal = 14.dp),
                        horizontalArrangement = Arrangement.spacedBy(6.dp, Alignment.CenterHorizontally),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        when (tab) {
                            PhoneDockTab.Home -> Text("⌂", color = tint, fontSize = 20.sp)
                            PhoneDockTab.Search -> Text("⌕", color = tint, fontSize = 20.sp)
                            PhoneDockTab.MyList -> BookmarkGlyph(tint)
                        }
                        Text(tab.label, color = tint, fontSize = 13.sp, fontWeight = if (lit) FontWeight.SemiBold else FontWeight.Normal)
                    }
                }
            }
        }
    }
}

/**
 * The menu: a full-screen sheet over the app. Rows come from [PhoneShellLogic.menuItems] (Home, My List, Recently added, the lanes, and
 * below the rule Settings and Switch profile). [isCurrent] marks the row for the page the person is on. Back closes it.
 */
@Composable
internal fun PhoneMenu(
    items: List<PhoneMenuItem>,
    isCurrent: (PhoneMenuItem) -> Boolean,
    onPick: (PhoneMenuItem) -> Unit,
    onClose: () -> Unit,
) {
    BackHandler(onBack = onClose)
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()) {
            Row(
                Modifier.fillMaxWidth().height(56.dp).padding(start = 16.dp, end = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(PhoneShellLogic.MENU_TITLE, style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
                BareIconButton("Close navigation", onClose) { Text("✕", color = TuvimaTokens.textPrimary, fontSize = 20.sp) }
            }
            items.forEach { item ->
                if (item.afterDivider) HorizontalDivider(Modifier.padding(vertical = 8.dp))
                val current = isCurrent(item)
                Row(
                    Modifier
                        .fillMaxWidth()
                        .heightIn(min = TuvimaTokens.touchArea)
                        .background(if (current) TuvimaTokens.accentPrimary.copy(alpha = 0.18f) else TuvimaTokens.bgRoot)
                        .clickable(onClickLabel = item.label, role = Role.Button) { onPick(item) }
                        .padding(horizontal = 16.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        item.label,
                        color = TuvimaTokens.textPrimary,
                        fontWeight = if (current) FontWeight.SemiBold else FontWeight.Normal,
                    )
                }
            }
        }
    }
}
