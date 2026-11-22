package com.tuvima.library.core

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The phone sheet's touch rules: a tap reads and selects, Use this applies the selected row, nothing waits for a rest. */
class FindAnotherTouchTest {
    private class NeverScheduler : DwellScheduler {
        var scheduled = 0

        override fun schedule(delayMs: Long, task: () -> Unit): DwellCancel {
            scheduled++
            return DwellCancel { }
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
    private val scheduler = NeverScheduler()
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

    @Test
    fun tappingAnUnreadRowReadsItAtOnceWithoutWaitingForARest() {
        openWith(item("1", words), item("2"), item("3"))
        model.tapRow(2)
        assertEquals(listOf("p:3"), api.previewed)
        assertEquals(0, scheduler.scheduled)
        assertEquals(2, model.state.keys.row)
        assertTrue(model.state.keys.fullWords)
        assertTrue("p:3" in model.state.reading)
    }

    @Test
    fun tappingARowThatIsAlreadyReadDoesNotReadItAgain() {
        openWith(item("1", words), item("2"))
        model.tapRow(0)
        model.tapRow(1)
        api.previewAnswers.getValue("p:2").complete(TextTrackAlternativePreview("Found", item("2", words)))
        model.tapRow(1)
        model.tapRow(0)
        assertEquals(listOf("p:2"), api.previewed)
    }

    @Test
    fun theBestMatchIsReadWhenTheSheetOpensAndIsTheSelectedRowUntilAnotherIsTapped() {
        openWith(item("1"), item("2"))
        assertEquals(listOf("p:1"), api.previewed)
        assertEquals(0, model.state.keys.row)
        model.useSelected()
        assertEquals(listOf("p:1" to "English"), api.chosen)
    }

    @Test
    fun useThisAppliesTheTappedRowInTheSearchedLanguage() {
        openWith(item("1", words), item("2", words))
        model.tapRow(1)
        model.useSelected()
        assertTrue(model.state.applying)
        assertEquals(listOf("p:2" to "English"), api.chosen)
        api.chooseAnswer.complete(TextTrackChoiceResult("Updated", "Now using it.", "t9"))
        assertEquals("t9", applied?.trackId)
    }

    @Test
    fun aRowThatCouldNotBeReadCanStillBeUsed() {
        openWith(item("1", words), item("2"))
        model.tapRow(1)
        api.previewAnswers.getValue("p:2").complete(TextTrackAlternativePreview("LimitReached", null))
        assertTrue("p:2" in model.state.failed)
        model.tapRow(1)
        assertEquals(listOf("p:2"), api.previewed)
        model.useSelected()
        assertEquals(listOf("p:2" to "English"), api.chosen)
    }

    @Test
    fun tapsAreIgnoredWhileAChoiceIsBeingApplied() {
        openWith(item("1", words), item("2", words))
        model.useSelected()
        model.tapRow(1)
        model.useSelected()
        model.cancel()
        assertEquals(0, model.state.keys.row)
        assertEquals(1, api.chosen.size)
        assertEquals(0, closed)
    }

    @Test
    fun cancelClosesTheSheet() {
        openWith(item("1", words))
        model.cancel()
        assertEquals(1, closed)
    }

    @Test
    fun aRefusedChoiceKeepsTheSheetOpenWithASentence() {
        openWith(item("1", words))
        model.useSelected()
        api.chooseAnswer.complete(TextTrackChoiceResult("Failed", "", null))
        assertFalse(model.state.applying)
        assertEquals("Could not download this one", model.state.applyProblem)
        assertNull(applied)
    }

    @Test
    fun noRowsMeansNothingToTapOrUse() {
        model.start()
        api.listAnswer.complete(TextTrackAlternatives("subtitles", "NoneFound", "", emptyList(), null, "English"))
        model.tapRow(0)
        model.useSelected()
        assertTrue(api.previewed.isEmpty())
        assertTrue(api.chosen.isEmpty())
    }

    @Test
    fun aTapOutsideTheListIsIgnored() {
        openWith(item("1", words))
        model.tapRow(5)
        model.tapRow(-1)
        assertEquals(0, model.state.keys.row)
        assertTrue(api.previewed.isEmpty())
    }

    @Test
    fun choosingALanguageSearchesAgainAndStartsOver() {
        openWith(item("1", words), item("2"))
        api.listAnswer = CompletableDeferred()
        model.chooseLanguage(1)
        assertEquals("French", model.state.language)
        assertEquals(listOf<String?>("English", "French"), api.listed)
        assertEquals(FindAnotherPhase.Loading, model.state.phase)
    }

    @Test
    fun theLanguageIsNotOfferedWhenTheSentenceIsAboutASwitchOrAKey() {
        model.start()
        api.listAnswer.complete(TextTrackAlternatives("subtitles", "NoSourceOn", "No subtitle source is switched on.", emptyList(), null, null))
        assertFalse(model.state.keys.languageAvailable)
        model.chooseLanguage(1)
        assertEquals(listOf<String?>("English"), api.listed)
    }

    @Test
    fun theEntryShowsOnlyWhenASourceIsReadyAndThePhoneIsNotPlayingADownloadedCopy() {
        assertTrue(FindAnotherSheet.entryVisible(TextTrackSources("Ready"), playingDownloadedCopy = false))
        assertFalse(FindAnotherSheet.entryVisible(TextTrackSources("NoSourceOn"), playingDownloadedCopy = false))
        assertFalse(FindAnotherSheet.entryVisible(TextTrackSources.Unavailable, playingDownloadedCopy = false))
        assertFalse(FindAnotherSheet.entryVisible(TextTrackSources("Ready"), playingDownloadedCopy = true))
    }

    @Test
    fun anUnreadRowShowsThePromptOnlyWhenItIsTheSelectedRow() {
        val unread = item("2")
        assertNull(FindAnotherSheet.rowPane(unread, selected = false, fullWords = false, reading = false, failed = false))
        assertTrue(FindAnotherSheet.rowPane(unread, selected = true, fullWords = true, reading = false, failed = false) is FindAnotherPane.Prompt)
    }

    @Test
    fun readingAndFailedShowOnTheirOwnRowWhetherOrNotItIsSelected() {
        val unread = item("2")
        assertEquals(FindAnotherPane.Reading, FindAnotherSheet.rowPane(unread, false, false, reading = true, failed = false))
        assertEquals(FindAnotherPane.CouldNotRead, FindAnotherSheet.rowPane(unread, false, false, reading = false, failed = true))
    }

    @Test
    fun theTappedRowShowsItsWordsInFullAndOtherRowsShowThePreview() {
        val long = item("1", (1..30).map { TextTrackPreviewLine(it.toDouble(), "Line $it") })
        val full = FindAnotherSheet.rowPane(long, selected = true, fullWords = true, reading = false, failed = false) as FindAnotherPane.Lines
        assertEquals(FindAnotherRows.FULL_WORDS_LINES, full.lines.size)
        val other = FindAnotherSheet.rowPane(long, selected = false, fullWords = true, reading = false, failed = false) as FindAnotherPane.Lines
        assertEquals(30, other.lines.size)
    }
}
