package net.viathefalcon.jnapupmon.remote.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable

private val DefaultColourScheme = lightColorScheme(
    primary = Purple40,
    secondary = PurpleGrey40,
    tertiary = Pink40,
    background = BackgroundLight,
    surface = BackgroundLight
)

@Composable
fun RemoteTheme(
    content: @Composable () -> Unit
) {
    MaterialTheme(
        colorScheme = DefaultColourScheme,
        typography = Typography,
        content = content
    )
}