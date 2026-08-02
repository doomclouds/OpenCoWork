[CmdletBinding()]
param(
    [switch]$Purge,
    [switch]$ConfirmPurge
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') {
    throw 'uninstall.ps1 must run on Windows.'
}
if (-not $env:LOCALAPPDATA -or -not $env:USERPROFILE) {
    throw 'LOCALAPPDATA and USERPROFILE are required.'
}
if ($ConfirmPurge -and -not $Purge) {
    throw '-ConfirmPurge requires -Purge.'
}
$dataRoot = Join-Path $env:USERPROFILE '.opencowork'
if ($Purge -and -not $ConfirmPurge) {
    Write-Error "Purge target: $dataRoot. Re-run with -Purge -ConfirmPurge to delete it."
}

$installParent = Join-Path $env:LOCALAPPDATA 'OpenCoWork'
$installRoot = Join-Path $installParent 'bin'
foreach ($path in @($installParent, $installRoot, $dataRoot)) {
    if (Test-Path -LiteralPath $path) {
        $attributes = [IO.File]::GetAttributes($path)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing a reparse-point uninstall boundary: $path"
        }
    }
}
if (Test-Path -LiteralPath $installRoot) {
    $manifest = Join-Path $installRoot 'release-manifest.json'
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
        throw "Refusing to remove an unrelated directory: $installRoot"
    }
}

$pathBefore = [Environment]::GetEnvironmentVariable('Path', 'User')
$pathAdded = $false
$state = Join-Path $installRoot 'install-state.json'
if (Test-Path -LiteralPath $state -PathType Leaf) {
    $pathAdded = [bool]((Get-Content -LiteralPath $state -Raw | ConvertFrom-Json).pathAdded)
}
$pathEntries = @($pathBefore -split ';' | Where-Object { $_ })
$pathAfter = ($pathEntries | Where-Object {
    -not [string]::Equals($_.TrimEnd('\'), $installRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
}) -join ';'
$backup = Join-Path $installParent ('.uninstall.' + [Guid]::NewGuid().ToString('N'))
$moved = $false
$pathChanged = $false

try {
    if (Test-Path -LiteralPath $installRoot) {
        [IO.Directory]::Move($installRoot, $backup)
        $moved = $true
    }
    if ($pathAdded) {
        [Environment]::SetEnvironmentVariable('Path', $pathAfter, 'User')
        $pathChanged = $true
    }
    if ($moved) {
        Remove-Item -LiteralPath $backup -Recurse -Force
        $moved = $false
    }
}
catch {
    if ($pathChanged) {
        [Environment]::SetEnvironmentVariable('Path', $pathBefore, 'User')
    }
    if ($moved -and (Test-Path -LiteralPath $backup)) {
        [IO.Directory]::Move($backup, $installRoot)
    }
    throw
}

if ($Purge -and (Test-Path -LiteralPath $dataRoot)) {
    Remove-Item -LiteralPath $dataRoot -Recurse -Force
    Write-Output "Purged user data: $dataRoot"
}
else {
    Write-Output "Uninstalled OpenCoWork; preserved user data: $dataRoot"
}
