using System.Net;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Execution;
using ExamTransfer.Infrastructure.Execution.Dispatch;
using ExamTransfer.Infrastructure.Execution.OnlyLan;
using ExamTransfer.Infrastructure.Execution.PublicCloud;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.LocalServer.Discovery;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

/// <summary>
/// Characterizes the current OnlyLAN backend workflow before module splitting.
/// These tests intentionally exercise existing validation instead of bypassing it.
/// </summary>
public sealed class OnlyLanWorkflowCharacterizationTests
{
    [Fact]
    public async Task FullLifecycle_CompletesWithStrictPolicyAndPersistsAfterContextReload()
    {
        await using var fixture = await OnlyLanFixture.CreateAsync();
        var seeded = await fixture.SeedPublishedQuizAsync();
        var session = await fixture.CreateAndOpenLanSessionAsync(seeded.Exam.Id, "LANE2E1");

        var discovered = await OpenSessionDiscoveryBuilder.BuildAsync(
            fixture.Db,
            fixture.Options.Value,
            "192.168.10.20",
            "characterization-server",
            session.Summary.RoomCode,
            CancellationToken.None);
        var discoveryItem = Assert.Single(discovered);
        Assert.Equal(session.Summary.Id, discoveryItem.SessionId);
        Assert.Equal(SessionAccessMode.LanOnly, discoveryItem.AccessMode);
        Assert.Equal(SessionAdmissionMode.OpenRequest, discoveryItem.AdmissionMode);
        Assert.Equal(SessionStatus.Waiting, discoveryItem.SessionState);

        var joined = await fixture.Sessions.JoinAsync(
            JoinRequest(session.Summary.RoomCode, seeded.Student, "student-device-1"),
            seeded.Student.Id,
            seeded.Student.StudentCode!,
            seeded.Student.DisplayName,
            "192.168.10.42",
            CancellationToken.None);
        Assert.Equal(ParticipantStatus.PendingApproval, joined.Status);
        Assert.False(string.IsNullOrWhiteSpace(joined.AccessToken));

        var approved = await fixture.Sessions.ApproveAsync(
            session.Summary.Id,
            joined.ParticipantId,
            Guid.NewGuid(),
            CancellationToken.None);
        Assert.Equal(ParticipantStatus.Approved, approved.Status);

        var policy = await fixture.Controls.SavePolicyAsync(
            session.Summary.Id,
            StandardPolicy(),
            CancellationToken.None);
        await fixture.Controls.ApplyPolicyAsync(
            session.Summary.Id,
            new ApplyControlPolicyRequest([joined.ParticipantId]),
            CancellationToken.None);
        await fixture.Controls.PolicyAckAsync(
            session.Summary.Id,
            joined.ParticipantId,
            AppliedAck(policy.Version),
            CancellationToken.None);

        var statuses = await fixture.Controls.GetDeviceStatusAsync(
            session.Summary.Id,
            CancellationToken.None);
        var status = Assert.Single(statuses);
        Assert.Equal(joined.ParticipantId, status.ParticipantId);
        Assert.Equal(policy.Version, status.PolicyVersion);
        Assert.Equal(PolicyApplyStatus.Applied, status.Status);

        var startedSession = await fixture.Sessions.TransitionAsync(
            session.Summary.Id,
            SessionStatus.InProgress,
            null,
            CancellationToken.None);
        Assert.Equal(SessionStatus.InProgress, startedSession.Summary.Status);

        var attempt = await fixture.Quiz.StartOrGetAttemptAsync(
            session.Summary.Id,
            joined.ParticipantId,
            CancellationToken.None);
        Assert.Equal(2, attempt.Questions.Count);
        Assert.Equal(10.00m, attempt.MaxScore);

        var answers = attempt.Questions
            .Select(question => new QuizAnswerDto(
                question.Id,
                [seeded.CorrectChoiceIds[question.Id]],
                1,
                DateTimeOffset.UtcNow))
            .ToList();
        await fixture.Quiz.SyncAnswersAsync(
            attempt.Id,
            joined.ParticipantId,
            new SyncQuizAnswersRequest(answers),
            CancellationToken.None);

        var finalized = await fixture.Quiz.FinalizeAsync(
            attempt.Id,
            joined.ParticipantId,
            new FinalizeQuizAttemptRequest("lan-e2e-finalize-1", DateTimeOffset.UtcNow),
            CancellationToken.None);
        var repeated = await fixture.Quiz.FinalizeAsync(
            attempt.Id,
            joined.ParticipantId,
            new FinalizeQuizAttemptRequest("lan-e2e-finalize-1", DateTimeOffset.UtcNow),
            CancellationToken.None);
        Assert.Equal(QuizAttemptStatus.Finalized, finalized.Status);
        Assert.False(finalized.ScoreVisible);
        Assert.Null(finalized.Score);
        Assert.Equal(finalized.Score, repeated.Score);
        Assert.Equal(finalized.Id, repeated.Id);
        Assert.Single(await fixture.Quiz.ListAttemptsForSessionAsync(
            session.Summary.Id,
            CancellationToken.None));

        await fixture.Sessions.TransitionAsync(
            session.Summary.Id,
            SessionStatus.Collecting,
            null,
            CancellationToken.None);
        var finished = await fixture.Sessions.TransitionAsync(
            session.Summary.Id,
            SessionStatus.Finished,
            new EndSessionRequest(false, null),
            CancellationToken.None);
        Assert.Equal(SessionStatus.Finished, finished.Summary.Status);
        Assert.Equal(0, fixture.Cloud.CallCount);

        await using var reloaded = fixture.CreateContext();
        var persistedSession = await reloaded.ExamSessionsSet
            .AsNoTracking()
            .SingleAsync(x => x.Id == session.Summary.Id);
        var persistedParticipant = await reloaded.SessionParticipantsSet
            .AsNoTracking()
            .SingleAsync(x => x.Id == joined.ParticipantId);
        var persistedAttempt = await reloaded.QuizAttemptsSet
            .AsNoTracking()
            .SingleAsync(x => x.Id == attempt.Id);
        Assert.Equal(SessionStatus.Finished, persistedSession.Status);
        Assert.False(persistedSession.AcceptingParticipants);
        Assert.Equal(ParticipantStatus.Approved, persistedParticipant.Status);
        Assert.Equal(QuizAttemptStatus.Finalized, persistedAttempt.Status);
        Assert.Equal(10.00m, persistedAttempt.Score);
        Assert.Equal("lan-e2e-finalize-1", persistedAttempt.FinalizeIdempotencyKey);
    }

