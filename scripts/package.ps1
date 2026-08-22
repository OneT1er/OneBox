[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
# Keep the build staging directory outside the repository.  The workspace is
# sometimes synced with directory reparse points (notably src/artifacts), and
# an SDK folder publish inside that tree can otherwise discover and copy the
# repository's own artifacts recursively.
$stagingRoot = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) 'OneBox-package-staging'))
$stagingDirectory = [System.IO.Path]::GetFullPath((Join-Path $stagingRoot $Runtime))
$publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "publish\$Runtime"))
$packageDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "packages\$Runtime"))
$requiredPrefix = $artifactsRoot.TrimEnd('\') + '\'
$stagingPrefix = $stagingRoot.TrimEnd('\') + '\'
$forbiddenSegmentPattern = '(?i)(^|/)(artifacts|bin|obj|tests?|testresults|fullbuild|testbuild)(/|$)'
$maximumPayloadFiles = 256
$maximumArchiveEntries = 512

function Get-RelativePayloadPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootPrefix = $Root.TrimEnd('\') + '\'
    if (-not $Path.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Payload path escaped its root: $Path"
    }
    return $Path.Substring($rootPrefix.Length).Replace('\', '/')
}

function Assert-CleanPayloadDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force)
    if ($files.Count -eq 0) {
        throw "$Label is empty."
    }
    if ($files.Count -gt $maximumPayloadFiles) {
        throw "$Label has $($files.Count) files; refusing abnormal payload larger than $maximumPayloadFiles."
    }

    $payloadPaths = @($files | ForEach-Object { Get-RelativePayloadPath -Root $Root -Path $_.FullName })
    $forbidden = @($payloadPaths | Where-Object { $_ -match $forbiddenSegmentPattern })
    if ($forbidden.Count -gt 0) {
        throw "$Label contains forbidden build path(s): $((($forbidden | Select-Object -First 8) -join ', '))"
    }

    $executables = @($files | Where-Object { $_.Extension -ieq '.exe' })
    if ($executables.Count -ne 3) {
        throw "$Label must contain exactly three executable processes (GUI/service/hardware); found $($executables.Count)."
    }
    foreach ($requiredFile in $requiredPayload) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $requiredFile) -PathType Leaf)) {
            throw "$Label is missing $requiredFile."
        }
    }
}

function Assert-CleanArchive {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string[]]$RequiredFiles
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = @($archive.Entries)
        if ($entries.Count -gt $maximumArchiveEntries) {
            throw "Archive $([System.IO.Path]::GetFileName($ArchivePath)) has $($entries.Count) entries; refusing abnormal recursive payload."
        }
        $entryNames = @($entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        $forbidden = @($entryNames | Where-Object { $_ -match $forbiddenSegmentPattern })
        if ($forbidden.Count -gt 0) {
            throw "Archive $([System.IO.Path]::GetFileName($ArchivePath)) contains forbidden build path(s): $((($forbidden | Select-Object -First 8) -join ', '))"
        }
        $executableNames = @($entries | Where-Object {
                -not $_.FullName.EndsWith('/') -and [System.IO.Path]::GetExtension($_.FullName) -ieq '.exe'
            } | ForEach-Object { [System.IO.Path]::GetFileName($_.FullName) })
        # Velopack's full nupkg carries its own execution stub and Squirrel
        # updater beside the three application processes.  They are packaging
        # infrastructure, not a second application entry point.
        $allowedApplicationExecutables = @('OneBox.exe', 'OneBox.Service.exe', 'OneBox.Hardware.exe')
        $allowedInfrastructureExecutables = @('OneBox_ExecutionStub.exe', 'Squirrel.exe', 'Update.exe')
        $unexpectedExecutables = @($executableNames | Where-Object {
                $_ -notin ($allowedApplicationExecutables + $allowedInfrastructureExecutables)
            })
        $guiExecutables = @($executableNames | Where-Object { $_ -ieq 'OneBox.exe' })
        $serviceExecutables = @($executableNames | Where-Object { $_ -ieq 'OneBox.Service.exe' })
        $hardwareExecutables = @($executableNames | Where-Object { $_ -ieq 'OneBox.Hardware.exe' })
        if ($unexpectedExecutables.Count -gt 0 -or
            $guiExecutables.Count -lt 1 -or $guiExecutables.Count -gt 2 -or
            $serviceExecutables.Count -ne 1 -or $hardwareExecutables.Count -ne 1) {
            throw "Archive $([System.IO.Path]::GetFileName($ArchivePath)) must contain the GUI (once or Velopack launcher plus current copy), service and hardware executables only; found $($executableNames -join ', ')."
        }
        foreach ($requiredFile in $RequiredFiles) {
            $present = @($entryNames | Where-Object {
                    $_ -eq $requiredFile -or $_.EndsWith('/' + $requiredFile, [System.StringComparison]::OrdinalIgnoreCase)
                }).Count -gt 0
            if (-not $present) {
                throw "Archive $([System.IO.Path]::GetFileName($ArchivePath)) is missing $requiredFile."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

foreach ($target in @($publishDirectory, $packageDirectory)) {
    if (-not $target.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside artifacts: $target"
    }
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
}
if (-not $stagingDirectory.StartsWith($stagingPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean a staging path outside the dedicated temp root: $stagingDirectory"
}
if (Test-Path -LiteralPath $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

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
        & $dotnet publish $project -c $Configuration -r $Runtime --self-contained false --no-restore -o $stagingDirectory
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
    }

    $requiredPayload = @(
        'OneBox.exe',
        'OneBox.Service.exe',
        'OneBox.Hardware.exe',
        'OneBox.Contracts.dll',
        'Velopack.dll',
        'LibreHardwareMonitorLib.dll',
        'Microsoft.Extensions.Hosting.dll'
    )
    Assert-CleanPayloadDirectory -Root $stagingDirectory -Label 'Publish staging'

    Copy-Item -Path (Join-Path $stagingDirectory '*') -Destination $publishDirectory -Recurse -Force
    Assert-CleanPayloadDirectory -Root $publishDirectory -Label 'Publish directory'

    foreach ($executable in @('OneBox.exe', 'OneBox.Service.exe', 'OneBox.Hardware.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $executable))) {
            throw "Publish staging is missing: $executable"
        }
    }

    & $dotnet tool run vpk -- pack `
        --packId OneBox `
        --packVersion $version `
        --packDir $stagingDirectory `
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
    $fullPackages = @(Get-ChildItem -LiteralPath $packageDirectory -File -Filter '*-full.nupkg')
    $portablePackages = @(Get-ChildItem -LiteralPath $packageDirectory -File -Filter '*-Portable.zip')
    if ($fullPackages.Count -ne 1) {
        throw "Expected exactly one Velopack full nupkg; found $($fullPackages.Count)."
    }
    if ($portablePackages.Count -ne 1) {
        throw "Expected exactly one Velopack Portable ZIP; found $($portablePackages.Count)."
    }

    Assert-CleanArchive -ArchivePath $fullPackages[0].FullName -RequiredFiles $requiredPayload
    Assert-CleanArchive -ArchivePath $portablePackages[0].FullName -RequiredFiles $requiredPayload

    Get-ChildItem -LiteralPath $packageDirectory -File |
        Sort-Object Name |
        Select-Object Name, Length, LastWriteTime
}
finally {
    Pop-Location
}
