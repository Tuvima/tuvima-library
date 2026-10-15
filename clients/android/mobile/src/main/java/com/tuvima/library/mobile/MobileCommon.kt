package com.tuvima.library.mobile

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.PHOTO_ACCESS_DENIED_MESSAGE
import com.tuvima.library.core.PhotoAccessDeniedException
import com.tuvima.library.core.TuvimaHttpException
import java.util.Locale

/** What a screen is showing while it reads from the server. */
internal sealed interface Load<out T> {
    data object Loading : Load<Nothing>
    data class Failed(val message: String) : Load<Nothing>
    data class Ready<T>(val value: T) : Load<T>
}

/** Plain words for a failed call. A photo refusal gets the agreed re-pair message. */
internal fun userMessage(error: Throwable): String = when {
    error is PhotoAccessDeniedException -> PHOTO_ACCESS_DENIED_MESSAGE
    error is TuvimaHttpException && error.statusCode == 404 -> "That item is not available."
    else -> error.message ?: "Something went wrong."
}

/** Shows a spinner, an error with a retry button, or the loaded content. */
@Composable
internal fun <T> LoadView(state: Load<T>, retry: () -> Unit, content: @Composable (T) -> Unit) {
    when (state) {
        is Load.Loading -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            CircularProgressIndicator()
        }
        is Load.Failed -> ErrorBlock(state.message, retry)
        is Load.Ready -> content(state.value)
    }
}

@Composable
internal fun ErrorBlock(message: String, retry: (() -> Unit)?) {
    Column(
        Modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(message)
        if (retry != null) {
            Spacer(Modifier.height(16.dp))
            Button(onClick = retry) { Text("Try again") }
        }
    }
}

/** 75 seconds reads as 1:15 and 3,700 seconds as 1:01:40; empty when the length is unknown. */
internal fun formatDuration(seconds: Double?): String {
    if (seconds == null) return ""
    val total = seconds.toInt().coerceAtLeast(0)
    val hours = total / 3600
    val minutes = (total % 3600) / 60
    val rest = total % 60
    return if (hours > 0) {
        "%d:%02d:%02d".format(Locale.ROOT, hours, minutes, rest)
    } else {
        "%d:%02d".format(Locale.ROOT, minutes, rest)
    }
}