    [Fact]
    public async Task StrictSupervisionGate_RequiresAppliedStatusForLatestPolicyVersion()
    {
        await using var fixture = await OnlyLanFixture.CreateAsync();
        var seeded = await fixture.SeedPublishedQuizAsync();
        var session = await fixture.CreateAndOpenLanSessionAsync(seeded.Exam.Id, "LANGATE");
        var joined = await fixture.Sessions.JoinAsync(
            JoinRequest(session.Summary.RoomCode, seeded.Student, "student-device-gate"),
            seeded.Student.Id,
            seeded.Student.StudentCode!,
            seeded.Student.DisplayName,
            "192.168.10.43",
            CancellationToken.None);
        await fixture.Sessions.ApproveAsync(
            session.Summary.Id,
            joined.ParticipantId,
            Guid.NewGuid(),
            CancellationToken.None);
        await fixture.Sessions.TransitionAsync(
            session.Summary.Id,
            SessionStatus.InProgress,
            null,
            CancellationToken.None);

        await AssertForbiddenPolicyGateAsync(fixture, session.Summary.Id, joined.ParticipantId);

        var first = await fixture.Controls.SavePolicyAsync(
            session.Summary.Id,
            StandardPolicy(),
            CancellationToken.None);
        await fixture.Controls.ApplyPolicyAsync(
            session.Summary.Id,
            new ApplyControlPolicyRequest([joined.ParticipantId]),
            CancellationToken.None);
        await AssertForbiddenPolicyGateAsync(fixture, session.Summary.Id, joined.ParticipantId);

        await fixture.Controls.PolicyAckAsync(
            session.Summary.Id,
            joined.ParticipantId,
            new PolicyApplyAckRequest(
                first.Version,
                PolicyApplyStatus.Unsupported,
                ["fullscreen"],
                "unsupported",
                FullCapabilities()),
            CancellationToken.None);
        await AssertForbiddenPolicyGateAsync(fixture, session.Summary.Id, joined.ParticipantId);

        await fixture.Controls.PolicyAckAsync(
            session.Summary.Id,
            joined.ParticipantId,
            AppliedAck(first.Version),
            CancellationToken.None);
        var latestFirst = await fixture.Controls.GetPolicyAsync(
            session.Summary.Id,
            CancellationToken.None);
        Assert.NotNull(latestFirst);
        Assert.Equal(first.Version, latestFirst.Version);
        Assert.NotEqual(first.RowVersion, latestFirst.RowVersion);

        var second = await fixture.Controls.SavePolicyAsync(
            session.Summary.Id,
            StandardPolicy(latestFirst.RowVersion),
            CancellationToken.None);
        await fixture.Controls.ApplyPolicyAsync(
            session.Summary.Id,
            new ApplyControlPolicyRequest([joined.ParticipantId]),
            CancellationToken.None);

        await AssertForbiddenPolicyGateAsync(fixture, session.Summary.Id, joined.ParticipantId);
        await fixture.Controls.PolicyAckAsync(
            session.Summary.Id,
            joined.ParticipantId,
            AppliedAck(first.Version),
            CancellationToken.None);
        await AssertForbiddenPolicyGateAsync(fixture, session.Summary.Id, joined.ParticipantId);

        await fixture.Controls.PolicyAckAsync(
            session.Summary.Id,
            joined.ParticipantId,
            AppliedAck(second.Version),
            CancellationToken.None);
        var attempt = await fixture.Quiz.StartOrGetAttemptAsync(
            session.Summary.Id,
            joined.ParticipantId,
            CancellationToken.None);
        Assert.Equal(QuizAttemptStatus.InProgress, attempt.Status);
        Assert.Equal(second.Version, (await fixture.Controls.GetDeviceStatusAsync(
            session.Summary.Id,
            CancellationToken.None)).Single().PolicyVersion);
    }

