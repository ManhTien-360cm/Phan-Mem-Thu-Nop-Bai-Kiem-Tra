[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '9.8.7'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$installerSource = Join-Path $root 'installer\ExamTransfer.iss'
$buildReleaseSource = Join-Path $PSScriptRoot 'build-release.ps1'
$metadataHelper = Join-Path $PSScriptRoot 'installer-version-metadata.ps1'
$artifactValidator = Join-Path $PSScriptRoot 'validate-release-artifacts.ps1'

foreach ($requiredFile in @(
    $installerSource,
    $buildReleaseSource,
    $metadataHelper,
    $artifactValidator)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required installer metadata test input was not found: $requiredFile"
    }
}
. $metadataHelper

function Find-InnoCompiler {
    $candidates = @(@(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LocalAppData 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) })
    if ($candidates.Count -eq 0) {
        throw 'Inno Setup 6 was not found.'
    }
    return $candidates[0]
}

function Assert-ThrowsLike {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage,
        [Parameter(Mandatory = $true)][string]$CaseName
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw "Case $CaseName failed with an unexpected error: $($_.Exception.Message)"
        }
        Write-Host "PASS installer metadata rejection case=$CaseName" -ForegroundColor Green
        return
    }
    throw "Case $CaseName unexpectedly passed."
}

$installerText = Get-Content -LiteralPath $installerSource -Raw
foreach ($directive in @(
    'VersionInfoVersion={#MyAppVersion}.0',
    'VersionInfoProductVersion={#MyAppVersion}')) {
    if ($installerText.IndexOf($directive, [StringComparison]::Ordinal) -lt 0) {
        throw "Installer producer is missing: $directive"
    }
}
if ($installerText -match 'VersionInfo(?:Product)?Version\s*=\s*1\.4\.7') {
    throw 'Installer version metadata must not hardcode candidate version 1.4.7.'
}

$buildReleaseText = Get-Content -LiteralPath $buildReleaseSource -Raw
foreach ($producerContract in @(
    'ConvertTo-WindowsNumericVersion',
    '"/DMyAppVersion=$Version"',
    'Assert-InstallerVersionMetadata')) {
    if ($buildReleaseText.IndexOf($producerContract, [StringComparison]::Ordinal) -lt 0) {
        throw "Canonical release producer is missing: $producerContract"
    }
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'ExamTransfer-InstallerMetadata-' + [Guid]::NewGuid().ToString('N'))
$releaseRoot = Join-Path $fixtureRoot 'Release'
$outputRoot = Join-Path $fixtureRoot 'Output'
$fixtureInstaller = Join-Path $outputRoot "ExamTransfer-Setup-$Version.exe"
$fixtureHashFile = "$fixtureInstaller.sha256.txt"
$legacySource = Join-Path $fixtureRoot 'LegacyMissingFileVersion.iss'
$legacyInstaller = Join-Path $outputRoot 'Legacy-Missing-FileVersion.exe'
$fixtureRepository = Join-Path $fixtureRoot 'Repository'

