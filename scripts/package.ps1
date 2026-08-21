[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "publish\$Runtime"))
$packageDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "packages\$Runtime"))
$requiredPrefix = $artifactsRoot.TrimEnd('\') + '\'

foreach ($target in @($publishDirectory, $packageDirectory)) {
    if (-not $target.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside artifacts: $target"
    }
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
}

Push-Location $repositoryRoot
try {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $versionOutput = & $dotnet msbuild 'src/OneBox.csproj' -nologo -getProperty:Version
    if ($LASTEXITCODE -ne 0) { throw 'Failed to read the project version.' }
    $version = ($versionOutput | Select-Object -Last 1).Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
        throw "Invalid project Version: $version"
    }

    & $dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Failed to restore the Velopack vpk tool.' }
    & $dotnet restore 'OneBox.sln' -r $Runtime
    if ($LASTEXITCODE -ne 0) { throw 'RID restore failed.' }

    $projects = @(
        'src/OneBox.csproj',
        'src/OneBox.Service/OneBox.Service.csproj',
        'src/OneBox.Hardware/OneBox.Hardware.csproj'
    )
    foreach ($project in $projects) {
        & $dotnet publish $project -c $Configuration -r $Runtime --self-contained false --no-restore -o $publishDirectory
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
    }

    foreach ($executable in @('OneBox.exe', 'OneBox.Service.exe', 'OneBox.Hardware.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $executable))) {
            throw "Publish staging is missing: $executable"
        }
    }

    & $dotnet tool run vpk -- pack `
        --packId OneBox `
        --packVersion $version `
        --packDir $publishDirectory `
        --mainExe OneBox.exe `
        --runtime $Runtime `
        --framework net10-x64-desktop `
        --packAuthors OneT1er `
        --packTitle OneBox `
        --icon 'src/app.ico' `
        --channel win `
        --outputDir $packageDirectory `
        --yes
    if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $requiredPayload = @(
        'OneBox.exe',
        'OneBox.Service.exe',
        'OneBox.Hardware.exe',
        'OneBox.Contracts.dll',
        'Velopack.dll',
        'LibreHardwareMonitorLib.dll',
        'Microsoft.Extensions.Hosting.dll'
    )
    $fullPackages = @(Get-ChildItem -LiteralPath $packageDirectory -File -Filter '*-full.nupkg')
    $portablePackages = @(Get-ChildItem -LiteralPath $packageDirectory -File -Filter '*-Portable.zip')
    if ($fullPackages.Count -ne 1) {
        throw "Expected exactly one Velopack full nupkg; found $($fullPackages.Count)."
    }
    if ($portablePackages.Count -ne 1) {
        throw "Expected exactly one Velopack Portable ZIP; found $($portablePackages.Count)."
    }

    foreach ($archivePath in @($fullPackages[0].FullName, $portablePackages[0].FullName)) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
            foreach ($requiredFile in $requiredPayload) {
                $present = @($entryNames | Where-Object {
                    $_ -eq $requiredFile -or $_.EndsWith('/' + $requiredFile, [System.StringComparison]::OrdinalIgnoreCase)
                }).Count -gt 0
                if (-not $present) {
                    throw "Package $([System.IO.Path]::GetFileName($archivePath)) is missing $requiredFile."
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }

    Get-ChildItem -LiteralPath $packageDirectory -File |
        Sort-Object Name |
        Select-Object Name, Length, LastWriteTime
}
finally {
    Pop-Location
}