    [Fact]
    public async Task JoinAndRejoin_EnforceAccountDeviceLanAndRouteBoundaries()
    {
        await using var fixture = await OnlyLanFixture.CreateAsync();
        var seeded = await fixture.SeedPublishedQuizAsync();
        var session = await fixture.CreateAndOpenLanSessionAsync(seeded.Exam.Id, "LANJOIN");
        var request = JoinRequest(session.Summary.RoomCode, seeded.Student, "student-device-rejoin");

        var beforeJoin = DateTimeOffset.UtcNow;
        var first = await fixture.Sessions.JoinAsync(
            request,
            seeded.Student.Id,
            seeded.Student.StudentCode!,
            seeded.Student.DisplayName,
            "192.168.10.44",
            CancellationToken.None);
        var afterJoin = DateTimeOffset.UtcNow;
        var tokenPrincipal = new SessionTokenService(fixture.Options).Validate(first.AccessToken);
        Assert.NotNull(tokenPrincipal);
        Assert.Equal(session.Summary.Id, tokenPrincipal.SessionId);
        Assert.Equal(first.ParticipantId, tokenPrincipal.ParticipantId);
        Assert.Equal(seeded.Student.Id, tokenPrincipal.UserId);
        Assert.Equal(request.DeviceId, tokenPrincipal.DeviceId);
        Assert.Equal(ParticipantStatus.PendingApproval, tokenPrincipal.ParticipantStatus);
        Assert.InRange(
            first.TokenExpiresAtUtc,
            beforeJoin.AddMinutes(210),
            afterJoin.AddMinutes(210));
        Assert.Contains(
            fixture.OutboxCalls,
            x => x.EntityType == "session_participants"
                && x.EntityId == first.ParticipantId.ToString()
                && x.Operation == "upsert");
        Assert.Contains(RealtimeEvents.ParticipantJoined, fixture.RealtimeEvents);
        Assert.True(await fixture.Db.AuditLogsSet.AnyAsync(
            x => x.Action == "ParticipantJoined"
                && x.SessionId == session.Summary.Id
                && x.EntityId == first.ParticipantId.ToString()));

        var rejoined = await fixture.Sessions.JoinAsync(
            request,
            seeded.Student.Id,
            seeded.Student.StudentCode!,
            seeded.Student.DisplayName,
            "192.168.10.44",
            CancellationToken.None);
        Assert.Equal(first.ParticipantId, rejoined.ParticipantId);
        Assert.Single(await fixture.Db.SessionParticipantsSet
            .Where(x => x.SessionId == session.Summary.Id)
            .ToListAsync());

        var mismatchedClaims = await Assert.ThrowsAsync<ApiException>(() => fixture.Sessions.JoinAsync(
            request,
            Guid.NewGuid(),
            seeded.Student.StudentCode!,
            seeded.Student.DisplayName,
            "192.168.10.44",
            CancellationToken.None));
        Assert.Equal(ErrorCodes.ParticipantAccountMismatch, mismatchedClaims.Code);
        Assert.Equal(403, mismatchedClaims.StatusCode);

        var otherDevice = await Assert.ThrowsAsync<ApiException>(() => fixture.Sessions.JoinAsync(
            request with { DeviceId = "other-device" },
            seeded.Student.Id,
            seeded.Student.StudentCode!,
            seeded.Student.DisplayName,
            "192.168.10.44",
            CancellationToken.None));
        Assert.Equal(ErrorCodes.DuplicateStudentCode, otherDevice.Code);
        Assert.Equal(409, otherDevice.StatusCode);

        var deniedService = fixture.CreateSessionService(new StaticLanAccessPolicy(false));
        var denied = await Assert.ThrowsAsync<ApiException>(() => deniedService.JoinAsync(
            request with
            {
                StudentCode = "LAN-DENIED",
                DisplayName = "Denied Student",
                DeviceId = "denied-device"
            },
            Guid.NewGuid(),
            "LAN-DENIED",
            "Denied Student",
            "203.0.113.10",
            CancellationToken.None));
        Assert.Equal(ErrorCodes.LanAccessDenied, denied.Code);
        Assert.Equal(403, denied.StatusCode);

        var dynamicPolicy = new LanAccessPolicy(
            [LanAccessPolicy.NetworkRange.FromPrefix(IPAddress.Parse("192.168.10.20"), 24)]);
        var dynamicService = fixture.CreateSessionService(dynamicPolicy);
        var sameSubnetJoin = await dynamicService.JoinAsync(
            request with
            {
                StudentCode = "LAN-DYNAMIC",
                DisplayName = "Dynamic Student",
                DeviceId = "dynamic-device"
            },
            Guid.NewGuid(),
            "LAN-DYNAMIC",
            "Dynamic Student",
            "192.168.10.55",
            CancellationToken.None);
        Assert.Equal(ParticipantStatus.PendingApproval, sameSubnetJoin.Status);

        var publicSession = await fixture.Sessions.CreateAndOpenAsync(
            SessionRequest(seeded.Exam.Id, "PUBROUTE", SessionAccessMode.PublicCloud),
            "teacher-host",
            CancellationToken.None);
        var publicRoute = await Assert.ThrowsAsync<ApiException>(() => fixture.Sessions.JoinAsync(
            request with
            {
                RoomCode = publicSession.Summary.RoomCode,
                StudentCode = "PUBLIC-LOCAL",
                DisplayName = "Public Local",
                DeviceId = "public-local-device"
            },
            Guid.NewGuid(),
            "PUBLIC-LOCAL",
            "Public Local",
            "192.168.10.45",
            CancellationToken.None));
        Assert.Equal(ErrorCodes.PublicCloudRouteRequired, publicRoute.Code);
        Assert.Equal(409, publicRoute.StatusCode);
        Assert.False(await fixture.Db.SessionParticipantsSet.AnyAsync(
            x => x.SessionId == publicSession.Summary.Id));
        Assert.Equal(0, fixture.Cloud.CallCount);
    }

