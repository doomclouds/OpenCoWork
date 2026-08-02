[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PublishDirectory,
    [switch]$Fixture
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') {
    throw 'build-package.ps1 must run on Windows.'
}
if ($Fixture -ne [bool]$PublishDirectory) {
    throw '-Fixture and -PublishDirectory must be used together.'
}

$scriptDirectory = $PSScriptRoot
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptDirectory '..\..'))
$project = Join-Path $repositoryRoot 'src\OpenCoWork.App\OpenCoWork.App.csproj'
$repositoryVersion = (& dotnet msbuild $project -nologo -getProperty:Version).Trim()
if ($repositoryVersion -ne $Version) {
    throw "Version mismatch: repository=$repositoryVersion requested=$Version"
}

$workDirectory = Join-Path ([IO.Path]::GetTempPath()) ('opencowork-package-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($workDirectory) | Out-Null
try {
    if (-not $PublishDirectory) {
        $head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
        if ($head -ne $Commit) {
            throw 'Commit does not match the current HEAD.'
        }
        if ((& git -C $repositoryRoot status --porcelain --untracked-files=all)) {
            throw 'Release packages require a clean worktree.'
        }
        $PublishDirectory = Join-Path $workDirectory 'publish'
        & dotnet publish $project -c Release -r win-x64 --self-contained true `
            "-p:SourceRevisionId=$Commit" -p:DebugType=None -p:DebugSymbols=false `
            -o $PublishDirectory
        if ($LASTEXITCODE -ne 0) {
            throw 'dotnet publish failed.'
        }
    }
    $PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
    $executable = Join-Path $PublishDirectory 'opencowork.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw 'Published executable is missing: opencowork.exe'
    }
    if (-not $Fixture) {
        $actualVersion = (& $executable --version).Trim()
        if ($LASTEXITCODE -ne 0 -or $actualVersion -ne "opencowork $Version") {
            throw "Published version mismatch: $actualVersion"
        }
    }

    $stage = Join-Path $workDirectory 'stage'
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    Get-ChildItem -LiteralPath $PublishDirectory -Force | Copy-Item -Destination $stage -Recurse -Force
    Get-ChildItem -LiteralPath $stage -Recurse -Force | ForEach-Object {
        if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Publish directory contains a reparse point: $($_.Name)"
        }
    }
    Get-ChildItem -LiteralPath $stage -Recurse -File -Filter '*.pdb' | Remove-Item -Force
    $disallowed = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object {
        $_.Name -match '(?i)(TestClient|IntegrationTests|TestResults)' -or
        $_.Extension -in @('.db', '.db-wal') -or $_.Name -eq '.DS_Store'
    } | Select-Object -First 1
    if ($disallowed) {
        throw "Disallowed release file: $($disallowed.Name)"
    }
    if ($env:OPENCOWORK_VALIDATION_SECRET_CANARY) {
        $canaryMatch = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object {
            try {
                (Get-Content -LiteralPath $_.FullName -Raw -ErrorAction Stop).IndexOf(
                    $env:OPENCOWORK_VALIDATION_SECRET_CANARY,
                    [StringComparison]::Ordinal) -ge 0
            }
            catch {
                $false
            }
        } | Select-Object -First 1
        if ($canaryMatch) {
            throw 'Release package contains the secret canary.'
        }
    }

    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\getting-started.md') -Destination (Join-Path $stage 'INSTALL.md')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\integration-guide.md') -Destination (Join-Path $stage 'INTEGRATIONS.md')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\release-notes.md') -Destination (Join-Path $stage 'RELEASE-NOTES.md')
    Copy-Item -LiteralPath (Join-Path $scriptDirectory 'install.ps1') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $scriptDirectory 'uninstall.ps1') -Destination $stage
    @(
        'OpenCoWork Runtime 1.0 package: Unsigned'
        'This package is not code-signed or notarized.'
        'Verify SHA256SUMS before making a local operating-system trust decision.'
    ) | Set-Content -LiteralPath (Join-Path $stage 'UNSIGNED.txt') -Encoding ascii
    @{
        schemaVersion = 1
        product = 'OpenCoWork'
        version = $Version
        commit = $Commit
        runtimeIdentifier = 'win-x64'
        unsigned = $true
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'release-manifest.json') -Encoding utf8

    [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    $artifactBase = "opencowork-$Version-win-x64"
    $sbom = Join-Path $OutputDirectory "$artifactBase.spdx"
    $builder = [Text.StringBuilder]::new()
    [void]$builder.AppendLine('SPDXVersion: SPDX-2.3')
    [void]$builder.AppendLine('DataLicense: CC0-1.0')
    [void]$builder.AppendLine('SPDXID: SPDXRef-DOCUMENT')
    [void]$builder.AppendLine("DocumentName: $artifactBase")
    [void]$builder.AppendLine("DocumentNamespace: https://spdx.org/spdxdocs/$artifactBase-$Commit")
    [void]$builder.AppendLine('Creator: Organization: OpenCoWork')
    [void]$builder.AppendLine('Creator: Tool: scripts/release/build-package.ps1')
    [void]$builder.AppendLine('Created: ' + [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('PackageName: OpenCoWork win-x64')
    [void]$builder.AppendLine('SPDXID: SPDXRef-Package')
    [void]$builder.AppendLine("PackageVersion: $Version")
    [void]$builder.AppendLine('PackageDownloadLocation: NOASSERTION')
    [void]$builder.AppendLine('FilesAnalyzed: true')
    $files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName)
    $sha1Values = @($files | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA1).Hash.ToLowerInvariant()
    } | Sort-Object)
    $verificationBytes = [Text.Encoding]::UTF8.GetBytes($sha1Values -join '')
    $sha1 = [Security.Cryptography.SHA1]::Create()
    try {
        $packageVerification = ([BitConverter]::ToString(
            $sha1.ComputeHash($verificationBytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha1.Dispose()
    }
    [void]$builder.AppendLine("PackageVerificationCode: $packageVerification")
    [void]$builder.AppendLine('PackageVerificationCodeExcludedFile: ./SBOM.spdx')
    [void]$builder.AppendLine('PackageLicenseConcluded: NOASSERTION')
    [void]$builder.AppendLine('PackageLicenseDeclared: NOASSERTION')
    [void]$builder.AppendLine('PackageCopyrightText: NOASSERTION')
    [void]$builder.AppendLine("PackageComment: Unsigned self-contained runtime from commit $Commit.")
    [void]$builder.AppendLine('Relationship: SPDXRef-DOCUMENT DESCRIBES SPDXRef-Package')
    $index = 0
    $files | ForEach-Object {
        $index++
        $relative = $_.FullName.Substring($stage.TrimEnd('\').Length + 1).Replace('\', '/')
        $checksum = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        [void]$builder.AppendLine()
        [void]$builder.AppendLine("FileName: ./$relative")
        [void]$builder.AppendLine("SPDXID: SPDXRef-File-$index")
        [void]$builder.AppendLine("FileChecksum: SHA256: $checksum")
        [void]$builder.AppendLine('LicenseConcluded: NOASSERTION')
        [void]$builder.AppendLine('LicenseInfoInFile: NOASSERTION')
        [void]$builder.AppendLine('FileCopyrightText: NOASSERTION')
        [void]$builder.AppendLine("Relationship: SPDXRef-Package CONTAINS SPDXRef-File-$index")
    }
    [IO.File]::WriteAllText($sbom, $builder.ToString(), [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath $sbom -Destination (Join-Path $stage 'SBOM.spdx')

    $archive = Join-Path $OutputDirectory "$artifactBase.zip"
    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal

    $checksumFile = Join-Path $OutputDirectory 'SHA256SUMS'
    $artifacts = Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object {
        $_.Name -like "opencowork-$Version-*.zip" -or
        $_.Name -like "opencowork-$Version-*.tar.gz" -or
        $_.Name -like "opencowork-$Version-*.spdx"
    } | Sort-Object Name
    $lines = $artifacts | ForEach-Object {
        $checksum = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$checksum  $($_.Name)"
    }
    [IO.File]::WriteAllLines($checksumFile, $lines, [Text.UTF8Encoding]::new($false))

    foreach ($line in $lines) {
        $parts = $line -split '  ', 2
        $actual = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory $parts[1]) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $parts[0]) {
            throw "Checksum mismatch: $($parts[1])"
        }
    }
    $verify = Join-Path $workDirectory 'verify'
    Expand-Archive -LiteralPath $archive -DestinationPath $verify
    foreach ($required in @('opencowork.exe', 'install.ps1', 'uninstall.ps1', 'release-manifest.json', 'UNSIGNED.txt', 'SBOM.spdx')) {
        if (-not (Test-Path -LiteralPath (Join-Path $verify $required) -PathType Leaf)) {
            throw "Archive is missing $required"
        }
    }
    if ((Get-ChildItem -LiteralPath $verify -Recurse -File -Filter '*.pdb')) {
        throw 'Archive contains PDB files.'
    }
    $internalSbom = Join-Path $verify 'SBOM.spdx'
    if ((Get-FileHash $internalSbom -Algorithm SHA256).Hash -ne (Get-FileHash $sbom -Algorithm SHA256).Hash) {
        throw 'Internal and external SBOM differ.'
    }
    Write-Output $archive
}
finally {
    if (Test-Path -LiteralPath $workDirectory) {
        Remove-Item -LiteralPath $workDirectory -Recurse -Force
    }
}
