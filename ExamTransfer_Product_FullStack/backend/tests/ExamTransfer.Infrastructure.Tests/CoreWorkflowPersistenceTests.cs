using ExamTransfer.Application;
using System.Text.Json;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Execution;
using ExamTransfer.Infrastructure.Execution.Dispatch;
using ExamTransfer.Infrastructure.Execution.OnlyLan;
using ExamTransfer.Infrastructure.Execution.PublicCloud;
using ExamTransfer.Infrastructure.Importing;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class CoreWorkflowPersistenceTests
{
    [Fact]
    public async Task Dashboard_AfterClassCreation_ReturnsUpdatedRealClassCount()
    {
        await using var database = await FileDatabase.CreateAsync();

        int initialClassCount;
        await using (var initialContext = database.CreateContext())
        {
            initialClassCount = (await DashboardService(initialContext).GetDashboardAsync(CancellationToken.None)).ClassCount;
        }

        Guid classId;
        await using (var createContext = database.CreateContext())
        {
            var created = await Services(createContext).Classes.CreateAsync(
                new("Dashboard refresh", "DASH-REFRESH", "2026-2027", "Physical SQLite test"),
                CancellationToken.None);
            classId = created.Id;
        }

        await using (var refreshedContext = database.CreateContext())
        {
            var refreshed = await DashboardService(refreshedContext).GetDashboardAsync(CancellationToken.None);
            Assert.Equal(initialClassCount + 1, refreshed.ClassCount);
        }

        await using (var reopenedContext = database.CreateContext())
        {
            var reopened = await DashboardService(reopenedContext).GetDashboardAsync(CancellationToken.None);
            Assert.Equal(initialClassCount + 1, reopened.ClassCount);
        }

        await using (var archiveContext = database.CreateContext())
        {
            await Services(archiveContext).Classes.ArchiveAsync(classId, CancellationToken.None);
        }

        await using (var archivedContext = database.CreateContext())
        {
            var archived = await DashboardService(archivedContext).GetDashboardAsync(CancellationToken.None);
            Assert.Equal(initialClassCount, archived.ClassCount);
        }
    }

    [Fact]
    public async Task Dashboard_WithNoSessions_ReturnsNullActiveSessionAndEmptyHistory()
    {
        await using var database = await FileDatabase.CreateAsync();

        var dashboard = await DashboardService(database.Context).GetDashboardAsync(CancellationToken.None);

        Assert.Null(dashboard.ActiveSession);
        Assert.Empty(dashboard.RecentSessions);
    }

    [Theory]
    [InlineData(SessionStatus.Finished)]
    [InlineData(SessionStatus.Cancelled)]
    [InlineData(SessionStatus.Archived)]
    public async Task Dashboard_TerminalSession_RemainsInHistoryButIsNotActive(SessionStatus status)
    {
        await using var database = await FileDatabase.CreateAsync();
        var session = await SeedDashboardSessionAsync(database.Context, status, DateTimeOffset.UtcNow);

        var dashboard = await DashboardService(database.Context).GetDashboardAsync(CancellationToken.None);

        Assert.Null(dashboard.ActiveSession);
        Assert.Contains(dashboard.RecentSessions, item => item.Id == session.Id && item.Status == status);
    }

    [Theory]
    [InlineData(SessionStatus.Waiting)]
    [InlineData(SessionStatus.InProgress)]
    [InlineData(SessionStatus.Paused)]
    [InlineData(SessionStatus.Collecting)]
    public async Task Dashboard_ActiveSessionStatus_IsEligible(SessionStatus status)
    {
        await using var database = await FileDatabase.CreateAsync();
        var session = await SeedDashboardSessionAsync(database.Context, status, DateTimeOffset.UtcNow);

        var dashboard = await DashboardService(database.Context).GetDashboardAsync(CancellationToken.None);

        Assert.Equal(session.Id, dashboard.ActiveSession?.Id);
        Assert.Equal(status, dashboard.ActiveSession?.Status);
    }

    [Fact]
    public async Task Dashboard_NewerFinishedSession_DoesNotHideOlderInProgressSession()
    {
        await using var database = await FileDatabase.CreateAsync();
        var inProgress = await SeedDashboardSessionAsync(
            database.Context,
            SessionStatus.InProgress,
            DateTimeOffset.UtcNow.AddMinutes(-10));
        ExamSession? newestFinished = null;
        for (var index = 0; index < 5; index++)
        {
            newestFinished = await SeedDashboardSessionAsync(
                database.Context,
                SessionStatus.Finished,
                DateTimeOffset.UtcNow.AddMinutes(index - 4));
        }

        var dashboard = await DashboardService(database.Context).GetDashboardAsync(CancellationToken.None);

        Assert.Equal(newestFinished!.Id, dashboard.RecentSessions[0].Id);
        Assert.DoesNotContain(dashboard.RecentSessions, item => item.Id == inProgress.Id);
        Assert.Equal(inProgress.Id, dashboard.ActiveSession?.Id);
    }

    [Fact]
    public async Task Dashboard_MultipleActiveSessions_UsesExistingRecentSortOrder()
    {
        await using var database = await FileDatabase.CreateAsync();
        var older = await SeedDashboardSessionAsync(
            database.Context,
            SessionStatus.Waiting,
            DateTimeOffset.UtcNow.AddMinutes(-10));
        var newer = await SeedDashboardSessionAsync(
            database.Context,
            SessionStatus.Paused,
            DateTimeOffset.UtcNow);

        var dashboard = await DashboardService(database.Context).GetDashboardAsync(CancellationToken.None);

        Assert.Equal(newer.Id, dashboard.RecentSessions[0].Id);
        Assert.Equal(older.Id, dashboard.RecentSessions[1].Id);
        Assert.Equal(newer.Id, dashboard.ActiveSession?.Id);
    }

    [Fact]
    public async Task Dashboard_FinishedDeadline_DoesNotDetermineActiveSession()
    {
        await using var database = await FileDatabase.CreateAsync();
        var pastDeadline = await SeedDashboardSessionAsync(
            database.Context,
            SessionStatus.Finished,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(-2));
        var futureDeadline = await SeedDashboardSessionAsync(
            database.Context,
            SessionStatus.Finished,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(2));

        var dashboard = await DashboardService(database.Context).GetDashboardAsync(CancellationToken.None);

        Assert.Null(dashboard.ActiveSession);
        Assert.Contains(dashboard.RecentSessions, item => item.Id == pastDeadline.Id);
        Assert.Contains(dashboard.RecentSessions, item => item.Id == futureDeadline.Id);
    }

    [Fact]
    public void DashboardContract_IsAdditiveAndPreservesRecentSessions()
    {
        var session = DashboardSessionSummary(SessionStatus.InProgress);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacyJson = JsonSerializer.Serialize(
            new
            {
                classCount = 1,
                examCount = 1,
                activeSessionCount = 1,
                pendingGradingCount = 0,
                storageBytes = 0,
                recentSessions = new[] { session },
                warnings = Array.Empty<string>()
            },
            options);

        var fromLegacyPayload = JsonSerializer.Deserialize<DashboardSummaryDto>(legacyJson, options);
        var roundTrip = JsonSerializer.Deserialize<DashboardSummaryDto>(
            JsonSerializer.Serialize(fromLegacyPayload! with { ActiveSession = session }, options),
            options);

        Assert.NotNull(fromLegacyPayload);
        Assert.Null(fromLegacyPayload.ActiveSession);
        Assert.Equal(session.Id, Assert.Single(fromLegacyPayload.RecentSessions).Id);
        Assert.Equal(session.Id, roundTrip?.ActiveSession?.Id);
        Assert.Equal(session.Id, Assert.Single(roundTrip!.RecentSessions).Id);
    }

    [Fact]
    public async Task Class_Create_PersistsAcrossNewDbContext()
    {
        await using var database = await FileDatabase.CreateAsync();
        var created = await Services(database.Context).Classes.CreateAsync(
            new("Lớp 10A", "10A", "2026-2027", "Persistence"), CancellationToken.None);

        await using var restarted = database.CreateContext();
        var persisted = await restarted.ClassesSet.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal("10A", persisted.Code);
    }

    [Fact]
    public async Task Class_Update_PersistsAcrossNewDbContext()
    {
        await using var database = await FileDatabase.CreateAsync();
        var classes = Services(database.Context).Classes;
        var created = await classes.CreateAsync(new("Lớp cũ", "OLD", "2026-2027", null), CancellationToken.None);
        var updated = await classes.UpdateAsync(created.Id, new("Lớp mới", "NEW", "2026-2027", "Updated", created.RowVersion), CancellationToken.None);

        await using var restarted = database.CreateContext();
        var persisted = await restarted.ClassesSet.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal(updated.RowVersion, persisted.RowVersion);
        Assert.Equal("NEW", persisted.Code);
    }

    [Fact]
    public async Task Exam_CreateLinkedToClass_PersistsAcrossNewDbContext()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var classroom = await services.Classes.CreateAsync(new("10B", "10B", "2026-2027", null), CancellationToken.None);
        var exam = await services.Exams.CreateAsync(ExamRequest(classroom.Id, false), CancellationToken.None);

        await using var restarted = database.CreateContext();
        Assert.Equal(classroom.Id, (await restarted.ExamsSet.AsNoTracking().SingleAsync(x => x.Id == exam.Id)).ClassId);
    }

    [Fact]
    public async Task Exam_PublishWithoutFile_Succeeds_WhenRuleDoesNotRequireFile()
    {
        await using var database = await FileDatabase.CreateAsync();
        var exams = Services(database.Context).Exams;
        var exam = await exams.CreateAsync(ExamRequest(null, false), CancellationToken.None);

        var published = await exams.PublishAsync(exam.Id, CancellationToken.None);

        Assert.Equal(ExamStatus.Published, published.Status);
    }

    [Fact]
    public async Task Exam_PublishWithoutFile_Fails_WhenRuleRequiresFile()
    {
        await using var database = await FileDatabase.CreateAsync();
        var exams = Services(database.Context).Exams;
        var exam = await exams.CreateAsync(ExamRequest(null, true), CancellationToken.None);

        var error = await Assert.ThrowsAsync<ApiException>(() => exams.PublishAsync(exam.Id, CancellationToken.None));

        Assert.Equal(ErrorCodes.ValidationFailed, error.Code);
        Assert.Equal(422, error.StatusCode);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(ExamStatus.Draft, (await database.Context.ExamsSet.SingleAsync(x => x.Id == exam.Id)).Status);
    }

    [Fact]
    public async Task Exam_ListAfterPublish_WorksOnSqlite_AndIncludesCompletedFileCount()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var exam = await services.Exams.CreateAsync(ExamRequest(null, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);

        var page = await services.Exams.ListAsync(null, ExamStatus.Published, 1, 50, CancellationToken.None);

        var listed = Assert.Single(page.Items);
        Assert.Equal(exam.Id, listed.Id);
        Assert.Equal(ExamStatus.Published, listed.Status);
    }

    [Fact]
    public async Task TeacherExamFile_AllowsArbitraryExtensionAboveStudentTenMiBLimit_WithChunks()
    {
        await using var database = await FileDatabase.CreateAsync();
        var exams = Services(database.Context).Exams;
        var exam = await exams.CreateAsync(ExamRequest(null, false), CancellationToken.None);
        var bytes = new byte[StudentSubmissionPolicy.MaxBytes + 1];
        Random.Shared.NextBytes(bytes);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        var init = await exams.InitFileAsync(exam.Id, new("teacher-material.custom", bytes.LongLength, sha, "application/octet-stream", 1024 * 1024), CancellationToken.None);
        for (var index = 0; index < init.TotalChunks; index++)
        {
            var offset = index * init.ChunkSizeBytes;
            var length = Math.Min(init.ChunkSizeBytes, bytes.Length - offset);
            await using var chunk = new MemoryStream(bytes, offset, length, false, true);
            await exams.UploadChunkAsync(exam.Id, init.FileId, index, chunk, length, null, CancellationToken.None);
        }
        var finalized = await exams.FinalizeFileAsync(exam.Id, init.FileId, new(sha), CancellationToken.None);

        Assert.Equal("teacher-material.custom", finalized.Name);
        Assert.Equal(bytes.LongLength, finalized.SizeBytes);
        Assert.True(finalized.SizeBytes > StudentSubmissionPolicy.MaxBytes);
    }

    [Fact]
    public async Task ClassAndSessionLists_WorkOnSqlite_WithDateTimeOffsetSortKeys()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var classroom = await services.Classes.CreateAsync(new("List", "LIST", "2026-2027", null), CancellationToken.None);
        var exam = await services.Exams.CreateAsync(ExamRequest(classroom.Id, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);
        var session = await services.Sessions.CreateAsync(SessionRequest(exam.Id, classroom.Id, "LIST01"), "test-host", CancellationToken.None);

        var classes = await services.Classes.ListAsync(null, 1, 50, CancellationToken.None);
        var sessions = await services.Sessions.ListAsync(null, 1, 50, CancellationToken.None);

        Assert.Contains(classes.Items, x => x.Id == classroom.Id);
        Assert.Contains(sessions.Items, x => x.Id == session.Summary.Id);
    }

    [Fact]
    public async Task PublishedExam_CanCreateSession_WithSameClass()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var classroom = await services.Classes.CreateAsync(new("10C", "10C", "2026-2027", null), CancellationToken.None);
        var exam = await services.Exams.CreateAsync(ExamRequest(classroom.Id, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);

        var session = await services.Sessions.CreateAsync(SessionRequest(exam.Id, classroom.Id, "ROOM10C"), "test-host", CancellationToken.None);

        Assert.Equal(classroom.Id, await database.Context.ExamSessionsSet.Where(x => x.Id == session.Summary.Id).Select(x => x.ClassId).SingleAsync());
    }

    [Fact]
    public async Task Session_ValidLifecycle_Works()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var exam = await services.Exams.CreateAsync(ExamRequest(null, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);
        var session = await services.Sessions.CreateAsync(SessionRequest(exam.Id, null, "FLOW01"), "test-host", CancellationToken.None);

        foreach (var target in new[] { SessionStatus.Waiting, SessionStatus.Distributing, SessionStatus.InProgress, SessionStatus.Paused, SessionStatus.InProgress, SessionStatus.Collecting, SessionStatus.Finished })
            session = await services.Sessions.TransitionAsync(session.Summary.Id, target, target == SessionStatus.Finished ? new(false, null) : null, CancellationToken.None);

        Assert.Equal(SessionStatus.Finished, session.Summary.Status);
    }

    [Fact]
    public async Task Session_InvalidTransition_ReturnsInvalidStateTransition_AndLeavesStateUnchanged()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var exam = await services.Exams.CreateAsync(ExamRequest(null, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);
        var session = await services.Sessions.CreateAsync(SessionRequest(exam.Id, null, "BADFLOW"), "test-host", CancellationToken.None);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => services.Sessions.TransitionAsync(session.Summary.Id, SessionStatus.Paused, null, CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidStateTransition, error.Code);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(SessionStatus.Draft, (await database.Context.ExamSessionsSet.SingleAsync(x => x.Id == session.Summary.Id)).Status);
    }

    [Fact]
    public async Task SessionHeartbeat_ReturnsTheServerTimestampPersistedAsLastSeen()
    {
        await using var database = await FileDatabase.CreateAsync();
        var exam = new Exam
        {
            Title = "Heartbeat",
            Subject = "Clock",
            DurationMinutes = 30,
            Status = ExamStatus.Published
        };
        var session = new ExamSession
        {
            Exam = exam,
            ExamId = exam.Id,
            RoomCode = "CLOCK01",
            Status = SessionStatus.InProgress,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var participant = new SessionParticipant
        {
            Session = session,
            SessionId = session.Id,
            StudentCode = "CLOCK-STUDENT",
            DisplayName = "Clock Student",
            DeviceId = "clock-device",
            MachineName = "clock-machine",
            AppVersion = "1.0",
            Status = ParticipantStatus.Approved,
            ApprovedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        database.Context.ExamsSet.Add(exam);
        database.Context.ExamSessionsSet.Add(session);
        database.Context.SessionParticipantsSet.Add(participant);
        await database.Context.SaveChangesAsync();

        var before = DateTimeOffset.UtcNow;
        var response = await Services(database.Context).Sessions.HeartbeatAsync(
            session.Id,
            participant.Id,
            participant.DeviceId,
            new HeartbeatRequest("Ready", before, 0),
            CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        database.Context.ChangeTracker.Clear();
        var persistedLastSeen = await database.Context.SessionParticipantsSet
            .Where(x => x.Id == participant.Id)
            .Select(x => x.LastSeenUtc)
            .SingleAsync();
        Assert.InRange(response.ServerNowUtc, before, after);
        Assert.Equal(response.ServerNowUtc, persistedLastSeen);
    }

    [Fact]
    public async Task SessionExtraTime_UpdatesExistingLocalQuizAttemptDeadline()
    {
        await using var database = await FileDatabase.CreateAsync();
        var startedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        var exam = new Exam
        {
            Title = "Extra time",
            Subject = "Quiz",
            DurationMinutes = 30,
            DeliveryType = ExamDeliveryType.MultipleChoice,
            Status = ExamStatus.Published
        };
        var session = new ExamSession
        {
            Exam = exam,
            ExamId = exam.Id,
            RoomCode = "EXTIME01",
            Status = SessionStatus.InProgress,
            StartedAtUtc = startedAtUtc
        };
        var participant = new SessionParticipant
        {
            Session = session,
            SessionId = session.Id,
            StudentCode = "EXTRA-STUDENT",
            DisplayName = "Extra Student",
            DeviceId = "extra-device",
            MachineName = "extra-machine",
            AppVersion = "1.0",
            Status = ParticipantStatus.Approved,
            ApprovedAtUtc = startedAtUtc
        };
        var attempt = new QuizAttempt
        {
            Session = session,
            SessionId = session.Id,
            Participant = participant,
            ParticipantId = participant.Id,
            Status = QuizAttemptStatus.InProgress,
            StartedAtUtc = startedAtUtc,
            DeadlineUtc = startedAtUtc.AddMinutes(exam.DurationMinutes),
            SnapshotJson = "[]"
        };
        database.Context.ExamsSet.Add(exam);
        database.Context.ExamSessionsSet.Add(session);
        database.Context.SessionParticipantsSet.Add(participant);
        database.Context.QuizAttemptsSet.Add(attempt);
        await database.Context.SaveChangesAsync();

        var updated = await Services(database.Context).Sessions.AddExtraTimeAsync(
            session.Id,
            participant.Id,
            new ExtraTimeRequest(10, "Hỗ trợ kỹ thuật", Guid.NewGuid()),
            CancellationToken.None);

        database.Context.ChangeTracker.Clear();
        var persistedDeadline = await database.Context.QuizAttemptsSet
            .Where(x => x.Id == attempt.Id)
            .Select(x => x.DeadlineUtc)
            .SingleAsync();
        Assert.Equal(10, updated.ExtraTimeMinutes);
        Assert.Equal(startedAtUtc.AddMinutes(40), updated.EffectiveDeadlineUtc);
        Assert.Equal(updated.EffectiveDeadlineUtc, persistedDeadline);
    }

    [Fact]
    public async Task RealtimeFailure_AfterCommit_DoesNotTurnLocalTransitionIntoFailure()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context, new ThrowingRealtimePublisher());
        var exam = await services.Exams.CreateAsync(ExamRequest(null, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);
        var session = await services.Sessions.CreateAsync(SessionRequest(exam.Id, null, "RTFAIL"), "test-host", CancellationToken.None);

        var transitioned = await services.Sessions.TransitionAsync(session.Summary.Id, SessionStatus.Waiting, null, CancellationToken.None);

        Assert.Equal(SessionStatus.Waiting, transitioned.Summary.Status);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(SessionStatus.Waiting, (await database.Context.ExamSessionsSet.SingleAsync(x => x.Id == session.Summary.Id)).Status);
    }

    [Fact]
    public async Task CloudOffline_DoesNotBreakLocalCreatePublishSessionWorkflow()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var classroom = await services.Classes.CreateAsync(new("Offline", "OFFLINE", "2026-2027", null), CancellationToken.None);
        var exam = await services.Exams.CreateAsync(ExamRequest(classroom.Id, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);
        var session = await services.Sessions.CreateAsync(SessionRequest(exam.Id, classroom.Id, "OFFLINE1"), "test-host", CancellationToken.None);

        Assert.Equal(SessionStatus.Draft, session.Summary.Status);
        Assert.True(await database.Context.SyncQueueSet.AnyAsync(x => x.EntityId == classroom.Id.ToString()));
        Assert.True(await database.Context.SyncQueueSet.AnyAsync(x => x.EntityId == exam.Id.ToString()));
        Assert.True(await database.Context.SyncQueueSet.AnyAsync(x => x.EntityId == session.Summary.Id.ToString()));
    }

    [Fact]
    public async Task StudentJoinValidationTests_EnforcesMembershipAndApprovalMode()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var classroom = await services.Classes.CreateAsync(new("Join", "JOIN", "2026-2027", null), CancellationToken.None);
        await services.Classes.AddStudentAsync(classroom.Id, new("SV001", "Sinh viên Một", null, null), CancellationToken.None);
        var exam = await services.Exams.CreateAsync(ExamRequest(classroom.Id, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);

        var approvalSession = await services.Sessions.CreateAsync(
            new(exam.Id, classroom.Id, null, "{}", false, 40, "JOIN01"), "host", CancellationToken.None);
        await services.Sessions.TransitionAsync(approvalSession.Summary.Id, SessionStatus.Waiting, null, CancellationToken.None);
        var pending = await services.Sessions.JoinAsync(
            new("JOIN01", "SV001", "Sinh viên Một", "Join", "device-1", "machine", "1", "nonce"),
            Guid.NewGuid(), "SV001", "Sinh viên Một", "127.0.0.1", CancellationToken.None);
        Assert.Equal(ParticipantStatus.PendingApproval, pending.Status);

        var automaticSession = await services.Sessions.CreateAsync(
            new(exam.Id, classroom.Id, null, "{}", true, 40, "JOIN02"), "host", CancellationToken.None);
        await services.Sessions.TransitionAsync(automaticSession.Summary.Id, SessionStatus.Waiting, null, CancellationToken.None);
        var approved = await services.Sessions.JoinAsync(
            new("JOIN02", "SV001", "Sinh viên Một", "Join", "device-2", "machine", "1", "nonce"),
            Guid.NewGuid(), "SV001", "Sinh viên Một", "127.0.0.1", CancellationToken.None);
        Assert.Equal(ParticipantStatus.Approved, approved.Status);

        var outsider = await Assert.ThrowsAsync<ApiException>(() => services.Sessions.JoinAsync(
            new("JOIN02", "SV999", "Ngoài lớp", "Join", "device-3", "machine", "1", "nonce"),
            Guid.NewGuid(), "SV999", "Ngoài lớp", "127.0.0.1", CancellationToken.None));
        Assert.Equal(ErrorCodes.Forbidden, outsider.Code);
        Assert.Contains("chưa có tên trong lớp học", outsider.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenRequest_CreateAndOpen_AllowsClasslessStudentApprovalAndStart()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var exam = await services.Exams.CreateAsync(ExamRequest(null, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);

        var session = await services.Sessions.CreateAndOpenAsync(
            new(
                exam.Id,
                null,
                DateTimeOffset.UtcNow.AddMinutes(5),
                "{}",
                false,
                40,
                "OPEN01",
                SessionAccessMode.LanOnly,
                SessionAdmissionMode.OpenRequest),
            "host",
            CancellationToken.None);
        var joined = await services.Sessions.JoinAsync(
            new("OPEN01", "SV-OPEN", "Học sinh mở", null, "device-open", "machine", "1", "nonce"),
            Guid.NewGuid(),
            "SV-OPEN",
            "Học sinh mở",
            "127.0.0.1",
            CancellationToken.None);

        Assert.Null(await database.Context.ExamSessionsSet
            .Where(x => x.Id == session.Summary.Id)
            .Select(x => x.ClassId)
            .SingleAsync());
        Assert.Equal(SessionAdmissionMode.OpenRequest, session.Summary.AdmissionMode);
        Assert.Equal(SessionStatus.Waiting, session.Summary.Status);
        Assert.True((await database.Context.ExamSessionsSet.SingleAsync(x => x.Id == session.Summary.Id)).AcceptingParticipants);
        Assert.Equal(ParticipantStatus.PendingApproval, joined.Status);

        var approved = await services.Sessions.ApproveAsync(
            session.Summary.Id,
            joined.ParticipantId,
            Guid.NewGuid(),
            CancellationToken.None);
        var started = await services.Sessions.TransitionAsync(
            session.Summary.Id,
            SessionStatus.InProgress,
            null,
            CancellationToken.None);
        Assert.Equal(ParticipantStatus.Approved, approved.Status);
        Assert.Equal(SessionStatus.InProgress, started.Summary.Status);
    }

    [Fact]
    public async Task SessionAdmissionMode_RejectsAmbiguousOpenClass_AndLocalPublicCloudJoin()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var classroom = await services.Classes.CreateAsync(
            new("Legacy", "LEGACY", "2026-2027", null, ClassAccessMode.Public),
            CancellationToken.None);
        var exam = await services.Exams.CreateAsync(ExamRequest(classroom.Id, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);

        var ambiguous = await Assert.ThrowsAsync<ApiException>(() => services.Sessions.CreateAsync(
            new(
                exam.Id,
                classroom.Id,
                null,
                "{}",
                false,
                40,
                "AMBIG01",
                SessionAccessMode.LanOnly,
                SessionAdmissionMode.OpenRequest),
            "host",
            CancellationToken.None));
        Assert.Equal(ErrorCodes.ValidationFailed, ambiguous.Code);

        var cloud = await services.Sessions.CreateAndOpenAsync(
            new(
                exam.Id,
                null,
                null,
                "{}",
                false,
                40,
                "CLOUD01",
                SessionAccessMode.PublicCloud,
                SessionAdmissionMode.OpenRequest),
            "host",
            CancellationToken.None);
        Assert.Equal(SessionStatus.Waiting, cloud.Summary.Status);

        var routeError = await Assert.ThrowsAsync<ApiException>(() => services.Sessions.JoinAsync(
            new("CLOUD01", "SV-CLOUD", "Học sinh cloud", null, "device-cloud", "machine", "1", "nonce"),
            Guid.NewGuid(),
            "SV-CLOUD",
            "Học sinh cloud",
            "127.0.0.1",
            CancellationToken.None));
        Assert.Equal(ErrorCodes.PublicCloudRouteRequired, routeError.Code);
    }

    [Fact]
    public async Task RestartPersistence_RetainsIdsAndFinalStates_InSamePhysicalSqliteFile()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var classroom = await services.Classes.CreateAsync(new("Restart", "RESTART", "2026-2027", null), CancellationToken.None);
        var exam = await services.Exams.CreateAsync(ExamRequest(classroom.Id, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);
        var session = await services.Sessions.CreateAsync(SessionRequest(exam.Id, classroom.Id, "RESTART1"), "test-host", CancellationToken.None);
        await services.Sessions.TransitionAsync(session.Summary.Id, SessionStatus.Waiting, null, CancellationToken.None);

        await database.Context.DisposeAsync();
        await using var restarted = database.CreateContext();
        Assert.True(await restarted.ClassesSet.AnyAsync(x => x.Id == classroom.Id));
        Assert.Equal(ExamStatus.Published, (await restarted.ExamsSet.SingleAsync(x => x.Id == exam.Id)).Status);
        Assert.Equal(SessionStatus.Waiting, (await restarted.ExamSessionsSet.SingleAsync(x => x.Id == session.Summary.Id)).Status);
    }

    [Fact]
    public async Task ExamPolicies_AreTypedNormalizedAndImmutableAfterSession()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var fileRule = new FileRuleDto([".txt"], 1024 * 1024, 2 * 1024 * 1024, 2, false, false);

        var invalid = await Assert.ThrowsAsync<ApiException>(() => services.Exams.CreateAsync(
            new(null, "Invalid quiz", "Rules", null, 30, fileRule,
                ExamDeliveryType.MultipleChoice,
                QuizResultPolicy.Hidden,
                SupervisionMode.None),
            CancellationToken.None));
        Assert.Equal(ErrorCodes.ValidationFailed, invalid.Code);

        var fileExam = await services.Exams.CreateAsync(
            new(null, "Essay", "Rules", null, 45, fileRule,
                ExamDeliveryType.FileSubmission,
                QuizResultPolicy.ShowAfterSubmission,
                SupervisionMode.Standard),
            CancellationToken.None);
        Assert.Equal(QuizResultPolicy.Hidden, fileExam.QuizResultPolicy);
        Assert.Equal(SupervisionMode.Standard, fileExam.SupervisionMode);

        await services.Exams.PublishAsync(fileExam.Id, CancellationToken.None);
        var createdSession = await services.Sessions.CreateAsync(
            SessionRequest(fileExam.Id, null, "POLICY1"),
            "host",
            CancellationToken.None);
        Assert.Equal(ExamDeliveryType.FileSubmission, createdSession.Summary.DeliveryType);
        Assert.Equal(SupervisionMode.Standard, createdSession.Summary.SupervisionMode);
        Assert.Equal(QuizResultPolicy.Hidden, createdSession.Summary.QuizResultPolicy);
        Assert.Equal(fileExam.Version, createdSession.Summary.ExamVersion);

        var immutable = await Assert.ThrowsAsync<ApiException>(() => services.Exams.UpdateAsync(
            fileExam.Id,
            new(
                null,
                fileExam.Title,
                fileExam.Subject,
                fileExam.Description,
                fileExam.DurationMinutes,
                fileRule,
                (database.Context.ExamsSet.Single(x => x.Id == fileExam.Id)).RowVersion,
                ExamDeliveryType.MultipleChoice,
                QuizResultPolicy.Hidden,
                SupervisionMode.Standard),
            CancellationToken.None));
        Assert.Equal(ErrorCodes.InvalidStateTransition, immutable.Code);
    }

    [Fact]
    public async Task ExamDuration_IsEditableOnlyBeforePublishSessionOrAttempt_AndCloneRemainsEditable()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var rule = new FileRuleDto([".txt"], 1024 * 1024, 2 * 1024 * 1024, 2, false, false);
        var draft = await services.Exams.CreateAsync(
            new(null, "Duration", "Rules", null, 30, rule),
            CancellationToken.None);

        var draftUpdated = await services.Exams.UpdateAsync(
            draft.Id,
            new(null, draft.Title, draft.Subject, draft.Description, 45, rule, draft.RowVersion),
            CancellationToken.None);
        Assert.Equal(45, draftUpdated.DurationMinutes);

        var published = await services.Exams.PublishAsync(draft.Id, CancellationToken.None);
        var publishedError = await Assert.ThrowsAsync<ApiException>(() => services.Exams.UpdateAsync(
            published.Id,
            new(null, published.Title, published.Subject, published.Description, 50, rule, published.RowVersion),
            CancellationToken.None));
        Assert.Equal(ErrorCodes.ExamDurationImmutable, publishedError.Code);
        Assert.Equal(409, publishedError.StatusCode);
        Assert.Contains("nhân bản", publishedError.Message, StringComparison.OrdinalIgnoreCase);

        var titleUpdated = await services.Exams.UpdateAsync(
            published.Id,
            new(null, "Duration renamed", published.Subject, published.Description, 45, rule, published.RowVersion),
            CancellationToken.None);
        Assert.Equal("Duration renamed", titleUpdated.Title);
        Assert.Equal(45, titleUpdated.DurationMinutes);

        var cloned = await services.Exams.CloneAsync(titleUpdated.Id, CancellationToken.None);
        Assert.Equal(ExamStatus.Draft, cloned.Status);
        var cloneUpdated = await services.Exams.UpdateAsync(
            cloned.Id,
            new(null, cloned.Title, cloned.Subject, cloned.Description, 75, rule, cloned.RowVersion),
            CancellationToken.None);
        Assert.Equal(75, cloneUpdated.DurationMinutes);

        var sessionDraft = await services.Exams.CreateAsync(
            new(null, "Session duration", "Rules", null, 30, rule),
            CancellationToken.None);
        database.Context.ExamSessionsSet.Add(new ExamSession
        {
            ExamId = sessionDraft.Id,
            RoomCode = "DURSESS",
            HostDeviceId = "host",
            Status = SessionStatus.Draft
        });
        await database.Context.SaveChangesAsync();
        var sessionError = await Assert.ThrowsAsync<ApiException>(() => services.Exams.UpdateAsync(
            sessionDraft.Id,
            new(null, sessionDraft.Title, sessionDraft.Subject, sessionDraft.Description, 31, rule,
                database.Context.ExamsSet.Single(x => x.Id == sessionDraft.Id).RowVersion),
            CancellationToken.None));
        Assert.Equal(ErrorCodes.ExamDurationImmutable, sessionError.Code);

        var attemptDraft = await services.Exams.CreateAsync(
            new(null, "Attempt duration", "Rules", null, 30, rule,
                ExamDeliveryType.MultipleChoice, QuizResultPolicy.Hidden, SupervisionMode.Standard),
            CancellationToken.None);
        var attemptSession = new ExamSession
        {
            ExamId = attemptDraft.Id,
            RoomCode = "DURATT",
            HostDeviceId = "host",
            Status = SessionStatus.InProgress
        };
        var participant = new SessionParticipant
        {
            Session = attemptSession,
            StudentCode = "SV-DURATION",
            DisplayName = "Duration Student",
            DeviceId = "device",
            MachineName = "machine",
            AppVersion = "1",
            Status = ParticipantStatus.Approved
        };
        database.Context.QuizAttemptsSet.Add(new QuizAttempt
        {
            Session = attemptSession,
            Participant = participant,
            ExamVersion = 1,
            StartedAtUtc = DateTimeOffset.UtcNow,
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(30),
            MaxScore = 1
        });
        await database.Context.SaveChangesAsync();
        var attemptError = await Assert.ThrowsAsync<ApiException>(() => services.Exams.UpdateAsync(
            attemptDraft.Id,
            new(null, attemptDraft.Title, attemptDraft.Subject, attemptDraft.Description, 31, rule,
                database.Context.ExamsSet.Single(x => x.Id == attemptDraft.Id).RowVersion,
                ExamDeliveryType.MultipleChoice, QuizResultPolicy.Hidden, SupervisionMode.Standard),
            CancellationToken.None));
        Assert.Equal(ErrorCodes.ExamDurationImmutable, attemptError.Code);
    }

    [Fact]
    public async Task CloneMultipleChoice_CopiesIndependentSourceAndEnqueuesCompleteOrderedCloudGraph()
    {
        await using var database = await FileDatabase.CreateAsync();
        var db = database.Context;
        var storageRoot = Path.Combine(Path.GetDirectoryName(db.Database.GetDbConnection().DataSource)!, "storage");
        var paths = new TestStoragePaths(storageRoot);
        paths.EnsureCreated();
        var sourceExam = new Exam
        {
            Title = "Quiz clone source",
            Subject = "Math",
            DurationMinutes = 40,
            DeliveryType = ExamDeliveryType.MultipleChoice,
            QuizResultPolicy = QuizResultPolicy.ShowAfterSubmission,
            SupervisionMode = SupervisionMode.Standard,
            Status = ExamStatus.Published,
            Version = 3
        };
        var question = new QuizQuestion
        {
            Version = 3,
            Order = 1,
            Text = "2 + 2?",
            Points = 1,
            Multiple = false
        };
        question.Choices.Add(new QuizChoice { Order = 1, Text = "4", IsCorrect = true });
        sourceExam.QuizQuestions.Add(question);
        var sourcePath = Path.Combine(paths.ExamVersionRoot(sourceExam.Id, sourceExam.Version), "quiz-source", "original.docx");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, "quiz source");
        var sourceDocument = new QuizImportSource
        {
            ExamVersion = 3,
            OriginalName = "source.docx",
            MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            SizeBytes = new FileInfo(sourcePath).Length,
            Sha256 = "abc123",
            RelativePath = Path.GetRelativePath(paths.RootPath, sourcePath),
            Status = "Committed",
            CreatedBy = Guid.NewGuid(),
            ImportedAtUtc = DateTimeOffset.UtcNow
        };
        sourceExam.QuizImportSources.Add(sourceDocument);
        db.ExamsSet.Add(sourceExam);
        await db.SaveChangesAsync();

        var outbox = new RecordingOutbox();
        var service = new ExamService(
            db,
            paths,
            new ChunkStorage(),
            new AuditService(db, new HttpContextAccessor()),
            outbox,
            new NoOpRealtimePublisher(),
            Options.Create(new ExamTransferOptions()),
            NullLogger<ExamService>.Instance);

        var clone = await service.CloneAsync(sourceExam.Id, CancellationToken.None);

        Assert.Equal(ExamStatus.Draft, clone.Status);
        Assert.Equal(sourceExam.DeliveryType, clone.DeliveryType);
        Assert.Equal(sourceExam.QuizResultPolicy, clone.QuizResultPolicy);
        Assert.Equal(sourceExam.SupervisionMode, clone.SupervisionMode);
        Assert.Equal(sourceExam.DurationMinutes, clone.DurationMinutes);
        var clonedEntity = await db.ExamsSet.AsNoTracking()
            .Include(x => x.QuizQuestions).ThenInclude(x => x.Choices)
            .Include(x => x.QuizImportSources)
            .SingleAsync(x => x.Id == clone.Id);
        var clonedSource = Assert.Single(clonedEntity.QuizImportSources);
        var clonedPath = Path.GetFullPath(Path.Combine(paths.RootPath, clonedSource.RelativePath));
        Assert.NotEqual(sourceDocument.Id, clonedSource.Id);
        Assert.NotEqual(Path.GetFullPath(sourcePath), clonedPath);
        Assert.True(File.Exists(clonedPath));
        Assert.Single(clonedEntity.QuizQuestions);
        Assert.Single(clonedEntity.QuizQuestions.Single().Choices);

        Assert.Equal(
            ["exams", "quiz_questions", "quiz_choices", "quiz_import_sources"],
            outbox.Calls.Select(x => x.EntityType).ToArray());
        Assert.Equal(clone.Id.ToString(), outbox.Calls[0].EntityId);
        Assert.Equal(clonedSource.Id.ToString(), outbox.Calls[3].EntityId);
        Assert.Equal(clonedPath, outbox.Calls[3].FilePath);
        Assert.False(await db.ExamSessionsSet.AnyAsync(x => x.ExamId == clone.Id));
        Assert.False(await db.QuizAttemptsSet.AnyAsync(x => x.Session.ExamId == clone.Id));
        Assert.False(await db.SubmissionsSet.AnyAsync(x => x.Session.ExamId == clone.Id));
    }

    [Fact]
    public async Task BulkArchive_PersistsSoftDelete_AndDefaultListsStayHiddenAfterRestart()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var firstClass = await services.Classes.CreateAsync(
            new("Archive A", "ARC-A", "2026-2027", null),
            CancellationToken.None);
        var secondClass = await services.Classes.CreateAsync(
            new("Archive B", "ARC-B", "2026-2027", null),
            CancellationToken.None);
        var exam = await services.Exams.CreateAsync(
            ExamRequest(null, false),
            CancellationToken.None);
        exam = await services.Exams.PublishAsync(exam.Id, CancellationToken.None);
        var session = new ExamSession
        {
            ExamId = exam.Id,
            RoomCode = "ARCHIVE1",
            HostDeviceId = "host",
            Status = SessionStatus.Finished,
            AcceptingParticipants = false
        };
        database.Context.ExamSessionsSet.Add(session);
        await database.Context.SaveChangesAsync();

        var classesResult = await services.Classes.BulkArchiveAsync(
            new([firstClass.Id, secondClass.Id]),
            CancellationToken.None);
        var sessionsResult = await services.Sessions.BulkArchiveAsync(
            new([session.Id]),
            CancellationToken.None);
        var examsResult = await services.Exams.BulkArchiveAsync(
            new([exam.Id]),
            CancellationToken.None);

        Assert.Equal(2, classesResult.Archived);
        Assert.Equal(1, sessionsResult.Archived);
        Assert.Equal(1, examsResult.Archived);

        await using var restarted = database.CreateContext();
        var restartedServices = Services(restarted);
        Assert.DoesNotContain(
            (await restartedServices.Classes.ListAsync(null, 1, 50, CancellationToken.None)).Items,
            item => item.Id == firstClass.Id || item.Id == secondClass.Id);
        Assert.DoesNotContain(
            (await restartedServices.Exams.ListAsync(null, null, 1, 50, CancellationToken.None)).Items,
            item => item.Id == exam.Id);
        Assert.DoesNotContain(
            (await restartedServices.Sessions.ListAsync(null, 1, 50, CancellationToken.None)).Items,
            item => item.Id == session.Id);
        Assert.All(
            await restarted.ClassesSet.Where(x => x.Id == firstClass.Id || x.Id == secondClass.Id).ToListAsync(),
            item => Assert.Equal(ClassStatus.Archived, item.Status));
        Assert.Equal(
            ExamStatus.Archived,
            (await restarted.ExamsSet.SingleAsync(x => x.Id == exam.Id)).Status);
        Assert.Equal(
            SessionStatus.Archived,
            (await restarted.ExamSessionsSet.SingleAsync(x => x.Id == session.Id)).Status);
        Assert.True(await restarted.AuditLogsSet.CountAsync(x =>
            x.Action == "ClassArchived"
            || x.Action == "ExamArchived"
            || x.Action == "SessionStateChanged") >= 4);
        Assert.True(await restarted.SyncQueueSet.CountAsync(x =>
            x.EntityType == "classes"
            || x.EntityType == "exams"
            || x.EntityType == "exam_sessions") >= 4);

        var idempotent = await restartedServices.Classes.BulkArchiveAsync(
            new([firstClass.Id]),
            CancellationToken.None);
        Assert.Equal(0, idempotent.Archived);
        Assert.Contains(firstClass.Id, idempotent.AlreadyArchived);
    }

    [Fact]
    public async Task BulkArchive_RejectionIsAtomic_ForMissingIdActiveExamAndRunningSession()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var classroom = await services.Classes.CreateAsync(
            new("Atomic", "ATOMIC", "2026-2027", null),
            CancellationToken.None);
        await Assert.ThrowsAsync<ApiException>(() => services.Classes.BulkArchiveAsync(
            new([classroom.Id, Guid.NewGuid()]),
            CancellationToken.None));
        database.Context.ChangeTracker.Clear();
        Assert.Equal(
            ClassStatus.Active,
            (await database.Context.ClassesSet.SingleAsync(x => x.Id == classroom.Id)).Status);

        var exam = await services.Exams.CreateAsync(ExamRequest(null, false), CancellationToken.None);
        exam = await services.Exams.PublishAsync(exam.Id, CancellationToken.None);
        var finished = new ExamSession
        {
            ExamId = exam.Id,
            RoomCode = "DONE01",
            HostDeviceId = "host",
            Status = SessionStatus.Finished
        };
        var running = new ExamSession
        {
            ExamId = exam.Id,
            RoomCode = "RUN01",
            HostDeviceId = "host",
            Status = SessionStatus.InProgress
        };
        database.Context.ExamSessionsSet.AddRange(finished, running);
        await database.Context.SaveChangesAsync();

        var examError = await Assert.ThrowsAsync<ApiException>(() =>
            services.Exams.BulkArchiveAsync(new([exam.Id]), CancellationToken.None));
        Assert.Equal(ErrorCodes.InvalidStateTransition, examError.Code);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(
            ExamStatus.Published,
            (await database.Context.ExamsSet.SingleAsync(x => x.Id == exam.Id)).Status);

        var sessionError = await Assert.ThrowsAsync<ApiException>(() =>
            services.Sessions.BulkArchiveAsync(
                new([finished.Id, running.Id]),
                CancellationToken.None));
        Assert.Equal(ErrorCodes.InvalidStateTransition, sessionError.Code);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(
            SessionStatus.Finished,
            (await database.Context.ExamSessionsSet.SingleAsync(x => x.Id == finished.Id)).Status);
    }

    [Theory]
    [InlineData("\uFEFFMã sinh viên,Họ và tên\n00001,\"Nguyễn, Văn An\"", "Nguyễn, Văn An")]
    [InlineData("Mã SV;Họ đệm;Tên\n00002;Trần Văn;Bình", "Trần Văn Bình")]
    [InlineData("Student Code\tDisplay Name\n00003\tLê Cường", "Lê Cường")]
    public void SpreadsheetReader_DetectsDelimiterQuotedFieldsAndVietnameseAliases(
        string csv,
        string expectedName)
    {
        var rows = SpreadsheetImportReader.ReadRows(
            "students.csv",
            System.Text.Encoding.UTF8.GetBytes(csv));
        var header = StudentImportHeaderMapper.TryFindHeader(
            rows,
            requireDateOfBirth: false,
            out _);

        Assert.NotNull(header);
        Assert.Equal(expectedName, header.ReadDisplayName(rows[header.RowIndex + 1]));
    }

    [Fact]
    public void SpreadsheetReader_UsesFirstXlsxWorksheetWithValidHeader()
    {
        var workbook = CreateXlsx(
            [["Báo cáo lớp"], ["Không phải tiêu đề"]],
            [["Danh sách sinh viên"], ["Mã sinh viên", "Họ và tên"], ["00004", "Phạm Minh"]]);
        var worksheets = SpreadsheetImportReader.ReadWorksheets("students.xlsx", workbook);

        var selected = worksheets
            .Select(rows => new
            {
                Rows = rows,
                Header = StudentImportHeaderMapper.TryFindHeader(rows, false, out _)
            })
            .First(item => item.Header is not null);

        Assert.Equal("Phạm Minh", selected.Header!.ReadDisplayName(selected.Rows[2]));
    }

    [Fact]
    public async Task MembershipImport_ScansDescription_Deduplicates_CommitsAndDoesNotCreateUsers()
    {
        await using var database = await FileDatabase.CreateAsync();
        var classes = Services(database.Context).Classes;
        var classroom = await classes.CreateAsync(
            new("Import", "IMPORT", "2026-2027", null),
            CancellationToken.None);
        var csv = "\uFEFFDanh sách sinh viên lớp 10A\n"
            + "Xuất ngày 27/07/2026\n"
            + "Mã SV;Họ đệm;Tên;Email\n"
            + "00001;Nguyễn Văn;An;an@example.test\n"
            + "\n"
            + "00001;Nguyễn Văn;Trùng;duplicate@example.test\n"
            + "00002;Trần;Bình;\n";
        var usersBefore = await database.Context.UsersSet.CountAsync();

        var preview = await classes.PreviewImportAsync(
            classroom.Id,
            new(
                "students.csv",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(csv)),
                null),
            CancellationToken.None);
        Assert.Equal(3, preview.TotalRows);
        Assert.Equal(2, preview.ValidRows);
        Assert.Single(preview.Errors);

        var committed = await classes.CommitImportAsync(
            classroom.Id,
            new(preview.PreviewToken, true),
            CancellationToken.None);
        Assert.Equal(2, committed.Inserted);
        Assert.Equal(1, committed.Skipped);
        Assert.Equal(usersBefore, await database.Context.UsersSet.CountAsync());

        await using var restarted = database.CreateContext();
        Assert.Equal(
            2,
            await restarted.ClassMembersSet.CountAsync(x => x.ClassId == classroom.Id));
    }

    [Fact]
    public void HeaderMapper_MissingRequiredHeader_ReturnsObservedHeaders()
    {
        List<List<string>> rows = [["Báo cáo"], ["Email", "Ngày sinh"]];
        var header = StudentImportHeaderMapper.TryFindHeader(rows, false, out var observed);
        Assert.Null(header);
        Assert.Contains(observed, row => row.Contains("Email", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubmissionReject_LanOnly_preserves_state_audit_outbox_and_realtime()
    {
        await using var database = await FileDatabase.CreateAsync();
        var seeded = await SeedSubmissionMutationAsync(
            database.Context,
            SessionAccessMode.LanOnly);
        var outbox = new RecordingOutbox();
        var realtime = new RecordingRealtimePublisher();
        var cloud = new OfflineCloudAdapter();
        var service = SubmissionMutations(
            database.Context,
            outbox,
            realtime,
            cloud);
        var sequenceBefore = seeded.Session.Sequence;

        await service.RejectAsync(
            seeded.Submission.Id,
            new RejectSubmissionRequest("Unreadable archive", Guid.Empty),
            CancellationToken.None);

        database.Context.ChangeTracker.Clear();
        var submission = await database.Context.SubmissionsSet
            .SingleAsync(x => x.Id == seeded.Submission.Id);
        var participant = await database.Context.SessionParticipantsSet
            .SingleAsync(x => x.Id == seeded.Participant.Id);
        var session = await database.Context.ExamSessionsSet
            .SingleAsync(x => x.Id == seeded.Session.Id);
        Assert.Equal(SubmissionStatus.Rejected, submission.Status);
        Assert.Equal("Unreadable archive", submission.TeacherRejectReason);
        Assert.Equal(SubmissionStatus.Rejected, participant.SubmissionStatus);
        Assert.Equal(sequenceBefore + 1, session.Sequence);
        var outboxCall = Assert.Single(outbox.Calls);
        Assert.Equal("submissions", outboxCall.EntityType);
        Assert.Equal(seeded.Submission.Id.ToString(), outboxCall.EntityId);
        Assert.Empty(realtime.ParticipantEvents);
        var notificationOutbox = await database.Context.SyncQueueSet.SingleAsync(
            x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType);
        Assert.Equal(SyncStatus.LocalOnly, notificationOutbox.Status);
        using var notificationJson = JsonDocument.Parse(notificationOutbox.PayloadJson);
        Assert.Equal("SubmissionRejected", notificationJson.RootElement.GetProperty("eventType").GetString());
        Assert.Equal(seeded.Submission.Id, notificationJson.RootElement.GetProperty("submissionId").GetGuid());
        Assert.Equal(seeded.Participant.Id, notificationJson.RootElement.GetProperty("participantId").GetGuid());
        Assert.Equal("Unreadable archive", notificationJson.RootElement.GetProperty("reason").GetString());
        Assert.Contains(
            await database.Context.AuditLogsSet.ToListAsync(),
            x => x.Action == "SubmissionRejected"
                && x.EntityId == seeded.Submission.Id.ToString());
        Assert.Equal(0, cloud.TeacherMutationCalls);
    }

    [Fact]
    public async Task AllowResubmit_LanOnly_preserves_attempt_and_updates_participant_only()
    {
        await using var database = await FileDatabase.CreateAsync();
        var seeded = await SeedSubmissionMutationAsync(
            database.Context,
            SessionAccessMode.LanOnly);
        seeded.Submission.Status = SubmissionStatus.Rejected;
        seeded.Participant.SubmissionStatus = SubmissionStatus.Rejected;
        await database.Context.SaveChangesAsync();
        var outbox = new RecordingOutbox();
        var realtime = new RecordingRealtimePublisher();
        var cloud = new OfflineCloudAdapter();
        var service = SubmissionMutations(
            database.Context,
            outbox,
            realtime,
            cloud);

        await service.AllowResubmitAsync(
            seeded.Participant.Id,
            new AllowResubmitRequest("Approved retry", Guid.Empty),
            CancellationToken.None);

        database.Context.ChangeTracker.Clear();
        var participant = await database.Context.SessionParticipantsSet
            .SingleAsync(x => x.Id == seeded.Participant.Id);
        var submission = await database.Context.SubmissionsSet
            .SingleAsync(x => x.Id == seeded.Submission.Id);
        Assert.True(participant.ResubmitAllowed);
        Assert.Equal("Approved retry", participant.ResubmitReason);
        Assert.Equal(SubmissionStatus.Rejected, submission.Status);
        Assert.Equal(1, submission.AttemptNumber);
        var outboxCall = Assert.Single(outbox.Calls);
        Assert.Equal("session_participants", outboxCall.EntityType);
        Assert.Equal(seeded.Participant.Id.ToString(), outboxCall.EntityId);
        Assert.Empty(realtime.ParticipantEvents);
        var resubmitNotification = await database.Context.SyncQueueSet.SingleAsync(
            x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType);
        Assert.Contains("\"eventType\":\"ResubmitAllowed\"", resubmitNotification.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(seeded.Submission.Id.ToString(), resubmitNotification.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            await database.Context.AuditLogsSet.ToListAsync(),
            x => x.Action == "ResubmitAllowed"
                && x.EntityId == seeded.Participant.Id.ToString());
        Assert.Equal(0, cloud.TeacherMutationCalls);
    }

    [Fact]
    public async Task SubmissionMutationDispatcher_unknown_mode_fails_closed_without_side_effects()
    {
        await using var database = await FileDatabase.CreateAsync();
        var seeded = await SeedSubmissionMutationAsync(
            database.Context,
            (SessionAccessMode)999);
        var outbox = new RecordingOutbox();
        var realtime = new RecordingRealtimePublisher();
        var cloud = new OfflineCloudAdapter();
        var service = SubmissionMutations(
            database.Context,
            outbox,
            realtime,
            cloud);

        var rejectError = await Assert.ThrowsAsync<ApiException>(() =>
            service.RejectAsync(
                seeded.Submission.Id,
                new RejectSubmissionRequest("Reject", Guid.NewGuid()),
                CancellationToken.None));
        var resubmitError = await Assert.ThrowsAsync<ApiException>(() =>
            service.AllowResubmitAsync(
                seeded.Participant.Id,
                new AllowResubmitRequest("Retry", Guid.NewGuid()),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidStateTransition, rejectError.Code);
        Assert.Equal(ErrorCodes.InvalidStateTransition, resubmitError.Code);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(
            SubmissionStatus.Submitted,
            (await database.Context.SubmissionsSet
                .SingleAsync(x => x.Id == seeded.Submission.Id))
                .Status);
        Assert.False(
            (await database.Context.SessionParticipantsSet
                .SingleAsync(x => x.Id == seeded.Participant.Id))
                .ResubmitAllowed);
        Assert.Empty(outbox.Calls);
        Assert.Empty(realtime.ParticipantEvents);
        Assert.Equal(0, cloud.TeacherMutationCalls);
    }

    private static byte[] CreateXlsx(params string[][][] worksheets)
    {
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(
                   stream,
                   System.IO.Compression.ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            WriteZipEntry(
                archive,
                "xl/workbook.xml",
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>"
                + string.Concat(worksheets.Select((_, index) =>
                    $"<sheet name=\"Sheet{index + 1}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\"/>"))
                + "</sheets></workbook>");
            WriteZipEntry(
                archive,
                "xl/_rels/workbook.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + string.Concat(worksheets.Select((_, index) =>
                    $"<Relationship Id=\"rId{index + 1}\" "
                    + "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" "
                    + $"Target=\"worksheets/sheet{index + 1}.xml\"/>"))
                + "</Relationships>");
            for (var sheetIndex = 0; sheetIndex < worksheets.Length; sheetIndex++)
            {
                var rows = string.Concat(worksheets[sheetIndex].Select((row, rowIndex) =>
                    $"<row r=\"{rowIndex + 1}\">"
                    + string.Concat(row.Select((value, columnIndex) =>
                        $"<c r=\"{(char)('A' + columnIndex)}{rowIndex + 1}\" t=\"inlineStr\"><is><t>"
                        + System.Security.SecurityElement.Escape(value)
                        + "</t></is></c>"))
                    + "</row>"));
                WriteZipEntry(
                    archive,
                    $"xl/worksheets/sheet{sheetIndex + 1}.xml",
                    "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"
                    + rows
                    + "</sheetData></worksheet>");
            }
        }
        return stream.ToArray();
    }

    private static void WriteZipEntry(
        System.IO.Compression.ZipArchive archive,
        string path,
        string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), System.Text.Encoding.UTF8);
        writer.Write(content);
    }

    private static CreateExamRequest ExamRequest(Guid? classId, bool requireFile) => new(
        classId,
        "Core workflow exam",
        "Integration",
        null,
        60,
        new FileRuleDto([".txt"], 1024 * 1024, 2 * 1024 * 1024, 2, false, requireFile));

    private static CreateSessionRequest SessionRequest(Guid examId, Guid? classId, string roomCode) =>
        new(
            examId,
            classId,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "{}",
            false,
            40,
            roomCode,
            SessionAccessMode.LanOnly,
            classId.HasValue ? SessionAdmissionMode.ClassMembersOnly : SessionAdmissionMode.OpenRequest);

    private static ServiceSet Services(AppDbContext db, IRealtimePublisher? realtime = null)
    {
        realtime ??= new NoOpRealtimePublisher();
        var options = Options.Create(new ExamTransferOptions());
        var audit = new AuditService(db, new HttpContextAccessor());
        var outbox = new OutboxService(db);
        var paths = new TestStoragePaths(Path.Combine(Path.GetDirectoryName(db.Database.GetDbConnection().DataSource)!, "storage"));
        var tokens = new SessionTokenService(options);
        var participantMutations = new SessionParticipantMutationDispatcher(
            db,
            new ISessionParticipantMutationHandler[]
            {
                new LanSessionParticipantMutationHandler(
                    db,
                    tokens,
                    audit,
                    outbox,
                    realtime,
                    options),
                new PublicCloudSessionParticipantMutationHandler(
                    options,
                    realtime)
            });
        return new(
            new ClassService(db, new MemoryCache(new MemoryCacheOptions()), audit, outbox),
            new ExamService(db, paths, new ChunkStorage(), audit, outbox, realtime, options, NullLogger<ExamService>.Instance),
            new SessionService(
                db,
                audit,
                outbox,
                realtime,
                options,
                NullLogger<SessionService>.Instance,
                participantMutations,
                new LanParticipantSessionExecution(
                    db,
                    tokens,
                    audit,
                    outbox,
                    realtime,
                    options,
                    new LanAccessPolicy(options)),
                new PublicCloudProjectionExecution(db)));
    }

    private static SubmissionService SubmissionMutations(
        AppDbContext db,
        IOutboxService outbox,
        IRealtimePublisher realtime,
        ICloudAdapter cloud)
    {
        var options = Options.Create(new ExamTransferOptions());
        var audit = new AuditService(db, new HttpContextAccessor());
        var paths = new TestStoragePaths(
            Path.Combine(
                Path.GetDirectoryName(db.Database.GetDbConnection().DataSource)!,
                "submission-storage"));
        var dispatcher = new SubmissionMutationDispatcher(
            db,
            new ISubmissionMutationHandler[]
            {
                new LanSubmissionMutationHandler(
                    db,
                    audit,
                    outbox),
                new PublicCloudSubmissionMutationHandler(cloud)
            });
        return new SubmissionService(
            db,
            paths,
            new ChunkStorage(),
            new ReceiptSigner(options),
            audit,
            outbox,
            realtime,
            options,
            dispatcher);
    }

    private static async Task<SubmissionMutationSeed>
        SeedSubmissionMutationAsync(
            AppDbContext db,
            SessionAccessMode accessMode)
    {
        var exam = new Exam
        {
            Title = "Submission mutation",
            Subject = "Characterization",
            DurationMinutes = 30,
            Status = ExamStatus.Published,
            DeliveryType = ExamDeliveryType.FileSubmission,
            Version = 1
        };
        var session = new ExamSession
        {
            Exam = exam,
            ExamId = exam.Id,
            RoomCode = $"SUB{Random.Shared.Next(10000, 99999)}",
            HostDeviceId = "submission-host",
            Status = SessionStatus.Collecting,
            AccessMode = accessMode,
            DeliveryTypeSnapshot = ExamDeliveryType.FileSubmission,
            ExamVersionSnapshot = exam.Version,
            Sequence = 7
        };
        var participant = new SessionParticipant
        {
            Session = session,
            SessionId = session.Id,
            StudentCode = "SUB-STUDENT",
            DisplayName = "Submission Student",
            DeviceId = "submission-device",
            MachineName = "submission-machine",
            AppVersion = "characterization",
            Status = ParticipantStatus.Approved,
            SubmissionStatus = SubmissionStatus.Submitted,
            SourceMode = accessMode == SessionAccessMode.PublicCloud
                ? "PublicCloud"
                : "Lan"
        };
        var submission = new Submission
        {
            Session = session,
            SessionId = session.Id,
            Participant = participant,
            ParticipantId = participant.Id,
            AttemptNumber = 1,
            IdempotencyKey = $"submission-{Guid.NewGuid():N}",
            Status = SubmissionStatus.Submitted,
            ClientSubmittedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(30),
            SourceMode = participant.SourceMode
        };
        db.AddRange(exam, session, participant, submission);
        await db.SaveChangesAsync();
        return new(exam, session, participant, submission);
    }

    private static async Task<ExamSession> SeedDashboardSessionAsync(
        AppDbContext db,
        SessionStatus status,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? startedAtUtc = null)
    {
        var exam = new Exam
        {
            Title = $"Dashboard {status}",
            Subject = "Dashboard",
            DurationMinutes = 60,
            Status = ExamStatus.Published,
            Version = 1
        };
        var session = new ExamSession
        {
            Exam = exam,
            ExamId = exam.Id,
            RoomCode = $"DASH-{Guid.NewGuid():N}",
            HostDeviceId = "dashboard-test",
            Status = status,
            StartedAtUtc = startedAtUtc,
            EndedAtUtc = status is SessionStatus.Finished or SessionStatus.Cancelled
                ? DateTimeOffset.UtcNow
                : null
        };
        db.Add(session);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(
            """UPDATE "exam_sessions" SET "UpdatedAtUtc" = {0} WHERE "Id" = {1};""",
            updatedAtUtc,
            session.Id);
        db.ChangeTracker.Clear();
        return session;
    }

    private static SessionSummaryDto DashboardSessionSummary(SessionStatus status) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Dashboard contract",
            "DASH-CONTRACT",
            status,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            null,
            DateTimeOffset.UtcNow.AddMinutes(55),
            new SessionCountsDto(1, 0, 1, 1, 0, 0, 0),
            1,
            "row-version");

    private static SystemService DashboardService(AppDbContext db)
    {
        var options = Options.Create(new ExamTransferOptions());
        var paths = new TestStoragePaths(Path.Combine(Path.GetDirectoryName(db.Database.GetDbConnection().DataSource)!, "storage"));
        return new SystemService(db, paths, new OfflineCloudAdapter(), options, new NoOpRealtimePublisher());
    }

    private sealed record ServiceSet(ClassService Classes, ExamService Exams, SessionService Sessions);

    private sealed record SubmissionMutationSeed(
        Exam Exam,
        ExamSession Session,
        SessionParticipant Participant,
        Submission Submission);

    private sealed class RecordingOutbox : IOutboxService
    {
        public List<OutboxCall> Calls { get; } = [];

        public Task EnqueueAsync(
            string entityType,
            string entityId,
            string operation,
            object payload,
            string? filePath = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new(entityType, entityId, operation, payload, filePath));
            return Task.CompletedTask;
        }
    }

    private sealed record OutboxCall(
        string EntityType,
        string EntityId,
        string Operation,
        object Payload,
        string? FilePath);

    private sealed class NoOpRealtimePublisher : IRealtimePublisher
    {
        public Task PublishSessionAsync<T>(Guid sessionId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishParticipantAsync<T>(Guid sessionId, Guid participantId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingRealtimePublisher : IRealtimePublisher
    {
        public List<ParticipantEvent> ParticipantEvents { get; } = [];

        public Task PublishSessionAsync<T>(
            Guid sessionId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PublishParticipantAsync<T>(
            Guid sessionId,
            Guid participantId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default)
        {
            ParticipantEvents.Add(new(
                sessionId,
                participantId,
                eventName,
                sequence,
                payload!));
            return Task.CompletedTask;
        }
    }

    private sealed record ParticipantEvent(
        Guid SessionId,
        Guid ParticipantId,
        string EventName,
        long Sequence,
        object Payload);

    private sealed class ThrowingRealtimePublisher : IRealtimePublisher
    {
        public Task PublishSessionAsync<T>(Guid sessionId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => throw new IOException("Simulated realtime outage");
        public Task PublishParticipantAsync<T>(Guid sessionId, Guid participantId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => throw new IOException("Simulated realtime outage");
    }

    private sealed class OfflineCloudAdapter : ICloudAdapter
    {
        public int TeacherMutationCalls { get; private set; }
        public bool Enabled => false;
        public bool Configured => false;
        public bool Authenticated => false;
        public bool CanSynchronize => false;
        public CloudLoginResult? CurrentSession => null;

        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<CloudPreflightResult> PreflightAsync(CancellationToken cancellationToken) => Task.FromResult(
            new CloudPreflightResult(false, false, false, false, "None", null, "Disabled", [], [], CloudAccessModes.UserSession, false, null, false));

        public Task<CloudPushResult> PushAsync(SyncQueueItem item, Func<CancellationToken, Task>? checkpoint, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CloudParticipantMutationResult> AllowPublicResubmissionAsync(
            Guid participantId,
            string reason,
            Guid requestId,
            CancellationToken cancellationToken)
        {
            TeacherMutationCalls++;
            throw new IOException("OnlyLAN must not call PublicCloud resubmit RPC.");
        }

        public Task<CloudSubmissionMutationResult> RejectPublicSubmissionAsync(
            Guid submissionId,
            string reason,
            Guid requestId,
            CancellationToken cancellationToken)
        {
            TeacherMutationCalls++;
            throw new IOException("OnlyLAN must not call PublicCloud reject RPC.");
        }

        public Task<CloudLoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CloudLoginResult?> RefreshSessionAsync(CancellationToken cancellationToken) => Task.FromResult<CloudLoginResult?>(null);
        public Task LogoutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<CloudBackupDescriptor>> ListBackupsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CloudBackupDescriptor>>([]);
        public Task DownloadObjectAsync(string cloudObjectPath, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestStoragePaths(string root) : IStoragePaths
    {
        public string RootPath { get; } = root;
        public string DatabasePath => Path.Combine(RootPath, "database", "exam-transfer.db");
        public string BackupRoot => Path.Combine(RootPath, "backups");
        public string ExportRoot => Path.Combine(RootPath, "exports");
        public string TemporaryRoot => Path.Combine(RootPath, "temporary");
        public string ExamVersionRoot(Guid examId, int version) => Path.Combine(RootPath, "exams", examId.ToString("N"), $"v{version}");
        public string SessionRoot(Guid sessionId) => Path.Combine(RootPath, "sessions", sessionId.ToString("N"));
        public string SubmissionRoot(Guid sessionId, string studentCode, Guid submissionId) => Path.Combine(SessionRoot(sessionId), studentCode, submissionId.ToString("N"));
        public string ReceiptRoot(Guid sessionId) => Path.Combine(SessionRoot(sessionId), "receipts");
        public void EnsureCreated() => Directory.CreateDirectory(RootPath);
    }

    private sealed class FileDatabase : IAsyncDisposable
    {
        private readonly string directory;
        private readonly string databasePath;

        private FileDatabase(string directory, string databasePath, AppDbContext context)
        {
            this.directory = directory;
            this.databasePath = databasePath;
            Context = context;
        }

        public AppDbContext Context { get; }

        public static async Task<FileDatabase> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "ExamTransfer.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "workflow.db");
            var context = CreateContext(path);
            await context.Database.EnsureCreatedAsync();
            return new(directory, path, context);
        }

        public AppDbContext CreateContext() => CreateContext(databasePath);

        private static AppDbContext CreateContext(string path) => new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
