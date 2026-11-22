package com.tuvima.library.core

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Drives the window the way a remote does, with a hand-written API and a fake clock (nothing waits). */
class FindAnotherModelTest {
    private class ManualScheduler : DwellScheduler {
        private class Entry(val dueAt: Long, val task: () -> Unit) { var done = false }

        private val entries = mutableListOf<Entry>()
        private var now = 0L

        override fun schedule(delayMs: Long, task: () -> Unit): DwellCancel {
            val entry = Entry(now + delayMs, task)
            entries.add(entry)
            return DwellCancel { entry.done = true }
        }

        fun advance(ms: Long) {
            now += ms
            entries.filter { !it.done && it.dueAt <= now }.forEach {
                it.done = true
                it.task()
            }
        }
    }

    private class SpyApi : FindAnotherApi {
        var listAnswer = CompletableDeferred<TextTrackAlternatives>()
        val previewAnswers = mutableMapOf<String, CompletableDeferred<TextTrackAlternativePreview>>()
        var chooseAnswer = CompletableDeferred<TextTrackChoiceResult>()
        val listed = mutableListOf<String?>()
        val previewed = mutableListOf<String>()
        val chosen = mutableListOf<Pair<String, String?>>()

        override suspend fun alternatives(language: String?): TextTrackAlternatives {
            listed.add(language)
            return listAnswer.await()
        }

        override suspend fun preview(item: TextTrackAlternative, language: String?): TextTrackAlternativePreview {
            previewed.add(item.key)
            return previewAnswers.getOrPut(item.key) { CompletableDeferred() }.await()
        }

        override suspend fun choose(item: TextTrackAlternative, language: String?): TextTrackChoiceResult {
            chosen.add(item.key to language)
            return chooseAnswer.await()
        }
    }

    private fun item(id: String, lines: List<TextTrackPreviewLine> = emptyList(), current: Boolean = false) = TextTrackAlternative(
        provider = "p", providerName = "P", sourceId = id, language = "en", isTimed = true, confidence = 0.95,
        durationMatchScore = null, isHearingImpaired = false, releaseName = "Release $id", isCurrent = current, title = null,
        artist = null, album = null, lengthSeconds = null, itemLengthSeconds = null, previewLines = lines, endsAtSeconds = null,
        lengthCheck = null, lengthDifferenceSeconds = null,
    )

    private val words = listOf(TextTrackPreviewLine(5.0, "Hello"))
    private fun found(vararg items: TextTrackAlternative) =
        TextTrackAlternatives("subtitles", "Found", "", items.toList(), "SubDL", "English")

    private val api = SpyApi()
    private val scheduler = ManualScheduler()
    private var applied: TextTrackChoiceResult? = null
    private var closed = 0
    private val model = FindAnotherModel(
        api = api,
        scope = CoroutineScope(Dispatchers.Unconfined),
        scheduler = scheduler,
        kind = TextTrackKind.Subtitles,
        initialLanguage = "English",
        languageChoices = listOf("English", "French", "Japanese"),
        itemNoun = "film",
        onChange = {},
        onApplied = { applied = it },
        onClosed = { closed++ },
    )

    private fun openWith(vararg items: TextTrackAlternative) {
        model.start()
        api.listAnswer.complete(found(*items))
    }

    private fun press(vararg keys: FindAnotherKey) = keys.forEach { model.press(it) }

    @Test
    fun theWindowShowsLoadingUntilTheListArrivesThenAllRowsTogether() {
        model.start()
        assertEquals(FindAnotherPhase.Loading, model.state.phase)
        assertEquals(listOf<String?>("English"), api.listed)
        api.listAnswer.complete(found(item("1", words), item("2"), item("3")))
        assertEquals(FindAnotherPhase.Rows, model.state.phase)
        assertEquals(3, model.state.items.size)
        assertEquals(FindAnotherZone.List, model.state.keys.zone)
    }

