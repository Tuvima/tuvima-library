package com.tuvima.library.core

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The Find another wire readers and routes, against sample answers in the Engine's snake_case. */
class FindAnotherParsersTest {
    private val alternatives = """
        {
          "asset_id": "11111111-1111-1111-1111-111111111111",
          "kind": "subtitles",
          "status": "Found",
          "message": "",
          "source_name": "SubDL",
          "language": "English",
          "items": [
            {
              "provider": "subdl", "provider_name": "SubDL", "source_id": "abc", "language": "en",
              "is_timed": true, "confidence": 0.95, "duration_match_score": 0.8, "is_hearing_impaired": true,
              "release_name": "Film.2021.1080p.BluRay", "preview": null, "is_current": true,
              "preview_lines": [
                { "at_seconds": 62.5, "text": "Where are we going?" },
                { "at_seconds": 65, "text": "Home." }
              ],
              "ends_at_seconds": 6500.0, "length_check": "Off", "length_difference_seconds": -300.0
            },
            {
              "provider": "subdl", "provider_name": "SubDL", "source_id": "def", "language": "en",
              "is_timed": true, "confidence": 0.5, "is_hearing_impaired": false,
              "release_name": null, "title": null, "preview_lines": []
            }
          ]
        }
    """.trimIndent()

    @Test
    fun theListReadsItsRowsInTheEnginesOrder() {
        val list = parseTextTrackAlternatives(JSONObject(alternatives))
        assertEquals("Found", list.status)
        assertEquals("SubDL", list.sourceName)
        assertEquals("English", list.language)
        assertTrue(list.hasRows)
        assertEquals(listOf("subdl:abc", "subdl:def"), list.items.map { it.key })
    }

    @Test
    fun aRowReadsItsWordsTimesAndTimingCheck() {
        val row = parseTextTrackAlternatives(JSONObject(alternatives)).items[0]
        assertEquals("Film.2021.1080p.BluRay", row.releaseName)
        assertTrue(row.isHearingImpaired)
        assertTrue(row.isCurrent)
        assertEquals(0.95, row.confidence, 0.0001)
        assertEquals(0.8, row.durationMatchScore!!, 0.0001)
        assertEquals(listOf(62.5, 65.0), row.previewLines.map { it.atSeconds })
        assertEquals(listOf("Where are we going?", "Home."), row.previewLines.map { it.text })
        assertEquals(6500.0, row.endsAtSeconds!!, 0.0001)
        assertEquals("Off", row.lengthCheck)
        assertEquals(-300.0, row.lengthDifferenceSeconds!!, 0.0001)
        assertTrue(row.hasWords)
    }

    @Test
    fun aRowThatHasNotBeenReadHasNoWordsAndNoTimingCheck() {
        val row = parseTextTrackAlternatives(JSONObject(alternatives)).items[1]
        assertFalse(row.hasWords)
        assertNull(row.releaseName)
        assertNull(row.lengthCheck)
        assertNull(row.lengthDifferenceSeconds)
        assertNull(row.endsAtSeconds)
        assertFalse(row.isCurrent)
    }

    @Test
    fun aListWithAnEmptyItemsArrayHasNoRows() {
        val list = parseTextTrackAlternatives(JSONObject("""{"status":"NoneFound","message":"Nothing here.","items":[]}"""))
        assertFalse(list.hasRows)
        assertEquals("Nothing here.", list.message)
        assertTrue(list.items.isEmpty())
    }

    @Test
    fun aLyricsRowReadsUnTimedLinesWithoutTimes() {
        val json = JSONObject(
            """{"status":"Found","item":{"provider":"lrclib","source_id":"9","is_timed":false,"title":"Song","artist":"Band",
               "length_seconds":201.0,"item_length_seconds":200.0,"length_check":"Fits","length_difference_seconds":1.0,
               "preview_lines":[{"text":"one"},{"text":"two","at_seconds":null}]}}""",
        )
        val preview = parseTextTrackAlternativePreview(json)
        assertTrue(preview.isRead)
        val item = preview.item!!
        assertEquals(listOf(null, null), item.previewLines.map { it.atSeconds })
        assertEquals("Song", item.title)
        assertEquals(201.0, item.lengthSeconds!!, 0.0001)
        assertEquals(200.0, item.itemLengthSeconds!!, 0.0001)
    }

