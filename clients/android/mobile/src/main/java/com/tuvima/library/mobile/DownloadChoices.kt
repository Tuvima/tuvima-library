package com.tuvima.library.mobile

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.BatchScope
import com.tuvima.library.core.DetailChildGroup
import com.tuvima.library.core.DownloadKind
import com.tuvima.library.core.OfflineBatchLogic
import com.tuvima.library.core.OfflineLogic

/**
 * The "Download all" choice on a Details page: everything the page lists (a whole show, album, playlist or audiobook), or one season
 * or group, and, when any of it is video, the quality. It only asks; [onStart] does the queueing and the page says in words what began.
 */
@Composable
internal fun DownloadAllDialog(
    groups: List<DetailChildGroup>,
    preferredQuality: String,
    onStart: (BatchScope, String) -> Unit,
    onDismiss: () -> Unit,
) {
    val scopes = remember(groups) { OfflineBatchLogic.scopes(groups) }
    var selectedKey by remember(scopes) { mutableStateOf(scopes.firstOrNull()?.key) }
    val hasVideo = remember(groups) {
        OfflineBatchLogic.allChildren(groups).any { OfflineLogic.downloadKindFor(it.entityType) == DownloadKind.Video }
    }
    var quality by remember(hasVideo) { mutableStateOf(OfflineLogic.defaultQualityFor(DownloadKind.Video, preferredQuality)) }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Download") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                scopes.forEach { scope ->
                    Row(
                        Modifier.fillMaxWidth().heightIn(min = TuvimaTokens.controlHeight).clickable { selectedKey = scope.key },
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        RadioButton(selected = selectedKey == scope.key, onClick = { selectedKey = scope.key })
                        Text(scope.label)
                    }
                }
                if (hasVideo) {
                    Text("Quality", style = MaterialTheme.typography.titleSmall)
                    OfflineLogic.qualityChoicesFor(DownloadKind.Video).forEach { choice ->
                        Row(
                            Modifier.fillMaxWidth().heightIn(min = TuvimaTokens.controlHeight).clickable { quality = choice.key },
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            RadioButton(selected = quality == choice.key, onClick = { quality = choice.key })
                            Text(choice.label)
                        }
                    }
                }
                Text(
                    "Larger downloads use more of your storage limit and are stopped at it. Copies expire after a while and can be downloaded again.",
                    style = MaterialTheme.typography.bodySmall,
                )
            }
        },
        confirmButton = {
            Button(
                onClick = { scopes.firstOrNull { it.key == selectedKey }?.let { onStart(it, quality) } },
                enabled = selectedKey != null,
                modifier = Modifier.heightIn(min = TuvimaTokens.controlHeight),
            ) { Text("Download") }
        },
        dismissButton = {
            TextButton(onClick = onDismiss, modifier = Modifier.heightIn(min = TuvimaTokens.controlHeight)) { Text("Cancel") }
        },
    )
}