    @Test
    fun theBestMatchIsReadAtOnceWhenItHasNoWords() {
        openWith(item("1"), item("2"))
        assertEquals(listOf("p:1"), api.previewed)
        assertTrue("p:1" in model.state.reading)
        api.previewAnswers.getValue("p:1").complete(TextTrackAlternativePreview("Found", item("1", words)))
        assertTrue(model.state.items[0].hasWords)
        assertTrue(model.state.reading.isEmpty())
    }

    @Test
    fun aBestMatchThatArrivedWithItsWordsIsNotReadAgain() {
        openWith(item("1", words), item("2"))
        assertTrue(api.previewed.isEmpty())
    }

    @Test
    fun scrollingQuicklyReadsNothingAndRestingForOneSecondReadsTheRow() {
        openWith(item("1", words), item("2"), item("3"), item("4"))
        press(FindAnotherKey.Down)
        scheduler.advance(300)
        press(FindAnotherKey.Down)
        scheduler.advance(300)
        press(FindAnotherKey.Down)
        scheduler.advance(900)
        assertTrue(api.previewed.isEmpty())
        scheduler.advance(100)
        assertEquals(listOf("p:4"), api.previewed)
        assertEquals("Reading…", FindAnotherWording.paneNote(reading = true, failed = false))
    }

    @Test
    fun aReadThatFailsMarksTheRowAndIsNotRetriedWhenFocusReturns() {
        openWith(item("1", words), item("2"))
        press(FindAnotherKey.Down)
        scheduler.advance(1_000)
        api.previewAnswers.getValue("p:2").complete(TextTrackAlternativePreview("LimitReached", null))
        assertTrue("p:2" in model.state.failed)
        press(FindAnotherKey.Up, FindAnotherKey.Down)
        scheduler.advance(5_000)
        assertEquals(listOf("p:2"), api.previewed)
        // The row can still be used.
        press(FindAnotherKey.Select, FindAnotherKey.Select)
        assertEquals(listOf("p:2" to "English"), api.chosen)
    }

    @Test
    fun selectOnAnUnreadRowReadsItAtOnceAndMovesFocusToUseThis() {
        openWith(item("1", words), item("2"))
        press(FindAnotherKey.Down, FindAnotherKey.Select)
        assertEquals(listOf("p:2"), api.previewed)
        assertEquals(FindAnotherZone.UseThis, model.state.keys.zone)
        assertTrue(model.state.keys.fullWords)
    }

    @Test
    fun movingToThePaneCallsOffTheWaitingRead() {
        openWith(item("1", words), item("2"))
        press(FindAnotherKey.Down)
        scheduler.advance(500)
        press(FindAnotherKey.Right)
        scheduler.advance(5_000)
        assertTrue(api.previewed.isEmpty())
    }

    @Test
    fun useThisAppliesTheFocusedRowInTheSearchedLanguageAndReportsTheChoice() {
        openWith(item("1", words), item("2", words))
        press(FindAnotherKey.Down, FindAnotherKey.Select, FindAnotherKey.Select)
        assertTrue(model.state.applying)
        assertEquals(listOf("p:2" to "English"), api.chosen)
        api.chooseAnswer.complete(TextTrackChoiceResult("Updated", "Now using it.", "t1"))
        assertEquals("t1", applied?.trackId)
    }

    @Test
    fun keysAreIgnoredWhileAChoiceIsBeingApplied() {
        openWith(item("1", words))
        press(FindAnotherKey.Select, FindAnotherKey.Select)
        press(FindAnotherKey.Back, FindAnotherKey.Down)
        assertEquals(0, closed)
        assertEquals(1, api.chosen.size)
    }

    @Test
    fun aRefusedChoiceKeepsTheWindowOpenWithASentence() {
        openWith(item("1", words))
        press(FindAnotherKey.Select, FindAnotherKey.Select)
        api.chooseAnswer.complete(TextTrackChoiceResult("Failed", "", null))
        assertFalse(model.state.applying)
        assertEquals("Could not download this one", model.state.applyProblem)
        assertNull(applied)
    }

