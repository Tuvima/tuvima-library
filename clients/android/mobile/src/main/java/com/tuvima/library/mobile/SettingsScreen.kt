package com.tuvima.library.mobile

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.DeviceProfile
import com.tuvima.library.core.SwitchProfileResult
import com.tuvima.library.core.TuvimaClient
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

private val qualityChoices = listOf(
    "mobile-standard" to "Standard (720p)",
    "mobile-small" to "Small (540p)",
)

/**
 * Settings: the server this phone is paired with, which person it is using (with a PIN prompt when that person
 * has one), the default download quality, the photo-access state, and sign out.
 */
@Composable
internal fun SettingsScreen(
    application: TuvimaMobileApplication,
    client: TuvimaClient,
    onProfileSwitched: (String) -> Unit,
    onChangeServer: () -> Unit,
    onSignedOut: (String?) -> Unit,
) {
    val scope = rememberCoroutineScope()
    var activeName by remember { mutableStateOf<String?>(null) }
    var quality by remember { mutableStateOf(application.downloadQuality()) }
    var showProfiles by remember { mutableStateOf(false) }
    var profiles by remember { mutableStateOf<Load<List<DeviceProfile>>>(Load.Loading) }
    var pending by remember { mutableStateOf<DeviceProfile?>(null) }
    var pin by remember { mutableStateOf("") }
    var message by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    var confirmSignOut by remember { mutableStateOf(false) }

    LaunchedEffect(client) {
        activeName = runCatching { client.deviceProfiles() }.getOrNull()
            ?.firstOrNull { it.isActive }?.displayName
    }

    fun openProfiles() {
        showProfiles = true
        pending = null
        pin = ""
        message = null
        profiles = Load.Loading
        scope.launch {
            profiles = try {
                Load.Ready(client.deviceProfiles())
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                Load.Failed(userMessage(failure))
            }
        }
    }

    fun switchTo(profile: DeviceProfile, pinValue: String?) {
        busy = true
        message = null
        scope.launch {
            val result: SwitchProfileResult? = try {
                client.switchProfile(profile.profileId, pinValue)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                null
            }
            busy = false
            when (result) {
                is SwitchProfileResult.Switched -> {
                    showProfiles = false
                    pending = null
                    pin = ""
                    activeName = profile.displayName
                    onProfileSwitched(profile.displayName)
                }
                is SwitchProfileResult.PinRequired -> {
                    pending = profile
                    message = if (pinValue.isNullOrBlank()) {
                        "Enter the PIN for ${profile.displayName}."
                    } else {
                        "That PIN is not right. Try again."
                    }
                }
                is SwitchProfileResult.NotAllowed ->
                    message = "This device cannot switch to ${profile.displayName}."
                is SwitchProfileResult.AlreadyActive ->
                    message = "${profile.displayName} is already in use on this device."
                is SwitchProfileResult.Locked ->
                    message = "Too many tries. Wait a little and try again."
                is SwitchProfileResult.Failed ->
                    message = result.message ?: "The profile could not be switched (error ${result.statusCode})."
                null ->
                    message = "The profile could not be switched. Check your connection and try again."
            }
        }
    }

    fun signOut() {
        busy = true
        scope.launch {
            // Remove the device on the server first; if that cannot be reached, still sign out on this phone.
            val revoked = try {
                client.revokeCurrentDevice()
                true
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                false
            }
            client.signOut()
            busy = false
            onSignedOut(
                if (revoked) null
                else "Signed out on this phone. The server could not be reached, so remove this device under Devices in the Dashboard.",
            )
        }
    }

    Column(
        Modifier.verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("Settings", style = MaterialTheme.typography.headlineMedium)

        Text("Server", style = MaterialTheme.typography.titleMedium)
        Text(client.serverOrigin)
        OutlinedButton(onClick = onChangeServer) { Text("Change server") }

        Spacer(Modifier.height(8.dp))
        Text("Profile", style = MaterialTheme.typography.titleMedium)
        Text(activeName?.let { "Using $it" } ?: "Using the profile this phone was paired with")
        OutlinedButton(onClick = { openProfiles() }, enabled = !busy) { Text("Switch profile") }

        Spacer(Modifier.height(8.dp))
        Text("Photos and videos", style = MaterialTheme.typography.titleMedium)
        Text(
            if (client.hasPhotoAccess()) {
                "Allowed on this phone."
            } else {
                "Not allowed on this phone. To enable it, sign out, connect again and tick the photos and videos option."
            },
        )

        Spacer(Modifier.height(8.dp))
        Text("Default download quality", style = MaterialTheme.typography.titleMedium)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            qualityChoices.forEach { (key, label) ->
                FilterChip(
                    selected = quality == key,
                    onClick = {
                        quality = key
                        application.setDownloadQuality(key)
                    },
                    label = { Text(label) },
                )
            }
        }

        Spacer(Modifier.height(8.dp))
        Button(onClick = { confirmSignOut = true }, enabled = !busy) { Text("Sign out") }
    }

    if (showProfiles) {
        AlertDialog(
            onDismissRequest = { if (!busy) showProfiles = false },
            title = { Text(if (pending != null) "Enter PIN" else "Switch profile") },
            text = {
                val target = pending
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    message?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                    if (target != null) {
                        OutlinedTextField(
                            value = pin,
                            onValueChange = { pin = it },
                            label = { Text("PIN for ${target.displayName}") },
                            singleLine = true,
                            visualTransformation = PasswordVisualTransformation(),
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                        )
                    } else {
                        when (val current = profiles) {
                            is Load.Loading -> Text("Loading profiles…")
                            is Load.Failed -> Text(current.message)
                            is Load.Ready -> current.value.forEach { profile ->
                                Row(
                                    Modifier.fillMaxWidth().clickable(enabled = !busy) {
                                        when {
                                            profile.isActive -> message = "${profile.displayName} is already in use on this device."
                                            profile.requiresPin -> {
                                                pending = profile
                                                pin = ""
                                                message = null
                                            }
                                            else -> switchTo(profile, null)
                                        }
                                    }.padding(vertical = 10.dp),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                ) {
                                    Text(profile.displayName)
                                    Text(
                                        listOfNotNull(
                                            if (profile.isActive) "In use" else null,
                                            if (profile.isKids) "Kids" else null,
                                            if (profile.requiresPin) "PIN" else null,
                                        ).joinToString(" · "),
                                        style = MaterialTheme.typography.bodySmall,
                                    )
                                }
                            }
                        }
                    }
                }
            },
            confirmButton = {
                val target = pending
                if (target != null) {
                    Button(onClick = { switchTo(target, pin) }, enabled = !busy && pin.isNotBlank()) { Text("Switch") }
                }
            },
            dismissButton = {
                TextButton(onClick = {
                    if (pending != null) {
                        pending = null
                        pin = ""
                        message = null
                    } else {
                        showProfiles = false
                    }
                }) { Text(if (pending != null) "Back" else "Close") }
            },
        )
    }

    if (confirmSignOut) {
        AlertDialog(
            onDismissRequest = { confirmSignOut = false },
            title = { Text("Sign out?") },
            text = { Text("This removes this phone from your account. You will need to pair it again to use it.") },
            confirmButton = {
                Button(onClick = {
                    confirmSignOut = false
                    signOut()
                }) { Text("Sign out") }
            },
            dismissButton = { TextButton(onClick = { confirmSignOut = false }) { Text("Cancel") } },
        )
    }
}
