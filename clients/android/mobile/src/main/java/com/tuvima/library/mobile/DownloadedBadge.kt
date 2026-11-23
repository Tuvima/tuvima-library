package com.tuvima.library.mobile

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.DownloadBadgeLogic
import com.tuvima.library.core.DownloadRecord
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.flow

/**
 * The files of the person using the phone that are on it now and play without a connection. The shell provides it once, so a poster
 * or a Details page can show the Downloaded mark without reading the downloads itself. Empty until the shell provides it.
 */
internal val LocalDownloadedAssets = compositionLocalOf<Set<String>> { emptySet() }

/**
 * The files on the phone that play, as the downloads change and as time passes (a copy expires while the app is open, and its mark
 * goes with it). Emits only when the set itself changes, so a progress tick never redraws the screen.
 */
internal fun heldAssetsFlow(records: Flow<List<DownloadRecord>>): Flow<Set<String>> {
    val minutes = flow {
        while (true) {
            emit(Unit)
            delay(EXPIRY_CHECK_MS)
        }
    }
    return combine(records, minutes) { list, _ -> DownloadBadgeLogic.heldAssetIds(list, System.currentTimeMillis()) }.distinctUntilChanged()
}

private const val EXPIRY_CHECK_MS = 60_000L

/** The small "Downloaded" pill. Dark only: a raised surface with light text, no border. */
@Composable
internal fun DownloadedBadge(modifier: Modifier = Modifier) {
    Box(
        modifier
            .background(TuvimaTokens.bgSurfaceElevated, RoundedCornerShape(TuvimaTokens.radiusXs))
            .padding(horizontal = 6.dp, vertical = 2.dp)
            .semantics { contentDescription = DownloadBadgeLogic.LABEL },
    ) {
        Text("↓ ${DownloadBadgeLogic.LABEL}", color = TuvimaTokens.textPrimary, fontSize = 10.sp, fontWeight = FontWeight.SemiBold)
    }
}

/** The mark on a poster: drawn only when the card is one file (a movie, an episode, an audiobook, a song) that is on the phone; only such a card names a file of its own. */
@Composable
internal fun CardDownloadedBadge(card: DisplayCard, modifier: Modifier = Modifier) {
    val held = LocalDownloadedAssets.current
    if (held.isEmpty()) return
    if (DownloadBadgeLogic.cardShowsBadge(card.assetId, held)) DownloadedBadge(modifier)
}
