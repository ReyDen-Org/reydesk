package ir.reyden.reydesk.ui.theme

import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Shapes
import androidx.compose.ui.unit.dp

object ReyDeskShapes {
    val Xs = RoundedCornerShape(6.dp)
    val Sm = RoundedCornerShape(10.dp)
    val Control = RoundedCornerShape(12.dp)
    val Card = RoundedCornerShape(16.dp)
    val Tile = RoundedCornerShape(11.dp)
    val TileSm = RoundedCornerShape(9.dp)
    val Pill = RoundedCornerShape(50)
}

val ReyDeskMaterialShapes = Shapes(
    extraSmall = ReyDeskShapes.Xs,
    small = ReyDeskShapes.Sm,
    medium = ReyDeskShapes.Control,
    large = ReyDeskShapes.Card,
    extraLarge = ReyDeskShapes.Card,
)
