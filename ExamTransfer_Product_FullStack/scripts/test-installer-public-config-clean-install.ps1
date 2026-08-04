[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$installerSource = Join-Path $root 'installer\ExamTransfer.iss'
$sharedValidation = Join-Path $PSScriptRoot 'public-config-packaging.ps1'
$metadataHelper = Join-Path $PSScriptRoot 'installer-version-metadata.ps1'
$resolvedReleaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
$releasePublicConfigPath = Join-Path $resolvedReleaseRoot 'Client\publiccloud.runtime.json'
$releaseManifestPath = Join-Path $resolvedReleaseRoot 'release-manifest.json'

foreach ($requiredFile in @(
    $installerSource,
    $sharedValidation,
    $metadataHelper,
    $releasePublicConfigPath,
    $releaseManifestPath)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required clean-install input was not found: $requiredFile"
    }
}
. $sharedValidation
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

function Invoke-InstallerProcess(
    [string]$Path,
    [string[]]$Arguments,
    [string]$Operation) {
    $process = Start-Process `
        -FilePath $Path `
        -ArgumentList $Arguments `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    try {
        if ($process.ExitCode -ne 0) {
            throw "$Operation failed with exit code $($process.ExitCode)."
        }
    }
    finally {
        $process.Dispose()
    }
}

$expectedConfig = Read-PublicCloudConfig -Path $releasePublicConfigPath
$expectedConfigHash = (Get-FileHash `
    -LiteralPath $releasePublicConfigPath `
    -Algorithm SHA256).Hash
$expectedManifestHash = (Get-FileHash `
    -LiteralPath $releaseManifestPath `
    -Algorithm SHA256).Hash

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'ExamTransfer-PublicConfigInstall-' + [Guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $fixtureRoot 'App'
$runtimeRoot = Join-Path $fixtureRoot 'Data'
$storageRoot = Join-Path $runtimeRoot 'Storage'
$outputRoot = Join-Path $fixtureRoot 'Installer'
$installLog = Join-Path $fixtureRoot 'install.log'
$uninstallLog = Join-Path $fixtureRoot 'uninstall.log'
$testInstaller = Join-Path $outputRoot "ExamTransfer-Setup-$Version.exe"
$uninstaller = Join-Path $installRoot 'unins000.exe'

try {
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $testAppId = '{{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
    $isccArguments = @(
        "/DMyAppVersion=$Version",
        "/DMyAppId=$testAppId",
        "/DMyDefaultDirName=$installRoot",
        '/DMyClientShortcutName=ExamTransfer Public Config Acceptance',
        "/DMyOutputDir=$outputRoot",
        "/DMyReleaseRoot=$resolvedReleaseRoot",
        '/DMyPrivilegesRequired=lowest',
        "/DMyRuntimeSettingsRoot=$runtimeRoot",
        "/DMyCanonicalStorageRoot=$($storageRoot.Replace('\', '/'))",
        '/DMyDisableFirewall=1',
        '/DMyDisableLegacyCleanup=1',
        $installerSource
    )
    & (Find-InnoCompiler) @isccArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Public-config acceptance installer compilation failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $testInstaller -PathType Leaf)) {
        throw "Public-config acceptance installer was not created: $testInstaller"
    }

    $installerMetadata = Assert-InstallerVersionMetadata `
        -InstallerPath $testInstaller `
        -ExpectedVersion $Version
    Write-Host (
        'PASS clean-install fixture metadata ' +
        "fileVersion=$($installerMetadata.FileVersionRaw) " +
        "productVersion=$($installerMetadata.ProductVersionRaw)") -ForegroundColor Green

    Invoke-InstallerProcess `
        -Path $testInstaller `
        -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/SP-',
            "/LOG=$installLog") `
        -Operation 'Clean install'

    $installedPublicConfigPath = Join-Path $installRoot 'Client\publiccloud.runtime.json'
    if (-not (Test-Path -LiteralPath $installedPublicConfigPath -PathType Leaf)) {
        throw 'Installed publiccloud.runtime.json is missing.'
    }
    $installedHash = (Get-FileHash `
        -LiteralPath $installedPublicConfigPath `
        -Algorithm SHA256).Hash
    if (-not [string]::Equals(
            $expectedConfigHash,
            $installedHash,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installed publiccloud.runtime.json is not byte-identical to the release payload.'
    }
    $installedConfig = Read-PublicCloudConfig -Path $installedPublicConfigPath
    Assert-PublicCloudConfigEqual `
        -Expected $expectedConfig `
        -Actual $installedConfig `
        -Stage 'installed-public-config'

    $installedManifestPath = Join-Path $installRoot 'release-manifest.json'
    if (-not (Test-Path -LiteralPath $installedManifestPath -PathType Leaf)) {
        throw 'Installed release-manifest.json is missing.'
    }
    $installedManifestHash = (Get-FileHash `
        -LiteralPath $installedManifestPath `
        -Algorithm SHA256).Hash
    if (-not [string]::Equals(
            $expectedManifestHash,
            $installedManifestHash,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installed release-manifest.json is not byte-identical to the release payload.'
    }

    $runtimeSettingsPath = Join-Path $runtimeRoot 'config\runtime-settings.json'
    if (-not (Test-Path -LiteralPath $runtimeSettingsPath -PathType Leaf)) {
        throw 'Clean install did not create runtime-settings.json.'
    }
    $runtimeSettings = Get-Content -LiteralPath $runtimeSettingsPath -Raw | ConvertFrom-Json
    foreach ($mapping in @(
        @{ Name = 'supabaseUrl'; RuntimeName = 'SupabaseUrl' },
        @{ Name = 'publishableKey'; RuntimeName = 'PublishableKey' },
        @{ Name = 'organizationId'; RuntimeName = 'OrganizationId' })) {
        if (-not [string]::Equals(
                [string]$expectedConfig.($mapping.Name),
                [string]$runtimeSettings.Cloud.($mapping.RuntimeName),
                [StringComparison]::Ordinal)) {
            throw "Clean-install runtime config mismatch: $($mapping.RuntimeName)"
        }
    }

    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw 'Clean-install uninstaller was not created.'
    }
    Invoke-InstallerProcess `
        -Path $uninstaller `
        -Arguments @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            "/LOG=$uninstallLog") `
        -Operation 'Clean-install cleanup'

    Write-Host (
        'PASS installer public-config clean-install ' +
        'release-payload=byte-identical manifest=byte-identical ' +
        'runtime-settings=three-fields-verified ' +
        'fixture-config=not-used') -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
        try {
            Invoke-InstallerProcess `
                -Path $uninstaller `
                -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') `
                -Operation 'Fallback clean-install cleanup'
        }
        catch {
            Write-Warning $_.Exception.Message
        }
    }

    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if ($resolvedFixture.StartsWith(
            $resolvedTemp,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedFixture).StartsWith(
            'ExamTransfer-PublicConfigInstall-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}
