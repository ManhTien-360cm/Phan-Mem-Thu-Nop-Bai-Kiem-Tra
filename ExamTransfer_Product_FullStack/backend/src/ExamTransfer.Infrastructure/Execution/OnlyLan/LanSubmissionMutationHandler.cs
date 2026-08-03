using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Execution.OnlyLan;

public sealed class LanSubmissionMutationHandler(
    AppDbContext db,
    IAuditService audit,
    IOutboxService outbox) : ISubmissionMutationHandler
{
    public SessionAccessMode AccessMode => SessionAccessMode.LanOnly;

    public async Task RejectAsync(
        Submission submission,
        RejectSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        if (submission.Status is not (
            SubmissionStatus.Submitted or SubmissionStatus.LateSubmitted))
        {
            throw new ApiException(
                ErrorCodes.InvalidStateTransition,
                "Chỉ từ chối bài đã nộp.",
                409);
        }

        submission.Status = SubmissionStatus.Rejected;
        submission.TeacherRejectReason = request.Reason;
        submission.Participant.SubmissionStatus = SubmissionStatus.Rejected;
        submission.Participant.Session.Sequence++;
        OnlyLanStudentNotificationOutbox.Enqueue(
            db,
            StudentNotificationEventType.SubmissionRejected,
            submission.SessionId,
            submission.Participant.Session.Sequence,
            participantId: submission.ParticipantId,
            submissionId: submission.Id,
            reason: request.Reason);
        await audit.WriteAsync(
            "SubmissionRejected",
            nameof(Submission),
            submission.Id.ToString(),
            submission.SessionId,
            null,
            request,
            cancellationToken);
        await outbox.EnqueueAsync(
            "submissions",
            submission.Id.ToString(),
            "upsert",
            SubmissionMutationPayloads.ToCloud(submission),
            cancellationToken: cancellationToken);
    }

    public async Task AllowResubmitAsync(
        SessionParticipant participant,
        AllowResubmitRequest request,
        CancellationToken cancellationToken)
    {
        var submissionCandidates = await db.SubmissionsSet
            .Where(x => x.ParticipantId == participant.Id
                && x.SessionId == participant.SessionId
                && x.Status == SubmissionStatus.Rejected)
            .ToListAsync(cancellationToken);
        var submission = submissionCandidates
            .OrderByDescending(x => x.AttemptNumber)
            .ThenByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault()
            ?? throw new ApiException(
                ErrorCodes.InvalidStateTransition,
                "Không tìm thấy bài bị từ chối để cho phép nộp lại.",
                409);
        participant.ResubmitAllowed = true;
        participant.ResubmitReason = request.Reason;
        participant.Session.Sequence++;
        OnlyLanStudentNotificationOutbox.Enqueue(
            db,
            StudentNotificationEventType.ResubmitAllowed,
            participant.SessionId,
            participant.Session.Sequence,
            participantId: participant.Id,
            submissionId: submission.Id,
            reason: request.Reason);
        await audit.WriteAsync(
            "ResubmitAllowed",
            nameof(SessionParticipant),
            participant.Id.ToString(),
            participant.SessionId,
            null,
            request,
            cancellationToken);
        await outbox.EnqueueAsync(
            "session_participants",
            participant.Id.ToString(),
            "upsert",
            SubmissionMutationPayloads.ToCloud(participant),
            cancellationToken: cancellationToken);
    }
}
