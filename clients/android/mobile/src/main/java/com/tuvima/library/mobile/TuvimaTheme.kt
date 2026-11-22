package com.tuvima.library.mobile

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.Shapes
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable

/** The Library color scheme, built from the generated [TuvimaTokens] (Look v3.2 D11) so the app matches the Dashboard instead of stock Material colors. */
val TuvimaColorScheme = darkColorScheme(
    primary = TuvimaTokens.accentPrimary,
    onPrimary = TuvimaTokens.textOnAccent,
    primaryContainer = TuvimaTokens.accentPrimaryActive,
    onPrimaryContainer = TuvimaTokens.textOnAccent,
    secondary = TuvimaTokens.accentSecondary,
    tertiary = TuvimaTokens.accentCollection,
    background = TuvimaTokens.bgRoot,
    onBackground = TuvimaTokens.textPrimary,
    surface = TuvimaTokens.bgSurface,
    onSurface = TuvimaTokens.textPrimary,
    surfaceVariant = TuvimaTokens.bgSurfaceRaised,
    onSurfaceVariant = TuvimaTokens.textSecondaryColor,
    surfaceContainer = TuvimaTokens.bgSurface,
    surfaceContainerHigh = TuvimaTokens.bgSurfaceHover,
    surfaceContainerHighest = TuvimaTokens.bgSurfaceElevated,
    outline = TuvimaTokens.textMuted,
)

private val TuvimaShapes = Shapes(
    small = RoundedCornerShape(TuvimaTokens.radiusMd),
    medium = RoundedCornerShape(TuvimaTokens.controlRadius),
    large = RoundedCornerShape(TuvimaTokens.radiusXl),
)

@Composable
fun TuvimaTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = TuvimaColorScheme, shapes = TuvimaShapes, content = content)
}
