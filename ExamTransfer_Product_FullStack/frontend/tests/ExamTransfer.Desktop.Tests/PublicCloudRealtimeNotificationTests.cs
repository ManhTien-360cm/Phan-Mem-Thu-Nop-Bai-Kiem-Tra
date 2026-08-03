using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class PublicCloudRealtimeNotificationTests
{
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    [Fact]
    public void AllNineEvents_DeserializeThroughSharedDto()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var transport = Transport(sessionId, participantId);
        var revision = 0L;

        foreach (var eventType in Enum.GetValues<StudentNotificationEventType>())
        {
            var expected = Notification(eventType, sessionId, participantId, ++revision);
            Assert.True(transport.TryAcceptRealtime(Frame(expected), out var actual));
            Assert.Equal(expected, actual!.StudentNotification);
        }
    }

    [Fact]
    public void UnknownAndMalformedEvents_AreRejectedWithoutThrowing()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var transport = Transport(sessionId, participantId);
        var valid = Frame(Notification(
            StudentNotificationEventType.ParticipantApproved,
            sessionId,
            participantId,
            1));

        Assert.False(transport.TryAcceptRealtime(
            valid.Replace("ParticipantApproved", "Unknown", StringComparison.Ordinal),
            out _));
        Assert.False(transport.TryAcceptRealtime(
            valid.Replace("\"revision\":1,", string.Empty, StringComparison.Ordinal),
            out _));
    }

    [Fact]
    public void DuplicateEventId_EmitsOnce()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var transport = Transport(sessionId, participantId);
        var frame = Frame(Notification(
            StudentNotificationEventType.ParticipantApproved,
            sessionId,
            participantId,
            1));

        Assert.True(transport.TryAcceptRealtime(frame, out _));
        Assert.False(transport.TryAcceptRealtime(frame, out _));
    }

    [Fact]
    public void OlderRevision_IsRejected()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var transport = Transport(sessionId, participantId);

        Assert.True(transport.TryAcceptRealtime(Frame(Notification(
            StudentNotificationEventType.ParticipantApproved,
            sessionId,
            participantId,
            2)), out _));
        Assert.False(transport.TryAcceptRealtime(Frame(Notification(
            StudentNotificationEventType.SubmissionRejected,
            sessionId,
            participantId,
            1)), out _));
    }

    [Fact]
    public void ParticipantScope_Isolated()
    {
        var sessionId = Guid.NewGuid();
        var participantA = Guid.NewGuid();
        var participantB = Guid.NewGuid();
        var transportA = Transport(sessionId, participantA);
        var transportB = Transport(sessionId, participantB);
        var eventA = Notification(
            StudentNotificationEventType.ParticipantApproved,
            sessionId,
            participantA,
            1);

        Assert.True(transportA.TryAcceptRealtime(Frame(eventA), out _));
        Assert.False(transportB.TryAcceptRealtime(Frame(eventA), out _));
    }

    [Fact]
    public void Broadcast_IsAcceptedOnlyForTheActiveSession()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var transport = Transport(sessionId, participantId);

        Assert.True(transport.TryAcceptRealtime(Frame(Notification(
            StudentNotificationEventType.TeacherMessageReceived,
            sessionId,
            participantId,
            1)), out _));
        Assert.False(transport.TryAcceptRealtime(Frame(Notification(
            StudentNotificationEventType.TeacherMessageReceived,
            Guid.NewGuid(),
            participantId,
            2)), out _));
    }

    [Fact]
    public async Task CatchUpAndRealtimeDuplicate_EmitsOnce()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var transport = Transport(sessionId, participantId);
        var value = Notification(
            StudentNotificationEventType.ParticipantApproved,
            sessionId,
            participantId,
            1);
        var emitted = new List<StudentRealtimeNotification>();

        Assert.True(transport.TryAcceptRealtime(Frame(value), out var live));
        emitted.Add(live!);
        await transport.CatchUpAsync(
            (_, _, _, _) => Task.FromResult<IReadOnlyList<StudentNotificationEventDto>>([value]),
            emitted.Add,
            CancellationToken.None);

        Assert.Single(emitted);
        Assert.Equal(1, transport.CatchUpRevision);
    }

    [Fact]
    public async Task BufferedRace_CatchUpThenRealtime_LosesNoEvent()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var transport = Transport(sessionId, participantId);
        var history = Notification(
            StudentNotificationEventType.ParticipantApproved,
            sessionId,
            participantId,
            1);
        var duringSubscribe = Notification(
            StudentNotificationEventType.SubmissionRejected,
            sessionId,
            participantId,
            2);
        var emitted = new List<StudentRealtimeNotification>();

        await transport.CatchUpAsync(
            (_, _, _, _) => Task.FromResult<IReadOnlyList<StudentNotificationEventDto>>([history]),
            emitted.Add,
            CancellationToken.None);
        Assert.True(transport.TryAcceptRealtime(Frame(duringSubscribe), out var live));
        emitted.Add(live!);

        Assert.Equal([history.EventId, duringSubscribe.EventId],
            emitted.Select(x => x.StudentNotification!.EventId));
    }

    [Fact]
    public async Task Reconnect_ContinuesFromValidatedCatchUpCursor()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var transport = Transport(sessionId, participantId);
        var first = Notification(
            StudentNotificationEventType.ParticipantApproved,
            sessionId,
            participantId,
            1);
        var second = Notification(
            StudentNotificationEventType.SubmissionRejected,
            sessionId,
            participantId,
            2);
        var requested = new List<long>();
        var call = 0;

        async Task<IReadOnlyList<StudentNotificationEventDto>> Fetch(
            long revision, Guid? _, int __, CancellationToken ___)
        {
            await Task.Yield();
            requested.Add(revision);
            return ++call == 1 ? [first] : [second];
        }

        await transport.CatchUpAsync(Fetch, _ => { }, CancellationToken.None);
        await transport.CatchUpAsync(Fetch, _ => { }, CancellationToken.None);

        Assert.Equal([0L, 1L], requested);
        Assert.Equal(2, transport.CatchUpRevision);
        Assert.Equal(second.EventId, transport.CatchUpEventId);
    }

    [Fact]
    public async Task CatchUp_PaginatesWithoutDroppingBeyondOneHundred()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var values = Enumerable.Range(1, 101)
            .Select(revision => Notification(
                StudentNotificationEventType.ParticipantApproved,
                sessionId,
                participantId,
                revision))
            .ToArray();
        var transport = Transport(sessionId, participantId);
        var emitted = new List<StudentRealtimeNotification>();
        var calls = 0;

        Task<IReadOnlyList<StudentNotificationEventDto>> Fetch(
            long _, Guid? __, int limit, CancellationToken ___)
        {
            var result = ++calls == 1
                ? values.Take(limit).ToArray()
                : values.Skip(limit).ToArray();
            return Task.FromResult<IReadOnlyList<StudentNotificationEventDto>>(result);
        }

        await transport.CatchUpAsync(Fetch, emitted.Add, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(101, emitted.Count);
        Assert.Equal(101, transport.CatchUpRevision);
    }

    [Fact]
    public void SubscriptionAcknowledgement_IsRequiredAndCorrelated()
    {
        const string topic = "realtime:student-notifications:session:participant";
        const string reference = "42";
        var acceptedReply = JsonSerializer.Serialize(new
        {
            topic,
            @event = "phx_reply",
            @ref = reference,
            payload = new { status = "ok" }
        });
        var staleReply = JsonSerializer.Serialize(new
        {
            topic,
            @event = "phx_reply",
            @ref = "41",
            payload = new { status = "ok" }
        });

        Assert.True(SupabaseRealtimeService.TryParseJoinReply(
            acceptedReply,
            topic,
            reference,
            out var accepted));
        Assert.True(accepted);
        Assert.False(SupabaseRealtimeService.TryParseJoinReply(
            staleReply,
            topic,
            reference,
            out _));
    }

    [Fact]
    public void TransportSource_HasNoUiOrPrivilegedCredentialDependency()
    {
        var root = FindProductRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "frontend",
            "src",
            "ExamTransfer.Desktop",
            "Infrastructure",
            "PublicCloudStudentNotificationTransport.cs"));
        var realtime = File.ReadAllText(Path.Combine(
            root,
            "frontend",
            "src",
            "ExamTransfer.Desktop",
            "Infrastructure",
            "SupabaseRealtimeService.cs"));

        Assert.DoesNotContain("ViewModel", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MessageBox", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service_role", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service_role", realtime, StringComparison.OrdinalIgnoreCase);
    }

    private static PublicCloudStudentNotificationTransport Transport(
        Guid sessionId,
        Guid participantId)
    {
        var transport = new PublicCloudStudentNotificationTransport();
        transport.SetScope(sessionId, participantId);
        return transport;
    }

    private static StudentNotificationEventDto Notification(
        StudentNotificationEventType eventType,
        Guid sessionId,
        Guid participantId,
        long revision)
    {
        var submissionEvent = eventType is StudentNotificationEventType.SubmissionRejected
            or StudentNotificationEventType.ResubmitAllowed
            or StudentNotificationEventType.GradeReturned
            or StudentNotificationEventType.GradeReopened;
        var quizEvent = eventType is StudentNotificationEventType.QuizGradeReturned
            or StudentNotificationEventType.QuizGradeReopened;
        var value = new StudentNotificationEventDto
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            SessionId = sessionId,
            ParticipantId = eventType == StudentNotificationEventType.TeacherMessageReceived
                ? null
                : participantId,
            SubmissionId = submissionEvent ? Guid.NewGuid() : null,
            AttemptId = quizEvent ? Guid.NewGuid() : null,
            Message = eventType == StudentNotificationEventType.TeacherMessageReceived
                ? "Teacher message"
                : eventType is StudentNotificationEventType.GradeReturned
                    or StudentNotificationEventType.QuizGradeReturned
                    ? "Returned"
                    : null,
            Reason = eventType is StudentNotificationEventType.ParticipantAdmissionRejected
                or StudentNotificationEventType.SubmissionRejected
                or StudentNotificationEventType.ResubmitAllowed
                or StudentNotificationEventType.GradeReopened
                or StudentNotificationEventType.QuizGradeReopened
                ? "Reason"
                : null,
            Score = eventType is StudentNotificationEventType.GradeReturned
                or StudentNotificationEventType.QuizGradeReturned ? 8m : null,
            MaxScore = eventType is StudentNotificationEventType.GradeReturned
                or StudentNotificationEventType.QuizGradeReturned ? 10m : null,
            OccurredAtUtc = new DateTimeOffset(2026, 8, 2, 8, 0, 0, TimeSpan.Zero)
                .AddSeconds(revision),
            Revision = revision
        };
        StudentNotificationEventValidator.EnsureValid(value);
        return value;
    }

    private static string Frame(StudentNotificationEventDto value)
    {
        var row = new Dictionary<string, object?>
        {
            ["id"] = value.EventId,
            ["session_id"] = value.SessionId,
            ["participant_id"] = value.ParticipantId,
            ["event_type"] = value.EventType.ToString(),
            ["payload"] = value,
            ["revision"] = value.Revision,
            ["occurred_at"] = value.OccurredAtUtc
        };
        return JsonSerializer.Serialize(new
        {
            topic = "realtime:student-notifications",
            @event = "postgres_changes",
            payload = new
            {
                data = new
                {
                    type = "INSERT",
                    schema = "public",
                    table = "student_notification_events",
                    record = row
                }
            }
        }, Json);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string FindProductRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (directory.Name == "ExamTransfer_Product_FullStack")
                return directory.FullName;
            var candidate = Path.Combine(directory.FullName, "ExamTransfer_Product_FullStack");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("ExamTransfer product root was not found.");
    }
}
