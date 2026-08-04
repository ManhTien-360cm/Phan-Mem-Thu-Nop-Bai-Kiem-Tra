param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$frontendProject = Join-Path $root 'frontend\src\ExamTransfer.Desktop\ExamTransfer.Desktop.csproj'
$backendSolution = Join-Path $root 'backend\ExamTransfer.sln'
$backendProject = Join-Path $root 'backend\src\ExamTransfer.LocalServer\ExamTransfer.LocalServer.csproj'
$frontendVerify = Join-Path $root 'frontend\scripts\verify-frontend.ps1'
$installerScript = Join-Path $root 'installer\ExamTransfer.iss'
$releaseRoot = Join-Path $root 'artifacts\release'
$clientOutput = Join-Path $releaseRoot 'Client'
$serverOutput = Join-Path $releaseRoot 'Server'
$publicCloudConfig = Join-Path $clientOutput 'publiccloud.runtime.json'
$releaseManifest = Join-Path $releaseRoot 'release-manifest.json'
$installerOutput = Join-Path $root 'artifacts\installer'
$publicConfigPackaging = Join-Path $root 'scripts\public-config-packaging.ps1'
$publicConfigPackagingTests = Join-Path $root 'scripts\test-public-config-packaging.ps1'
$dockerPackagingContractTests = Join-Path $root 'scripts\test-docker-packaging-contract.ps1'
$installerGuardTests = Join-Path $root 'scripts\test-installer-localserver-guard.ps1'
$installerCleanInstallTest = Join-Path $root 'scripts\test-installer-public-config-clean-install.ps1'
$installerMetadataHelper = Join-Path $root 'scripts\installer-version-metadata.ps1'
$installerMetadataTests = Join-Path $root 'scripts\test-installer-version-metadata.ps1'
$releaseArtifactValidator = Join-Path $root 'scripts\validate-release-artifacts.ps1'

function Require-File([string]$Path) {
    if (-not (Test-Path $Path -PathType Leaf)) {
        throw "Required file was not found: $Path"
    }
}

function Find-InnoCompiler {
    $candidates = @(@(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LocalAppData 'Programs\Inno Setup 6\ISCC.exe')
        ) | Where-Object { $_ -and (Test-Path $_ -PathType Leaf) })

    if ($candidates.Count -eq 0) {
        throw 'Inno Setup 6 was not found. Install Inno Setup 6 and retry.'
    }

    return $candidates[0]
}

Require-File $frontendProject
Require-File $backendSolution
Require-File $backendProject
Require-File $frontendVerify
Require-File $installerScript
Require-File $publicConfigPackaging
Require-File $publicConfigPackagingTests
Require-File $dockerPackagingContractTests
Require-File $installerGuardTests
Require-File $installerCleanInstallTest
Require-File $installerMetadataHelper
Require-File $installerMetadataTests
Require-File $releaseArtifactValidator
. $publicConfigPackaging
. $installerMetadataHelper

# Validate and normalize the one authoritative release version before any
# restore, publish, artifact cleanup, or ISCC work begins.
$assemblyVersion = ConvertTo-WindowsNumericVersion -SemanticVersion $Version

$publicCloudUrl = [string]$env:EXAMTRANSFER_SUPABASE_URL
$publicCloudKey = [string]$env:EXAMTRANSFER_SUPABASE_PUBLISHABLE_KEY
$publicCloudOrganizationId = [string]$env:EXAMTRANSFER_ORGANIZATION_ID
$expectedPublicCloudConfig = New-PublicCloudConfig `
    -SupabaseUrl $publicCloudUrl `
    -PublishableKey $publicCloudKey `
    -OrganizationId $publicCloudOrganizationId

# These subprocess cases prove every invalid public-config input exits before
# release work or ISCC. Keep this before dotnet discovery and artifact cleanup.
& powershell `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $publicConfigPackagingTests
if ($LASTEXITCODE -ne 0) {
    throw 'Public-config packaging preflight tests failed before release creation.'
}

& powershell `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $dockerPackagingContractTests
if ($LASTEXITCODE -ne 0) {
    throw 'Docker packaging contract tests failed before release creation.'
}

& powershell `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $installerMetadataTests
if ($LASTEXITCODE -ne 0) {
    throw 'Installer version metadata producer tests failed before release creation.'
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw 'dotnet was not found. Install .NET SDK 10.'
}

