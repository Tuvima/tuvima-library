package com.tuvima.library.mobile

import android.content.Intent
import android.widget.Toast
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.playableAssetId

private enum class MobileTab(val label: String, val glyph: String) {
    Home("Home", "⌂"),
    Libraries("Libraries", "▦"),
    Search("Search", "⌕"),
    Downloads("Downloads", "⇩"),
    Settings("Settings", "⚙"),
}

/**
 * The signed-in app: bottom navigation (Home, Libraries, Search, Downloads, Settings) with full-screen pages
 * (details, a collection, a Universe, a gallery, the photo viewer) shown over it. Back removes the top page.
 */
@Composable
internal fun MobileShell(
    application: TuvimaMobileApplication,
    client: TuvimaClient,
    initialDetail: Pair<String, String>?,
    play: (String) -> Unit,
    onChangeServer: () -> Unit,
    onSignedOut: (String?) -> Unit,
) {
    val context = LocalContext.current
    var tab by remember { mutableStateOf(MobileTab.Home) }
    // Bumped after a profile switch so every screen reads again as the new person.
    var epoch by remember { mutableIntStateOf(0) }
    val stack = remember { mutableStateListOf<Overlay>() }

    LaunchedEffect(initialDetail) {
        initialDetail?.let { stack.add(Overlay.Detail(it.first, it.second, null)) }
    }
    // Offline downloads: resume what a restart interrupted, drop expired copies, and bring the Engine in step.
    LaunchedEffect(client) {
        application.offline.onProfileActivated()
    }
    BackHandler(enabled = stack.isNotEmpty()) { stack.removeAt(stack.lastIndex) }

    fun openCard(card: DisplayCard) {
        val target = mobileCardDetailTarget(card)
        stack.add(Overlay.Detail(target.first, target.second, card.playableAssetId()))
    }

    fun profileSwitched(name: String) {
        stack.clear()
        epoch += 1
        // The new person has their own downloads; start their queue and sync their offline progress.
        application.offline.onProfileActivated()
        // The system media service holds the previous person's access; stopping it makes the next play start fresh.
        context.stopService(Intent(context, TuvimaMediaLibraryService::class.java))
        Toast.makeText(context, "Switched to $name", Toast.LENGTH_SHORT).show()
    }

    Box(Modifier.fillMaxSize()) {
        Scaffold(
            bottomBar = {
                NavigationBar {
                    MobileTab.entries.forEach { item ->
                        NavigationBarItem(
                            selected = tab == item,
                            onClick = {
                                tab = item
                                stack.clear()
                            },
                            icon = { Text(item.glyph) },
                            label = { Text(item.label) },
                        )
                    }
                }
            },
        ) { padding ->
            Box(Modifier.padding(padding).fillMaxSize()) {
                key(epoch) {
                    when (tab) {
                        MobileTab.Home -> HomeScreen(client, ::openCard)
                        MobileTab.Libraries -> LibrariesScreen(
                            client = client,
                            openCard = ::openCard,
                            openCollection = { stack.add(Overlay.CollectionOpen(it)) },
                            openViewer = { items, index -> stack.add(Overlay.ViewerOpen(items, index)) },
                            openGallery = { stack.add(Overlay.GalleryOpen(it)) },
                        )
                        MobileTab.Search -> SearchScreen(client) { entityType, id ->
                            stack.add(Overlay.Detail(entityType, id, null))
                        }
                        MobileTab.Downloads -> DownloadsScreen(
                            application = application,
                            client = client,
                            play = play,
                            openPhotos = { items, index -> stack.add(Overlay.ViewerOpen(items, index)) },
                        )
                        MobileTab.Settings -> SettingsScreen(
                            application = application,
                            client = client,
                            onProfileSwitched = ::profileSwitched,
                            onChangeServer = onChangeServer,
                            onSignedOut = onSignedOut,
                        )
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
    }
}
