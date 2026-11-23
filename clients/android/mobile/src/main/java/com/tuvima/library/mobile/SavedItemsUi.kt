package com.tuvima.library.mobile

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Stable
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.MyListLogic
import com.tuvima.library.core.SavedItem
import com.tuvima.library.core.SavedKey
import com.tuvima.library.core.SavedSet
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.savedKeyForEntity
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

/**
 * The active profile's saved titles, shared by every screen that draws a plus or tick. A press shows its result at once and is put back
 * when the Engine refuses ([changeFailed] then says so, in words, where a screen has room for them). [version] goes up after every
 * change so My List and Home's My List row read again.
 */
@Stable
internal class SavedItems(private val client: TuvimaClient, private val scope: CoroutineScope) {
    /** Null until the first read has succeeded; without it no plus or tick is drawn, so nothing is shown that might be wrong. */
    var titles by mutableStateOf<SavedSet?>(null)
        private set
    var changeFailed by mutableStateOf(false)
        private set
    var version by mutableIntStateOf(0)
        private set

    /** Reads the saved list when it is not known yet (a first read that failed is tried again by the next screen that opens). */
    fun ensureLoaded() {
        if (titles == null) load()
    }

    fun load() {
        scope.launch {
            try {
                titles = SavedSet.of(client.savedItems())
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                // Best effort: the plus and tick simply stay hidden until a later read works (the next screen that opens asks again).
            }
        }
    }

    /** The key a card is saved under: from what it is, or, for a group saved on the Dashboard, from the saved list itself. */
    fun keyFor(card: DisplayCard): SavedKey? {
        val target = mobileCardDetailTarget(card)
        return savedKeyForEntity(target.first, target.second)
            ?: titles?.keyForId(card.collectionId ?: card.workId ?: card.id)
    }

    // The last request queued for each title, so a quick second press waits for the first and the Engine sees them in order.
    private val queued = HashMap<SavedKey, Job>()

    /** Saves the title, or takes it off, as a press on its plus or tick means. Presses on one title are sent one after another. */
    fun toggle(key: SavedKey) {
        val current = titles ?: return
        val save = current.pressWouldSave(key)
        titles = if (save) current.with(key) else current.without(key)
        changeFailed = false
        val before = queued[key]
        val job = scope.launch {
            before?.join()
            val accepted = try {
                if (save) client.saveItem(key) else client.unsaveItem(key)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                false
            }
            if (!accepted) {
                val now = titles ?: current
                titles = if (save) now.without(key) else now.with(key)
                changeFailed = true
            }
            version += 1
        }
        queued[key] = job
        job.invokeOnCompletion { if (queued[key] === job) queued.remove(key) }
    }

    /** Tells the store a remove or restore made elsewhere (the My List page) so every tick agrees, and lists read again. */
    fun applied(removed: List<SavedItem>, restored: List<SavedItem>) {
        titles?.let { current ->
            var now = current
            removed.forEach { now = now.without(it.key) }
            restored.forEach { now = now.with(it.key) }
            titles = now
        }
        version += 1
    }

    fun clearFailure() {
        changeFailed = false
    }
}

/** The saved titles for the screens below; null where no plus or tick should be drawn. */
internal val LocalSavedItems = compositionLocalOf<SavedItems?> { null }

/**
 * The plus (not saved) or tick (saved) on a cover, hero or Details page. A bare icon button: no border, a 44 dp target, no toast.
 * Draws nothing until the saved list is known, or when [key] is null (a title My List cannot hold). [onPress] replaces the default
 * save-or-remove, for the My List page, which removes through its own Undo.
 */
@Composable
internal fun SavedToggle(
    key: SavedKey?,
    modifier: Modifier = Modifier,
    onPress: ((SavedKey) -> Unit)? = null,
) {
    val saved = LocalSavedItems.current ?: return
    val titles = saved.titles ?: return
    if (key == null) return
    val isSaved = key in titles
    val label = if (isSaved) MyListLogic.REMOVE else MyListLogic.ADD
    Box(
        modifier
            .size(TuvimaTokens.tapTarget)
            .clip(CircleShape)
            .clickable(onClickLabel = label, role = Role.Button) { if (onPress != null) onPress(key) else saved.toggle(key) }
            .semantics { contentDescription = label },
        contentAlignment = Alignment.Center,
    ) {
        Box(
            Modifier.size(28.dp).clip(CircleShape).background(Color.Black.copy(alpha = 0.55f)),
            contentAlignment = Alignment.Center,
        ) {
            Text(
                if (isSaved) "✓" else "+",
                color = if (isSaved) TuvimaTokens.accentPrimaryHover else TuvimaTokens.textPrimary,
                fontSize = 18.sp,
                fontWeight = FontWeight.Bold,
            )
        }
    }
}

/** The outline bookmark used for My List in the top bar and the dock (the Dashboard's My List icon). */
@Composable
internal fun BookmarkGlyph(color: Color, modifier: Modifier = Modifier) {
    Canvas(modifier.size(22.dp)) {
        val w = size.width
        val h = size.height
        val path = Path().apply {
            moveTo(w * 0.2f, h * 0.08f)
            lineTo(w * 0.8f, h * 0.08f)
            lineTo(w * 0.8f, h * 0.92f)
            lineTo(w * 0.5f, h * 0.7f)
            lineTo(w * 0.2f, h * 0.92f)
            close()
        }
        drawPath(path, color, style = Stroke(width = 2.dp.toPx()))
    }
}

/** Three bars, the menu button (the Dashboard's hamburger). */
@Composable
internal fun HamburgerGlyph(color: Color, modifier: Modifier = Modifier) {
    Canvas(modifier.size(22.dp)) {
        val stroke = 2.dp.toPx()
        listOf(0.22f, 0.5f, 0.78f).forEach { fraction ->
            drawLine(color, Offset(size.width * 0.1f, size.height * fraction), Offset(size.width * 0.9f, size.height * fraction), strokeWidth = stroke)
        }
    }
}
