package com.tuvima.library.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

enum class FindAnotherPhase { Loading, Rows, Problem }

/** Everything the Find another window draws, as one immutable snapshot. */
data class FindAnotherState(
    val kind: TextTrackKind,
    val phase: FindAnotherPhase,
    /** The language being searched (the Language button shows it). */
    val language: String,
    val alternatives: TextTrackAlternatives?,
    /** The rows' results, in the Engine's order; a row read on request gains its words and timing check in place. */
    val items: List<TextTrackAlternative>,
    /** The centered sentence for an answer with no rows. */
    val problem: String?,
    /** Rows being read right now, and rows whose read failed (the row can still be used). */
    val reading: Set<String>,
    val failed: Set<String>,
    val keys: FindAnotherKeyState,
    /** True from Use this until the host closes the window. */
    val applying: Boolean,
    /** A sentence under the list when Use this did not work. */
    val applyProblem: String?,
    /** The pane scrolls by [paneScrollLines] each time [paneScrollTick] changes. */
    val paneScrollLines: Int = 0,
    val paneScrollTick: Int = 0,
) {
    /** The focused row's result, when there is one. */
    val focusedItem: TextTrackAlternative? get() = items.getOrNull(keys.row)
}

/**
 * The Find another window without a screen: asks the Engine for the list, reads rows by the one-second rest rule, moves
 * focus by the remote's key map and uses one result. A screen draws each [FindAnotherState] it is given and forwards the
 * remote's keys to [press]. All calls and callbacks run on [scope]'s own thread (the screen's main thread).
 *
 * @param itemNoun "film", "episode" or "song", for the plain sentences.
 * @param onApplied the Engine accepted the choice; the host closes the window and the panel and puts the new track on.
 * @param onClosed Back or Cancel: the host closes the window and returns to the panel.
 */
