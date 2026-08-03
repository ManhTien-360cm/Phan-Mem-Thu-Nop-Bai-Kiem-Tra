[CmdletBinding()]
param(
    [string]$ServerDirectory,
    [string]$ClientDirectory,
    [ValidateRange(1, 10)]
    [int]$Repeat = 1,
    [ValidateRange(5, 120)]
    [int]$TimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ServerDirectory)) {
    $ServerDirectory = Join-Path $projectRoot 'artifacts\release\Server'
}
$serverDirectoryPath = (Resolve-Path -LiteralPath $ServerDirectory).Path
$serverExe = Join-Path $serverDirectoryPath 'ExamTransfer.LocalServer.exe'
$manifestPath = Join-Path (Split-Path -Parent $serverDirectoryPath) 'release-manifest.json'
$testClientProject = Join-Path $projectRoot 'backend\tests\ExamTransfer.OnlyLan.TestClient\ExamTransfer.OnlyLan.TestClient.csproj'
$dbMigratorProject = Join-Path $projectRoot 'backend\src\ExamTransfer.DbMigrator\ExamTransfer.DbMigrator.csproj'

foreach ($required in @($serverExe, $manifestPath, $testClientProject, $dbMigratorProject)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required OnlyLAN E2E input was not found: $required"
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.discoveryProtocol -ne 'ExamTransfer/2' -or
    [int]$manifest.discoveryUdpPort -ne 40550 -or
    [string]::IsNullOrWhiteSpace([string]$manifest.buildId)) {
    throw 'Published release manifest has invalid discovery/build identity.'
}
$publishedServerHash = (Get-FileHash -LiteralPath $serverExe -Algorithm SHA256).Hash
if (-not [string]::Equals(
        $publishedServerHash,
        [string]$manifest.server.sha256,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Published Local Server SHA-256 does not match release-manifest.json.'
}

if (-not [string]::IsNullOrWhiteSpace($ClientDirectory)) {
    $clientDirectoryPath = (Resolve-Path -LiteralPath $ClientDirectory).Path
    $candidateRoot = (Split-Path -Parent $serverDirectoryPath)
    if (-not [string]::Equals(
            (Split-Path -Parent $clientDirectoryPath),
            $candidateRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Published frontend and backend must belong to the same candidate root.'
    }

    $clientExe = Join-Path $clientDirectoryPath 'ExamTransfer.Desktop.exe'
    if (-not (Test-Path -LiteralPath $clientExe -PathType Leaf)) {
        throw "Published frontend executable was not found: $clientExe"
    }
    if ($null -eq $manifest.frontend -or
        [string]::IsNullOrWhiteSpace([string]$manifest.frontend.sha256)) {
        throw 'Published release manifest does not contain frontend identity.'
    }
    $publishedClientHash = (Get-FileHash -LiteralPath $clientExe -Algorithm SHA256).Hash
    if (-not [string]::Equals(
            $publishedClientHash,
            [string]$manifest.frontend.sha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Published frontend SHA-256 does not match release-manifest.json.'
    }
}

function Get-PhysicalLanCandidate {
    $virtualPattern = 'VMware|VMnet|Hyper-V|vEthernet|WSL|Docker|VPN|TAP|TUN|Loopback|Virtual'
    $interfaceMetrics = @{}
    Get-NetIPInterface -AddressFamily IPv4 -ConnectionState Connected -ErrorAction Stop |
        ForEach-Object { $interfaceMetrics[[int]$_.InterfaceIndex] = [int]$_.InterfaceMetric }
    $candidate = Get-NetIPConfiguration -ErrorAction Stop |
        Where-Object {
            $_.NetAdapter.Status -eq 'Up' -and
            $_.IPv4Address -and
            $_.IPv4DefaultGateway -and
            ($_.InterfaceAlias + ' ' + $_.NetAdapter.InterfaceDescription) -notmatch $virtualPattern -and
            $_.IPv4Address.IPAddress -notlike '169.254.*'
        } |
        Sort-Object `
            @{ Expression = { if ($_.NetAdapter.NdisPhysicalMedium -match 'Wireless') { 0 } else { 1 } } }, `
            @{ Expression = { $interfaceMetrics[[int]$_.InterfaceIndex] } } |
        Select-Object -First 1
    if ($null -eq $candidate) {
        throw 'No active physical Wi-Fi/Ethernet IPv4 interface with a gateway is available.'
    }
    return $candidate
}

function Get-DirectedBroadcast([Net.IPAddress]$Address, [int]$Prefix) {
    if ($Prefix -lt 1 -or $Prefix -gt 30) {
        throw "Physical interface prefix length cannot produce a directed broadcast: /$Prefix"
    }
    $bytes = $Address.GetAddressBytes()
    [uint64]$value = ([uint64]$bytes[0] -shl 24) -bor
        ([uint64]$bytes[1] -shl 16) -bor
        ([uint64]$bytes[2] -shl 8) -bor
        [uint64]$bytes[3]
    [uint64]$allIpv4Bits = 4294967295
    [uint64]$mask = (($allIpv4Bits -shl (32 - $Prefix)) -band $allIpv4Bits)
    [uint64]$broadcast = (($value -band $mask) -bor ((-bnot $mask) -band $allIpv4Bits))
    return [Net.IPAddress]::new([byte[]]@(
        [byte](($broadcast -shr 24) -band 255),
        [byte](($broadcast -shr 16) -band 255),
        [byte](($broadcast -shr 8) -band 255),
        [byte]($broadcast -band 255)))
}

function Invoke-ExamApi {
    param(
        [Parameter(Mandatory)] [string]$Method,
        [Parameter(Mandatory)] [string]$Uri,
        [hashtable]$Headers,
        [object]$Body,
        [int]$TimeoutSec = 10
    )
    $invoke = @{
        Method = $Method
        Uri = $Uri
        TimeoutSec = $TimeoutSec
    }
    if ($null -ne $Headers) { $invoke['Headers'] = $Headers }
    if ($PSBoundParameters.ContainsKey('Body')) {
        $invoke['ContentType'] = 'application/json'
        $invoke['Body'] = $Body | ConvertTo-Json -Depth 12 -Compress
    }
    try {
        return Invoke-RestMethod @invoke
    } catch {
        $detail = if ($null -ne $_.ErrorDetails) { [string]$_.ErrorDetails.Message } else { [string]$_.Exception.Message }
        $detail = $detail `
            -replace '(?i)("?(?:accessToken|refreshToken|password|authorization|apikey)"?\s*[:=]\s*")[^"]+', '$1<redacted>' `
            -replace '(?i)Bearer\s+[A-Za-z0-9._~-]+', 'Bearer <redacted>'
        if ($detail.Length -gt 1200) { $detail = $detail.Substring(0, 1200) }
        throw "API request failed method=$Method uri=$Uri detail=$detail"
    }
}

function Wait-ServerReady {
    param([Diagnostics.Process]$Process, [string]$BuildId)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "Published Local Server exited before health was ready. ExitCode=$($Process.ExitCode)"
        }
        try {
            $health = Invoke-RestMethod -Uri 'http://127.0.0.1:5048/health' -TimeoutSec 2
            if ($health.backendRuntime.code -eq 'BACKEND_RUNTIME_READY' -and
                $health.udpDiscovery.code -eq 'UDP_DISCOVERY_LISTENING' -and
                $health.buildId -eq $BuildId -and
                $health.protocol -eq 'ExamTransfer/2' -and
                [int]$health.discoveryPort -eq 40550) {
                return $health
            }
        } catch {
            # Retry until the bounded deadline.
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'Published Local Server did not report both HTTP and UDP readiness.'
}

function Assert-UdpDiscovery {
    param(
        [Net.IPAddress]$LocalAddress,
        [Net.IPAddress]$BroadcastAddress,
        [string]$BuildId,
        [string]$ExpectedRoom)
    $requestId = [Guid]::NewGuid().ToString('N')
    $requestBytes = [Text.Encoding]::UTF8.GetBytes((@{
        protocol = 'ExamTransfer/2'
        requestId = $requestId
        roomCode = $ExpectedRoom
    } | ConvertTo-Json -Compress))
    $udp = [Net.Sockets.UdpClient]::new([Net.Sockets.AddressFamily]::InterNetwork)
    try {
        $udp.EnableBroadcast = $true
        $udp.Client.Bind([Net.IPEndPoint]::new($LocalAddress, 0))
        $udp.Client.ReceiveTimeout = $TimeoutSeconds * 1000
        [void]$udp.Send($requestBytes, $requestBytes.Length, [Net.IPEndPoint]::new($BroadcastAddress, 40550))
        [void]$udp.Send($requestBytes, $requestBytes.Length, [Net.IPEndPoint]::new($LocalAddress, 40550))
        $remote = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
        $payload = $udp.Receive([ref]$remote)
    } finally {
        $udp.Dispose()
    }
    $discovery = [Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json
    if ($discovery.protocol -ne 'ExamTransfer/2' -or
        $discovery.requestId -ne $requestId -or
        $discovery.buildId -ne $BuildId -or
        $discovery.address -ne $LocalAddress.ToString()) {
        throw 'Published UDP discovery response failed protocol/nonce/address validation.'
    }
    $room = @($discovery.sessions | Where-Object {
        $_.roomCode -eq $ExpectedRoom -and
        ([string]$_.sessionState -eq 'Waiting' -or [string]$_.sessionState -eq '1') -and
        ([string]$_.accessMode -eq 'LanOnly' -or [string]$_.accessMode -eq '0')
    })
    if ($room.Count -ne 1) {
        throw "Published UDP discovery did not return exactly one $ExpectedRoom room."
    }
}

$candidate = Get-PhysicalLanCandidate
$localAddress = [Net.IPAddress]::Parse([string]$candidate.IPv4Address.IPAddress)
$broadcastAddress = Get-DirectedBroadcast $localAddress ([int]$candidate.IPv4Address.PrefixLength)
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$testTokenSigningKey = "onlylan-e2e-$([Guid]::NewGuid().ToString('N'))-$([Guid]::NewGuid().ToString('N'))"
$runRoot = Join-Path $projectRoot "artifacts\onlylan-e2e\$timestamp"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

for ($iteration = 1; $iteration -le $Repeat; $iteration++) {
    $tcpOwner = Get-NetTCPConnection -LocalPort 5048 -State Listen -ErrorAction SilentlyContinue
    $udpOwner = Get-NetUDPEndpoint -LocalPort 40550 -ErrorAction SilentlyContinue
    if ($tcpOwner -or $udpOwner) {
        throw "OnlyLAN E2E iteration $iteration requires TCP 5048 and UDP 40550 to be free."
    }

    $iterationRoot = Join-Path $runRoot ("run-{0:D2}" -f $iteration)
    $storageRoot = Join-Path $iterationRoot 'storage'
    $handoffFile = Join-Path $iterationRoot 'fixture-handoff.tmp.json'
    $stdoutLog = Join-Path $iterationRoot 'server.stdout.log'
    $stderrLog = Join-Path $iterationRoot 'server.stderr.log'
    New-Item -ItemType Directory -Path $storageRoot -Force | Out-Null

    $savedEnvironment = @{}
    $environmentUpdates = @{
        'DOTNET_ENVIRONMENT' = 'Testing'
        'EXAMTRANSFER_ALLOW_TEST_FIXTURE' = '1'
        'Storage__RootPath' = $storageRoot
        'EXAMTRANSFER_Storage__RootPath' = $storageRoot
        'Cloud__Enabled' = 'false'
        'EXAMTRANSFER_Cloud__Enabled' = 'false'
        'Security__TokenSigningKey' = $testTokenSigningKey
        'EXAMTRANSFER_Security__TokenSigningKey' = $testTokenSigningKey
        'Server__Port' = '5048'
        'Server__PreferredIp' = $localAddress.ToString()
        'Discovery__Enabled' = 'true'
        'Discovery__Port' = '40550'
    }
    foreach ($name in $environmentUpdates.Keys) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environmentUpdates[$name], 'Process')
    }

    $serverProcess = $null
    try {
        & dotnet run `
            --project $dbMigratorProject `
            -c Release `
            --no-build `
            -- `
            --seed-onlylan-e2e-fixture `
            --handoff-file $handoffFile
        if ($LASTEXITCODE -ne 0) { throw 'OnlyLAN E2E fixture seeding failed.' }
        if (-not (Test-Path -LiteralPath $handoffFile -PathType Leaf)) {
            throw 'OnlyLAN E2E fixture did not create its protected handoff file.'
        }
        $handoff = Get-Content -LiteralPath $handoffFile -Raw | ConvertFrom-Json
        foreach ($name in @('teacherAccountToken', 'studentAccountToken', 'sessionId', 'roomCode', 'studentCode', 'studentDeviceId')) {
            if ([string]::IsNullOrWhiteSpace([string]$handoff.$name)) {
                throw "OnlyLAN E2E handoff is missing $name."
            }
        }

        $serverProcess = Start-Process `
            -FilePath $serverExe `
            -WorkingDirectory $serverDirectoryPath `
            -WindowStyle Hidden `
            -PassThru `
            -RedirectStandardOutput $stdoutLog `
            -RedirectStandardError $stderrLog
        [void](Wait-ServerReady $serverProcess ([string]$manifest.buildId))

        $baseUrl = "http://$localAddress`:5048"
        Assert-UdpDiscovery $localAddress $broadcastAddress ([string]$manifest.buildId) ([string]$handoff.roomCode)
        Write-Host "PASS LAN_E2E_DISCOVERY iteration=$iteration" -ForegroundColor Green

        $studentHeaders = @{ Authorization = "Bearer $($handoff.studentAccountToken)" }
        $teacherHeaders = @{ Authorization = "Bearer $($handoff.teacherAccountToken)" }
        $join = Invoke-ExamApi -Method Post -Uri "$baseUrl/api/v1/sessions/join" -Headers $studentHeaders -Body @{
            roomCode = [string]$handoff.roomCode
            studentCode = [string]$handoff.studentCode
            displayName = [string]$handoff.studentDisplayName
            className = $null
            deviceId = [string]$handoff.studentDeviceId
            machineName = [Environment]::MachineName
            appVersion = [string]$manifest.semanticVersion
            nonce = [Guid]::NewGuid().ToString('N')
        }
        if (-not $join.success -or
            [string]$join.data.status -ne 'PendingApproval' -or
            [string]::IsNullOrWhiteSpace([string]$join.data.accessToken)) {
            throw 'OnlyLAN join did not return PendingApproval plus participant token.'
        }
        $participantId = [Guid]$join.data.participantId
        $participantToken = [string]$join.data.accessToken
        Write-Host "PASS LAN_E2E_JOIN_PENDING iteration=$iteration" -ForegroundColor Green

        $approve = Invoke-ExamApi -Method Post `
            -Uri "$baseUrl/api/v1/sessions/$($handoff.sessionId)/participants/$participantId/approve" `
            -Headers $teacherHeaders `
            -Body @{ mutationRequestId = [Guid]::NewGuid() }
        if (-not $approve.success -or [string]$approve.data.status -ne 'Approved') {
            throw 'Teacher approval did not persist Approved status.'
        }
        Write-Host "PASS LAN_E2E_APPROVED iteration=$iteration" -ForegroundColor Green

        $dualHeaders = @{
            Authorization = "Bearer $($handoff.studentAccountToken)"
            'X-Exam-Session-Token' = $participantToken
        }
        [void](Invoke-ExamApi -Method Post `
            -Uri "$baseUrl/api/v1/sessions/$($handoff.sessionId)/participants/$participantId/heartbeat" `
            -Headers $dualHeaders `
            -Body @{
                deviceStatus = 'Ready'
                clientNowUtc = [DateTimeOffset]::UtcNow.ToString('O')
                sequenceAck = 0
            })

        $policy = Invoke-ExamApi -Method Put `
            -Uri "$baseUrl/api/v1/sessions/$($handoff.sessionId)/control-policy" `
            -Headers $teacherHeaders `
            -Body @{
                fullscreen = $true
                focusRule = 'BlockFocusLoss'
                clipboardRule = 'Block'
                allowedProcesses = @()
                blockedProcesses = @()
                networkRule = 'LanOnly'
                emergencyExit = $true
                ttlMinutes = 60
                rowVersion = $null
            }
        if (-not $policy.success -or [int]$policy.data.version -le 0) {
            throw 'Teacher policy save did not return a positive version.'
        }
        $policyVersion = [int]$policy.data.version
        [void](Invoke-ExamApi -Method Post `
            -Uri "$baseUrl/api/v1/sessions/$($handoff.sessionId)/control-policy/apply" `
            -Headers $teacherHeaders `
            -Body @{ participantIds = @($participantId) })

        & dotnet run `
            --project $testClientProject `
            -c Release `
            --no-build `
            -- `
            --base-url $baseUrl `
            --participant-token $participantToken `
            --policy-version $policyVersion `
            --timeout-seconds $TimeoutSeconds
        if ($LASTEXITCODE -ne 0) { throw 'SignalR policy acknowledgement failed.' }

        $deviceStatus = Invoke-ExamApi -Method Get `
            -Uri "$baseUrl/api/v1/sessions/$($handoff.sessionId)/devices/control-status" `
            -Headers $teacherHeaders
        $applied = @($deviceStatus.data | Where-Object {
            [string]$_.participantId -eq [string]$participantId -and
            [int]$_.policyVersion -eq $policyVersion -and
            ([string]$_.status -eq 'Applied' -or [string]$_.status -eq '2')
        })
        if ($applied.Count -ne 1) { throw 'Teacher did not observe Applied policy status.' }
        Write-Host "PASS LAN_E2E_POLICY_APPLIED iteration=$iteration" -ForegroundColor Green

        $started = Invoke-ExamApi -Method Post `
            -Uri "$baseUrl/api/v1/sessions/$($handoff.sessionId)/start" `
            -Headers $teacherHeaders
        if (-not $started.success -or [string]$started.data.summary.status -ne 'InProgress') {
            throw 'Teacher start did not move the session to InProgress.'
        }

        $attempt = Invoke-ExamApi -Method Post `
            -Uri "$baseUrl/api/v1/student/quiz/sessions/$($handoff.sessionId)/attempt" `
            -Headers $dualHeaders
        if (-not $attempt.success -or @($attempt.data.questions).Count -ne 2) {
            throw 'Student quiz start did not return the two-question fixture.'
        }
        $attemptId = [Guid]$attempt.data.id
        $answerRows = @()
        foreach ($question in @($attempt.data.questions)) {
            $questionKey = [string]$question.id
            $answerProperty = $handoff.correctAnswers.PSObject.Properties[$questionKey]
            if ($null -eq $answerProperty) {
                throw "Fixture handoff has no correct answer for question $questionKey."
            }
            $answerRows += @{
                questionId = $question.id
                choiceIds = @([string]$answerProperty.Value)
                revision = 1
                clientUpdatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            }
        }
        [void](Invoke-ExamApi -Method Put `
            -Uri "$baseUrl/api/v1/student/quiz/attempts/$attemptId/answers" `
            -Headers $dualHeaders `
            -Body @{ answers = $answerRows })

        $finalizeKey = "onlylan-e2e-$iteration-$([Guid]::NewGuid().ToString('N'))"
        $finalizeBody = @{
            idempotencyKey = $finalizeKey
            clientFinalizedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        }
        $finalized = Invoke-ExamApi -Method Post `
            -Uri "$baseUrl/api/v1/student/quiz/attempts/$attemptId/finalize" `
            -Headers $dualHeaders `
            -Body $finalizeBody
        $repeated = Invoke-ExamApi -Method Post `
            -Uri "$baseUrl/api/v1/student/quiz/attempts/$attemptId/finalize" `
            -Headers $dualHeaders `
            -Body $finalizeBody
        if (-not $finalized.success -or
            [string]$finalized.data.status -ne 'Finalized' -or
            $null -ne $finalized.data.score -or
            [bool]$finalized.data.scoreVisible -or
            [string]$repeated.data.id -ne [string]$finalized.data.id) {
            throw 'Quiz finalize was not successful, student-score-hidden, and idempotent.'
        }

        $teacherAttempts = Invoke-ExamApi -Method Get `
            -Uri "$baseUrl/api/v1/sessions/$($handoff.sessionId)/quiz-attempts" `
            -Headers $teacherHeaders
        $teacherAttemptRows = @($teacherAttempts.data)
        if ($teacherAttemptRows.Count -ne 1 -or
            [string]$teacherAttemptRows[0].status -ne 'Finalized' -or
            [decimal]$teacherAttemptRows[0].score -ne 10.0) {
            throw 'Teacher monitoring did not observe exactly one full-score finalized attempt.'
        }
        Write-Host "PASS LAN_E2E_QUIZ_FINALIZED iteration=$iteration" -ForegroundColor Green

        $collect = Invoke-ExamApi -Method Post `
            -Uri "$baseUrl/api/v1/sessions/$($handoff.sessionId)/collect" `
            -Headers $teacherHeaders
        if ([string]$collect.data.summary.status -ne 'Collecting') {
            throw 'Teacher collect did not move session to Collecting.'
        }
        $ended = Invoke-ExamApi -Method Post `
            -Uri "$baseUrl/api/v1/sessions/$($handoff.sessionId)/end" `
            -Headers $teacherHeaders `
            -Body @{ force = $false; reason = $null }
        if ([string]$ended.data.summary.status -ne 'Finished') {
            throw 'Teacher end did not move session to Finished.'
        }
        $finalSession = Invoke-ExamApi -Method Get `
            -Uri "$baseUrl/api/v1/sessions/$($handoff.sessionId)" `
            -Headers $teacherHeaders
        if ([string]$finalSession.data.summary.status -ne 'Finished') {
            throw 'Finished state did not persist through the read API.'
        }
        Write-Host "PASS LAN_E2E_FINISHED iteration=$iteration" -ForegroundColor Green
        Write-Host "ONLYLAN_E2E_OK iteration=$iteration/$Repeat room=$($handoff.roomCode) buildId=$($manifest.buildId)" -ForegroundColor Green
    } finally {
        if ($serverProcess -and -not $serverProcess.HasExited) {
            Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
            [void]$serverProcess.WaitForExit(5000)
        }
        if (Test-Path -LiteralPath $handoffFile -PathType Leaf) {
            Remove-Item -LiteralPath $handoffFile -Force
        }
        foreach ($name in $environmentUpdates.Keys) {
            [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
        }
    }
}

Write-Host "PASS code=ONLYLAN_E2E_REPEAT_OK repeat=$Repeat artifactRoot=$runRoot" -ForegroundColor Green
