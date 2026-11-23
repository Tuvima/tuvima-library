package com.tuvima.library.mobile

import android.content.Intent
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import com.tuvima.library.core.DeviceProfile
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.PhoneDockTab
import com.tuvima.library.core.PhoneMenuItem
import com.tuvima.library.core.PhoneMenuTarget
import com.tuvima.library.core.PhoneSection
import com.tuvima.library.core.PhoneShellLogic
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.playableAssetId

/** Where the shell is: one of the three dock tabs, a section chip, or a page the menu or top bar opens. */
private sealed interface Destination {
    data object Home : Destination
    data object MyList : Destination
    data object RecentlyAdded : Destination
    data object Downloads : Destination
    data class Section(val section: PhoneSection) : Destination
    data class Settings(val openProfiles: Boolean, val openSignOut: Boolean = false) : Destination
}

/**
 * The signed-in app, laid out as the Dashboard is on a phone: a top bar (menu, logo, Downloads, My List, avatar), the section chips
 * (Read, Watch, Listen, View, Collections), the page, and a floating dock of three tabs (Home, Search, My List). The Search tab opens a
 * full-screen search over everything rather than a page. The menu lists Home, My List, Recently added, the lanes, Settings, Switch
 * profile and Sign out. Full-screen pages (details, a collection, a Universe, a gallery, an artist, a person, the photo viewer) are shown
 * over all of it; Back removes the top one, then returns to Home.
 */
