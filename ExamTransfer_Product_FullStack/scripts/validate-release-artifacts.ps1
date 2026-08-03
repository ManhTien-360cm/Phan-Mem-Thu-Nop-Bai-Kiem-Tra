[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedHead,
    [Parameter(Mandatory = $true)][string]$ExpectedBuildId,
    [Parameter(Mandatory = $true)][string]$RepositoryRoot,
    [Parameter(Mandatory = $true)][string]$ReleaseRoot,
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][string]$HashFilePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$metadataHelper = Join-Path $PSScriptRoot 'installer-version-metadata.ps1'
if (-not (Test-Path -LiteralPath $metadataHelper -PathType Leaf)) {
    throw "Installer metadata helper was not found: $metadataHelper"
}
. $metadataHelper

$script:identityCheckCount = 0
$expectedIdentityCheckCount = 16

function Assert-IdentityCheck {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    $script:identityCheckCount++
    if (-not $Condition) {
        throw "ARTIFACT_IDENTITY_FAIL check=$Name detail=$FailureMessage"
    }
    Write-Host "PASS artifact-identity [$script:identityCheckCount/$expectedIdentityCheckCount] $Name"
}

try {
    $windowsVersion = ConvertTo-WindowsNumericVersion -SemanticVersion $ExpectedVersion
    Assert-IdentityCheck $true 'expected-version-numeric' 'Expected version is invalid.'

    $actualHeadOutput = @(& git -C $RepositoryRoot rev-parse HEAD 2>&1)
    $actualHead = if ($LASTEXITCODE -eq 0 -and $actualHeadOutput.Count -eq 1) {
        ([string]$actualHeadOutput[0]).Trim()
    }
    else { '' }
    Assert-IdentityCheck `
        ($actualHead -eq $ExpectedHead) `
        'repository-head' `
        'Repository HEAD does not match the release source HEAD.'

    $statusOutput = @(& git -C $RepositoryRoot status --porcelain 2>&1)
    $statusClean = $LASTEXITCODE -eq 0 -and
        @($statusOutput | Where-Object {
            -not [string]::IsNullOrWhiteSpace([string]$_)
        }).Count -eq 0
    Assert-IdentityCheck `
        $statusClean `
        'repository-worktree-clean' `
        'Repository worktree is not clean.'

    $manifestPath = Join-Path $ReleaseRoot 'release-manifest.json'
    Assert-IdentityCheck `
        (Test-Path -LiteralPath $manifestPath -PathType Leaf) `
        'manifest-present' `
        'Release manifest was not found.'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

    Assert-IdentityCheck `
        ([string]$manifest.semanticVersion -eq $ExpectedVersion) `
        'manifest-version' `
        'Manifest semantic version does not match the candidate.'
    Assert-IdentityCheck `
        ([string]$manifest.gitCommit -eq $ExpectedHead) `
        'manifest-head' `
        'Manifest Git commit does not match the release source HEAD.'
    Assert-IdentityCheck `
        ($manifest.workingTreeDirty -is [bool] -and -not $manifest.workingTreeDirty) `
        'manifest-clean-marker' `
        'Manifest must record workingTreeDirty=false.'
    $expectedBuildPrefix = "$ExpectedVersion+$($ExpectedHead.Substring(0, 8))-clean."
    Assert-IdentityCheck `
        ([string]$manifest.buildId -eq $ExpectedBuildId -and
            $ExpectedBuildId.StartsWith($expectedBuildPrefix, [StringComparison]::Ordinal)) `
        'manifest-build-id' `
        'Manifest Build ID does not match version, HEAD, and clean source state.'

    $expectedInstallerName = "ExamTransfer-Setup-$ExpectedVersion.exe"
    Assert-IdentityCheck `
        ((Test-Path -LiteralPath $InstallerPath -PathType Leaf) -and
            [IO.Path]::GetFileName($InstallerPath) -eq $expectedInstallerName) `
        'installer-file-name' `
        'Installer is missing or has the wrong candidate filename.'

    $hashFilePresent = Test-Path -LiteralPath $HashFilePath -PathType Leaf
    $hashLine = if ($hashFilePresent) {
        (Get-Content -LiteralPath $HashFilePath -Raw).Trim()
    }
    else { '' }
    $hashMatch = $hashLine -match '^(?<hash>[A-Fa-f0-9]{64})\s+\S+$'
    if ($hashMatch) {
        $actualInstallerHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash
        $hashMatch = [string]::Equals(
            $Matches.hash,
            $actualInstallerHash,
            [StringComparison]::OrdinalIgnoreCase)
    }
    Assert-IdentityCheck `
        ($hashFilePresent -and $hashMatch) `
        'installer-sha256' `
        'Installer SHA-256 sidecar is missing or does not match the installer.'

    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($InstallerPath)
    $fileVersionRaw = ([string]$versionInfo.FileVersion).Trim()
    $productVersionRaw = ([string]$versionInfo.ProductVersion).Trim()
    Assert-IdentityCheck `
        (-not [string]::IsNullOrWhiteSpace($fileVersionRaw)) `
        'installer-file-version-present' `
        'Installer FileVersion is empty.'
    $fileVersionNormalized = ConvertFrom-InstallerVersionText `
        -Value $fileVersionRaw `
        -MetadataName 'FileVersion'
    Assert-IdentityCheck `
        ($fileVersionNormalized -eq $ExpectedVersion) `
        'installer-file-version' `
        'Installer FileVersion does not match the candidate.'
    Assert-IdentityCheck `
        (-not [string]::IsNullOrWhiteSpace($productVersionRaw)) `
        'installer-product-version-present' `
        'Installer ProductVersion is empty.'
    $productVersionNormalized = ConvertFrom-InstallerVersionText `
        -Value $productVersionRaw `
        -MetadataName 'ProductVersion'
    Assert-IdentityCheck `
        ($productVersionNormalized -eq $ExpectedVersion) `
        'installer-product-version' `
        'Installer ProductVersion does not match the candidate.'
    Assert-IdentityCheck `
        (([string]$versionInfo.ProductName).Trim() -eq 'ExamTransfer') `
        'installer-product-name' `
        'Installer ProductName does not match ExamTransfer.'

    $manifestInstaller = $manifest.installer
    $manifestInstallerMatches = $null -ne $manifestInstaller -and
        [string]$manifestInstaller.file -eq $expectedInstallerName -and
        (ConvertFrom-InstallerVersionText `
            -Value ([string]$manifestInstaller.fileVersion) `
            -MetadataName 'manifest installer FileVersion') -eq $ExpectedVersion -and
        (ConvertFrom-InstallerVersionText `
            -Value ([string]$manifestInstaller.productVersion) `
            -MetadataName 'manifest installer ProductVersion') -eq $ExpectedVersion -and
        [string]$manifestInstaller.productName -eq 'ExamTransfer' -and
        $windowsVersion -eq [string]$manifestInstaller.fileVersion
    Assert-IdentityCheck `
        $manifestInstallerMatches `
        'manifest-installer-identity' `
        'Manifest installer identity does not match the emitted installer.'

    if ($script:identityCheckCount -ne $expectedIdentityCheckCount) {
        throw (
            "ARTIFACT_IDENTITY_FAIL expected=$expectedIdentityCheckCount " +
            "actual=$script:identityCheckCount")
    }
    Write-Host "ARTIFACT_IDENTITY: PASS $script:identityCheckCount/$expectedIdentityCheckCount" -ForegroundColor Green
}
catch {
    Write-Host 'ARTIFACT_IDENTITY: FAIL' -ForegroundColor Red
    Write-Host "REASON: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
