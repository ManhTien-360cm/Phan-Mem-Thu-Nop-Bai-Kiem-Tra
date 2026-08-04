function ConvertTo-WindowsNumericVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$SemanticVersion
    )

    if ([string]::IsNullOrWhiteSpace($SemanticVersion) -or
        $SemanticVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "Release version must contain exactly three numeric components: $SemanticVersion"
    }

    try {
        $parsed = [Version]::Parse($SemanticVersion)
    }
    catch {
        throw "Release version is not a valid Windows numeric version: $SemanticVersion"
    }

    foreach ($component in @($parsed.Major, $parsed.Minor, $parsed.Build)) {
        if ($component -lt 0 -or $component -gt 65535) {
            throw "Each Windows version component must be between 0 and 65535: $SemanticVersion"
        }
    }

    return '{0}.{1}.{2}.0' -f $parsed.Major, $parsed.Minor, $parsed.Build
}

function ConvertFrom-InstallerVersionText {
    [CmdletBinding()]
    param(
        [AllowEmptyString()]
        [string]$Value,

        [Parameter(Mandatory = $true)]
        [string]$MetadataName
    )

    $trimmed = ([string]$Value).Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        throw "Installer $MetadataName is empty."
    }

    try {
        $parsed = [Version]::Parse($trimmed)
    }
    catch {
        throw "Installer $MetadataName is not numeric: $trimmed"
    }

    if ($parsed.Build -lt 0) {
        throw "Installer $MetadataName must contain at least three components: $trimmed"
    }

    if ($parsed.Revision -gt 0) {
        return '{0}.{1}.{2}.{3}' -f `
            $parsed.Major, $parsed.Minor, $parsed.Build, $parsed.Revision
    }

    return '{0}.{1}.{2}' -f $parsed.Major, $parsed.Minor, $parsed.Build
}

function Assert-InstallerVersionMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallerPath,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion
    )

    if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
        throw "Installer was not found: $InstallerPath"
    }

    $expectedWindowsVersion = ConvertTo-WindowsNumericVersion `
        -SemanticVersion $ExpectedVersion
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($InstallerPath)
    $fileVersionRaw = ([string]$versionInfo.FileVersion).Trim()
    $productVersionRaw = ([string]$versionInfo.ProductVersion).Trim()
    $fileVersionNormalized = ConvertFrom-InstallerVersionText `
        -Value $fileVersionRaw `
        -MetadataName 'FileVersion'
    $productVersionNormalized = ConvertFrom-InstallerVersionText `
        -Value $productVersionRaw `
        -MetadataName 'ProductVersion'

    if (-not [string]::Equals(
            $fileVersionNormalized,
            $ExpectedVersion,
            [StringComparison]::Ordinal)) {
        throw (
            "Installer FileVersion mismatch: expected=$ExpectedVersion " +
            "actual=$fileVersionRaw normalized=$fileVersionNormalized")
    }
    if (-not [string]::Equals(
            $productVersionNormalized,
            $ExpectedVersion,
            [StringComparison]::Ordinal)) {
        throw (
            "Installer ProductVersion mismatch: expected=$ExpectedVersion " +
            "actual=$productVersionRaw normalized=$productVersionNormalized")
    }

    return [pscustomobject]@{
        FileVersionRaw           = $fileVersionRaw
        FileVersionNormalized    = $fileVersionNormalized
        ProductVersionRaw        = $productVersionRaw
        ProductVersionNormalized = $productVersionNormalized
        ProductName              = ([string]$versionInfo.ProductName).Trim()
        WindowsVersionExpected   = $expectedWindowsVersion
    }
}