Write-Host "=== ExamTransfer release $Version ===" -ForegroundColor Cyan
Write-Host "Project root: $root"
dotnet --version

$gitCommit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or -not $gitCommit) {
    throw 'Unable to resolve the Git commit for release identity.'
}
$workingTreeDirty = [bool](& git -C $root status --porcelain)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to determine whether the Git working tree is dirty.'
}
$dirtyMarker = if ($workingTreeDirty) { 'dirty' } else { 'clean' }
$buildTimestamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$buildId = "$Version+$($gitCommit.Substring(0, 8))-$dirtyMarker.$buildTimestamp"
Write-Host "Build ID: $buildId"

foreach ($publishOutput in @($clientOutput, $serverOutput)) {
    if (Test-Path $publishOutput -PathType Container) {
        Remove-Item -LiteralPath $publishOutput -Recurse -Force
    }
}
if (Test-Path $releaseManifest -PathType Leaf) {
    Remove-Item -LiteralPath $releaseManifest -Force
}
$installer = Join-Path $installerOutput "ExamTransfer-Setup-$Version.exe"
$installerHashFile = "$installer.sha256.txt"
foreach ($oldInstallerOutput in @($installer, $installerHashFile)) {
    if (Test-Path $oldInstallerOutput -PathType Leaf) {
        Remove-Item -LiteralPath $oldInstallerOutput -Force
    }
}
New-Item -ItemType Directory -Path $clientOutput -Force | Out-Null
New-Item -ItemType Directory -Path $serverOutput -Force | Out-Null
New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null

Write-Host "\n[1/8] Restore backend..." -ForegroundColor Yellow
dotnet restore $backendSolution
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

if (-not $SkipTests) {
    Write-Host "\n[2/8] Test backend Release..." -ForegroundColor Yellow
    dotnet test $backendSolution -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Backend tests failed. Release creation stopped.' }

    Write-Host "\n[3/8] Verify frontend..." -ForegroundColor Yellow
    powershell -ExecutionPolicy Bypass -File $frontendVerify -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Frontend verification failed. Release creation stopped.' }
}
else {
    Write-Warning 'Tests were skipped by -SkipTests. Do not use this option for an official release.'
}

Write-Host "\n[4/8] Publish frontend WPF..." -ForegroundColor Yellow
dotnet publish $frontendProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$assemblyVersion `
    -p:FileVersion=$assemblyVersion `
    -p:ExamTransferSemanticVersion=$Version `
    -p:ExamTransferBuildId=$buildId `
    -p:ExamTransferGitCommit=$gitCommit `
    -p:ExamTransferWorkingTreeDirty=$($workingTreeDirty.ToString().ToLowerInvariant()) `
    -o $clientOutput
if ($LASTEXITCODE -ne 0) { throw 'Frontend publish failed.' }

Write-PublicCloudConfig `
    -Path $publicCloudConfig `
    -Config $expectedPublicCloudConfig
