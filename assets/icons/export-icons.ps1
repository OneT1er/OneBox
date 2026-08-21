$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$magick = Get-Command magick -ErrorAction SilentlyContinue
if (-not $magick) {
    $known = @(Join-Path ${env:ProgramFiles} 'ImageMagick-*\magick.exe') + @(Join-Path ${env:ProgramFiles(x86)} 'ImageMagick-*\magick.exe')
    $knownPath = Get-ChildItem -Path $known -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($knownPath) { $magick = [PSCustomObject]@{ Source = $knownPath.FullName } }
}
if (-not $magick) {
    Write-Error 'ImageMagick (magick.exe) is not installed. Install it, then rerun this script.'
    exit 2
}
$sizes = @(16,20,24,32,40,48,64,128,256)
$brand = Join-Path $root 'brand.svg'
$master = Join-Path $root '.brand-master.png'
$png = Join-Path $root 'app-preview.png'
$renderSvg = Join-Path $root '.brand-export.svg'
$svgText = (Get-Content -LiteralPath $brand -Raw -Encoding UTF8).Replace('currentColor', '#8E8CD8')
[IO.File]::WriteAllText($renderSvg, $svgText, [Text.UTF8Encoding]::new($false))
& $magick.Source -density 1024 $renderSvg -background none -alpha on -type TrueColorAlpha -depth 8 -define png:color-type=6 -define png:bit-depth=8 -filter Lanczos -resize 1024x1024 $master
& $magick.Source $master -background none -alpha on -type TrueColorAlpha -depth 8 -define png:color-type=6 -define png:bit-depth=8 -filter Lanczos -resize 256x256 $png
$frames = foreach ($size in $sizes) {
    $tmp = Join-Path $root (".brand-{0}.png" -f $size)
    & $magick.Source $master -background none -alpha on -type TrueColorAlpha -depth 8 -define png:color-type=6 -define png:bit-depth=8 -filter Lanczos -resize ("{0}x{0}" -f $size) $tmp
    $tmp
}
& $magick.Source @frames (Join-Path $root 'app.ico')
Remove-Item -LiteralPath $frames -Force
Remove-Item -LiteralPath $master -Force
Remove-Item -LiteralPath $renderSvg -Force
Write-Output 'Generated app-preview.png and app.ico.'
