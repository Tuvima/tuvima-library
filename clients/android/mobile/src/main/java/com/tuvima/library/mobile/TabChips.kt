package com.tuvima.library.mobile

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp

/**
 * A section's tabs as chips that wrap onto a second line instead of scrolling sideways, so nothing at phone width needs a sideways
 * swipe. [perRow] chips share a line; each chip is a 44 dp target.
 */
@Composable
internal fun <T> TabChipRows(
    items: List<T>,
    selected: T,
    label: (T) -> String,
    onPick: (T) -> Unit,
    perRow: Int = 3,
) {
    Column(
        Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        items.chunked(perRow.coerceAtLeast(1)).forEach { line ->
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                line.forEach { item ->
                    FilterChip(
                        selected = item == selected,
                        onClick = { onPick(item) },
                        label = { Text(label(item)) },
                        modifier = Modifier.heightIn(min = TuvimaTokens.tapTarget),
                    )
                }
            }
        }
    }
}