@Composable
internal fun MobileShell(
    application: TuvimaMobileApplication,
    client: TuvimaClient,
    initialDetail: Pair<String, String>?,
    play: (PlayRequest) -> Unit,
    onChangeServer: () -> Unit,
    onSignedOut: (String?) -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var destination by remember { mutableStateOf<Destination>(Destination.Home) }
    var menuOpen by remember { mutableStateOf(false) }
    var searchOpen by remember { mutableStateOf(false) }
    // Bumped after a profile switch so every screen reads again as the new person.
    var epoch by remember { mutableIntStateOf(0) }
    val stack = remember { mutableStateListOf<Overlay>() }
    // The new person's saved titles are read again after a switch (a fresh holder per person).
    val saved = remember(client, epoch) { SavedItems(client, scope) }
    var profiles by remember { mutableStateOf<List<DeviceProfile>>(emptyList()) }

    LaunchedEffect(initialDetail) {
        initialDetail?.let { stack.add(Overlay.Detail(it.first, it.second, null)) }
    }
    // Offline downloads: resume what a restart interrupted, drop expired copies, and bring the Engine in step.
    LaunchedEffect(client) {
        application.offline.onProfileActivated()
    }
    LaunchedEffect(saved) { saved.load() }
    LaunchedEffect(client, epoch) {
        // Best effort: without the list the avatar shows a dot and the menu leaves out Switch profile until the next read.
        runCatching { client.deviceProfiles() }.getOrNull()?.let { profiles = it }
    }
    BackHandler(enabled = stack.isNotEmpty()) { stack.removeAt(stack.lastIndex) }
    BackHandler(enabled = stack.isEmpty() && !menuOpen && destination != Destination.Home) { destination = Destination.Home }

    val active = profiles.firstOrNull { it.isActive }
    // Fails closed: until the profile is known to be an adult, the shell is the Kids shell (no Settings, no Recently added)
    val isKids = PhoneShellLogic.treatAsKids(active?.isKids)
    val profileCount = profiles.size

    fun goTo(next: Destination) {
        destination = next
        stack.clear()
        menuOpen = false
    }

    fun openCard(card: DisplayCard) {
        val target = mobileCardDetailTarget(card)
        stack.add(Overlay.Detail(target.first, target.second, card.playableAssetId()))
    }

    fun profileSwitched() {
        stack.clear()
        searchOpen = false
        destination = Destination.Home
        epoch += 1
        // The new person has their own downloads; start their queue and sync their offline progress.
        application.offline.onProfileActivated()
        // The system media service holds the previous person's access; stopping it makes the next play start fresh.
        context.stopService(Intent(context, TuvimaMediaLibraryService::class.java))
    }

    fun pickMenu(item: PhoneMenuItem) {
        when (item.target) {
            PhoneMenuTarget.Home -> goTo(Destination.Home)
            PhoneMenuTarget.MyList -> goTo(Destination.MyList)
            PhoneMenuTarget.RecentlyAdded -> goTo(Destination.RecentlyAdded)
            PhoneMenuTarget.Section -> item.section?.let { goTo(Destination.Section(it)) }
            PhoneMenuTarget.Settings -> goTo(Destination.Settings(openProfiles = false))
            PhoneMenuTarget.SwitchProfile -> goTo(Destination.Settings(openProfiles = true))
            PhoneMenuTarget.SignOut -> goTo(Destination.Settings(openProfiles = false, openSignOut = true))
        }
    }

    fun isCurrent(item: PhoneMenuItem): Boolean = when (item.target) {
        PhoneMenuTarget.Home -> destination == Destination.Home
        PhoneMenuTarget.MyList -> destination == Destination.MyList
        PhoneMenuTarget.RecentlyAdded -> destination == Destination.RecentlyAdded
        PhoneMenuTarget.Section -> (destination as? Destination.Section)?.section == item.section
        PhoneMenuTarget.Settings -> destination is Destination.Settings
        PhoneMenuTarget.SwitchProfile -> false
        PhoneMenuTarget.SignOut -> false
    }

    val selectedDock: PhoneDockTab? = when (destination) {
        Destination.Home -> PhoneDockTab.Home
        Destination.MyList -> PhoneDockTab.MyList
        else -> null
    }

    CompositionLocalProvider(LocalSavedItems provides saved) {
        Box(Modifier.fillMaxSize()) {
            Scaffold(
                topBar = {
                    PhoneTopBar(
                        profileName = active?.displayName,
                        onMenu = { menuOpen = true },
                        onDownloads = { goTo(Destination.Downloads) },
                        onMyList = { goTo(Destination.MyList) },
                        // The avatar leads to the people on this phone when there is more than one, otherwise to the Settings page
                        // (for a Kids profile that page holds only the profile and Sign out).
                        onAccount = {
                            goTo(Destination.Settings(openProfiles = profileCount > 1))
                        },
                    )
                },
                bottomBar = {
                    PhoneDock(selected = selectedDock) { tab ->
                        when (tab) {
                            PhoneDockTab.Home -> goTo(Destination.Home)
                            PhoneDockTab.Search -> { searchOpen = true }
                            PhoneDockTab.MyList -> goTo(Destination.MyList)
                        }
                    }
                },
            ) { padding ->
                Column(Modifier.padding(padding).fillMaxSize()) {
                    PhoneSectionChips(
                        sections = PhoneShellLogic.sections(isKids),
                        selected = (destination as? Destination.Section)?.section,
                        onPick = { goTo(Destination.Section(it)) },
                    )
                    Box(Modifier.weight(1f).fillMaxWidth()) {
                        key(epoch) {
                            when (val current = destination) {
                                Destination.Home -> HomeScreen(
                                    client = client,
                                    saved = saved,
                                    play = play,
                                    openCard = ::openCard,
                                    openMyList = { goTo(Destination.MyList) },
                                )
                                Destination.MyList -> MyListScreen(
                                    client = client,
                                    saved = saved,
                                    openCard = ::openCard,
                                    goHome = { goTo(Destination.Home) },
                                )
                                Destination.RecentlyAdded -> RecentlyAddedScreen(client, saved, ::openCard)
                                Destination.Downloads -> DownloadsScreen(
                                    application = application,
                                    client = client,
                                    play = play,
                                    openPhotos = { items, index -> stack.add(Overlay.ViewerOpen(items, index)) },
                                )
                                is Destination.Section -> LibrariesScreen(
                                    section = current.section,
                                    client = client,
                                    play = play,
                                    openCard = ::openCard,
                                    openCollection = { stack.add(Overlay.CollectionOpen(it)) },
                                    openViewer = { items, index -> stack.add(Overlay.ViewerOpen(items, index)) },
                                    openGallery = { stack.add(Overlay.GalleryOpen(it)) },
                                    openArtist = { stack.add(Overlay.ArtistOpen(it)) },
                                    openPerson = { stack.add(Overlay.PersonOpen(it)) },
                                )
                                is Destination.Settings -> SettingsScreen(
                                    application = application,
                                    client = client,
                                    onProfileSwitched = { profileSwitched() },
                                    onChangeServer = onChangeServer,
                                    onSignedOut = onSignedOut,
                                    openProfilesOnStart = current.openProfiles,
                                    openSignOutOnStart = current.openSignOut,
                                    limited = isKids,
                                )
                            }
                        }
                    }
                }
            }

            val top = stack.lastOrNull()
            if (top != null) {
                Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                    key(stack.size, top) {
                        when (top) {
                            is Overlay.Detail -> DetailScreen(
                                application = application,
                                client = client,
                                entityType = top.entityType,
                                id = top.id,
                                assetId = top.assetId,
                                onBack = { stack.removeAt(stack.lastIndex) },
                                play = play,
                            )
                            is Overlay.CollectionOpen -> CollectionDetailScreen(
                                client = client,
                                entry = top.entry,
                                openDetail = { entityType, id, assetId -> stack.add(Overlay.Detail(entityType, id, assetId)) },
                                openUniverse = { qid -> stack.add(Overlay.UniverseOpen(qid)) },
                                onBack = { stack.removeAt(stack.lastIndex) },
                            )
                            is Overlay.UniverseOpen -> UniverseScreen(
                                client = client,
                                qid = top.qid,
                                openCard = ::openCard,
                                onBack = { stack.removeAt(stack.lastIndex) },
                            )
                            is Overlay.GalleryOpen -> GalleryScreen(
                                client = client,
                                gallery = top.gallery,
                                openViewer = { items, index -> stack.add(Overlay.ViewerOpen(items, index)) },
                                onBack = { stack.removeAt(stack.lastIndex) },
                            )
                            is Overlay.ArtistOpen -> ArtistSongsScreen(
                                client = client,
                                artist = top.name,
                                play = play,
                                onBack = { stack.removeAt(stack.lastIndex) },
                            )
                            is Overlay.PersonOpen -> PersonPhotosScreen(
                                client = client,
                                person = top.person,
                                openViewer = { items, index -> stack.add(Overlay.ViewerOpen(items, index)) },
                                onBack = { stack.removeAt(stack.lastIndex) },
                            )
                            is Overlay.ViewerOpen -> PhotoViewerScreen(
                                client = client,
                                items = top.items,
                                startIndex = top.index,
                                onClose = { stack.removeAt(stack.lastIndex) },
                            )
                        }
                    }
                }
            }

            if (searchOpen) {
                SearchOverlay(
                    client = client,
                    onOpen = { hit ->
                        searchOpen = false
                        stack.add(Overlay.Detail(hit.entityType, hit.id, null))
                    },
                    onClose = { searchOpen = false },
                )
            }

            if (menuOpen) {
                PhoneMenu(
                    items = PhoneShellLogic.menuItems(isKids, profileCount),
                    isCurrent = ::isCurrent,
                    onPick = ::pickMenu,
                    onClose = { menuOpen = false },
                )
            }
        }
    }
}