    @Test
    fun backClosesTheFullWordsThenTheWindow() {
        openWith(item("1", words))
        press(FindAnotherKey.Select)
        press(FindAnotherKey.Back)
        assertEquals(0, closed)
        assertFalse(model.state.keys.fullWords)
        press(FindAnotherKey.Back)
        assertEquals(1, closed)
    }

    @Test
    fun anEmptyAnswerShowsTheEnginesSentenceWithCancelFocused() {
        model.start()
        api.listAnswer.complete(TextTrackAlternatives("subtitles", "NoSourceOn", "No subtitle source is switched on.", emptyList(), null, null))
        assertEquals(FindAnotherPhase.Problem, model.state.phase)
        assertEquals("No subtitle source is switched on.", model.state.problem)
        assertEquals(FindAnotherZone.Cancel, model.state.keys.zone)
        // A switch that is off is not fixed by another language: no Language button to reach.
        press(FindAnotherKey.Up)
        assertEquals(FindAnotherZone.Cancel, model.state.keys.zone)
        press(FindAnotherKey.Select)
        assertEquals(1, closed)
    }

    @Test
    fun noneFoundStillOffersTheLanguageButton() {
        model.start()
        api.listAnswer.complete(TextTrackAlternatives("subtitles", "NoneFound", "", emptyList(), null, "English"))
        assertEquals("No English subtitles were found for this film.", model.state.problem)
        press(FindAnotherKey.Up)
        assertEquals(FindAnotherZone.Language, model.state.keys.zone)
    }

    @Test
    fun aSearchThatFailsOnTheNetworkSaysSoInPlainWords() {
        val failing = object : FindAnotherApi {
            override suspend fun alternatives(language: String?): TextTrackAlternatives = throw java.io.IOException("offline")
            override suspend fun preview(item: TextTrackAlternative, language: String?) = error("not used")
            override suspend fun choose(item: TextTrackAlternative, language: String?) = error("not used")
        }
        val offline = FindAnotherModel(
            failing, CoroutineScope(Dispatchers.Unconfined), scheduler, TextTrackKind.Subtitles, "English",
            listOf("English"), "film", {}, {}, {},
        )
        offline.start()
        assertEquals("The search could not be completed.", offline.state.problem)
    }

    @Test
    fun choosingALanguageSearchesAgainAndStartsTheWindowOver() {
        openWith(item("1", words), item("2"))
        press(FindAnotherKey.Up, FindAnotherKey.Select)
        assertTrue(model.state.keys.languageListOpen)
        api.listAnswer = CompletableDeferred()
        press(FindAnotherKey.Down, FindAnotherKey.Select)
        assertEquals("French", model.state.language)
        assertEquals(listOf<String?>("English", "French"), api.listed)
        assertEquals(FindAnotherPhase.Loading, model.state.phase)
        assertTrue(model.state.items.isEmpty())
    }

    @Test
    fun anAnswerForAnEarlierLanguageIsIgnored() {
        model.start()
        val first = api.listAnswer
        api.listAnswer = CompletableDeferred()
        // Searching again before the first answer comes (Language is reachable only after rows, so drive it directly).
        openWith(item("9", words))
        first.complete(found(item("1", words)))
        assertEquals(listOf("p:9"), model.state.items.map { it.key })
    }

    @Test
    fun closingTheModelIgnoresAnswersStillOnTheirWay() {
        model.start()
        model.close()
        api.listAnswer.complete(found(item("1", words)))
        assertEquals(FindAnotherPhase.Loading, model.state.phase)
    }

    @Test
    fun theInUseChipSurvivesReadingTheRow() {
        openWith(item("1", current = true), item("2"))
        api.previewAnswers.getValue("p:1").complete(TextTrackAlternativePreview("Found", item("1", words, current = false)))
        assertTrue(model.state.items[0].isCurrent)
    }
}