    [Fact]
    public async Task Heartbeat_EnforcesDeviceAndRestoresDisconnectedParticipantWithoutTouchingOthers()
    {
        await using var fixture = await OnlyLanFixture.CreateAsync();
        var seeded = await fixture.SeedPublishedQuizAsync();
        var session = await fixture.CreateAndOpenLanSessionAsync(seeded.Exam.Id, "LANBEAT");
        var first = await fixture.Sessions.JoinAsync(
            JoinRequest(session.Summary.RoomCode, seeded.Student, "heartbeat-device"),
            seeded.Student.Id,
            seeded.Student.StudentCode!,
            seeded.Student.DisplayName,
            "192.168.10.46",
            CancellationToken.None);
        var other = await fixture.Sessions.JoinAsync(
            new JoinSessionRequest(
                session.Summary.RoomCode,
                "LAN-OTHER",
                "Other Student",
                null,
                "other-heartbeat-device",
                "other-machine",
                "characterization",
                Guid.NewGuid().ToString("N")),
            Guid.NewGuid(),
            "LAN-OTHER",
            "Other Student",
            "192.168.10.47",
            CancellationToken.None);
        await fixture.Sessions.ApproveAsync(
            session.Summary.Id,
            first.ParticipantId,
            Guid.NewGuid(),
            CancellationToken.None);

        fixture.Db.ChangeTracker.Clear();
        var firstBeforeMismatch = await fixture.Db.SessionParticipantsSet
            .AsNoTracking()
            .SingleAsync(x => x.Id == first.ParticipantId);
        var sequenceBeforeMismatch = await fixture.Db.ExamSessionsSet
            .Where(x => x.Id == session.Summary.Id)
            .Select(x => x.Sequence)
            .SingleAsync();
        var mismatch = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.Sessions.HeartbeatAsync(
                session.Summary.Id,
                first.ParticipantId,
                "wrong-device",
                new HeartbeatRequest("Ready", DateTimeOffset.UtcNow, 0),
                CancellationToken.None));
        Assert.Equal(ErrorCodes.Forbidden, mismatch.Code);
        Assert.Equal(403, mismatch.StatusCode);

        fixture.Db.ChangeTracker.Clear();
        var firstAfterMismatch = await fixture.Db.SessionParticipantsSet
            .Include(x => x.Session)
            .SingleAsync(x => x.Id == first.ParticipantId);
        Assert.Equal(firstBeforeMismatch.LastSeenUtc, firstAfterMismatch.LastSeenUtc);
        Assert.Equal(sequenceBeforeMismatch, firstAfterMismatch.Session.Sequence);

        var otherLastSeen = await fixture.Db.SessionParticipantsSet
            .Where(x => x.Id == other.ParticipantId)
            .Select(x => x.LastSeenUtc)
            .SingleAsync();
        firstAfterMismatch.Status = ParticipantStatus.Disconnected;
        firstAfterMismatch.LastSeenUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        firstAfterMismatch.Session.Sequence++;
        await fixture.Db.SaveChangesAsync();
        var sequenceBeforeHeartbeat = firstAfterMismatch.Session.Sequence;
        var realtimeCountBeforeHeartbeat = fixture.RealtimeEvents.Count;
        var beforeHeartbeat = DateTimeOffset.UtcNow;

