using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class OnlyLanRealtimeNotificationAdapterTests
{
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    [Fact]
    public void AllNineEvents_DeserializeValidateAndEmitTypedContract()
    {
        var adapter = new StudentNotificationRealtimeAdapter();
        var revision = 0L;
        foreach (var eventType in Enum.GetValues<StudentNotificationEventType>())
        {
            var expected = Notification(eventType, ++revision);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(Envelope(expected), Json));

            Assert.True(adapter.TryAccept(document.RootElement, out var actual));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void DuplicateAndOlderRevision_AreRejectedWithinSameScope()
    {
        var adapter = new StudentNotificationRealtimeAdapter();
        var current = Notification(StudentNotificationEventType.ParticipantApproved, 12);

        Assert.True(adapter.TryAccept(Envelope(current), out _));
        Assert.False(adapter.TryAccept(Envelope(current), out _));
        Assert.False(adapter.TryAccept(
            Envelope(current with { EventId = Guid.NewGuid(), Revision = 11 }),
            out _));
        Assert.False(adapter.TryAccept(
            Envelope(current with { EventId = Guid.NewGuid() }),
            out _));
    }

    [Fact]
    public void EqualRevisionAcrossDifferentParticipants_HasIndependentDedupeScope()
    {
        var adapter = new StudentNotificationRealtimeAdapter();
        var first = Notification(StudentNotificationEventType.ParticipantApproved, 7);
        var second = first with { EventId = Guid.NewGuid(), ParticipantId = Guid.NewGuid() };

        Assert.True(adapter.TryAccept(Envelope(first), out _));
        Assert.True(adapter.TryAccept(Envelope(second), out _));
    }

    [Fact]
    public void UnknownOrMissingRequiredPayload_IsRejectedWithoutThrowing()
    {
        var adapter = new StudentNotificationRealtimeAdapter();
        using var unknown = JsonDocument.Parse(
            """
            {"eventId":"00000000-0000-0000-0000-000000000001","sessionId":"00000000-0000-0000-0000-000000000002","sequence":1,"occurredAtUtc":"2026-08-02T00:00:00Z","eventType":"Unknown","payload":{"eventId":"00000000-0000-0000-0000-000000000001","eventType":"Unknown","sessionId":"00000000-0000-0000-0000-000000000002","occurredAtUtc":"2026-08-02T00:00:00Z","revision":1}}
            """);
        using var missing = JsonDocument.Parse(
            """
            {"eventId":"00000000-0000-0000-0000-000000000001","sessionId":"00000000-0000-0000-0000-000000000002","sequence":1,"occurredAtUtc":"2026-08-02T00:00:00Z","eventType":"TeacherMessageReceived","payload":{"eventId":"00000000-0000-0000-0000-000000000001","eventType":"TeacherMessageReceived","sessionId":"00000000-0000-0000-0000-000000000002","occurredAtUtc":"2026-08-02T00:00:00Z","revision":1}}
            """);

        Assert.False(adapter.TryAccept(unknown.RootElement, out _));
        Assert.False(adapter.TryAccept(missing.RootElement, out _));
    }

    [Fact]
    public void AdapterSource_DoesNotInvokePresentationLayer()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "ExamTransfer_Product_FullStack",
            "frontend",
            "src",
            "ExamTransfer.Desktop",
            "Infrastructure",
            "StudentNotificationRealtimeAdapter.cs"));
        Assert.DoesNotContain("MessageBox", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel", source, StringComparison.Ordinal);
    }

    private static StudentNotificationEventDto Notification(
        StudentNotificationEventType eventType,
        long revision)
    {
        var participantId = Guid.NewGuid();
        var value = new StudentNotificationEventDto
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            SessionId = Guid.NewGuid(),
            ParticipantId = eventType == StudentNotificationEventType.TeacherMessageReceived
                ? null
                : participantId,
            SubmissionId = eventType is StudentNotificationEventType.SubmissionRejected
                or StudentNotificationEventType.ResubmitAllowed
                or StudentNotificationEventType.GradeReturned
                or StudentNotificationEventType.GradeReopened
                ? Guid.NewGuid()
                : null,
            AttemptId = eventType is StudentNotificationEventType.QuizGradeReturned
                or StudentNotificationEventType.QuizGradeReopened
                ? Guid.NewGuid()
                : null,
            Message = eventType == StudentNotificationEventType.TeacherMessageReceived
                ? "Teacher notice"
                : null,
            Score = eventType is StudentNotificationEventType.GradeReturned
                or StudentNotificationEventType.QuizGradeReturned
                ? 8.5m
                : null,
            MaxScore = eventType is StudentNotificationEventType.GradeReturned
                or StudentNotificationEventType.QuizGradeReturned
                ? 10m
                : null,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Revision = revision
        };
        StudentNotificationEventValidator.EnsureValid(value);
        return value;
    }

    private static RealtimeEnvelope<StudentNotificationEventDto> Envelope(
        StudentNotificationEventDto value) =>
        new(
            value.EventId,
            value.SessionId,
            value.Revision,
            value.OccurredAtUtc,
            value.EventType.ToString(),
            value);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "ExamTransfer_Product_FullStack")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
