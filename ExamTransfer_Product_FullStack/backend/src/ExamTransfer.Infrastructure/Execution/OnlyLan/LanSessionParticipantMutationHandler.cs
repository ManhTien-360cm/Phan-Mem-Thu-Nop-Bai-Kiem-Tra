using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Execution.OnlyLan;

public sealed class LanSessionParticipantMutationHandler(
    AppDbContext db,
    ISessionTokenService tokens,
    IAuditService audit,
    IOutboxService outbox,
    IRealtimePublisher realtime,
    IOptions<ExamTransferOptions> options) : ISessionParticipantMutationHandler
{
    private readonly ExamTransferOptions _options = options.Value;

    public SessionAccessMode AccessMode => SessionAccessMode.LanOnly;

    public async Task<ParticipantDto> ApproveAsync(
        SessionParticipant participant,
        Guid mutationRequestId,
        CancellationToken cancellationToken)
    {
        if (participant.Session.Status != SessionStatus.Waiting)
        {
            throw new ApiException(
                ErrorCodes.InvalidStateTransition,
                "Chỉ duyệt trong phòng chờ.",
                409);
        }

        participant.Status = ParticipantStatus.Approved;
        participant.ApprovedAtUtc = DateTimeOffset.UtcNow;
        participant.Session.Sequence++;

        var issued = tokens.IssueParticipantToken(
            participant.SessionId,
            participant.Id,
            participant.UserId ?? Guid.Empty,
            participant.DeviceId,
            participant.Status,
            SessionParticipantMutationRules.ParticipantTokenLifetime(
                _options,
                participant.Session));

        OnlyLanStudentNotificationOutbox.Enqueue(
            db,
            StudentNotificationEventType.ParticipantApproved,
            participant.SessionId,
            participant.Session.Sequence,
            participantId: participant.Id);

        await audit.WriteAsync(
            "ParticipantApproved",
            nameof(SessionParticipant),
            participant.Id.ToString(),
            participant.SessionId,
            null,
            SessionParticipantMutationRules.ToCloud(participant),
            cancellationToken);
        await outbox.EnqueueAsync(
            "session_participants",
            participant.Id.ToString(),
            "upsert",
            SessionParticipantMutationRules.ToCloud(participant),
            cancellationToken: cancellationToken);
        return participant.ToDto(
            DateTimeOffset.UtcNow,
            _options.Session.DisconnectAfterSeconds,
            SessionParticipantMutationRules.ParticipantDeadline(participant));
    }

    public async Task RejectAsync(
        SessionParticipant participant,
        string? reason,
        Guid mutationRequestId,
        CancellationToken cancellationToken)
    {
        participant.Status = ParticipantStatus.Rejected;
        participant.Session.Sequence++;
        OnlyLanStudentNotificationOutbox.Enqueue(
            db,
            StudentNotificationEventType.ParticipantAdmissionRejected,
            participant.SessionId,
            participant.Session.Sequence,
            participantId: participant.Id,
            reason: reason);
        await audit.WriteAsync(
            "ParticipantRejected",
            nameof(SessionParticipant),
            participant.Id.ToString(),
            participant.SessionId,
            null,
            new
            {
                participant = SessionParticipantMutationRules.ToCloud(participant),
                reason
            },
            cancellationToken);
        await outbox.EnqueueAsync(
            "session_participants",
            participant.Id.ToString(),
            "upsert",
            SessionParticipantMutationRules.ToCloud(participant),
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<ParticipantDto>> BulkApproveAsync(
        ExamSession session,
        IReadOnlyList<Guid> requestedIds,
        Guid mutationRequestId,
        CancellationToken cancellationToken)
    {
        if (session.Status != SessionStatus.Waiting)
        {
            throw new ApiException(
                ErrorCodes.InvalidStateTransition,
                "Chỉ duyệt học sinh trong phòng chờ.",
                409);
        }

        var participants = session.Participants
            .Where(x => requestedIds.Contains(x.Id))
            .ToList();

        if (participants.Count != requestedIds.Count)
        {
            var found = participants.Select(x => x.Id).ToHashSet();
            var missing = requestedIds.Where(x => !found.Contains(x)).ToList();
            throw new ApiException(
                ErrorCodes.NotFound,
                "Một hoặc nhiều học sinh không còn tồn tại trong phòng chờ.",
                404,
                details: new { missingParticipantIds = missing });
        }

        var invalid = participants
            .Where(x => x.Status == ParticipantStatus.Rejected)
            .Select(x => x.Id)
            .ToList();
        if (invalid.Count > 0)
        {
            throw new ApiException(
                ErrorCodes.InvalidStateTransition,
                "Không thể duyệt hàng loạt học sinh đã bị từ chối.",
                409,
                details: new { participantIds = invalid });
        }

        var events =
            new List<(SessionParticipant Participant, long Sequence, IssuedToken Token)>();
        foreach (var participant in participants)
        {
            if (participant.Status == ParticipantStatus.Approved)
            {
                continue;
            }

            participant.Status = ParticipantStatus.Approved;
            participant.ApprovedAtUtc = DateTimeOffset.UtcNow;
            session.Sequence++;
            var issued = tokens.IssueParticipantToken(
                session.Id,
                participant.Id,
                participant.UserId ?? Guid.Empty,
                participant.DeviceId,
                participant.Status,
                SessionParticipantMutationRules.ParticipantTokenLifetime(
                    _options,
                    session));
            events.Add((participant, session.Sequence, issued));
        }

        foreach (var item in events)
        {
            OnlyLanStudentNotificationOutbox.Enqueue(
                db,
                StudentNotificationEventType.ParticipantApproved,
                session.Id,
                item.Sequence,
                participantId: item.Participant.Id);
        }

        await audit.WriteAsync(
            "ParticipantsBulkApproved",
            nameof(SessionParticipant),
            null,
            session.Id,
            null,
            new { ids = requestedIds, approvedCount = events.Count },
            cancellationToken);

        foreach (var item in events)
        {
            await outbox.EnqueueAsync(
                "session_participants",
                item.Participant.Id.ToString(),
                "upsert",
                SessionParticipantMutationRules.ToCloud(item.Participant),
                cancellationToken: cancellationToken);
        }

        return participants
            .Select(x => x.ToDto(
                DateTimeOffset.UtcNow,
                _options.Session.DisconnectAfterSeconds))
            .ToList();
    }

    public async Task<ParticipantDto> AddExtraTimeAsync(
        SessionParticipant participant,
        ExtraTimeRequest request,
        CancellationToken cancellationToken)
    {
        if (participant.Session.Status is not (
            SessionStatus.InProgress or SessionStatus.Paused))
        {
            throw new ApiException(
                ErrorCodes.InvalidStateTransition,
                "Chỉ cộng giờ khi phòng đang thi hoặc tạm dừng.",
                409);
        }

        participant.ExtraTimeMinutes += request.Minutes;
        participant.Session.Sequence++;
        var deadline = participant.Session.StartedAtUtc!.Value.AddMinutes(
            participant.Session.Exam.DurationMinutes
            + participant.ExtraTimeMinutes);
        var activeQuizAttempts = await db.QuizAttemptsSet
            .Where(x => x.SessionId == participant.SessionId
                && x.ParticipantId == participant.Id
                && x.Status == QuizAttemptStatus.InProgress)
            .ToListAsync(cancellationToken);
        foreach (var attempt in activeQuizAttempts)
        {
            attempt.DeadlineUtc = deadline;
        }

        db.ParticipantExtraTimesSet.Add(new ParticipantExtraTime
        {
            ParticipantId = participant.Id,
            Minutes = request.Minutes,
            Reason = request.Reason
        });
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            "ParticipantExtraTimeAdded",
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
            SessionParticipantMutationRules.ToCloud(participant),
            cancellationToken: cancellationToken);
        await realtime.PublishSessionAsync(
            participant.SessionId,
            RealtimeEvents.TimeExtended,
            participant.Session.Sequence,
            new TimeExtendedEvent(
                participant.Id,
                request.Minutes,
                deadline),
            cancellationToken);
        return participant.ToDto(
            DateTimeOffset.UtcNow,
            _options.Session.DisconnectAfterSeconds,
            deadline);
    }
}
