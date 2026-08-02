[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') {
    throw 'install.ps1 must run on Windows.'
}
if (-not $env:LOCALAPPDATA) {
    throw 'LOCALAPPDATA is required.'
}

$sourceDirectory = $PSScriptRoot
$installParent = Join-Path $env:LOCALAPPDATA 'OpenCoWork'
$installRoot = Join-Path $installParent 'bin'
$executable = Join-Path $sourceDirectory 'opencowork.exe'
$manifest = Join-Path $sourceDirectory 'release-manifest.json'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf) -or
    -not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
    throw 'Run install.ps1 from an extracted OpenCoWork Windows package.'
}
if (Test-Path -LiteralPath $installParent) {
    $attributes = [IO.File]::GetAttributes($installParent)
    if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing a reparse-point install boundary: $installParent"
    }
}
if (Test-Path -LiteralPath $installRoot) {
    $installedManifest = Join-Path $installRoot 'release-manifest.json'
    if (-not (Test-Path -LiteralPath $installedManifest -PathType Leaf)) {
        throw "Refusing to replace an unrelated directory: $installRoot"
    }
}

[IO.Directory]::CreateDirectory($installParent) | Out-Null
$stage = Join-Path $installParent ('.install.' + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $installParent ('.backup.' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
$pathBefore = [Environment]::GetEnvironmentVariable('Path', 'User')
$pathEntries = @($pathBefore -split ';' | Where-Object { $_ })
$pathWasPresent = $pathEntries | Where-Object {
    [string]::Equals($_.TrimEnd('\'), $installRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
}
$previousPathAdded = $false
$previousState = Join-Path $installRoot 'install-state.json'
if (Test-Path -LiteralPath $previousState -PathType Leaf) {
    $previousPathAdded = [bool]((Get-Content -LiteralPath $previousState -Raw | ConvertFrom-Json).pathAdded)
}
$pathAdded = $previousPathAdded -or -not $pathWasPresent
$movedNew = $false
$movedOld = $false
$pathChanged = $false

try {
    Get-ChildItem -LiteralPath $sourceDirectory -Force | Copy-Item -Destination $stage -Recurse -Force
    & (Join-Path $stage 'opencowork.exe') --version | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'The staged OpenCoWork executable failed.'
    }
    if (Test-Path -LiteralPath $installRoot) {
        [IO.Directory]::Move($installRoot, $backup)
        $movedOld = $true
    }
    if ($env:OPENCOWORK_INSTALL_TEST_FAILPOINT -eq 'after-backup') {
        throw 'Injected install failure after backup.'
    }
    [IO.Directory]::Move($stage, $installRoot)
    $movedNew = $true

    if (-not $pathWasPresent) {
        $nextPath = (@($pathEntries) + $installRoot) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $nextPath, 'User')
        $pathChanged = $true
    }
    @{
        schemaVersion = 1
        pathAdded = $pathAdded
        pathEntry = $installRoot
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $installRoot 'install-state.json') -Encoding utf8

    if ($movedOld) {
        Remove-Item -LiteralPath $backup -Recurse -Force
        $movedOld = $false
    }
}
catch {
    if ($pathChanged) {
        [Environment]::SetEnvironmentVariable('Path', $pathBefore, 'User')
    }
    if ($movedNew -and (Test-Path -LiteralPath $installRoot)) {
        Remove-Item -LiteralPath $installRoot -Recurse -Force
    }
    if ($movedOld -and (Test-Path -LiteralPath $backup)) {
        [IO.Directory]::Move($backup, $installRoot)
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}

Write-Output "Installed Unsigned OpenCoWork to $installRoot"
