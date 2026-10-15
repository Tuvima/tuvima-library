package com.tuvima.library.mobile

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.LruCache
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.TuvimaClient
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit

/** Decoded artwork kept for the life of the app so scrolling back does not download it again. */
internal object BitmapCache {
    private val cache = LruCache<String, Bitmap>(160)

    fun get(key: String): Bitmap? = cache.get(key)

    fun put(key: String, value: Bitmap) {
        cache.put(key, value)
    }
}

// The Engine limits how many image requests run at once, so the app keeps its own downloads under that.
private val imageGate = Semaphore(6)

/**
 * Shows an image fetched with the device's own credentials. [path] may be relative to the server; null shows the
 * placeholder only. There is no separate image library: this uses the same authenticated download as the TV app.
 */
@Composable
internal fun RemoteImage(
    client: TuvimaClient,
    path: String?,
    modifier: Modifier = Modifier,
    contentScale: ContentScale = ContentScale.Crop,
) {
    val bitmap by produceState<Bitmap?>(initialValue = path?.let { BitmapCache.get(it) }, key1 = path) {
        value = path?.let { key ->
            BitmapCache.get(key) ?: imageGate.withPermit {
                runCatching {
                    val bytes = client.authenticatedBytes(key)
                    BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                }.getOrNull()
            }?.also { BitmapCache.put(key, it) }
        }
    }
    val image = bitmap
    Box(modifier.background(MaterialTheme.colorScheme.surfaceVariant)) {
        if (image != null) {
            Image(
                bitmap = image.asImageBitmap(),
                contentDescription = null,
                modifier = Modifier.fillMaxSize(),
                contentScale = contentScale,
            )
        }
    }
}

/** A portrait poster with its title, and a progress line when the work has been started. */
@Composable
internal fun PosterTile(
    client: TuvimaClient,
    card: DisplayCard,
    modifier: Modifier = Modifier,
    onClick: () -> Unit,
) {
    Column(modifier.clickable(onClick = onClick)) {
        Box(Modifier.fillMaxWidth().aspectRatio(2f / 3f).clip(RoundedCornerShape(8.dp))) {
            RemoteImage(client, card.posterUrl ?: card.artworkUrl, Modifier.fillMaxSize())
            val percent = card.progressPercent
            if (percent != null && percent > 0.0) {
                LinearProgressIndicator(
                    progress = { (percent / 100.0).toFloat().coerceIn(0f, 1f) },
                    modifier = Modifier.fillMaxWidth().height(3.dp).align(Alignment.BottomStart),
                )
            }
        }
        Text(
            card.title,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.padding(top = 4.dp),
        )
        card.subtitle?.let {
            Text(it, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall)
        }
    }
}