Require-File $publicCloudConfig
$verifiedPublicCloudConfig = Read-PublicCloudConfig -Path $publicCloudConfig
Assert-PublicCloudConfigEqual `
    -Expected $expectedPublicCloudConfig `
    -Actual $verifiedPublicCloudConfig `
    -Stage 'generated-release-payload'
$publicCloudConfigHash = (Get-FileHash $publicCloudConfig -Algorithm SHA256).Hash

Write-Host "\n[5/8] Publish Local Server..." -ForegroundColor Yellow
dotnet publish $backendProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$assemblyVersion `
    -p:FileVersion=$assemblyVersion `
    -p:ExamTransferSemanticVersion=$Version `
    -p:ExamTransferBuildId=$buildId `
    -p:ExamTransferGitCommit=$gitCommit `
    -p:ExamTransferWorkingTreeDirty=$($workingTreeDirty.ToString().ToLowerInvariant()) `
    -o $serverOutput
if ($LASTEXITCODE -ne 0) { throw 'Local Server publish failed.' }

Require-File (Join-Path $clientOutput 'ExamTransfer.Desktop.exe')
Require-File (Join-Path $serverOutput 'ExamTransfer.LocalServer.exe')

$clientHash = (Get-FileHash (Join-Path $clientOutput 'ExamTransfer.Desktop.exe') -Algorithm SHA256).Hash
$serverHash = (Get-FileHash (Join-Path $serverOutput 'ExamTransfer.LocalServer.exe') -Algorithm SHA256).Hash
$manifest = [ordered]@{
    semanticVersion   = $Version
    buildId           = $buildId
    gitCommit         = $gitCommit
    workingTreeDirty  = $workingTreeDirty
    buildTimestampUtc = $buildTimestamp
    discoveryProtocol = 'ExamTransfer/2'
    discoveryUdpPort  = 40550
    client            = [ordered]@{
        file   = 'Client/ExamTransfer.Desktop.exe'
        sha256 = $clientHash
    }
    server            = [ordered]@{
        file   = 'Server/ExamTransfer.LocalServer.exe'
        sha256 = $serverHash
    }
    publicCloudConfig = [ordered]@{
        file           = 'Client/publiccloud.runtime.json'
        sha256         = $publicCloudConfigHash
        classification = 'publishable-client-config'
    }
    installer         = [ordered]@{
        file           = "ExamTransfer-Setup-$Version.exe"
        fileVersion    = $assemblyVersion
        productVersion = $Version
        productName    = 'ExamTransfer'
    }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $releaseManifest -Encoding utf8
Copy-Item -LiteralPath $releaseManifest -Destination (Join-Path $clientOutput 'release-manifest.json')
Copy-Item -LiteralPath $releaseManifest -Destination (Join-Path $serverOutput 'release-manifest.json')
Require-File $releaseManifest

Write-Host "\n[6/8] Verify installer guard with release public config..." -ForegroundColor Yellow
& powershell `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $installerGuardTests `
    -ReleaseRoot $releaseRoot
if ($LASTEXITCODE -ne 0) {
    throw 'Installer guard tests failed with the generated release public config.'
}

Write-Host "\n[7/8] Build installer..." -ForegroundColor Yellow
$iscc = Find-InnoCompiler
& $iscc "/DMyAppVersion=$Version" $installerScript
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

Require-File $installer

$installerMetadata = Assert-InstallerVersionMetadata `
    -InstallerPath $installer `
    -ExpectedVersion $Version
Write-Host "Installer FileVersion: $($installerMetadata.FileVersionRaw)"
Write-Host "Installer ProductVersion: $($installerMetadata.ProductVersionRaw)"

$postIsccPublicCloudConfig = Read-PublicCloudConfig -Path $publicCloudConfig
Assert-PublicCloudConfigEqual `
    -Expected $expectedPublicCloudConfig `
    -Actual $postIsccPublicCloudConfig `
    -Stage 'post-ISCC-release-payload'
$postIsccPublicCloudConfigHash = (
    Get-FileHash $publicCloudConfig -Algorithm SHA256).Hash
if (-not [string]::Equals(
        $publicCloudConfigHash,
        $postIsccPublicCloudConfigHash,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PUBLICCLOUD_CONFIG_CHANGED_DURING_ISCC: release payload hash changed.'
}

Write-Host "\n[8/8] Clean-install packaged public config..." -ForegroundColor Yellow
& powershell `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $installerCleanInstallTest `
    -Version $Version `
    -ReleaseRoot $releaseRoot
if ($LASTEXITCODE -ne 0) {
    throw 'Installer clean-install public-config acceptance failed.'
}

$finalPublicCloudConfig = Read-PublicCloudConfig -Path $publicCloudConfig
Assert-PublicCloudConfigEqual `
    -Expected $expectedPublicCloudConfig `
    -Actual $finalPublicCloudConfig `
    -Stage 'post-clean-install-release-payload'

$hash = Get-FileHash $installer -Algorithm SHA256
"$($hash.Hash)  $([IO.Path]::GetFileName($installer))" | Set-Content -Path $installerHashFile -Encoding ascii

& powershell `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $releaseArtifactValidator `
    -ExpectedVersion $Version `
    -ExpectedHead $gitCommit `
    -ExpectedBuildId $buildId `
    -RepositoryRoot $root `
    -ReleaseRoot $releaseRoot `
    -InstallerPath $installer `
    -HashFilePath $installerHashFile
if ($LASTEXITCODE -ne 0) {
    throw 'Release artifact identity validation failed.'
}

Write-Host "\nBUILD SUCCEEDED" -ForegroundColor Green
Write-Host "Installer : $installer"
Write-Host "SHA-256  : $($hash.Hash)"
Write-Host "Hash file: $installerHashFile"
Write-Host "Manifest : $releaseManifest"
