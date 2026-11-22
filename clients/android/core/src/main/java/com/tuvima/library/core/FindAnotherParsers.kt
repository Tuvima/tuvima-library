package com.tuvima.library.core

import org.json.JSONObject

// Readers for the Find another answers. Names are the Engine's own snake_case; nothing here guesses at a missing field:
// an absent value becomes null, an empty list, an empty string or false.

internal fun parseTextTrackAlternative(json: JSONObject): TextTrackAlternative = TextTrackAlternative(
    provider = json.optString("provider"),
    providerName = json.optString("provider_name"),
    sourceId = json.optString("source_id"),
    language = json.optString("language", "und"),
    isTimed = json.optBoolean("is_timed"),
    confidence = json.optDouble("confidence", 0.0).takeUnless(Double::isNaN) ?: 0.0,
    durationMatchScore = json.optionalDouble("duration_match_score"),
    isHearingImpaired = json.optBoolean("is_hearing_impaired"),
    releaseName = json.optionalString("release_name"),
    isCurrent = json.optBoolean("is_current"),
    title = json.optionalString("title"),
    artist = json.optionalString("artist"),
    album = json.optionalString("album"),
    lengthSeconds = json.optionalDouble("length_seconds"),
    itemLengthSeconds = json.optionalDouble("item_length_seconds"),
    previewLines = json.optJSONArray("preview_lines")?.objects { line ->
        TextTrackPreviewLine(atSeconds = line.optionalDouble("at_seconds"), text = line.optString("text"))
    }.orEmpty(),
    endsAtSeconds = json.optionalDouble("ends_at_seconds"),
    lengthCheck = json.optionalString("length_check"),
    lengthDifferenceSeconds = json.optionalDouble("length_difference_seconds"),
)

internal fun parseTextTrackAlternatives(json: JSONObject): TextTrackAlternatives = TextTrackAlternatives(
    kind = json.optString("kind"),
    status = json.optString("status"),
    message = json.optString("message"),
    items = json.optJSONArray("items")?.objects(::parseTextTrackAlternative).orEmpty(),
    sourceName = json.optionalString("source_name"),
    language = json.optionalString("language"),
)

internal fun parseTextTrackAlternativePreview(json: JSONObject): TextTrackAlternativePreview =
    TextTrackAlternativePreview(
        status = json.optString("status"),
        item = json.optJSONObject("item")?.let(::parseTextTrackAlternative),
    )

internal fun parseTextTrackSources(json: JSONObject): TextTrackSources = TextTrackSources(json.optString("status"))

internal fun parseTextTrackChoice(json: JSONObject): TextTrackChoiceResult = TextTrackChoiceResult(
    status = json.optString("status"),
    message = json.optString("message"),
    trackId = json.optionalString("track_id"),
)
