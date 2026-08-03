using ExamTransfer.Application;
using ExamTransfer.LocalServer.Hubs;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace ExamTransfer.LocalServer.Realtime;

public sealed class SignalROnlyLanStudentNotificationTransport(
    IHubContext<ExamHub> hub) : IOnlyLanStudentNotificationTransport
{
    public Task PublishSessionAsync(
        StudentNotificationEventDto notification,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            hub.Clients.Group(ExamHub.StudentSessionGroup(notification.SessionId)),
            notification,
            cancellationToken);

    public Task PublishParticipantAsync(
        StudentNotificationEventDto notification,
        CancellationToken cancellationToken = default)
    {
        if (!notification.ParticipantId.HasValue)
            throw new ArgumentException("Participant notification requires a participant id.", nameof(notification));
        return SendAsync(
            hub.Clients.Group(ExamHub.ParticipantGroup(
                notification.SessionId,
                notification.ParticipantId.Value)),
            notification,
            cancellationToken);
    }

    public Task PublishConnectionAsync(
        string connectionId,
        StudentNotificationEventDto notification,
        CancellationToken cancellationToken = default) =>
        SendAsync(hub.Clients.Client(connectionId), notification, cancellationToken);

    private static Task SendAsync(
        IClientProxy client,
        StudentNotificationEventDto notification,
        CancellationToken cancellationToken)
    {
        StudentNotificationEventValidator.EnsureValid(notification);
        var eventName = notification.EventType.ToString();
        var envelope = new RealtimeEnvelope<StudentNotificationEventDto>(
            notification.EventId,
            notification.SessionId,
            notification.Revision,
            notification.OccurredAtUtc,
            eventName,
            notification);
        return client.SendAsync(eventName, envelope, cancellationToken);
    }
}
