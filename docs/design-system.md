# ReyDesk design system

Dark only, one amber accent, native controls on both platforms. The token files are
the source of truth: `Windows/ReyDesk.Windows/Theme/Tokens.Dark.xaml` (WinUI resource
dictionary) and `Android/app/src/main/kotlin/ir/reyden/reydesk/ui/theme/Color.kt`.
Both use the same values.

## Color

| Role | Value |
|---|---|
| Background / deep | `#080B11` / `#06080C` |
| Surface, raised, edge | `#0E131B`, `#141A24`, line `#222A37` (strong `#323C4D`) |
| Text, muted, faint | `#F3F5F8`, `#A7B0BF`, `#8590A2` |
| Accent (fills, active rail, icons) | `#FFB020` (hover `#FFC04D`, pressed `#E69A10`) |
| Text on accent | `#080B11` |
| Success, warning, danger | `#3DDC84`, `#FF8A3D`, `#FF5A5F` |

State is never shown by color alone: status chips and badges carry an icon and a text label.

## Typography

Inter for Latin UI, JetBrains Mono for codes, IDs and diagnostics, Vazirmatn for
Persian. All three are bundled (SIL OFL 1.1) so the apps work fully offline.

## Shape and motion

Cards use a 12 px radius, buttons and inputs 8 to 9 px, pills 999 px. Icons are
Lucide-style strokes on a 24 grid (stroke 1.9, round caps). Transitions are short
(about 120 to 250 ms, ease-out) and respect the OS reduced-motion setting.

## Device artwork

The Windows app draws a generic device in code (`MainPage.CreateDeviceArtwork`): a
rounded dark body with an amber rim, a screen area, a camera dot and a small glyph
(tablet silhouette when the reported model name contains "tab" or "pad", otherwise
a phone). The screen is tinted by a stable hue taken from the device id, so two
devices look different. It works for any model and uses no images; the model name
is shown as reported, never guessed.
