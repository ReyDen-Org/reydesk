param([string]$ProjectRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
[xml]$svg = Get-Content (Join-Path $PSScriptRoot 'reydesk-mark.svg') -Raw
$paths = [System.Drawing.Drawing2D.GraphicsPath]::new()
foreach ($node in $svg.svg.path) {
    $tokens = [regex]::Matches($node.d, '[MLCZ]|-?\d+(?:\.\d+)?') | ForEach-Object Value
    $index = 0
    [single]$x = 0; [single]$y = 0
    while ($index -lt $tokens.Count) {
        $operation = $tokens[$index++]
        switch ($operation) {
            'M' { $paths.StartFigure(); $x = [single]$tokens[$index++]; $y = [single]$tokens[$index++] }
            'L' {
                [single]$nx = $tokens[$index++]; [single]$ny = $tokens[$index++]
                $paths.AddLine($x, $y, $nx, $ny); $x = $nx; $y = $ny
            }
            'C' {
                [single]$a = $tokens[$index++]; [single]$b = $tokens[$index++]
                [single]$c = $tokens[$index++]; [single]$d = $tokens[$index++]
                [single]$nx = $tokens[$index++]; [single]$ny = $tokens[$index++]
                $paths.AddBezier($x, $y, $a, $b, $c, $d, $nx, $ny); $x = $nx; $y = $ny
            }
            'Z' { $paths.CloseFigure() }
        }
    }
}
$lime = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#C4EF6B'))
$background = [System.Drawing.ColorTranslator]::FromHtml('#0D1418')
function New-MarkBitmap([int]$Width, [int]$Height, [bool]$Transparent = $false, [bool]$Round = $false) {
    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = 'AntiAlias'
    if (!$Transparent) {
        if ($Round) {
            $brush = [System.Drawing.SolidBrush]::new($background)
            $graphics.FillEllipse($brush, 0, 0, $Width, $Height)
            $brush.Dispose()
        } else { $graphics.Clear($background) }
    }
    [single]$scale = [Math]::Min($Width / 96.0, $Height / 112.0) * $(if ($Transparent) { 1 } else { 0.7 })
    $graphics.TranslateTransform(($Width - 96 * $scale) / 2, ($Height - 112 * $scale) / 2)
    $graphics.ScaleTransform($scale, $scale)
    $graphics.FillPath($lime, $paths)
    $graphics.Dispose()
    return $bitmap
}
$windowsAssets = Join-Path $ProjectRoot 'Windows/ReyDesk.Windows/Assets'
$preview = New-MarkBitmap 384 448 $true
$preview.Save((Join-Path $PSScriptRoot 'reydesk-mark.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$iconImages = foreach ($size in $sizes) {
    $bitmap = New-MarkBitmap $size $size
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    ,$stream.ToArray()
    $stream.Dispose(); $bitmap.Dispose()
}
$file = [System.IO.File]::Create((Join-Path $windowsAssets 'AppIcon.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([ushort]0); $writer.Write([ushort]1); $writer.Write([ushort]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $writer.Write([byte]($sizes[$i] % 256)); $writer.Write([byte]($sizes[$i] % 256))
    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([ushort]1); $writer.Write([ushort]32)
    $writer.Write([uint32]$iconImages[$i].Length); $writer.Write([uint32]$offset)
    $offset += $iconImages[$i].Length
}
foreach ($bytes in $iconImages) { $writer.Write([byte[]]$bytes) }
$writer.Dispose()
# Existing tile and legacy launcher slots retain their original dimensions.
$slots = @(Get-ChildItem $windowsAssets -Filter '*.png' | Where-Object { $_.Name -notlike 'Logo-Horizontal-*' })
$slots += @(Get-ChildItem (Join-Path $ProjectRoot 'Android/app/src/main/res') -Directory -Filter 'mipmap-*' |
    ForEach-Object { Get-ChildItem $_.FullName -Filter '*.png' })
foreach ($slot in $slots) {
    $image = [System.Drawing.Image]::FromFile($slot.FullName)
    $width = $image.Width; $height = $image.Height; $image.Dispose()
    $bitmap = New-MarkBitmap $width $height $false ($slot.BaseName -eq 'ic_launcher_round')
    if ($slot.BaseName -eq 'ic_launcher_background') {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap); $graphics.Clear($background); $graphics.Dispose()
    }
    $bitmap.Save($slot.FullName, [System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
}
$paths.Dispose(); $lime.Dispose()
