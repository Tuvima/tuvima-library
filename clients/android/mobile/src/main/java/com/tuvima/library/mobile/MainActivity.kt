package com.tuvima.library.mobile

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.tuvima.library.core.PairingPollResult
import com.tuvima.library.core.PairingSession
import com.tuvima.library.core.PhotoScopes
import com.tuvima.library.core.PlaybackRouting
import com.tuvima.library.core.TuvimaClient
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val initialDetail = intent.data?.takeIf { it.scheme == "tuvima" && it.host == "details" }
            ?.pathSegments?.takeIf { it.size >= 2 }?.let { it[0] to it[1] }
        if (intent.data?.scheme == "tuvima" && intent.data?.host == "play") {
            intent.data?.pathSegments?.firstOrNull()?.let { play(PlayRequest(it, null, null, null)) }
        }
        setContent {
            MaterialTheme(colorScheme = darkColorScheme()) {
                MobileRoot(application as TuvimaMobileApplication, initialDetail, ::play)
            }
        }
    }

    private fun play(request: PlayRequest) {
        if (PlaybackRouting.opensVideoScreen(request.entityType)) {
            startActivity(VideoPlayerActivity.intent(this, request.assetId, request.title, request.subtitle, request.showId))
            return
        }
        ContextCompat.startForegroundService(
            this,
            Intent(this, TuvimaMediaLibraryService::class.java)
                .setAction(TuvimaMediaLibraryService.ACTION_PLAY_ASSET)
                .putExtra(TuvimaMediaLibraryService.EXTRA_ASSET_ID, request.assetId),
        )
    }
}

private sealed interface MobileStage {
    data object Server : MobileStage
    data class Pairing(val session: PairingSession) : MobileStage
    data object Ready : MobileStage
    data class Message(val text: String) : MobileStage
}

@Composable
private fun MobileRoot(
    application: TuvimaMobileApplication,
    initialDetail: Pair<String, String>?,
    play: (PlayRequest) -> Unit,
) {
    val scope = rememberCoroutineScope()
    var stage: MobileStage by remember { mutableStateOf(MobileStage.Server) }
    var client: TuvimaClient? by remember { mutableStateOf(application.client) }

    fun connect(address: String) {
        stage = MobileStage.Message("Connecting…")
        scope.launch {
            runCatching {
                application.connect(address).also {
                    client = it
                    it.discover()
                    stage = if (it.isPaired()) MobileStage.Ready
                    else MobileStage.Pairing(it.beginPairing(PhotoScopes.requested(MOBILE_SCOPES, application.photosOptIn())))
                }
            }.onFailure { failure ->
                // A paired phone with no connection still opens, so Downloads can play what is saved on it.
                stage = if (failure is java.io.IOException && application.client?.isPaired() == true) {
                    MobileStage.Ready
                } else {
                    MobileStage.Message(failure.message ?: "Connection failed.")
                }
            }
        }
    }

    LaunchedEffect(Unit) {
        application.savedServer()?.let(::connect)
    }

    // The Engine revoked this device: its sign-in and offline data are already cleared, so go back to pairing.
    val revoked by application.sessionRevoked.collectAsState()
    LaunchedEffect(revoked) {
        if (revoked) {
            application.sessionRevoked.value = false
            val address = application.savedServer()
            if (address != null) connect(address) else stage = MobileStage.Server
        }
    }

    when (val current = stage) {
        MobileStage.Server -> ServerEntry(application.savedServer().orEmpty(), application.photosOptIn()) { address, photos ->
            application.setPhotosOptIn(photos)
            connect(address)
        }
        is MobileStage.Pairing -> MobilePairing(client!!, current.session) { result ->
            when (result) {
                is PairingPollResult.Authorized -> stage = MobileStage.Ready
                is PairingPollResult.Failed -> stage = MobileStage.Message(result.description ?: result.code)
                is PairingPollResult.Pending -> Unit
            }
        }
        MobileStage.Ready -> {
            val signedIn = client
            if (signedIn != null) {
                MobileShell(
                    application = application,
                    client = signedIn,
                    initialDetail = initialDetail,
                    play = play,
                    onChangeServer = { stage = MobileStage.Server },
                    onSignedOut = { note -> stage = if (note == null) MobileStage.Server else MobileStage.Message(note) },
                )
            } else {
                Text("This phone is not connected to a server.", modifier = Modifier.padding(24.dp))
            }
        }
        is MobileStage.Message -> Column(Modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.Center) {
            Text(current.text)
            Spacer(Modifier.height(16.dp))
            Button(onClick = { stage = MobileStage.Server }) { Text("Try another server") }
        }
    }
}

@Composable
private fun ServerEntry(savedAddress: String, savedPhotos: Boolean, connect: (String, Boolean) -> Unit) {
    var address by remember { mutableStateOf(savedAddress) }
    var photos by remember { mutableStateOf(savedPhotos) }
    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp),
        verticalArrangement = Arrangement.Center,
    ) {
        Text("Library", style = MaterialTheme.typography.displaySmall)
        Spacer(Modifier.height(20.dp))
        OutlinedTextField(address, { address = it }, label = { Text("Dashboard address") }, modifier = Modifier.fillMaxWidth())
        Spacer(Modifier.height(16.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            Checkbox(checked = photos, onCheckedChange = { photos = it })
            Column(Modifier.padding(start = 8.dp)) {
                Text("Allow access to my photos and videos")
                Text(
                    "Optional. Browse your own photos and videos, view and download originals, and your galleries, " +
                        "including galleries shared with you. You also approve this when you pair.",
                    style = MaterialTheme.typography.bodySmall,
                )
            }
        }
        Spacer(Modifier.height(16.dp))
        Button(onClick = { connect(address, photos) }) { Text("Connect") }
    }
}

@Composable
private fun MobilePairing(client: TuvimaClient, session: PairingSession, result: (PairingPollResult) -> Unit) {
    LaunchedEffect(session.deviceCode) {
        var interval = session.pollingIntervalSeconds
        repeat(session.expiresInSeconds / interval) {
            delay(interval * 1000L)
            when (val next = client.pollPairing(session)) {
                is PairingPollResult.Pending -> interval = next.retryAfterSeconds
                else -> { result(next); return@LaunchedEffect }
            }
        }
        result(PairingPollResult.Failed("expired_token", "The pairing code expired."))
    }
    Column(Modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.Center) {
        Text("Pair this device", style = MaterialTheme.typography.displaySmall)
        Text(session.verificationUri)
        Text(session.userCode, style = MaterialTheme.typography.displayMedium)
        Text("Waiting for approval…")
    }
}

private const val MOBILE_SCOPES =
    "library.read artwork.read progress.read progress.write queue.read queue.write playback.read playback.write downloads.read downloads.write"
