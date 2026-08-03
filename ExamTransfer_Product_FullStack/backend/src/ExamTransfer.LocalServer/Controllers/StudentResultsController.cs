using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1/student")]
public sealed class StudentResultsController(
    IStudentResultService results,
    IGradeService grades,
    ISubmissionService submissions,
    AppDbContext db,
    IStoragePaths paths) : ApiControllerBase
{
    [HttpGet("results")]
    [Authorize(Policy = "Student")]
    public async Task<ActionResult<ApiResponse<StudentResultPageDto>>> Results(
        [FromQuery] int pageSize = 50,
        [FromQuery] DateTimeOffset? cursorReturnedAtUtc = null,
        [FromQuery] StudentResultType? cursorResultType = null,
        [FromQuery] Guid? cursorResultId = null,
        CancellationToken ct = default)
    {
        if ((cursorReturnedAtUtc.HasValue || cursorResultType.HasValue || cursorResultId.HasValue)
            && (!cursorReturnedAtUtc.HasValue || !cursorResultType.HasValue || !cursorResultId.HasValue))
        {
            throw new ApiException(ErrorCodes.ValidationFailed, "Cursor kết quả không đầy đủ.");
        }
        var cursor = cursorReturnedAtUtc.HasValue
            ? new StudentResultCursorDto
            {
                ReturnedAtUtc = cursorReturnedAtUtc.Value,
                ResultType = cursorResultType!.Value,
                ResultId = cursorResultId!.Value
            }
            : null;
        var actorId = RequiredGuidClaim(ClaimTypes.NameIdentifier);
        var page = await results.GetReturnedAsync(
            actorId,
            User.FindFirst("organization_id")?.Value,
            pageSize,
            cursor,
            ct);
        return Data(page);
    }

    [HttpGet("submissions/{submissionId:guid}/grade")]
    [Authorize(Policy = "StudentWithParticipant")]
    public async Task<ActionResult<ApiResponse<GradeDto>>> Grade(Guid submissionId, CancellationToken ct)
    {
        var submission = await submissions.GetAsync(submissionId, ct);
        EnsureStudentScope(submission.SessionId, submission.ParticipantId);
        var grade = await grades.GetAsync(submissionId, ct);
        if (grade.Status != GradingStatus.Returned)
            throw new ApiException(ErrorCodes.Forbidden, "Kết quả chưa được giáo viên công bố.", 403);
        grade = grade with
        {
            Attachments = grade.Attachments.Select(x => x with
            {
                DownloadUrl = $"/api/v1/student/submissions/{submissionId}/grade/attachments/{x.Id}/content"
            }).ToList()
        };
        return Data(grade);
    }

    [HttpGet("submissions/{submissionId:guid}/grade/attachments/{attachmentId:guid}/content")]
    [Authorize(Policy = "StudentWithParticipant")]
    public async Task<IActionResult> Attachment(Guid submissionId, Guid attachmentId, CancellationToken ct)
    {
        var submission = await submissions.GetAsync(submissionId, ct);
        EnsureStudentScope(submission.SessionId, submission.ParticipantId);
        var attachment = await db.GradedAttachmentsSet.AsNoTracking()
            .Include(x => x.Grade)
            .FirstOrDefaultAsync(x => x.Id == attachmentId && x.Grade.SubmissionId == submissionId && x.Grade.Status == GradingStatus.Returned, ct)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy file kết quả đã công bố.", 404);
        var fullPath = Path.GetFullPath(Path.Combine(paths.RootPath, attachment.RelativePath));
        if (!fullPath.StartsWith(Path.GetFullPath(paths.RootPath), StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(fullPath))
            throw new ApiException(ErrorCodes.NotFound, "File kết quả không tồn tại.", 404);
        return PhysicalFile(fullPath, attachment.MimeType, attachment.OriginalName, true);
    }
}
