package com.tuvima.library.mobile

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.HeroSlide
import com.tuvima.library.core.HomeHeroLogic
import com.tuvima.library.core.PhonePlayerLogic
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.playShowId
import kotlinx.coroutines.launch

/**
 * The Home hero card (the Dashboard's phone hero): one fixed-height card at the top of Home. Swipe or press the dots for the next
 * featured title. Each slide has its title, one meta line, a progress line while started, one full-width button (Play, Resume or
 * Details) and the My List plus. A press on the card opens the title's details. Dark only, bare icon buttons, no toasts.
 */
@Composable
internal fun HomeHeroCard(
    client: TuvimaClient,
    slides: List<HeroSlide>,
    openCard: (DisplayCard) -> Unit,
    play: (PlayRequest) -> Unit,
    openMusic: () -> Unit,
) {
    if (slides.isEmpty()) return
    val pager = rememberPagerState(pageCount = { slides.size })
    val scope = rememberCoroutineScope()
    Column(Modifier.fillMaxWidth()) {
        HorizontalPager(state = pager, modifier = Modifier.fillMaxWidth().height(HERO_HEIGHT)) { page ->
            HeroSlideCard(client, slides[page], openCard, play, openMusic)
        }
        if (slides.size > 1) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) {
                slides.forEachIndexed { index, slide ->
                    val current = index == pager.currentPage
                    Box(
                        Modifier
                            .size(width = TuvimaTokens.tapTarget, height = TuvimaTokens.tapTarget)
                            .clickable(onClickLabel = HomeHeroLogic.dotLabel(index, slides.size, slide.card.title)) {
                                scope.launch { pager.animateScrollToPage(index) }
                            }
                            .semantics { contentDescription = HomeHeroLogic.dotLabel(index, slides.size, slide.card.title) },
                        contentAlignment = Alignment.Center,
                    ) {
                        Box(
                            Modifier
                                .size(if (current) 10.dp else 8.dp)
                                .clip(CircleShape)
                                .background(if (current) TuvimaTokens.accentPrimaryHover else TuvimaTokens.textMuted),
                        )
                    }
                }
            }
        }
    }
}

private val HERO_HEIGHT = 300.dp

@Composable
private fun HeroSlideCard(
    client: TuvimaClient,
    slide: HeroSlide,
    openCard: (DisplayCard) -> Unit,
    play: (PlayRequest) -> Unit,
    openMusic: () -> Unit,
) {
    val card = slide.card
    val saved = LocalSavedItems.current
    val primary = HomeHeroLogic.primary(slide)
    val shape = RoundedCornerShape(12.dp)
    Box(
        Modifier
            .fillMaxSize()
            .padding(horizontal = 16.dp, vertical = 4.dp)
            .clip(shape)
            .background(TuvimaTokens.bgSurface)
            .clickable(onClickLabel = if (slide.isCollage) HomeHeroLogic.OPEN_MUSIC else HomeHeroLogic.OPEN_DETAILS) {
                // The "Recently added music" slide stands for all the new music, so it opens the music library, not one album.
                if (slide.isCollage) openMusic() else openCard(card)
            },
    ) {
        if (slide.isCollage) {
            CollageCovers(client, slide.collageUrls)
        } else {
            RemoteImage(client, card.artworkUrl ?: card.posterUrl, Modifier.fillMaxSize())
        }
        // A dark fade from the middle down keeps the words readable over any picture.
        Box(
            Modifier
                .fillMaxSize()
                .background(Brush.verticalGradient(listOf(Color.Transparent, Color.Black.copy(alpha = 0.88f)), startY = 120f)),
        )
        // The collage is not one saveable title, so it carries no My List plus.
        if (saved != null && !slide.isCollage) SavedToggle(saved.keyFor(card), Modifier.align(Alignment.TopEnd))
        Column(
            Modifier.align(Alignment.BottomStart).fillMaxWidth().padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            slide.eyebrow?.let {
                Text(it, style = MaterialTheme.typography.labelMedium, color = TuvimaTokens.accentPrimaryHover, maxLines = 1)
            }
            Text(
                card.title,
                style = MaterialTheme.typography.headlineSmall,
                color = TuvimaTokens.textPrimary,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            if (slide.metaLine.isNotEmpty()) {
                Text(
                    slide.metaLine,
                    style = MaterialTheme.typography.bodyMedium,
                    color = TuvimaTokens.textSecondaryColor,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            if (HomeHeroLogic.showsProgress(card)) {
                LinearProgressIndicator(
                    progress = { ((card.progressPercent ?: 0.0) / 100.0).toFloat().coerceIn(0f, 1f) },
                    modifier = Modifier.fillMaxWidth().height(3.dp),
                )
            }
            Button(
                onClick = {
                    val assetId = primary.assetId
                    if (primary.opensMusic) {
                        openMusic()
                    } else if (assetId == null) {
                        openCard(card)
                    } else {
                        val target = mobileCardDetailTarget(card)
                        play(
                            PlayRequest(
                                assetId = assetId,
                                entityType = target.first,
                                title = card.title,
                                subtitle = card.subtitle,
                                showId = playShowId(target.first, target.second, null),
                                workId = PhonePlayerLogic.workIdFor(target.first, target.second),
                            ),
                        )
                    }
                },
                modifier = Modifier.fillMaxWidth().heightIn(min = TuvimaTokens.controlHeightMain),
            ) { Text(primary.label) }
        }
    }
}

/** The newest covers as a two-by-two block (fewer when the Engine sent fewer), filling the card. */
@Composable
private fun CollageCovers(client: TuvimaClient, urls: List<String>) {
    val covers = urls.take(4)
    Column(Modifier.fillMaxSize()) {
        covers.chunked(2).forEach { pair ->
            Row(Modifier.fillMaxWidth().weight(1f)) {
                pair.forEach { url -> RemoteImage(client, url, Modifier.weight(1f).fillMaxSize()) }
            }
        }
    }
}
