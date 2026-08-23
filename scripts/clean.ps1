[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'

# Everything listed here is a reproducible build, package, test, or legacy
# output. Run scripts/package.ps1 again whenever release artifacts are needed.
$generatedDirectories = @(
    'artifacts',
    'output',
    'publish',
    'src\output',
    'src\src',
    'src\bin',
    'src\obj',
    'src\OneBox.Contracts\bin',
    'src\OneBox.Contracts\obj',
    'src\OneBox.Hardware\bin',
    'src\OneBox.Hardware\obj',
    'src\OneBox.Service\bin',
    'src\OneBox.Service\obj',
    'TestResults',
    'tests\OneBox.Tests\artifacts',
    'tests\OneBox.Tests\bin',
    'tests\OneBox.Tests\obj'
)

function Remove-GeneratedDirectory {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $target = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $RelativePath))
    if (-not $target.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the repository: $target"
    }
    if (-not [System.IO.Directory]::Exists($target)) {
        return
    }

    Write-Host "Removing $RelativePath"
    try {
        Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
    }
    catch {
        # The old in-repository publish staging can exceed MAX_PATH. Modern
        # .NET accepts the extended path prefix even when a provider does not.
        $extendedPath = if ($target.StartsWith('\\')) {
            '\\?\UNC\' + $target.Substring(2)
        }
        else {
            '\\?\' + $target
        }
        [System.IO.Directory]::Delete($extendedPath, $true)
    }
}

foreach ($relativePath in $generatedDirectories) {
    Remove-GeneratedDirectory -RelativePath $relativePath
}

Write-Host 'Clean complete. All generated outputs were removed.'
