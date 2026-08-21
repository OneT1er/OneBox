$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$manifestPath = Join-Path $root 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$errors = [System.Collections.Generic.List[string]]::new()

foreach ($item in $manifest.icons) {
    $path = Join-Path $root $item.file
    if (-not (Test-Path -LiteralPath $path)) { $errors.Add("missing: $($item.file)"); continue }
    try { [xml]$xml = Get-Content -LiteralPath $path -Raw -Encoding UTF8 } catch { $errors.Add("invalid XML: $($item.file)"); continue }
    $svg = $xml.DocumentElement
    if ($svg.LocalName -ne 'svg') { $errors.Add("root is not svg: $($item.file)") }
    if ($svg.viewBox -ne '0 0 24 24') { $errors.Add("viewBox mismatch: $($item.file)") }
    if ($svg.'stroke-width' -ne '1.8') { $errors.Add("stroke-width mismatch: $($item.file)") }
    if ($svg.InnerXml -match '<(text|font|image)(\s|>)') { $errors.Add("forbidden element: $($item.file)") }
    if ($svg.InnerXml -match '(?i)(<image|<text|<font|href\s*=|url\(|data:image|Segoe|emoji|materialdesign)') { $errors.Add("external/font/icon reference: $($item.file)") }
    if ($svg.InnerXml -match '[\uD800-\uDBFF][\uDC00-\uDFFF]') { $errors.Add("emoji/surrogate pair: $($item.file)") }
}

if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_ }; exit 1 }
Write-Output ("Validated {0} SVG icons." -f $manifest.icons.Count)
