using System.Net.Http.Headers;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExamTransfer.Infrastructure.Services;

public sealed class OnlyLanSubmissionDownloadService(
    AppDbContext db,
    IStoragePaths paths,
    ILogger<OnlyLanSubmissionDownloadService> logger)
    : IOnlyLanSubmissionDownloadService
{
    private static readonly HashSet<string> WindowsReservedNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    public async Task<SubmissionDownloadContent> OpenAsync(
        Guid submissionId,
        Guid fileId,
        Guid actorId,
        string? actorOrganizationId,
        string? traceId,
        CancellationToken cancellationToken)
    {
        var actor = await db.UsersSet.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == actorId, cancellationToken);
        if (actor is null
            || !actor.IsActive
            || actor.Role is not (UserRole.Teacher or UserRole.Admin))
        {
            throw Denied(actorId, null, submissionId, traceId);
        }

        var submission = await db.SubmissionsSet.AsNoTracking()
            .Include(x => x.Files)
            .Include(x => x.Participant)
            .Include(x => x.Session)
            .ThenInclude(x => x.Exam)
            .SingleOrDefaultAsync(x => x.Id == submissionId, cancellationToken);
        if (submission is null)
            throw NotFound(actorId, null, submissionId, traceId);

        var sessionId = submission.SessionId;
        if (submission.Session.AccessMode != SessionAccessMode.LanOnly)
            throw NotFound(actorId, sessionId, submissionId, traceId);
        if (submission.ParticipantId != submission.Participant.Id
            || submission.Participant.SessionId != sessionId)
        {
            throw NotFound(actorId, sessionId, submissionId, traceId);
        }
        if (!SubmissionStatePolicy.IsCompletedSubmissionStatus(submission.Status)
            || submission.Files.Count != StudentSubmissionPolicy.MaxFileCount)
        {
            throw NotFound(actorId, sessionId, submissionId, traceId);
        }

        var file = submission.Files.Single();
        if (file.Id != fileId
            || file.SubmissionId != submissionId
            || file.TransferStatus != TransferStatus.Completed)
        {
            throw NotFound(actorId, sessionId, submissionId, traceId);
        }

        await EnsureOwnershipAsync(
            actor,
            actorOrganizationId,
            submission.Session.Exam.CreatedBy,
            sessionId,
            submissionId,
            traceId,
            cancellationToken);

        string physicalPath;
        try
        {
            physicalPath = SubmissionDownloadPathResolver.ResolveExistingFile(
                paths,
                sessionId,
                submission.Participant.StudentCode,
                submissionId,
                file.RelativePath);
        }
        catch (ApiException)
        {
            throw NotFound(actorId, sessionId, submissionId, traceId);
        }

        var contentType = SafeContentType(file.MimeType);
        var downloadName = SanitizeDownloadName(
            file.OriginalName,
            submissionId,
            file.StoredName);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                physicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            SubmissionDownloadPathResolver.RejectReparsePoints(paths.RootPath, physicalPath);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ApiException)
        {
            if (stream is not null)
                await stream.DisposeAsync();
            throw NotFound(actorId, sessionId, submissionId, traceId);
        }

        logger.LogInformation(
            "OnlyLAN submission download Success. ActorId={ActorId}; SessionId={SessionId}; SubmissionId={SubmissionId}; TraceId={TraceId}",
            actorId,
            sessionId,
            submissionId,
            traceId);
        return new(
            stream,
            contentType,
            downloadName);
    }

    private async Task EnsureOwnershipAsync(
        User actor,
        string? actorOrganizationId,
        Guid? ownerId,
        Guid sessionId,
        Guid submissionId,
        string? traceId,
        CancellationToken cancellationToken)
    {
        if (!ownerId.HasValue)
            throw Denied(actor.Id, sessionId, submissionId, traceId);
        if (ownerId.Value == actor.Id)
            return;

        var ownerOrganizationId = await db.UsersSet.AsNoTracking()
            .Where(x => x.Id == ownerId.Value && x.IsActive)
            .Select(x => x.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(actorOrganizationId)
            || string.IsNullOrWhiteSpace(actor.OrganizationId)
            || !string.Equals(actor.OrganizationId, actorOrganizationId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(ownerOrganizationId)
            || !string.Equals(ownerOrganizationId, actorOrganizationId, StringComparison.Ordinal))
        {
            throw Denied(actor.Id, sessionId, submissionId, traceId);
        }
    }

    private ApiException Denied(
        Guid actorId,
        Guid? sessionId,
        Guid submissionId,
        string? traceId)
    {
        logger.LogWarning(
            "OnlyLAN submission download Denied. ActorId={ActorId}; SessionId={SessionId}; SubmissionId={SubmissionId}; TraceId={TraceId}",
            actorId,
            sessionId,
            submissionId,
            traceId);
        return new(
            ErrorCodes.Forbidden,
            "Không được tải file bài nộp này.",
            403);
    }

    private ApiException NotFound(
        Guid actorId,
        Guid? sessionId,
        Guid submissionId,
        string? traceId)
    {
        logger.LogWarning(
            "OnlyLAN submission download NotFound. ActorId={ActorId}; SessionId={SessionId}; SubmissionId={SubmissionId}; TraceId={TraceId}",
            actorId,
            sessionId,
            submissionId,
            traceId);
        return new(
            ErrorCodes.NotFound,
            "Không tìm thấy file bài nộp hợp lệ.",
            404);
    }

    internal static string SanitizeDownloadName(
        string? originalName,
        Guid submissionId,
        string? storedName)
    {
        var normalized = (originalName ?? string.Empty)
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFileName(normalized);
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        candidate = new string(candidate
            .Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');

        if (string.IsNullOrWhiteSpace(candidate))
        {
            var extension = Path.GetExtension(storedName ?? string.Empty);
            candidate = $"submission-{submissionId:N}{extension}";
        }
        if (WindowsReservedNames.Contains(candidate.Split('.', 2)[0]))
            candidate = "_" + candidate;

        const int maximumLength = 120;
        if (candidate.Length <= maximumLength)
            return candidate;
        var candidateExtension = Path.GetExtension(candidate);
        if (candidateExtension.Length == 0 || candidateExtension.Length >= maximumLength - 1)
            return candidate[..maximumLength].TrimEnd('.', ' ');
        return candidate[..(maximumLength - candidateExtension.Length)].TrimEnd('.', ' ')
            + candidateExtension;
    }

    private static string SafeContentType(string? mimeType) =>
        MediaTypeHeaderValue.TryParse(mimeType, out var parsed)
            ? parsed.ToString()
            : "application/octet-stream";
}
