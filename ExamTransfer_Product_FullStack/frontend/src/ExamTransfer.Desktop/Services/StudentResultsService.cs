using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Models;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Services;

public interface IStudentResultsService
{
    Task<IReadOnlyList<StudentReturnedResult>> GetReturnedResultsAsync(CancellationToken cancellationToken);
    Task DownloadAttachmentAsync(
        StudentResultAttachment attachment,
        string destinationPath,
        CancellationToken cancellationToken);
}

public sealed class StudentResultsService(
    IBackendClient backend,
    SupabasePublicCloudClient publicCloud,
    StudentSessionState session) : IStudentResultsService
{
    public async Task<IReadOnlyList<StudentReturnedResult>> GetReturnedResultsAsync(
        CancellationToken cancellationToken)
    {
        var page = session.AccessMode == SessionAccessMode.PublicCloud
            ? await publicCloud.GetStudentResultsAsync(50, null, cancellationToken)
            : ApiGuard.Require(await backend.GetAsync<StudentResultPageDto>(
                "api/v1/student/results?pageSize=50",
                cancellationToken));
        StudentResultPageValidator.EnsureValid(page);
        return page.Items.Select(result => Map(result, session.AccessMode)).ToArray();
    }

    public Task DownloadAttachmentAsync(
        StudentResultAttachment attachment,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(attachment.DownloadPath))
            throw new InvalidOperationException("Attachment chưa có đường dẫn tải authoritative.");
        return backend.DownloadFileAsync(
            attachment.DownloadPath.TrimStart('/'),
            destinationPath,
            null,
            cancellationToken);
    }

    private StudentReturnedResult Map(StudentResultDto result, SessionAccessMode sourceMode)
    {
        StudentResultValidator.EnsureValid(result);
        if (result.Status != StudentResultStatus.Returned
            || !result.MaxScore.HasValue
            || !result.ReturnedAtUtc.HasValue)
            throw new StudentResultsIntegrationException("Student result payload không phải kết quả Returned đầy đủ.");

        var resultId = result.ResultType == StudentResultType.EssayFile
            ? result.SubmissionId!.Value
            : result.AttemptId!.Value;
        var participantId = session.SessionId == result.SessionId
            ? session.ParticipantId ?? Guid.Empty
            : Guid.Empty;
        return new(
            resultId,
            result.SessionId,
            participantId,
            result.ExamTitle,
            result.ResultType == StudentResultType.Quiz
                ? StudentResultKind.Quiz
                : StudentResultKind.EssayFile,
            result.AttemptNumber,
            GradingStatus.Returned,
            result.Score,
            result.MaxScore.Value,
            result.GeneralComment,
            result.ReturnedAtUtc,
            sourceMode,
            [],
            result.Attachments.Select(attachment => new StudentResultAttachment(
                attachment.AttachmentId,
                attachment.FileName,
                attachment.SizeBytes,
                attachment.ContentType,
                sourceMode == SessionAccessMode.LanOnly && result.SubmissionId.HasValue
                    ? $"api/v1/student/submissions/{result.SubmissionId.Value}/grade/attachments/{attachment.AttachmentId}/content"
                    : string.Empty,
                resultId)).ToArray());
    }
}

public sealed class StudentResultsIntegrationException(string message) : Exception(message);
