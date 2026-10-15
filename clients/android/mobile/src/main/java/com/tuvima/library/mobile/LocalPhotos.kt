package com.tuvima.library.mobile

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import com.tuvima.library.core.DownloadRecord
import com.tuvima.library.core.LocalBookFiles
import com.tuvima.library.core.OfflineKind
import com.tuvima.library.core.ViewItem
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File

/** Reads an image file kept on this phone at about [maxEdge] pixels on its longest side, or null when it cannot be read. */
internal fun decodeLocalImage(path: String, maxEdge: Int): Bitmap? {
    val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    BitmapFactory.decodeFile(path, bounds)
    if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
    var sample = 1
    while (bounds.outWidth / (sample * 2) >= maxEdge && bounds.outHeight / (sample * 2) >= maxEdge) sample *= 2
    return BitmapFactory.decodeFile(path, BitmapFactory.Options().apply { inSampleSize = sample })
}

/**
 * The finished photos kept on this phone as viewer items, newest first, so they can be browsed with no connection. The
 * date shown is the one recorded when the photo was kept (the day it was taken).
 */
internal fun keptPhotoItems(records: List<DownloadRecord>): List<ViewItem> =
    records.filter { it.isPhoto && it.isComplete }
        .sortedWith(compareByDescending<DownloadRecord> { it.subtitle ?: "" }.thenByDescending { it.storedAtMs ?: 0L })
        .map { record ->
            ViewItem(
                id = record.assetId,
                mediaKind = "image",
                title = record.title,
                fileName = null,
                mimeType = "image/jpeg",
                capturedAt = record.subtitle,
                width = null,
                height = null,
                durationSeconds = null,
                byteSize = record.bytesDone,
                favorite = false,
                locationName = null,
                cameraName = null,
                description = null,
            )
        }

/** An image read from a file on this phone, with no network. Shows the placeholder until it is decoded. */
@Composable
internal fun LocalImage(
    path: String,
    maxEdge: Int,
    modifier: Modifier = Modifier,
    contentScale: ContentScale = ContentScale.Crop,
) {
    val key = "file:$path@$maxEdge"
    val bitmap by produceState<Bitmap?>(initialValue = BitmapCache.get(key), key1 = key) {
        value = BitmapCache.get(key) ?: withContext(Dispatchers.IO) { decodeLocalImage(path, maxEdge) }
            ?.also { BitmapCache.put(key, it) }
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

/**
 * The unwired hook for books kept on the phone (see [LocalBookFiles]): the finished local file of a book record, or
 * null. Nothing creates a book record yet, so today it always answers null; a reader will ask it before the network.
 */
internal class OfflineBookFiles(private val store: OfflineStore) : LocalBookFiles {
    override fun localBookFile(assetId: String): String? {
        val record = store.record(assetId) ?: return null
        val path = record.filePath ?: return null
        return if (record.kind == OfflineKind.Book && record.isComplete && File(path).isFile) path else null
    }
}
