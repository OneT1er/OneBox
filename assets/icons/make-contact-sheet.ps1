$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$manifest = Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$out = Join-Path $root 'contact-sheet.svg'
$cell = 56
$columns = 8
$rows = [Math]::Ceiling($manifest.icons.Count / $columns)
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ' + ($columns * $cell) + ' ' + ($rows * $cell) + '" fill="none" stroke="#8E8CD8" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">')
[void]$sb.AppendLine('<rect width="100%" height="100%" fill="#1C1A28" stroke="none"/>')
for ($i = 0; $i -lt $manifest.icons.Count; $i++) {
    $item = $manifest.icons[$i]
    $svg = [xml](Get-Content -LiteralPath (Join-Path $root $item.file) -Raw -Encoding UTF8)
    $x = (($i % $columns) * $cell) + 16
    $y = ([Math]::Floor($i / $columns) * $cell) + 16
    $inner = $svg.DocumentElement.InnerXml
    $inner = [regex]::Replace($inner, '<\?xml[^>]*>', '')
    [void]$sb.AppendLine(('<g transform="translate({0},{1}) scale(1.0)">{2}</g>' -f $x, $y, $inner))
}
[void]$sb.AppendLine('</svg>')
[IO.File]::WriteAllText($out, $sb.ToString(), [Text.UTF8Encoding]::new($false))
Write-Output ("Generated contact sheet for {0} icons: {1}" -f $manifest.icons.Count, $out)
