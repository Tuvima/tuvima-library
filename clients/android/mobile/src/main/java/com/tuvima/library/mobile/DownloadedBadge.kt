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
import com.tuvima.library.core.playableAssetId

/**
 * The files of the person using the phone that are on it now and play without a connection. The shell provides it once, so a poster
 * or a Details page can show the Downloaded mark without reading the downloads itself. Empty until the shell provides it.
 */
internal val LocalDownloadedAssets = compositionLocalOf<Set<String>> { emptySet() }

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

/** The mark on a poster: drawn only when the card is one file (a movie, an episode, an audiobook, a song) that is on the phone. */
@Composable
internal fun CardDownloadedBadge(card: DisplayCard, modifier: Modifier = Modifier) {
    val held = LocalDownloadedAssets.current
    if (held.isEmpty()) return
    if (DownloadBadgeLogic.showsBadge(mobileCardDetailTarget(card).first, card.playableAssetId(), held)) DownloadedBadge(modifier)
}
