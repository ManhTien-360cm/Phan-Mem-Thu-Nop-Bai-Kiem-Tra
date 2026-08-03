[CmdletBinding()]
param(
    [string]$PreferredIp,
    [int]$InterfaceIndex,
    [string]$AllowedCidr,
    [Alias('EnvFile')]
    [string]$EnvironmentFile,
    [switch]$NonInteractive,
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Convert-IPv4ToUInt32([Net.IPAddress]$Address) {
    $bytes = $Address.GetAddressBytes()
    return ([uint32]$bytes[0] -shl 24) -bor
        ([uint32]$bytes[1] -shl 16) -bor
        ([uint32]$bytes[2] -shl 8) -bor
        [uint32]$bytes[3]
}

function Convert-UInt32ToIPv4([uint32]$Value) {
    return [Net.IPAddress]::new([byte[]]@(
        (($Value -shr 24) -band 255),
        (($Value -shr 16) -band 255),
        (($Value -shr 8) -band 255),
        ($Value -band 255)))
}

function Get-Cidr([Net.IPAddress]$Address, [int]$PrefixLength) {
    if ($PrefixLength -lt 8 -or $PrefixLength -gt 32) {
        throw "Unsafe or invalid LAN prefix length: /$PrefixLength"
    }
    [uint32]$mask = [uint32]::MaxValue -shl (32 - $PrefixLength)
    $network = Convert-UInt32ToIPv4 ((Convert-IPv4ToUInt32 $Address) -band $mask)
    return "$network/$PrefixLength"
}

function Test-IsPrivateIPv4([Net.IPAddress]$Address) {
    $bytes = $Address.GetAddressBytes()
    return $bytes[0] -eq 10 -or
        ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
        ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
}

function Test-IsVirtualAdapter([object]$Adapter) {
    $text = "$($Adapter.InterfaceAlias) $($Adapter.InterfaceDescription)".ToLowerInvariant()
    return [bool](@('docker','hyper-v','vethernet','virtual','virtualbox','vmware','vpn','tap','tun','loopback','wsl') |
        Where-Object { $text.Contains($_) } |
        Select-Object -First 1)
}

function Test-CidrContainsAddress {
    param(
        [Parameter(Mandatory)][string]$Cidr,
        [Parameter(Mandatory)][Net.IPAddress]$Address
    )

    $parts = $Cidr.Split('/')
    [Net.IPAddress]$networkAddress = $null
    [int]$prefix = 0
    if ($parts.Count -ne 2 -or
        -not [Net.IPAddress]::TryParse($parts[0], [ref]$networkAddress) -or
        $networkAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
        -not [int]::TryParse($parts[1], [ref]$prefix) -or
        $prefix -lt 8 -or $prefix -gt 32 -or
        -not (Test-IsPrivateIPv4 $networkAddress)) {
        return $false
    }

    $networkBytes = $networkAddress.GetAddressBytes()
    $minimumPrefix = if ($networkBytes[0] -eq 10) {
        9
    } elseif ($networkBytes[0] -eq 172 -and $networkBytes[1] -ge 16 -and $networkBytes[1] -le 31) {
        13
    } else {
        17
    }
    if ($prefix -lt $minimumPrefix) { return $false }

    [uint32]$mask = [uint32]::MaxValue -shl (32 - $prefix)
    $networkValue = Convert-IPv4ToUInt32 $networkAddress
    $addressValue = Convert-IPv4ToUInt32 $Address
    return (($networkValue -band $mask) -eq ($addressValue -band $mask))
}

function Set-EnvironmentEntry {
    param([string[]]$Lines, [string]$Name, [string]$Value)
    $result = [Collections.Generic.List[string]]::new()
    $found = $false
    foreach ($line in $Lines) {
        if ($line -match "^\s*$([regex]::Escape($Name))=") {
            if (-not $found) {
                $result.Add("$Name=$Value")
                $found = $true
            }
        } else {
            $result.Add($line)
        }
    }
    if (-not $found) { $result.Add("$Name=$Value") }
    return $result.ToArray()
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($EnvironmentFile)) {
    $EnvironmentFile = Join-Path $projectRoot '.env.docker'
}
$EnvironmentFile = [IO.Path]::GetFullPath($EnvironmentFile)

$candidates = @(Get-NetIPConfiguration |
    Where-Object {
        $_.NetAdapter.Status -eq 'Up' -and
        $_.IPv4Address.Count -gt 0 -and
        -not (Test-IsVirtualAdapter $_.NetAdapter)
    } |
    ForEach-Object {
        foreach ($address in $_.IPv4Address) {
            $parsed = [Net.IPAddress]::Parse($address.IPAddress)
            if ((Test-IsPrivateIPv4 $parsed) -and -not [Net.IPAddress]::IsLoopback($parsed)) {
                [pscustomobject]@{
                    InterfaceIndex = $_.InterfaceIndex
                    Alias = $_.InterfaceAlias
                    Description = $_.NetAdapter.InterfaceDescription
                    Address = $parsed
                    PrefixLength = [int]$address.PrefixLength
                    Cidr = Get-Cidr $parsed ([int]$address.PrefixLength)
                }
            }
        }
    })

if ($candidates.Count -eq 0) {
    throw 'No active physical Wi-Fi/Ethernet adapter with a private IPv4 address was found.'
}

Write-Host 'Active physical LAN adapters:' -ForegroundColor Cyan
$candidates | Format-Table InterfaceIndex,Alias,Address,PrefixLength,Cidr -AutoSize

$selected = $null
if (-not [string]::IsNullOrWhiteSpace($PreferredIp)) {
    $selected = $candidates | Where-Object { $_.Address.ToString() -eq $PreferredIp.Trim() } | Select-Object -First 1
    if ($null -eq $selected) { throw "PreferredIp is not assigned to an active physical adapter: $PreferredIp" }
} elseif ($InterfaceIndex -gt 0) {
    $selected = $candidates | Where-Object { $_.InterfaceIndex -eq $InterfaceIndex } | Select-Object -First 1
    if ($null -eq $selected) { throw "InterfaceIndex is not an eligible physical LAN adapter: $InterfaceIndex" }
} elseif ($candidates.Count -eq 1) {
    $selected = $candidates[0]
} elseif ($NonInteractive) {
    throw 'Multiple LAN adapters are active. Specify -PreferredIp or -InterfaceIndex.'
} else {
    $choice = Read-Host 'Enter the InterfaceIndex to advertise'
    if (-not [int]::TryParse($choice, [ref]$InterfaceIndex)) { throw 'InterfaceIndex must be an integer.' }
    $selected = $candidates | Where-Object { $_.InterfaceIndex -eq $InterfaceIndex } | Select-Object -First 1
    if ($null -eq $selected) { throw "InterfaceIndex was not found: $InterfaceIndex" }
}

$effectiveCidr = if ([string]::IsNullOrWhiteSpace($AllowedCidr)) {
    $selected.Cidr
} else {
    $AllowedCidr.Trim()
}
if (-not (Test-CidrContainsAddress -Cidr $effectiveCidr -Address $selected.Address)) {
    throw "AllowedCidr must be a narrowly scoped private IPv4 CIDR containing PreferredIp: ip=$($selected.Address) cidr=$effectiveCidr"
}

if (-not (Test-Path -LiteralPath $EnvironmentFile)) {
    if ($ValidateOnly) { throw "Environment file not found: $EnvironmentFile" }
    $example = Join-Path $projectRoot '.env.docker.example'
    if (-not (Test-Path -LiteralPath $example)) { throw "Environment template not found: $example" }
    Copy-Item -LiteralPath $example -Destination $EnvironmentFile
}

if ($ValidateOnly) {
    $currentLines = Get-Content -LiteralPath $EnvironmentFile
    $currentIpLine = $currentLines | Where-Object { $_ -match '^\s*Server__PreferredIp=' } | Select-Object -Last 1
    $currentCidrLine = $currentLines | Where-Object { $_ -match '^\s*LanAccess__AllowedCidrs__0=' } | Select-Object -Last 1
    $currentIp = if ($null -eq $currentIpLine) { '' } else { ($currentIpLine -split '=', 2)[1].Trim() }
    $currentCidr = if ($null -eq $currentCidrLine) { '' } else { ($currentCidrLine -split '=', 2)[1].Trim() }
    if ($currentIp -cne $selected.Address.ToString()) {
        throw "Server__PreferredIp '$currentIp' is not the selected active physical adapter '$($selected.Address)'."
    }
    if ($currentCidr -cne $effectiveCidr) {
        throw "LanAccess__AllowedCidrs__0 '$currentCidr' does not match validated CIDR '$effectiveCidr'."
    }
    Write-Host "PASS code=DOCKER_LAN_CONFIGURATION_VALID ip=$currentIp cidr=$currentCidr" -ForegroundColor Green
    return
}

$lines = Get-Content -LiteralPath $EnvironmentFile
$lines = Set-EnvironmentEntry $lines 'Server__PreferredIp' $selected.Address.ToString()
$lines = @($lines | Where-Object { $_ -notmatch '^\s*LanAccess__AllowedCidrs__\d+=' })
$lines = Set-EnvironmentEntry $lines 'LanAccess__AllowedCidrs__0' $effectiveCidr
$writeId = [Guid]::NewGuid().ToString('N')
$temporaryEnvironmentFile = "$EnvironmentFile.$writeId.tmp"
$backupEnvironmentFile = "$EnvironmentFile.$writeId.bak"
try {
    [IO.File]::WriteAllLines($temporaryEnvironmentFile, $lines, (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $EnvironmentFile) {
        [IO.File]::Replace($temporaryEnvironmentFile, $EnvironmentFile, $backupEnvironmentFile)
    } else {
        Move-Item -LiteralPath $temporaryEnvironmentFile -Destination $EnvironmentFile
    }
} finally {
    if (Test-Path -LiteralPath $temporaryEnvironmentFile) {
        Remove-Item -LiteralPath $temporaryEnvironmentFile -Force
    }
    if (Test-Path -LiteralPath $backupEnvironmentFile) {
        Remove-Item -LiteralPath $backupEnvironmentFile -Force
    }
}

Write-Host "Configured Server__PreferredIp=$($selected.Address)" -ForegroundColor Green
Write-Host "Configured LanAccess__AllowedCidrs__0=$effectiveCidr" -ForegroundColor Green
Write-Host "Environment file: $EnvironmentFile (secret values were not printed)" -ForegroundColor Yellow

$tcpOwner = Get-NetTCPConnection -LocalPort 5048 -State Listen -ErrorAction SilentlyContinue
$udpOwner = Get-NetUDPEndpoint -LocalPort 40550 -ErrorAction SilentlyContinue
Write-Host "TCP 5048: $(if ($tcpOwner) {'LISTENING/IN USE'} else {'AVAILABLE'})"
Write-Host "UDP 40550: $(if ($udpOwner) {'BOUND/IN USE'} else {'AVAILABLE'})"

try {
    $health = Invoke-RestMethod -Uri 'http://127.0.0.1:5048/health' -TimeoutSec 3
    if ($health.advertisedAddress -ne $selected.Address.ToString()) {
        throw "Running container advertises '$($health.advertisedAddress)' instead of '$($selected.Address)'. Restart it after updating .env.docker."
    }
    Write-Host 'Container health advertises the selected Windows host IP.' -ForegroundColor Green
} catch {
    Write-Warning "Container advertised-IP check was not completed: $($_.Exception.Message)"
}
