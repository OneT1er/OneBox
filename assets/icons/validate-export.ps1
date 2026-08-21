$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$icoPath = Join-Path $root 'app.ico'
$pngPath = Join-Path $root 'app-preview.png'
if (-not (Test-Path -LiteralPath $icoPath) -or -not (Test-Path -LiteralPath $pngPath)) { throw 'Run export-icons.ps1 first.' }

$bytes = [IO.File]::ReadAllBytes($icoPath)
if ($bytes.Length -lt 6 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0 -or [BitConverter]::ToUInt16($bytes, 2) -ne 1) { throw 'Invalid ICO header.' }
$count = [BitConverter]::ToUInt16($bytes, 4)
$expected = @(16,20,24,32,40,48,64,128,256)
if ($count -ne $expected.Count) { throw "Expected $($expected.Count) ICO frames, got $count." }
for ($i = 0; $i -lt $count; $i++) {
    $offset = 6 + 16 * $i
    $width = $bytes[$offset]; if ($width -eq 0) { $width = 256 }
    $height = $bytes[$offset + 1]; if ($height -eq 0) { $height = 256 }
    $bits = [BitConverter]::ToUInt16($bytes, $offset + 6)
    if ($width -ne $expected[$i] -or $height -ne $expected[$i] -or $bits -ne 32) { throw "ICO frame $i is ${width}x${height}, ${bits}-bit." }
    Write-Output ("frame[{0}] {1}x{1} 32-bit" -f $i, $width)
}

$magick = Get-Command magick -ErrorAction SilentlyContinue
if (-not $magick) {
    $known = @(Join-Path ${env:ProgramFiles} 'ImageMagick-*\magick.exe') + @(Join-Path ${env:ProgramFiles(x86)} 'ImageMagick-*\magick.exe')
    $knownPath = Get-ChildItem -Path $known -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($knownPath) { $magick = [PSCustomObject]@{ Source = $knownPath.FullName } }
}
if (-not $magick) { throw 'ImageMagick is required for PNG metadata validation.' }
$identify = & $magick.Source identify -format '%wx%h %[channels] %[depth]' $pngPath
if ($identify -notmatch '^256x256 srgba( 4\.0)? 8$') { throw "Unexpected PNG metadata: $identify" }
Write-Output 'PNG 256x256 sRGBA 8-bit'
