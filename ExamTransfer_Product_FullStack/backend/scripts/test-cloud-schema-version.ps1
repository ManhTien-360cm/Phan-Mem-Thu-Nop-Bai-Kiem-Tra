param(
    [Parameter(Mandatory)][string]$SupabaseUrl,
    [Parameter(Mandatory)][string]$PublishableKey,
    [Parameter(Mandatory)][string]$TeacherOrServiceJwt
)
. "$PSScriptRoot/acceptance-common.ps1"
$traceId = New-AcceptanceTraceId
try {
    $headers = @{ apikey=$PublishableKey; Authorization="Bearer $TeacherOrServiceJwt"; 'Content-Type'='application/json' }
    $result = Invoke-RestMethod -Method Post -Uri "$($SupabaseUrl.TrimEnd('/'))/rest/v1/rpc/get_examtransfer_cloud_capabilities" -Headers $headers -Body '{}'
    $required = @(
        'join_public_session','join_open_public_session_by_room_code',
        'init_public_submission','finalize_public_submission',
        'upsert_public_device_heartbeat','ack_public_device_command','report_public_violation',
        'start_public_quiz_attempt','save_public_quiz_answers','finalize_public_quiz_attempt',
        'get_public_quiz_attempt','get_public_quiz_attempt_review','get_teacher_quiz_attempts',
        'save_public_quiz_grade','return_public_quiz_grade','reopen_public_quiz_grade',
        'verify_public_submission_archive','get_public_exam_manifest',
        'get_public_exam_file_download',
        'approve_public_participant','reject_public_participant',
        'bulk_approve_public_participants','add_public_participant_extra_time',
        'allow_public_resubmission','reject_public_submission',
        'approve_public_enrollment_request','reject_public_enrollment_request',
        'get_public_student_timeline'
    )
    if ([int]$result.schemaVersion -ne 25) { throw "Expected schema 25; received $($result.schemaVersion)." }
    foreach ($rpc in $required) { if ($result.criticalRpcs -notcontains $rpc) { throw "Missing RPC $rpc." } }
    foreach ($bucket in @('exam-archives','public-submission-archives')) { if ($result.buckets -notcontains $bucket) { throw "Missing bucket $bucket." } }
    Write-AcceptanceResult -Passed $true -Code 'CLOUD_SCHEMA_VERSION_OK' -TraceId $traceId -Detail 'live capability RPC reports schema 23, critical RPCs and buckets'
} catch { Write-AcceptanceResult -Passed $false -Code 'CLOUD_SCHEMA_VERSION_FAILED' -TraceId $traceId -Detail $_.Exception.Message }