try {
    foreach ($directory in @(
        (Join-Path $releaseRoot 'Client'),
        (Join-Path $releaseRoot 'Server'),
        $outputRoot)) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    [IO.File]::WriteAllText((Join-Path $releaseRoot 'Client\placeholder.txt'), 'fixture')
    [IO.File]::WriteAllText(
        (Join-Path $releaseRoot 'Client\publiccloud.runtime.json'), '{}')
    [IO.File]::WriteAllText((Join-Path $releaseRoot 'Server\placeholder.txt'), 'fixture')
    [IO.File]::WriteAllText((Join-Path $releaseRoot 'release-manifest.json'), '{}')

    $testAppId = '{{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
    $isccArguments = @(
        "/DMyAppVersion=$Version",
        "/DMyAppId=$testAppId",
        "/DMyDefaultDirName=$(Join-Path $fixtureRoot 'App')",
        "/DMyOutputDir=$outputRoot",
        "/DMyReleaseRoot=$releaseRoot",
        '/DMyPrivilegesRequired=lowest',
        '/DMyDisableFirewall=1',
        '/DMyDisableLegacyCleanup=1',
        $installerSource
    )
    & (Find-InnoCompiler) @isccArguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Installer metadata fixture compilation failed with exit code $LASTEXITCODE."
    }

    $metadata = Assert-InstallerVersionMetadata `
        -InstallerPath $fixtureInstaller `
        -ExpectedVersion $Version
    Write-Host (
        'PASS installer metadata producer fixture ' +
        "fileVersion=$($metadata.FileVersionRaw) " +
        "productVersion=$($metadata.ProductVersionRaw)") -ForegroundColor Green

    [IO.Directory]::CreateDirectory($fixtureRepository) | Out-Null
    & git -C $fixtureRepository init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Unable to initialize artifact validator fixture repository.' }
    & git -C $fixtureRepository config user.name 'ExamTransfer Fixture'
    & git -C $fixtureRepository config user.email 'fixture@example.test'
    [IO.File]::WriteAllText((Join-Path $fixtureRepository 'tracked.txt'), 'fixture')
    & git -C $fixtureRepository add -- tracked.txt
    & git -C $fixtureRepository commit --quiet -m 'fixture'
    if ($LASTEXITCODE -ne 0) { throw 'Unable to commit artifact validator fixture repository.' }
    $fixtureHead = (& git -C $fixtureRepository rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $fixtureHead) {
        throw 'Unable to resolve artifact validator fixture HEAD.'
    }
    $fixtureBuildId = "$Version+$($fixtureHead.Substring(0, 8))-clean.fixture"
    $fixtureManifest = [ordered]@{
        semanticVersion  = $Version
        buildId          = $fixtureBuildId
        gitCommit        = $fixtureHead
        workingTreeDirty = $false
        installer        = [ordered]@{
            file           = [IO.Path]::GetFileName($fixtureInstaller)
            fileVersion    = ConvertTo-WindowsNumericVersion -SemanticVersion $Version
            productVersion = $Version
            productName    = 'ExamTransfer'
        }
    }
    $fixtureManifest | ConvertTo-Json -Depth 4 | Set-Content `
        -LiteralPath (Join-Path $releaseRoot 'release-manifest.json') `
        -Encoding utf8
    $fixtureHash = (Get-FileHash -LiteralPath $fixtureInstaller -Algorithm SHA256).Hash
    "$fixtureHash  $([IO.Path]::GetFileName($fixtureInstaller))" | Set-Content `
        -LiteralPath $fixtureHashFile `
        -Encoding ascii

    & powershell `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $artifactValidator `
        -ExpectedVersion $Version `
        -ExpectedHead $fixtureHead `
        -ExpectedBuildId $fixtureBuildId `
        -RepositoryRoot $fixtureRepository `
        -ReleaseRoot $releaseRoot `
        -InstallerPath $fixtureInstaller `
        -HashFilePath $fixtureHashFile
    if ($LASTEXITCODE -ne 0) {
        throw 'Valid artifact identity fixture did not pass 16/16.'
    }

    $fixtureManifest.semanticVersion = '0.0.1'
    $fixtureManifest | ConvertTo-Json -Depth 4 | Set-Content `
        -LiteralPath (Join-Path $releaseRoot 'release-manifest.json') `
        -Encoding utf8
    & powershell `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $artifactValidator `
        -ExpectedVersion $Version `
        -ExpectedHead $fixtureHead `
        -ExpectedBuildId $fixtureBuildId `
        -RepositoryRoot $fixtureRepository `
        -ReleaseRoot $releaseRoot `
        -InstallerPath $fixtureInstaller `
        -HashFilePath $fixtureHashFile *> $null
    if ($LASTEXITCODE -eq 0) {
        throw 'Artifact identity validator accepted a stale manifest version.'
    }
    Write-Host 'PASS artifact identity rejection case=stale-manifest-version' -ForegroundColor Green

    $versionParts = $Version.Split('.')
    $staleVersion = if ([int]$versionParts[2] -lt 65535) {
        '{0}.{1}.{2}' -f $versionParts[0], $versionParts[1], ([int]$versionParts[2] + 1)
    }
    else {
        '{0}.{1}.0' -f $versionParts[0], ([int]$versionParts[1] + 1)
    }
    Assert-ThrowsLike `
        -Action {
            Assert-InstallerVersionMetadata `
                -InstallerPath $fixtureInstaller `
                -ExpectedVersion $staleVersion | Out-Null
        } `
        -ExpectedMessage 'FileVersion mismatch' `
        -CaseName 'stale-file-and-product-version'

    $legacyScript = @"
[Setup]
AppName=ExamTransfer Metadata Fixture
AppVersion=$Version
DefaultDirName={tmp}\ExamTransferMetadataFixture
OutputDir=$outputRoot
OutputBaseFilename=Legacy-Missing-FileVersion
Uninstallable=no
PrivilegesRequired=lowest
"@
    [IO.File]::WriteAllText($legacySource, $legacyScript)
    & (Find-InnoCompiler) $legacySource | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Legacy metadata fixture compilation failed with exit code $LASTEXITCODE."
    }
    Assert-ThrowsLike `
        -Action {
            Assert-InstallerVersionMetadata `
                -InstallerPath $legacyInstaller `
                -ExpectedVersion $Version | Out-Null
        } `
        -ExpectedMessage 'FileVersion is empty' `
        -CaseName 'empty-file-version'

    Assert-ThrowsLike `
        -Action {
            ConvertFrom-InstallerVersionText `
                -Value '' `
                -MetadataName 'ProductVersion' | Out-Null
        } `
        -ExpectedMessage 'ProductVersion is empty' `
        -CaseName 'empty-product-version'

    Assert-ThrowsLike `
        -Action {
            ConvertTo-WindowsNumericVersion `
                -SemanticVersion '1.70000.0' | Out-Null
        } `
        -ExpectedMessage 'between 0 and 65535' `
        -CaseName 'windows-component-out-of-range'
}
finally {
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if ($resolvedFixture.StartsWith(
            $resolvedTemp,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedFixture).StartsWith(
            'ExamTransfer-InstallerMetadata-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'PASS installer version metadata producer tests' -ForegroundColor Green
