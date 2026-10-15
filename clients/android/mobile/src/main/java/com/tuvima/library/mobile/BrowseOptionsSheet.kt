package com.tuvima.library.mobile

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.tuvima.library.core.BrowseQuery

private val statusOptions = listOf(
    "" to "All",
    "in-progress" to "In progress",
    "not-started" to "Not started",
    "favorite" to "Favorites",
    "finished" to "Finished",
)

private const val MaxFacetChips = 30

/**
 * The Dashboard's sort and filter in a sheet that rises from the bottom: sort order, progress status, and the
 * genre, year and creator values the Engine reported for this library page. Nothing is sent until Apply.
 */
@Composable
internal fun BrowseOptionsSheet(
    current: BrowseQuery,
    sortOptions: List<Pair<String, String>>,
    genres: List<String>,
    creators: List<String>,
    years: List<String>,
    onApply: (BrowseQuery) -> Unit,
    onDismiss: () -> Unit,
) {
    val defaultSort = sortOptions.first().first
    var sort by remember { mutableStateOf(current.sort?.takeIf { value -> sortOptions.any { it.first == value } } ?: defaultSort) }
    var status by remember { mutableStateOf(current.status.orEmpty()) }
    var pickedGenres by remember { mutableStateOf(current.genres) }
    var pickedYears by remember { mutableStateOf(current.years) }
    var pickedCreators by remember { mutableStateOf(current.creators) }

    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Box(
            Modifier.fillMaxSize().background(Color(0x99000000)).clickable(onClick = onDismiss),
            contentAlignment = Alignment.BottomCenter,
        ) {
            Surface(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(max = 560.dp)
                    .pointerInput(Unit) { detectTapGestures { } },
                shape = RoundedCornerShape(topStart = 24.dp, topEnd = 24.dp),
                tonalElevation = 6.dp,
            ) {
                Column(Modifier.verticalScroll(rememberScrollState()).padding(20.dp)) {
                    Text("Sort and filter", style = MaterialTheme.typography.titleLarge)
                    OptionGroup("Sort by") {
                        sortOptions.forEach { (value, label) ->
                            FilterChip(selected = sort == value, onClick = { sort = value }, label = { Text(label) })
                        }
                    }
                    OptionGroup("Status") {
                        statusOptions.forEach { (value, label) ->
                            FilterChip(selected = status == value, onClick = { status = value }, label = { Text(label) })
                        }
                    }
                    if (genres.isNotEmpty()) {
                        OptionGroup("Genre") {
                            genres.take(MaxFacetChips).forEach { value ->
                                FilterChip(
                                    selected = value in pickedGenres,
                                    onClick = { pickedGenres = toggled(pickedGenres, value) },
                                    label = { Text(value) },
                                )
                            }
                        }
                    }
                    if (years.isNotEmpty()) {
                        OptionGroup("Year") {
                            years.take(MaxFacetChips).forEach { value ->
                                FilterChip(
                                    selected = value in pickedYears,
                                    onClick = { pickedYears = toggled(pickedYears, value) },
                                    label = { Text(value) },
                                )
                            }
                        }
                    }
                    if (creators.isNotEmpty()) {
                        OptionGroup("Creator") {
                            creators.take(MaxFacetChips).forEach { value ->
                                FilterChip(
                                    selected = value in pickedCreators,
                                    onClick = { pickedCreators = toggled(pickedCreators, value) },
                                    label = { Text(value) },
                                )
                            }
                        }
                    }
                    Spacer(Modifier.padding(top = 16.dp))
                    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        OutlinedButton(onClick = {
                            sort = defaultSort
                            status = ""
                            pickedGenres = emptyList()
                            pickedYears = emptyList()
                            pickedCreators = emptyList()
                        }) { Text("Reset") }
                        Button(onClick = {
                            onApply(
                                current.copy(
                                    sort = sort,
                                    status = status.ifBlank { null },
                                    genres = pickedGenres,
                                    years = pickedYears,
                                    creators = pickedCreators,
                                ),
                            )
                        }) { Text("Apply") }
                    }
                }
            }
        }
    }
}

@Composable
private fun OptionGroup(title: String, chips: @Composable () -> Unit) {
    Text(title, style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 16.dp, bottom = 4.dp))
    Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        chips()
    }
}

private fun toggled(values: List<String>, value: String): List<String> =
    if (value in values) values - value else values + value