    @Test
    fun aPreviewThatCameBackWithoutAnItemIsNotRead() {
        val preview = parseTextTrackAlternativePreview(JSONObject("""{"status":"LimitReached","item":null}"""))
        assertEquals("LimitReached", preview.status)
        assertNull(preview.item)
        assertFalse(preview.isRead)
    }

    @Test
    fun aFoundPreviewWithNoLinesIsNotRead() {
        val preview = parseTextTrackAlternativePreview(
            JSONObject("""{"status":"Found","item":{"provider":"p","source_id":"s","preview_lines":[]}}"""),
        )
        assertFalse(preview.isRead)
    }

    @Test
    fun onlyReadyMeansASourceIsOn() {
        assertTrue(parseTextTrackSources(JSONObject("""{"status":"Ready"}""")).isReady)
        assertFalse(parseTextTrackSources(JSONObject("""{"status":"NoSourceOn"}""")).isReady)
        assertFalse(parseTextTrackSources(JSONObject("""{"status":"NotAllowed"}""")).isReady)
        assertFalse(parseTextTrackSources(JSONObject("{}")).isReady)
        assertFalse(TextTrackSources.Unavailable.isReady)
    }

    @Test
    fun aChoiceReadsItsStatusMessageAndTrack() {
        val choice = parseTextTrackChoice(
            JSONObject("""{"status":"Updated","message":"Now using the SubDL result.","track_id":"22222222-2222-2222-2222-222222222222"}"""),
        )
        assertTrue(choice.isUpdated)
        assertEquals("Now using the SubDL result.", choice.message)
        assertEquals("22222222-2222-2222-2222-222222222222", choice.trackId)
        assertNull(parseTextTrackChoice(JSONObject("""{"status":"Failed","message":"No.","track_id":null}""")).trackId)
    }

    @Test
    fun theChooseBodyUsesTheEnginesSnakeCaseNamesAndLeavesOutABlankLanguage() {
        val body = ChooseTextTrackRequest(TextTrackKind.Subtitles, "subdl", "abc", "English").toJson()
        assertEquals("subtitles", body.getString("kind"))
        assertEquals("subdl", body.getString("provider"))
        assertEquals("abc", body.getString("source_id"))
        assertEquals("English", body.getString("language"))
        assertFalse(ChooseTextTrackRequest(TextTrackKind.Subtitles, "subdl", "abc", " ").toJson().has("language"))
        assertFalse(ChooseTextTrackRequest(TextTrackKind.Lyrics, "lrclib", "9").toJson().has("language"))
    }

    // Routes: every Find another path sits under /api/v1/stream/{asset}/text-tracks.

    private val asset = "11111111-1111-1111-1111-111111111111"

    @Test
    fun theSourcesCheckAsksForSubtitlesByDefault() {
        assertEquals(
            "/api/v1/stream/$asset/text-tracks/sources?kind=subtitles",
            ClientRoutes.textTrackSources(asset),
        )
        assertEquals(
            "/api/v1/stream/$asset/text-tracks/sources?kind=lyrics",
            ClientRoutes.textTrackSources(asset, TextTrackKind.Lyrics),
        )
    }

    @Test
    fun theListRouteCarriesTheKindAndTheEncodedLanguage() {
        assertEquals(
            "/api/v1/stream/$asset/text-tracks/alternatives?kind=subtitles&language=Portuguese+%28Brazil%29",
            ClientRoutes.textTrackAlternatives(asset, TextTrackKind.Subtitles, "Portuguese (Brazil)"),
        )
        assertEquals(
            "/api/v1/stream/$asset/text-tracks/alternatives?kind=subtitles",
            ClientRoutes.textTrackAlternatives(asset, TextTrackKind.Subtitles, " "),
        )
    }

    @Test
    fun thePreviewRouteUsesTheEnginesSourceIdQueryName() {
        assertEquals(
            "/api/v1/stream/$asset/text-tracks/alternatives/preview?kind=subtitles&provider=subdl&sourceId=a%2Fb&language=English",
            ClientRoutes.textTrackAlternativePreview(asset, "subdl", "a/b", TextTrackKind.Subtitles, "English"),
        )
    }

    @Test
    fun theChooseRouteIsAPostPathUnderTheSameGroup() {
        assertEquals(
            "/api/v1/stream/$asset/text-tracks/alternatives/choose",
            ClientRoutes.chooseTextTrackAlternative(asset),
        )
    }
}