class FindAnotherModel(
    private val api: FindAnotherApi,
    private val scope: CoroutineScope,
    scheduler: DwellScheduler,
    private val kind: TextTrackKind,
    initialLanguage: String,
    private val languageChoices: List<String>,
    private val itemNoun: String,
    private val onChange: (FindAnotherState) -> Unit,
    private val onApplied: (TextTrackChoiceResult) -> Unit,
    private val onClosed: () -> Unit,
) {
    private val dwell = FindAnotherDwell(scheduler, this::startRead)
    private var generation = 0

    var state: FindAnotherState = FindAnotherState(
        kind = kind,
        phase = FindAnotherPhase.Loading,
        language = initialLanguage,
        alternatives = null,
        items = emptyList(),
        problem = null,
        reading = emptySet(),
        failed = emptySet(),
        keys = FindAnotherKeys.initial(0, languageChoices.size),
        applying = false,
        applyProblem = null,
    )
        private set

    /** Starts the first search, in the profile's language. */
    fun start() = search(state.language)

    /** Calls off every wait and ignores any answer still on its way (the window is going away). */
    fun close() {
        generation++
        dwell.reset()
    }

    /** One key from the remote. Keys are ignored while a choice is being applied. */
    fun press(key: FindAnotherKey) {
        if (state.applying) return
        val before = state.keys
        val result = FindAnotherKeys.reduce(before, key)
        val after = result.state
        var next = state.copy(keys = after, applyProblem = null)
        when (val effect = result.effect) {
            is FindAnotherEffect.ScrollPane -> next = next.copy(paneScrollLines = effect.lines, paneScrollTick = next.paneScrollTick + 1)
            else -> Unit
        }
        publish(next)

        // The reading rule: a row's wait lives only while focus rests on it in the list.
        val leftList = before.zone == FindAnotherZone.List && after.zone != FindAnotherZone.List
        val enteredList = before.zone != FindAnotherZone.List && after.zone == FindAnotherZone.List
        if (leftList) dwell.focusLeftList()
        if (enteredList) state.items.getOrNull(after.row)?.let { dwell.focusMoved(it.key, it.hasWords) }

        when (val effect = result.effect) {
            is FindAnotherEffect.RowFocused -> state.items.getOrNull(effect.row)?.let { dwell.focusMoved(it.key, it.hasWords) }
            is FindAnotherEffect.OpenFullWords -> state.items.getOrNull(effect.row)?.let { dwell.readNow(it.key, it.hasWords) }
            is FindAnotherEffect.Apply -> state.items.getOrNull(effect.row)?.let { applyChoice(it) }
            is FindAnotherEffect.ChooseLanguage -> languageChoices.getOrNull(effect.index)?.let { search(it) }
            FindAnotherEffect.Close -> onClosed()
            else -> Unit
        }
    }

    private fun publish(next: FindAnotherState) {
        state = next
        onChange(next)
    }

    private fun search(language: String) {
        val mine = ++generation
        dwell.reset()
        publish(
            state.copy(
                phase = FindAnotherPhase.Loading,
                language = language,
                alternatives = null,
                items = emptyList(),
                problem = null,
                reading = emptySet(),
                failed = emptySet(),
                keys = FindAnotherKeys.initial(0, languageChoices.size),
                applyProblem = null,
            ),
        )
        scope.launch {
            val answer = try {
                api.alternatives(language)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // Not swallowed: a failed search is shown as the plain "could not be completed" sentence in the window.
                null
            }
            if (mine != generation) return@launch
            showAnswer(answer, language)
        }
    }

    private fun showAnswer(answer: TextTrackAlternatives?, language: String) {
        val indexOfLanguage = languageChoices.indexOfFirst { it.equals(language, ignoreCase = true) }.coerceAtLeast(0)
        if (answer != null && answer.hasRows) {
            publish(
                state.copy(
                    phase = FindAnotherPhase.Rows,
                    alternatives = answer,
                    items = answer.items,
                    problem = null,
                    keys = FindAnotherKeys.initial(answer.items.size, languageChoices.size).copy(languageIndex = indexOfLanguage),
                ),
            )
            // The best match is read as soon as the list opens.
            val best = answer.items.first()
            dwell.listOpened(best.key, best.hasWords)
            return
        }
        val status = answer?.status ?: "Failed"
        val sentence = if (answer == null) {
            FindAnotherWording.statusSentence(kind, status, null, null, language, itemNoun)
        } else {
            FindAnotherWording.statusSentence(kind, status, answer.message, answer.sourceName, language, itemNoun)
        }
        publish(
            state.copy(
                phase = FindAnotherPhase.Problem,
                alternatives = answer,
                items = emptyList(),
                problem = sentence,
                // The language choice is no use when the sentence is about a switch, a key, an item that cannot be matched or a profile that may not search.
                keys = FindAnotherKeys.initial(0, languageChoices.size, languageAvailable = status !in NO_LANGUAGE_STATUSES)
                    .copy(languageIndex = indexOfLanguage),
            ),
        )
    }

    private fun startRead(key: String) {
        val item = state.items.firstOrNull { it.key == key } ?: return
        val mine = generation
        val language = state.language
        publish(state.copy(reading = state.reading + key))
        scope.launch {
            val read = try {
                api.preview(item, language)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // Recorded below as a failed read: the pane says "Could not read this one" and the row can still be used.
                null
            }
            if (mine != generation) return@launch
            val words = read?.takeIf { it.isRead }?.item
            publish(
                if (words != null) {
                    state.copy(
                        items = FindAnotherRows.withRead(state.items, words.copy(isCurrent = words.isCurrent || item.isCurrent)),
                        reading = state.reading - key,
                    )
                } else {
                    state.copy(reading = state.reading - key, failed = state.failed + key)
                },
            )
        }
    }

    private fun applyChoice(item: TextTrackAlternative) {
        val mine = generation
        val language = state.language
        publish(state.copy(applying = true, applyProblem = null))
        scope.launch {
            val result = try {
                api.choose(item, language)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // Shown as a sentence under the list below; the window stays open.
                null
            }
            if (mine != generation) return@launch
            if (result != null && result.isUpdated) {
                onApplied(result)
            } else {
                publish(state.copy(applying = false, applyProblem = result?.message?.takeIf { it.isNotBlank() } ?: COULD_NOT_USE))
            }
        }
    }

    companion object {
        const val COULD_NOT_USE = "Could not download this one"

        // Statuses whose sentence is about something a different language cannot fix.
        private val NO_LANGUAGE_STATUSES = setOf(
            "NoSourceOn", "SourceKeyRequired", "Blocked", "NotAllowed", "IdentityUnverified", "Unsupported", "AssetMissing",
        )
    }
}
