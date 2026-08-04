using ExamTransfer.Domain;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Infrastructure.Execution;

public interface ISubmissionMutationHandler
{
    SessionAccessMode AccessMode { get; }

    Task RejectAsync(
        Submission submission,
        RejectSubmissionRequest request,
        CancellationToken cancellationToken);

    Task AllowResubmitAsync(
        SessionParticipant participant,
        AllowResubmitRequest request,
        CancellationToken cancellationToken);
}

internal static class SubmissionMutationPayloads
{
    internal static object ToCloud(Submission submission) => new
    {
        id = submission.Id,
        session_id = submission.SessionId,
        participant_id = submission.ParticipantId,
        attempt_number = submission.AttemptNumber,
        idempotency_key = submission.IdempotencyKey,
        status = submission.Status.ToString(),
        client_submitted_at = submission.ClientSubmittedAtUtc,
        server_received_at = submission.ServerReceivedAtUtc,
        deadline_at = submission.DeadlineUtc,
        computed_is_late = submission.ComputedIsLate,
        late_override = submission.LateOverride,
        is_late = submission.IsLate,
        is_official = submission.IsOfficial,
        receipt_code = submission.ReceiptCode,
        receipt_signature = submission.ReceiptSignature,
        teacher_reject_reason = submission.TeacherRejectReason,
        client_note = submission.ClientNote,
        created_at = submission.CreatedAtUtc,
        updated_at = submission.UpdatedAtUtc
    };

    internal static object ToCloud(SessionParticipant participant) => new
    {
        id = participant.Id,
        session_id = participant.SessionId,
        user_id = participant.UserId,
        student_code = participant.StudentCode,
        display_name = participant.DisplayName,
        class_name = participant.ClassName,
        device_id = participant.DeviceId,
        machine_name = participant.MachineName,
        ip_address = participant.IpAddress,
        app_version = participant.AppVersion,
        status = participant.Status.ToString(),
        joined_at = participant.JoinedAtUtc,
        approved_at = participant.ApprovedAtUtc,
        last_seen_at = participant.LastSeenUtc,
        download_status = participant.DownloadStatus.ToString(),
        submission_status = participant.SubmissionStatus.ToString(),
        extra_time_minutes = participant.ExtraTimeMinutes,
        resubmit_allowed = participant.ResubmitAllowed,
        resubmit_reason = participant.ResubmitReason,
        capability_json = participant.CapabilityJson,
        created_at = participant.CreatedAtUtc,
        updated_at = participant.UpdatedAtUtc
    };
}
