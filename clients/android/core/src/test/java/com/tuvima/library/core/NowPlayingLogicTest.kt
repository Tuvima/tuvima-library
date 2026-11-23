package com.tuvima.library.core

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class NowPlayingLogicTest {
    private val chapters = listOf(
        Chapter(0, "Opening", 0.0, 600.0),
        Chapter(1, "", 600.0, 1_500.0),
        Chapter(2, "The Way Beyond", 1_500.0, 2_400.0),
    )

    private val book = AudioNow(
        assetId = "book-1",
        kind = TvMediaKind.Audiobook,
        title = "The Way Beyond",
        author = "Maya Ellis",
        chapters = chapters,
    )

    private val song = AudioNow(
        assetId = "song-1",
        kind = TvMediaKind.Music,
        title = "Stay a Little Longer",
        artist = "Maya Ellis",
        album = "Northern Lights",
        sourceName = "Northern Lights",
    )

    private val contract: String by lazy {
        var dir: File? = File("").absoluteFile
        var text: String? = null
        while (dir != null && text == null) {
            val candidate = File(dir, "clients/contracts/v1/player-controls.md")
            if (candidate.isFile) text = candidate.readText()
            dir = dir.parentFile
        }
        text ?: error("clients/contracts/v1/player-controls.md was not found")
    }

    @Test
    fun theKindComesFromTheMediaTypeAndAnAudiobookIsNeverMusic() {
        assertEquals(TvMediaKind.Audiobook, NowPlayingLogic.kindOf("Audiobooks"))
        assertEquals(TvMediaKind.Audiobook, NowPlayingLogic.kindOf(null, "audiobook"))
        assertEquals(TvMediaKind.Music, NowPlayingLogic.kindOf("Music"))
        assertEquals(TvMediaKind.Music, NowPlayingLogic.kindOf(null))
    }

    @Test
    fun theSecondLineIsArtistAndAlbumForASongAndAuthorAndChapterForABook() {
        assertEquals("Maya Ellis · Northern Lights", NowPlayingLogic.secondLine(song, 10.0))
        assertEquals("Maya Ellis · Opening", NowPlayingLogic.secondLine(book, 10.0))
        // A chapter the file calls nothing is named by its number.
        assertEquals("Maya Ellis · Chapter 2", NowPlayingLogic.secondLine(book, 700.0))
        assertEquals("Maya Ellis · The Way Beyond", NowPlayingLogic.secondLine(book, 1_600.0))
    }

    @Test
    fun theTopLineSaysPlayingFromAndLeavesAnUnnamedSourceOut() {
        assertEquals("Playing from Northern Lights", NowPlayingLogic.playingFrom(" Northern Lights "))
        assertNull(NowPlayingLogic.playingFrom(null))
        assertNull(NowPlayingLogic.playingFrom("  "))
    }

    @Test
    fun previousOnASongRestartsAfterThreeSecondsAndOtherwiseGoesBack() {
        assertEquals(TransportStep.SeekTo(0.0), NowPlayingLogic.previous(TvMediaKind.Music, 3.5, hasPreviousItem = true, chapters = emptyList()))
        assertEquals(TransportStep.PreviousItem, NowPlayingLogic.previous(TvMediaKind.Music, 1.0, hasPreviousItem = true, chapters = emptyList()))
        assertEquals(TransportStep.SeekTo(0.0), NowPlayingLogic.previous(TvMediaKind.Music, 1.0, hasPreviousItem = false, chapters = emptyList()))
    }

    @Test
    fun previousAndNextOnABookMoveByChapterAndDoNothingWithoutChapters() {
        assertEquals(TransportStep.SeekTo(600.0), NowPlayingLogic.previous(TvMediaKind.Audiobook, 700.0, false, chapters))
        assertEquals(TransportStep.SeekTo(0.0), NowPlayingLogic.previous(TvMediaKind.Audiobook, 601.0, false, chapters))
        assertEquals(TransportStep.SeekTo(1_500.0), NowPlayingLogic.next(TvMediaKind.Audiobook, 700.0, false, chapters))
        assertEquals(TransportStep.Nothing, NowPlayingLogic.next(TvMediaKind.Audiobook, 1_600.0, false, chapters))
        assertEquals(TransportStep.Nothing, NowPlayingLogic.previous(TvMediaKind.Audiobook, 5.0, false, emptyList()))
        assertEquals(TransportStep.Nothing, NowPlayingLogic.next(TvMediaKind.Audiobook, 5.0, true, emptyList()))
    }

    @Test
    fun nextOnASongGoesToTheNextSongOnlyWhenOneFollows() {
        assertEquals(TransportStep.NextItem, NowPlayingLogic.next(TvMediaKind.Music, 5.0, hasNextItem = true, chapters = emptyList()))
        assertEquals(TransportStep.Nothing, NowPlayingLogic.next(TvMediaKind.Music, 5.0, hasNextItem = false, chapters = emptyList()))
    }

    @Test
    fun previousAndNextAreDimmedInPlaceWhereTheyCannotAct() {
        assertFalse(NowPlayingLogic.canPrevious(TvMediaKind.Music, 1.0, hasPreviousItem = false, chapters = emptyList()))
        assertTrue(NowPlayingLogic.canPrevious(TvMediaKind.Music, 4.0, hasPreviousItem = false, chapters = emptyList()))
        assertTrue(NowPlayingLogic.canPrevious(TvMediaKind.Music, 1.0, hasPreviousItem = true, chapters = emptyList()))
        assertFalse(NowPlayingLogic.canNext(TvMediaKind.Music, 1.0, hasNextItem = false, chapters = emptyList()))
        assertFalse(NowPlayingLogic.canNext(TvMediaKind.Audiobook, 1_600.0, hasNextItem = true, chapters = chapters))
        assertTrue(NowPlayingLogic.canNext(TvMediaKind.Audiobook, 10.0, hasNextItem = false, chapters = chapters))
    }

    @Test
    fun repeatAllMakesTheEndsOfTheQueueConnect() {
        assertFalse(NowPlayingLogic.hasNextSong(index = 2, count = 3, repeat = TvRepeatMode.Off))
        assertTrue(NowPlayingLogic.hasNextSong(index = 2, count = 3, repeat = TvRepeatMode.All))
        assertTrue(NowPlayingLogic.hasPreviousSong(index = 0, count = 3, repeat = TvRepeatMode.All))
        assertFalse(NowPlayingLogic.hasPreviousSong(index = 0, count = 3, repeat = TvRepeatMode.Off))
        assertFalse(NowPlayingLogic.hasNextSong(index = 0, count = 1, repeat = TvRepeatMode.All))
        assertFalse(NowPlayingLogic.hasNextSong(index = 0, count = 0, repeat = TvRepeatMode.Off))
    }

    @Test
    fun theSkipsUseTheGivenSecondsAndStayInsideTheFile() {
        assertEquals(TransportStep.SeekTo(90.0), NowPlayingLogic.skip(100.0, -10, 3_000.0))
        assertEquals(TransportStep.SeekTo(0.0), NowPlayingLogic.skip(4.0, -10, 3_000.0))
        assertEquals(TransportStep.SeekTo(3_000.0), NowPlayingLogic.skip(2_990.0, 30, 3_000.0))
        assertEquals(TransportStep.SeekTo(130.0), NowPlayingLogic.skip(100.0, 30, null))
    }

    @Test
    fun theTimelineShowsTheChapterOrTheWholeBookAsChosenAndAlwaysTheWholeSong() {
        val chapter = NowPlayingLogic.timeline(book, 700.0, 2_400.0, wholeBook = false)
        val whole = NowPlayingLogic.timeline(book, 700.0, 2_400.0, wholeBook = true)
        val songTimeline = NowPlayingLogic.timeline(song, 60.0, 240.0, wholeBook = false)

        assertEquals(100.0 / 900.0, chapter.fraction, 0.0001)
        assertEquals(700.0 / 2_400.0, whole.fraction, 0.0001)
        assertEquals(0.25, songTimeline.fraction, 0.0001)
    }

    @Test
    fun aDragOnTheChapterTimelineStaysInsideTheChapter() {
        // Half way along the second chapter (600 s to 1,500 s) is 1,050 s of the book.
        assertEquals(1_050.0, NowPlayingLogic.seekFromTimeline(book, 0.5, 700.0, 2_400.0, wholeBook = false), 0.001)
        assertEquals(1_200.0, NowPlayingLogic.seekFromTimeline(book, 0.5, 700.0, 2_400.0, wholeBook = true), 0.001)
        assertEquals(120.0, NowPlayingLogic.seekFromTimeline(song, 0.5, 10.0, 240.0, wholeBook = false), 0.001)
        assertEquals(0.0, NowPlayingLogic.seekFromTimeline(song, -1.0, 10.0, 240.0, wholeBook = false), 0.001)
    }

    @Test
    fun theLockScreenCardForASongAndABookUsesTheContractLinesAndActions() {
        val songCard = LockScreen.card(NowPlayingLogic.factsFor(song, 0.0, hasNext = true))
        val bookCard = LockScreen.card(NowPlayingLogic.factsFor(book, 1_600.0, hasNext = false))

        assertEquals("Stay a Little Longer", songCard.title)
        assertEquals("Maya Ellis · Northern Lights", songCard.line2)
        assertEquals(listOf("play", "pause", "previoustrack", "nexttrack", "seekto"), songCard.actions)
        assertEquals("Maya Ellis · The Way Beyond", bookCard.line2)
        assertEquals(
            listOf("play", "pause", "seekbackward", "seekforward", "previoustrack", "nexttrack", "seekto"),
            bookCard.actions,
        )
    }

    @Test
    fun theSleepTimerPresetsStartATimerAndEndOfChapterStopsAtTheChapterEnd() {
        val fifteen = NowPlayingLogic.sleepTimerFor("15", 1_000L, chapters, 0.0, 2_400.0)
        val endOfChapter = NowPlayingLogic.sleepTimerFor(SleepTimerLogic.END_OF_CHAPTER_KEY, 1_000L, chapters, 700.0, 2_400.0)

        assertEquals(SleepTimerLogic.after(15, 1_000L), fifteen)
        assertEquals(SleepTimer.EndOfChapter(2, 1_500.0), endOfChapter)
        assertNull(NowPlayingLogic.sleepTimerFor(SleepTimerLogic.CUSTOM_KEY, 0L, chapters, 0.0, 2_400.0))
        // A book with no chapters has no End of chapter timer to start.
        assertNull(NowPlayingLogic.sleepTimerFor(SleepTimerLogic.END_OF_CHAPTER_KEY, 0L, emptyList(), 0.0, 2_400.0))
    }

    @Test
    fun anEndOfChapterTimerSurvivesTheSameBookBeingPutBackInPlaceButNotAnotherBook() {
        val timer = SleepTimer.EndOfChapter(2, 1_500.0)

        assertEquals(timer, NowPlayingLogic.timerAfterItemChange(timer, "book-1", "book-1"))
        assertEquals(SleepTimer.Off, NowPlayingLogic.timerAfterItemChange(timer, "book-1", "book-2"))
        assertEquals(SleepTimer.Off, NowPlayingLogic.timerAfterItemChange(timer, "book-1", null))
    }

    @Test
    fun aTimedSleepTimerAndAnOffTimerAreNeverChangedByAnItemChange() {
        val timed = SleepTimerLogic.after(15, 1_000L)

        assertEquals(timed, NowPlayingLogic.timerAfterItemChange(timed, "a", "b"))
        assertEquals(SleepTimer.Off, NowPlayingLogic.timerAfterItemChange(SleepTimer.Off, "a", "b"))
    }

    @Test
    fun anEndOfChapterTimerTicksItsOwnRowAndAMinuteTimerTicksNone() {
        val rows = SleepTimerLogic.choices(hasChapters = true)
        val endRow = rows.first { it.key == SleepTimerLogic.END_OF_CHAPTER_KEY }

        assertTrue(NowPlayingLogic.sleepChoiceChecked(endRow, SleepTimer.EndOfChapter(2, 1_500.0)))
        assertFalse(rows.any { NowPlayingLogic.sleepChoiceChecked(it, SleepTimerLogic.after(15, 0L)) })
    }

    @Test
    fun anAudiobookStartsAtTheSpeedThePhoneKeptAndMusicAlwaysAtOne() {
        val device = PhoneDeviceChoices(audiobookSpeed = 1.25)

        assertEquals(1.25, NowPlayingLogic.startSpeed(TvMediaKind.Audiobook, null, device), 0.0001)
        assertEquals(1.0, NowPlayingLogic.startSpeed(TvMediaKind.Music, null, device), 0.0001)
        assertEquals(1.0, NowPlayingLogic.startSpeed(TvMediaKind.Audiobook, null, null), 0.0001)
    }

    @Test
    fun onlySyncedLyricsSeekAndOnlyAHandScrollShowsTheResumePill() {
        val synced = LyricsFollow.parse("[00:12.00]First line\n[00:20.50]Second line")
        val plain = LyricsFollow.parse("Just words\nMore words")

        assertEquals(12.0, NowPlayingLogic.lyricSeek(synced.lines[0])!!, 0.001)
        assertNull(NowPlayingLogic.lyricSeek(plain.lines[0]))
        assertNotNull(NowPlayingLogic.lyricsHint(synced))
        assertNull(NowPlayingLogic.lyricsHint(plain))
        assertTrue(NowPlayingLogic.showResumeFollowing(synced, userScrolled = true))
        assertFalse(NowPlayingLogic.showResumeFollowing(synced, userScrolled = false))
        assertFalse(NowPlayingLogic.showResumeFollowing(plain, userScrolled = true))
    }

    @Test
    fun everyFixedWordOfThePlayersIsInTheContract() {
        listOf(
            NowPlayingLogic.RESUME_FOLLOWING,
            NowPlayingLogic.OPEN_NOW_PLAYING,
            NowPlayingLogic.SELECT_LINE,
            NowPlayingLogic.LYRICS_SYNCED_FOOTER,
            NowPlayingLogic.LYRICS_UNSYNCED_FOOTER,
            QueueLogic.PLAYING_FROM,
            QueueLogic.CLEAR,
            QueueLogic.HISTORY,
            SleepTimerLogic.PLUS_FIVE_LABEL,
            SleepTimerLogic.CANCEL_LABEL,
            SleepTimerLogic.RUNNING_NOTE,
            PhoneBookmarks.ADD_A_NOTE,
        ).forEach { word -> assertTrue("the contract does not contain \"$word\"", contract.contains(word)) }
    }

    @Test
    fun theMiniPlayerShowsOnlyWhileSomethingIsLoaded() {
        assertFalse(NowPlayingLogic.showsMiniPlayer(AudioNow.None))
        assertTrue(NowPlayingLogic.showsMiniPlayer(song))
        assertEquals("Maya Ellis · Northern Lights", NowPlayingLogic.barLine(song, 0.0))
    }
}
