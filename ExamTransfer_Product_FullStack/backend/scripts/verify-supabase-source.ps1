param(
    [string]$BackendRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$requiredMigrations = @(
    "202607110001_initial_schema.sql",
    "202607140002_full_frontend_backend_schema.sql",
    "202607150003_security_hardening.sql",
    "202607150004_cloud_bootstrap_helpers.sql",
    "202607150005_user_session_storage_policies.sql",
    "202607150006_production_readiness.sql",
    "20260722141147_public_classes_device_control.sql",
    "20260722161450_public_cloud_completion_v2.sql"
)

foreach ($migration in $requiredMigrations) {
    $path = Join-Path $BackendRoot "supabase\migrations\$migration"
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing Supabase migration: $path"
    }
}

$adapter = Get-Content -LiteralPath (
    Join-Path $BackendRoot "src\ExamTransfer.Infrastructure\Cloud\SupabaseCloudAdapter.cs") -Raw
$verifyFunction = Get-Content -LiteralPath (Join-Path $BackendRoot "supabase\functions\verify-public-submission-archive\index.ts") -Raw
$downloadFunction = Get-Content -LiteralPath (Join-Path $BackendRoot "supabase\functions\get-public-exam-file-url\index.ts") -Raw
$commandFunction = Get-Content -LiteralPath (Join-Path $BackendRoot "supabase\functions\issue-public-device-command\index.ts") -Raw
if ($verifyFunction -match 'rest/v1/submission_files\?id=.*method:\s*"PATCH"') {
    throw "Archive verifier must call the service-only RPC instead of PATCHing submission_files."
}
foreach ($requiredText in @('verify_public_submission_archive','get_public_exam_file_download','CloudSchemaCompatibility.RequiredVersion')) {
    $allSource = $adapter + $verifyFunction + $downloadFunction + (Get-Content -LiteralPath (Join-Path $BackendRoot "supabase\migrations\20260722161450_public_cloud_completion_v2.sql") -Raw)
    if ($allSource -notmatch [Regex]::Escape($requiredText)) { throw "Missing PublicCloud completion capability: $requiredText" }
}

foreach ($entry in @(
    @{ Name = 'verify-public-submission-archive'; Source = $verifyFunction },
    @{ Name = 'get-public-exam-file-url'; Source = $downloadFunction },
    @{ Name = 'issue-public-device-command'; Source = $commandFunction })) {
    if ($entry.Source -notmatch 'request\.method\s*===\s*"OPTIONS"' -or
        $entry.Source -notmatch 'access-control-allow-origin') {
        throw "Edge Function is missing CORS preflight handling: $($entry.Name)"
    }
}
if ($commandFunction -match 'COMMAND_REJECTED[\s\S]{0,120}\bdetail\b') {
    throw 'Device command function exposes a backend rejection detail to the client.'
}

$duplicateSupabase = Join-Path (Split-Path $BackendRoot -Parent) "database\supabase"
if (Test-Path -LiteralPath $duplicateSupabase) {
    $legacyFiles = @(Get-ChildItem -LiteralPath $duplicateSupabase -Recurse -File |
        Where-Object { $_.Name -ne 'README.md' })
    if ($legacyFiles.Count -gt 0) {
        throw "Duplicate Supabase migration source detected: $($legacyFiles.FullName -join ', ')"
    }
}

foreach ($requiredText in @(
    "UserSession",
    "TrustedServer",
    "upload/resumable",
    "RefreshSessionAsync",
    "organization_id",
    "Admin",
    "Teacher")) {
    if ($adapter -notmatch [Regex]::Escape($requiredText)) {
        throw "Supabase adapter is missing required capability: $requiredText"
    }
}

Write-Host "Supabase source verification passed." -ForegroundColor Green