        var response = await fixture.Sessions.HeartbeatAsync(
            session.Summary.Id,
            first.ParticipantId,
            "heartbeat-device",
            new HeartbeatRequest("Ready", beforeHeartbeat.AddHours(-1), 0),
            CancellationToken.None);
        var afterHeartbeat = DateTimeOffset.UtcNow;

        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.SessionParticipantsSet
            .Include(x => x.Session)
            .SingleAsync(x => x.Id == first.ParticipantId);
        Assert.InRange(response.ServerNowUtc, beforeHeartbeat, afterHeartbeat);
        Assert.Equal(response.ServerNowUtc, persisted.LastSeenUtc);
        Assert.Equal(ParticipantStatus.Approved, persisted.Status);
        Assert.Equal(sequenceBeforeHeartbeat + 1, persisted.Session.Sequence);
        Assert.Equal(
            otherLastSeen,
            await fixture.Db.SessionParticipantsSet
                .Where(x => x.Id == other.ParticipantId)
                .Select(x => x.LastSeenUtc)
                .SingleAsync());
        Assert.Equal(realtimeCountBeforeHeartbeat + 1, fixture.RealtimeEvents.Count);
        Assert.Equal(
            RealtimeEvents.ParticipantConnectionChanged,
            fixture.RealtimeEvents[^1]);
    }

    [Fact]
    public async Task Reject_LanOnly_persists_status_sequence_audit_and_outbox_without_cloud()
    {
        await using var fixture = await OnlyLanFixture.CreateAsync();
        var seeded = await fixture.SeedPublishedQuizAsync();
        var session = SessionEntity(
            seeded.Exam,
            "LANREJ",
            SessionAccessMode.LanOnly,
            SessionStatus.Waiting,
            true);
        var participant = ParticipantEntity(session, "REJECT-1");
        fixture.Db.AddRange(session, participant);
        await fixture.Db.SaveChangesAsync();
        var sequenceBefore = session.Sequence;
        var outboxBefore = fixture.OutboxCalls.Count;
        var realtimeBefore = fixture.RealtimeEvents.Count;

        await fixture.Sessions.RejectAsync(
            session.Id,
            participant.Id,
            "Identity mismatch",
            Guid.Empty,
            CancellationToken.None);

        fixture.Db.ChangeTracker.Clear();
        var persistedSession = await fixture.Db.ExamSessionsSet
            .SingleAsync(x => x.Id == session.Id);
        var persistedParticipant = await fixture.Db.SessionParticipantsSet
            .SingleAsync(x => x.Id == participant.Id);
        Assert.Equal(ParticipantStatus.Rejected, persistedParticipant.Status);
        Assert.Equal(sequenceBefore + 1, persistedSession.Sequence);
        Assert.Equal(outboxBefore + 1, fixture.OutboxCalls.Count);
        Assert.Equal(realtimeBefore, fixture.RealtimeEvents.Count);
        var rejectedNotification = await fixture.Db.SyncQueueSet.SingleAsync(
            x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType);
        Assert.Contains("\"eventType\":\"ParticipantAdmissionRejected\"", rejectedNotification.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(participant.Id.ToString(), rejectedNotification.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            await fixture.Db.AuditLogsSet.ToListAsync(),
            x => x.Action == "ParticipantRejected"
                && x.EntityId == participant.Id.ToString());
        Assert.Equal(0, fixture.Cloud.CallCount);
    }

    [Fact]
    public async Task BulkApprove_LanOnly_persists_then_outboxes_broadcasts_and_audits()
    {
        await using var fixture = await OnlyLanFixture.CreateAsync();
        var seeded = await fixture.SeedPublishedQuizAsync();
        var session = SessionEntity(
            seeded.Exam,
            "LANBULK",
            SessionAccessMode.LanOnly,
            SessionStatus.Waiting,
            true);
        var first = ParticipantEntity(session, "BULK-1");
        var second = ParticipantEntity(session, "BULK-2");
        fixture.Db.AddRange(session, first, second);
        await fixture.Db.SaveChangesAsync();
        var sequenceBefore = session.Sequence;
        var outboxBefore = fixture.OutboxCalls.Count;
        var realtimeBefore = fixture.RealtimeEvents.Count;

        var result = await fixture.Sessions.BulkApproveAsync(
            session.Id,
            new BulkApproveRequest(
                [first.Id, second.Id, first.Id],
                Guid.Empty),
            CancellationToken.None);

        fixture.Db.ChangeTracker.Clear();
        var persistedSession = await fixture.Db.ExamSessionsSet
            .SingleAsync(x => x.Id == session.Id);
        var statuses = await fixture.Db.SessionParticipantsSet
            .Where(x => x.SessionId == session.Id)
            .Select(x => x.Status)
            .ToListAsync();
        Assert.Equal(2, result.Count);
        Assert.All(result, x => Assert.Equal(ParticipantStatus.Approved, x.Status));
        Assert.All(statuses, x => Assert.Equal(ParticipantStatus.Approved, x));
        Assert.Equal(sequenceBefore + 2, persistedSession.Sequence);
        Assert.Equal(outboxBefore + 2, fixture.OutboxCalls.Count);
        Assert.Equal(realtimeBefore, fixture.RealtimeEvents.Count);
        Assert.Equal(
            2,
            await fixture.Db.SyncQueueSet.CountAsync(
                x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType
                    && x.Status == SyncStatus.LocalOnly));
        Assert.All(
            await fixture.Db.SyncQueueSet
                .Where(x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType)
                .ToListAsync(),
            x => Assert.Contains("\"eventType\":\"ParticipantApproved\"", x.PayloadJson, StringComparison.Ordinal));
        Assert.Contains(
            await fixture.Db.AuditLogsSet.ToListAsync(),
            x => x.Action == "ParticipantsBulkApproved"
                && x.SessionId == session.Id);
        Assert.Equal(0, fixture.Cloud.CallCount);
    }

    [Fact]
    public async Task TeacherMessage_LanOnly_UsesVerifiedParticipantOrStudentSessionRoute()
    {
        await using var fixture = await OnlyLanFixture.CreateAsync();
        var seeded = await fixture.SeedPublishedQuizAsync();
        var session = SessionEntity(
            seeded.Exam,
            "LANMSG",
            SessionAccessMode.LanOnly,
            SessionStatus.Waiting,
            true);
        var participant = ParticipantEntity(session, "MSG-1");
        var otherSession = SessionEntity(
            seeded.Exam,
            "LANMSG2",
            SessionAccessMode.LanOnly,
            SessionStatus.Waiting,
            true);
        var outsider = ParticipantEntity(otherSession, "MSG-OUT");
        fixture.Db.AddRange(session, participant, otherSession, outsider);
        await fixture.Db.SaveChangesAsync();

        await fixture.Sessions.SendMessageAsync(
            session.Id,
            new SendMessageRequest(participant.Id, MessageType.Information, "Private"),
            default);
        await fixture.Sessions.SendMessageAsync(
            session.Id,
            new SendMessageRequest(null, MessageType.Information, "Broadcast"),
            default);
        await Assert.ThrowsAsync<ApiException>(() => fixture.Sessions.SendMessageAsync(
            session.Id,
            new SendMessageRequest(outsider.Id, MessageType.Warning, "Cross-session"),
            default));

        var rows = (await fixture.Db.SyncQueueSet
                .Where(x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType)
                .ToListAsync())
            .OrderBy(x => x.CreatedAtUtc)
            .ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("participant", rows[0].Operation);
        Assert.Contains(participant.Id.ToString(), rows[0].PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("session", rows[1].Operation);
        Assert.Contains("\"participantId\":null", rows[1].PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(outsider.Id.ToString(), string.Join("", rows.Select(x => x.PayloadJson)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddExtraTime_LanOnly_updates_attempt_history_and_absolute_deadline()
    {
        await using var fixture = await OnlyLanFixture.CreateAsync();
        var seeded = await fixture.SeedPublishedQuizAsync();
        var startedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        var session = SessionEntity(
            seeded.Exam,
            "LANEXTRA",
            SessionAccessMode.LanOnly,
            SessionStatus.InProgress,
            false);
        session.StartedAtUtc = startedAtUtc;
        var participant = ParticipantEntity(
            session,
            "EXTRA-1",
            ParticipantStatus.Approved);
        participant.ApprovedAtUtc = startedAtUtc;
        var attempt = new QuizAttempt
        {
            Session = session,
            SessionId = session.Id,
            Participant = participant,
            ParticipantId = participant.Id,
            Status = QuizAttemptStatus.InProgress,
            StartedAtUtc = startedAtUtc,
            DeadlineUtc = startedAtUtc.AddMinutes(seeded.Exam.DurationMinutes),
            SnapshotJson = "[]"
        };
        fixture.Db.AddRange(session, participant, attempt);
        await fixture.Db.SaveChangesAsync();
        var sequenceBefore = session.Sequence;
        var outboxBefore = fixture.OutboxCalls.Count;
        var realtimeBefore = fixture.RealtimeEvents.Count;

        var result = await fixture.Sessions.AddExtraTimeAsync(
            session.Id,
            participant.Id,
            new ExtraTimeRequest(10, "Approved accommodation", Guid.Empty),
            CancellationToken.None);

        var expectedDeadline = startedAtUtc.AddMinutes(
            seeded.Exam.DurationMinutes + 10);
        fixture.Db.ChangeTracker.Clear();
        var persistedSession = await fixture.Db.ExamSessionsSet
            .SingleAsync(x => x.Id == session.Id);
        var persistedParticipant = await fixture.Db.SessionParticipantsSet
            .SingleAsync(x => x.Id == participant.Id);
        var persistedAttempt = await fixture.Db.QuizAttemptsSet
            .SingleAsync(x => x.Id == attempt.Id);
        var history = await fixture.Db.ParticipantExtraTimesSet
            .SingleAsync(x => x.ParticipantId == participant.Id);
        Assert.Equal(10, result.ExtraTimeMinutes);
        Assert.Equal(expectedDeadline, result.EffectiveDeadlineUtc);
        Assert.Equal(10, persistedParticipant.ExtraTimeMinutes);
        Assert.Equal(sequenceBefore + 1, persistedSession.Sequence);
        Assert.Equal(expectedDeadline, persistedAttempt.DeadlineUtc);
        Assert.Equal(10, history.Minutes);
        Assert.Equal("Approved accommodation", history.Reason);
        Assert.Equal(outboxBefore + 1, fixture.OutboxCalls.Count);
        Assert.Equal(realtimeBefore + 1, fixture.RealtimeEvents.Count);
        Assert.Equal(
            RealtimeEvents.TimeExtended,
            fixture.RealtimeEvents[^1]);
        Assert.Contains(
            await fixture.Db.AuditLogsSet.ToListAsync(),
            x => x.Action == "ParticipantExtraTimeAdded"
                && x.EntityId == participant.Id.ToString());
        Assert.Equal(0, fixture.Cloud.CallCount);
    }

    [Fact]
    public async Task Discovery_ExposesOnlyPublishedWaitingAcceptingLanSessions()
    {
        await using var fixture = await OnlyLanFixture.CreateAsync();
        var seeded = await fixture.SeedPublishedQuizAsync();
        var expected = await fixture.CreateAndOpenLanSessionAsync(seeded.Exam.Id, "LANDISC");

        fixture.Db.ExamSessionsSet.AddRange(
            SessionEntity(seeded.Exam, "DRAFTLAN", SessionAccessMode.LanOnly, SessionStatus.Draft, true),
            SessionEntity(seeded.Exam, "RUNLAN", SessionAccessMode.LanOnly, SessionStatus.InProgress, true),
            SessionEntity(seeded.Exam, "LOCKLAN", SessionAccessMode.LanOnly, SessionStatus.Waiting, false),
            SessionEntity(seeded.Exam, "CLOUDWAIT", SessionAccessMode.PublicCloud, SessionStatus.Waiting, true));
        var unpublished = new Exam
        {
            Title = "Unpublished",
            Subject = "Characterization",
            DurationMinutes = 30,
            Status = ExamStatus.Draft,
            DeliveryType = ExamDeliveryType.MultipleChoice,
            SupervisionMode = SupervisionMode.Standard
        };
        fixture.Db.ExamsSet.Add(unpublished);
        fixture.Db.ExamSessionsSet.Add(
            SessionEntity(unpublished, "UNPUBLISHED", SessionAccessMode.LanOnly, SessionStatus.Waiting, true));
        await fixture.Db.SaveChangesAsync();

        var rows = await OpenSessionDiscoveryBuilder.BuildAsync(
            fixture.Db,
            fixture.Options.Value,
            "192.168.10.20",
            "characterization-server",
            null,
            CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal(expected.Summary.Id, row.SessionId);
        Assert.Equal("LANDISC", row.RoomCode);
        Assert.Equal("http://192.168.10.20:5048", row.BaseAddress);
        Assert.Equal(DiscoveryProtocol.ProtocolVersion, row.ProtocolVersion);
    }

    private static async Task AssertForbiddenPolicyGateAsync(
        OnlyLanFixture fixture,
        Guid sessionId,
        Guid participantId)
    {
        var error = await Assert.ThrowsAsync<ApiException>(() => fixture.Quiz.StartOrGetAttemptAsync(
            sessionId,
            participantId,
            CancellationToken.None));
        Assert.Equal(ErrorCodes.Forbidden, error.Code);
        Assert.Equal(403, error.StatusCode);
        Assert.Contains("chính sách giám sát", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static JoinSessionRequest JoinRequest(string roomCode, User student, string deviceId) =>
        new(
            roomCode,
            student.StudentCode!,
            student.DisplayName,
            null,
            deviceId,
            "student-machine",
            "characterization",
            Guid.NewGuid().ToString("N"));

    private static CreateSessionRequest SessionRequest(
        Guid examId,
        string roomCode,
        SessionAccessMode accessMode = SessionAccessMode.LanOnly) =>
        new(
            examId,
            null,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "{}",
            false,
            40,
            roomCode,
            accessMode,
            SessionAdmissionMode.OpenRequest);

    private static SaveControlPolicyRequest StandardPolicy(string? rowVersion = null) =>
        new(
            true,
            "BlockFocusLoss",
            "Block",
            [],
            [],
            "LanOnly",
            true,
            60,
            rowVersion);

    private static PolicyApplyAckRequest AppliedAck(int version) =>
        new(version, PolicyApplyStatus.Applied, [], null, FullCapabilities());

    private static ControlCapabilitiesDto FullCapabilities() =>
        new(true, true, true, true, true);

    private static ExamSession SessionEntity(
        Exam exam,
        string roomCode,
        SessionAccessMode accessMode,
        SessionStatus status,
        bool accepting) =>
        new()
        {
            Exam = exam,
            ExamId = exam.Id,
            RoomCode = roomCode,
            HostDeviceId = "characterization-host",
            Status = status,
            AcceptingParticipants = accepting,
            AccessMode = accessMode,
            AdmissionMode = SessionAdmissionMode.OpenRequest,
            DeliveryTypeSnapshot = exam.DeliveryType,
            SupervisionModeSnapshot = exam.SupervisionMode,
            QuizResultPolicySnapshot = exam.QuizResultPolicy,
            ExamVersionSnapshot = exam.Version
        };

    private static SessionParticipant ParticipantEntity(
        ExamSession session,
        string studentCode,
        ParticipantStatus status = ParticipantStatus.PendingApproval) =>
        new()
        {
            Session = session,
            SessionId = session.Id,
            StudentCode = studentCode,
            DisplayName = studentCode,
            DeviceId = $"{studentCode}-device",
            MachineName = $"{studentCode}-machine",
            AppVersion = "characterization",
            Status = status,
            SourceMode = "Lan"
        };

    private sealed class OnlyLanFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly string databasePath;
        private readonly AuditService audit;
        private readonly RecordingOutbox outbox;
        private readonly RecordingRealtimePublisher realtime;

        private OnlyLanFixture(
            string root,
            string databasePath,
            AppDbContext db,
            IOptions<ExamTransferOptions> options)
        {
            this.root = root;
            this.databasePath = databasePath;
            Db = db;
            Options = options;
            audit = new AuditService(db, new HttpContextAccessor());
            outbox = new RecordingOutbox();
            realtime = new RecordingRealtimePublisher();
            Cloud = new CountingThrowingCloudAdapter();
            Sessions = CreateSessionService(new StaticLanAccessPolicy(true));
            Controls = new ControlService(
                db,
                audit,
                realtime,
                outbox,
                new DeviceStatusReadExecution(db));
            Quiz = new QuizService(
                db,
                new QuizProjectionOutbox(outbox));
        }

        public AppDbContext Db { get; }
        public IOptions<ExamTransferOptions> Options { get; }
        public SessionService Sessions { get; }
        public ControlService Controls { get; }
        public QuizService Quiz { get; }
        public CountingThrowingCloudAdapter Cloud { get; }
        public IReadOnlyList<(string EntityType, string EntityId, string Operation)>
            OutboxCalls => outbox.Calls;
        public IReadOnlyList<string> RealtimeEvents => realtime.Events;

        public static async Task<OnlyLanFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "ExamTransfer.Tests",
                "OnlyLanCharacterization",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "onlylan.db");
            var db = CreateContext(databasePath);
            await db.Database.EnsureCreatedAsync();
            var options = Microsoft.Extensions.Options.Options.Create(new ExamTransferOptions());
            options.Value.Server.Port = 5048;
            options.Value.Server.UseHttps = false;
            return new(root, databasePath, db, options);
        }

        public AppDbContext CreateContext() => CreateContext(databasePath);

        public SessionService CreateSessionService(ILanAccessPolicy lanPolicy)
        {
            var tokens = new SessionTokenService(Options);
            var participantMutations = new SessionParticipantMutationDispatcher(
                Db,
                new ISessionParticipantMutationHandler[]
                {
                    new LanSessionParticipantMutationHandler(
                        Db,
                        tokens,
                        audit,
                        outbox,
                        realtime,
                        Options),
                    new PublicCloudSessionParticipantMutationHandler(
                        Options,
                        realtime,
                        Cloud)
                });
            return new SessionService(
                Db,
                audit,
                outbox,
                realtime,
                Options,
                NullLogger<SessionService>.Instance,
                participantMutations,
                new LanParticipantSessionExecution(
                    Db,
                    tokens,
                    audit,
                    outbox,
                    realtime,
                    Options,
                    lanPolicy),
                new PublicCloudProjectionExecution(Db));
        }

        public async Task<SeededQuiz> SeedPublishedQuizAsync()
        {
            var teacher = new User
            {
                Username = $"lan-teacher-{Guid.NewGuid():N}",
                DisplayName = "OnlyLAN Teacher",
                Role = UserRole.Teacher,
                IsActive = true
            };
            var student = new User
            {
                Username = $"lan-student-{Guid.NewGuid():N}",
                DisplayName = "OnlyLAN Student",
                StudentCode = $"LAN{Random.Shared.Next(100000, 999999)}",
                DateOfBirth = new DateOnly(2010, 1, 1),
                Role = UserRole.Student,
                IsActive = true,
                MustChangePassword = false
            };
            var exam = new Exam
            {
                Title = "OnlyLAN characterization exam",
                Subject = "Characterization",
                DurationMinutes = 30,
                Status = ExamStatus.Published,
                DeliveryType = ExamDeliveryType.MultipleChoice,
                SupervisionMode = SupervisionMode.Standard,
                QuizResultPolicy = QuizResultPolicy.ShowAfterSubmission,
                Version = 1,
                CreatedBy = teacher.Id
            };
            var firstQuestion = Question(exam, 1, "Question one", "A1", "B1");
            var secondQuestion = Question(exam, 2, "Question two", "A2", "B2");
            Db.AddRange(teacher, student, exam, firstQuestion, secondQuestion);
            await Db.SaveChangesAsync();
            return new(
                teacher,
                student,
                exam,
                new Dictionary<Guid, Guid>
                {
                    [firstQuestion.Id] = firstQuestion.Choices.Single(x => x.IsCorrect).Id,
                    [secondQuestion.Id] = secondQuestion.Choices.Single(x => x.IsCorrect).Id
                });
        }

        public Task<SessionDetailDto> CreateAndOpenLanSessionAsync(Guid examId, string roomCode) =>
            Sessions.CreateAndOpenAsync(
                SessionRequest(examId, roomCode),
                "teacher-host",
                CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // Test cleanup is best effort only.
            }
        }

        private static AppDbContext CreateContext(string path) =>
            new(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={path}")
                    .Options);

        private static QuizQuestion Question(
            Exam exam,
            int order,
            string text,
            string firstChoice,
            string secondChoice)
        {
            var question = new QuizQuestion
            {
                Exam = exam,
                ExamId = exam.Id,
                Version = exam.Version,
                Order = order,
                Text = text,
                Points = 5.00m,
                Multiple = false
            };
            question.Choices.Add(new QuizChoice
            {
                Question = question,
                QuestionId = question.Id,
                Order = 1,
                Text = firstChoice,
                IsCorrect = false
            });
            question.Choices.Add(new QuizChoice
            {
                Question = question,
                QuestionId = question.Id,
                Order = 2,
                Text = secondChoice,
                IsCorrect = true
            });
            return question;
        }
    }

    private sealed record SeededQuiz(
        User Teacher,
        User Student,
        Exam Exam,
        IReadOnlyDictionary<Guid, Guid> CorrectChoiceIds);

    private sealed class StaticLanAccessPolicy(bool allowed) : ILanAccessPolicy
    {
        public bool IsAllowed(string? remoteAddress) => allowed;
    }

    private sealed class RecordingOutbox : IOutboxService
    {
        public List<(string EntityType, string EntityId, string Operation)> Calls { get; } = [];

        public Task EnqueueAsync(
            string entityType,
            string entityId,
            string operation,
            object payload,
            string? filePath = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((entityType, entityId, operation));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRealtimePublisher : IRealtimePublisher
    {
        public List<string> Events { get; } = [];

        public Task PublishSessionAsync<T>(
            Guid sessionId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default)
        {
            Events.Add(eventName);
            return Task.CompletedTask;
        }

        public Task PublishParticipantAsync<T>(
            Guid sessionId,
            Guid participantId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default)
        {
            Events.Add(eventName);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingThrowingCloudAdapter : ICloudAdapter
    {
        public int CallCount { get; private set; }
        public bool Enabled => true;
        public bool Configured => true;
        public bool Authenticated => true;
        public bool CanSynchronize => true;
        public CloudLoginResult? CurrentSession => null;

        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Fail<bool>();
        public Task<CloudPreflightResult> PreflightAsync(CancellationToken cancellationToken) => Fail<CloudPreflightResult>();
        public Task<CloudPushResult> PushAsync(
            SyncQueueItem item,
            Func<CancellationToken, Task>? checkpoint,
            CancellationToken cancellationToken) => Fail<CloudPushResult>();
        public Task<CloudLoginResult> LoginAsync(
            string email,
            string password,
            CancellationToken cancellationToken) => Fail<CloudLoginResult>();
        public Task<CloudLoginResult?> RefreshSessionAsync(CancellationToken cancellationToken) => Fail<CloudLoginResult?>();
        public Task LogoutAsync(CancellationToken cancellationToken) => Fail();
        public Task<IReadOnlyList<CloudBackupDescriptor>> ListBackupsAsync(CancellationToken cancellationToken) =>
            Fail<IReadOnlyList<CloudBackupDescriptor>>();
        public Task DownloadObjectAsync(
            string cloudObjectPath,
            string destinationPath,
            CancellationToken cancellationToken) => Fail();

        private Task Fail()
        {
            CallCount++;
            return Task.FromException(new IOException("Cloud must not be called by OnlyLAN characterization."));
        }

        private Task<T> Fail<T>()
        {
            CallCount++;
            return Task.FromException<T>(new IOException("Cloud must not be called by OnlyLAN characterization."));
        }
    }
}
