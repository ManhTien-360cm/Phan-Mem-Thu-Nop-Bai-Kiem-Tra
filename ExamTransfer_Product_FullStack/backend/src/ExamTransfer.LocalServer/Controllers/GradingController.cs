using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1/grading")]
[Authorize(Policy = "TeacherOrAdmin")]
public sealed class GradingController(
    IGradeService service,
    IQuizGradingService quizGrades,
    ISubmissionPreviewService previews,
    AppDbContext db,
    IStoragePaths paths) : ApiControllerBase
{
    [HttpGet("submissions/{submissionId:guid}/files/{fileId:guid}/preview-manifest")]
    public async Task<ActionResult<ApiResponse<SubmissionPreviewManifestDto>>> PreviewManifest(
        Guid submissionId,
        Guid fileId,
        CancellationToken ct) =>
        Data(await previews.GetManifestAsync(
            submissionId,
            fileId,
            User.FindFirst("organization_id")?.Value,
            ct));
    [HttpGet("submissions/{submissionId:guid}/files/{fileId:guid}/preview")]
    public async Task<ActionResult<ApiResponse<SubmissionPreviewDto>>> Preview(
        Guid submissionId,
        Guid fileId,
        [FromQuery] string? entry,
        CancellationToken ct) =>
        Data(await previews.GetPreviewAsync(
            submissionId,
            fileId,
            entry,
            User.FindFirst("organization_id")?.Value,
            ct));
    [HttpGet("work-items")]
    public async Task<ActionResult<ApiResponse<PagedResult<GradingWorkItemDto>>>> WorkItems(
        [FromQuery] GradingStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken ct = default) =>
        Data(await quizGrades.GetWorkItemsAsync(
            status,
            page,
            pageSize,
            RequiredGuidClaim(ClaimTypes.NameIdentifier),
            User.FindFirst("organization_id")?.Value,
            ct));
    [HttpGet("quiz-attempts/{id:guid}")]
    public async Task<ActionResult<ApiResponse<QuizGradeDetailDto>>> GetQuiz(Guid id, CancellationToken ct) =>
        Data(await quizGrades.GetAsync(id, RequiredGuidClaim(ClaimTypes.NameIdentifier), User.FindFirst("organization_id")?.Value, ct));
    [HttpPut("quiz-attempts/{id:guid}")]
    public async Task<ActionResult<ApiResponse<QuizGradeDetailDto>>> SaveQuiz(Guid id, SaveQuizGradeRequest request, CancellationToken ct) =>
        Data(await quizGrades.SaveAsync(id, request, RequiredGuidClaim(ClaimTypes.NameIdentifier), User.FindFirst("organization_id")?.Value, ct));
    [HttpPost("quiz-attempts/{id:guid}/return")]
    public async Task<ActionResult<ApiResponse<QuizGradeDetailDto>>> ReturnQuiz(Guid id, ReturnQuizGradeRequest request, CancellationToken ct) =>
        Data(await quizGrades.ReturnAsync(id, request, RequiredGuidClaim(ClaimTypes.NameIdentifier), User.FindFirst("organization_id")?.Value, ct));
    [HttpPost("quiz-attempts/{id:guid}/reopen")]
    public async Task<ActionResult<ApiResponse<QuizGradeDetailDto>>> ReopenQuiz(Guid id, ReopenQuizGradeRequest request, CancellationToken ct) =>
        Data(await quizGrades.ReopenAsync(id, request, RequiredGuidClaim(ClaimTypes.NameIdentifier), User.FindFirst("organization_id")?.Value, ct));
    [HttpGet("queue")]
    public async Task<ActionResult<ApiResponse<PagedResult<SubmissionSummaryDto>>>> Queue([FromQuery] GradingStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        var result = await service.GetQueueAsync(status, page, pageSize, ct);
        var visible = new List<SubmissionSummaryDto>();
        foreach (var item in result.Items)
            if (await CanAccessSubmissionAsync(item.Id, ct))
                visible.Add(item);
        return Data(result with { Items = visible, TotalCount = visible.Count });
    }
    [HttpGet("submissions/{id:guid}")]
    public async Task<ActionResult<ApiResponse<GradeDto>>> Get(Guid id, CancellationToken ct)
    {
        return Data(await service.GetTeacherAsync(
            id,
            RequiredGuidClaim(ClaimTypes.NameIdentifier),
            User.FindFirst("organization_id")?.Value,
            ct));
    }
    [HttpPut("submissions/{id:guid}")]
    public async Task<ActionResult<ApiResponse<GradeDto>>> Save(Guid id, SaveGradeRequest request, CancellationToken ct)
    {
        return Data(await service.SaveAsync(
            id,
            request,
            RequiredGuidClaim(ClaimTypes.NameIdentifier),
            User.FindFirst("organization_id")?.Value,
            ct));
    }
    [HttpPost("submissions/{id:guid}/return")]
    public async Task<ActionResult<ApiResponse<GradeDto>>> Return(Guid id, ReturnGradeRequest request, CancellationToken ct)
    {
        return Data(await service.ReturnAsync(
            id,
            request,
            RequiredGuidClaim(ClaimTypes.NameIdentifier),
            User.FindFirst("organization_id")?.Value,
            ct));
    }
    [HttpPost("submissions/{id:guid}/reopen")]
    public async Task<ActionResult<ApiResponse<GradeDto>>> Reopen(Guid id, ReopenGradeRequest request, CancellationToken ct)
    {
        return Data(await service.ReopenAsync(
            id,
            request,
            RequiredGuidClaim(ClaimTypes.NameIdentifier),
            User.FindFirst("organization_id")?.Value,
            ct));
    }
    [HttpPost("submissions/{id:guid}/attachments")][RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<FileDescriptorDto>>> Attachment(Guid id, [FromQuery] string fileName, [FromQuery] string mimeType, CancellationToken ct)
    {
        return Data(await service.AddAttachmentAsync(
            id,
            fileName,
            mimeType,
            Request.Body,
            Request.ContentLength ?? -1,
            RequiredGuidClaim(ClaimTypes.NameIdentifier),
            User.FindFirst("organization_id")?.Value,
            ct));
    }
    [HttpGet("submissions/{submissionId:guid}/attachments/{attachmentId:guid}/content")]
    public async Task<IActionResult> AttachmentContent(Guid submissionId, Guid attachmentId, CancellationToken ct)
    {
        _ = await service.GetTeacherAsync(
            submissionId,
            RequiredGuidClaim(ClaimTypes.NameIdentifier),
            User.FindFirst("organization_id")?.Value,
            ct);
        var a = await db.GradedAttachmentsSet.AsNoTracking().Include(x => x.Grade).FirstOrDefaultAsync(x => x.Id == attachmentId && x.Grade.SubmissionId == submissionId, ct) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy file đã chấm.", 404);
        var full = Path.GetFullPath(Path.Combine(paths.RootPath, a.RelativePath)); if (!System.IO.File.Exists(full)) throw new ApiException(ErrorCodes.NotFound, "File vật lý không tồn tại.", 404); return PhysicalFile(full, a.MimeType, a.OriginalName, true);
    }
    [HttpGet("gradebook/export")]
    public async Task<IActionResult> Export([FromQuery] Guid? sessionId, CancellationToken ct)
    {
        if (!sessionId.HasValue && !string.IsNullOrWhiteSpace(User.FindFirst("organization_id")?.Value))
            throw new ApiException(ErrorCodes.ValidationFailed, "Phải chọn phiên khi xuất gradebook theo tổ chức.");
        if (sessionId.HasValue)
            await EnsureSessionAccessAsync(sessionId.Value, ct);
        return File(await service.ExportGradebookCsvAsync(sessionId, ct), "text/csv; charset=utf-8", "gradebook.csv");
    }

    private async Task<bool> CanAccessSubmissionAsync(Guid submissionId, CancellationToken ct)
    {
        var claimedOrganization = User.FindFirst("organization_id")?.Value;
        if (string.IsNullOrWhiteSpace(claimedOrganization))
            return false;
        var actorOrganization = await db.UsersSet.AsNoTracking()
            .Where(x => x.Id == RequiredGuidClaim(ClaimTypes.NameIdentifier)
                && x.IsActive
                && (x.Role == UserRole.Teacher || x.Role == UserRole.Admin))
            .Select(x => x.OrganizationId)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(actorOrganization)
            || !string.Equals(actorOrganization, claimedOrganization, StringComparison.Ordinal))
        {
            return false;
        }
        var createdBy = await db.SubmissionsSet.AsNoTracking()
            .Where(x => x.Id == submissionId)
            .Select(x => x.Session.Exam.CreatedBy)
            .SingleOrDefaultAsync(ct);
        if (!createdBy.HasValue)
            return false;
        var ownerOrganization = await db.UsersSet.AsNoTracking()
            .Where(x => x.Id == createdBy.Value && x.IsActive)
            .Select(x => x.OrganizationId)
            .SingleOrDefaultAsync(ct);
        return !string.IsNullOrWhiteSpace(ownerOrganization)
            && string.Equals(ownerOrganization, actorOrganization, StringComparison.Ordinal);
    }

    private async Task EnsureSessionAccessAsync(Guid sessionId, CancellationToken ct)
    {
        var claimedOrganization = User.FindFirst("organization_id")?.Value;
        var actorOrganization = await db.UsersSet.AsNoTracking()
            .Where(x => x.Id == RequiredGuidClaim(ClaimTypes.NameIdentifier)
                && x.IsActive
                && (x.Role == UserRole.Teacher || x.Role == UserRole.Admin))
            .Select(x => x.OrganizationId)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(claimedOrganization)
            || string.IsNullOrWhiteSpace(actorOrganization)
            || !string.Equals(actorOrganization, claimedOrganization, StringComparison.Ordinal))
        {
            throw new ApiException(ErrorCodes.Forbidden, "Không xác định được tổ chức của người chấm.", 403);
        }
        var createdBy = await db.ExamSessionsSet.AsNoTracking()
            .Where(x => x.Id == sessionId)
            .Select(x => x.Exam.CreatedBy)
            .SingleOrDefaultAsync(ct);
        if (!createdBy.HasValue)
            throw new ApiException(ErrorCodes.Forbidden, "Không xác định được chủ sở hữu phiên thi.", 403);
        var ownerOrganization = await db.UsersSet.AsNoTracking()
            .Where(x => x.Id == createdBy.Value && x.IsActive)
            .Select(x => x.OrganizationId)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(ownerOrganization)
            || !string.Equals(ownerOrganization, actorOrganization, StringComparison.Ordinal))
            throw new ApiException(ErrorCodes.Forbidden, "Không được xuất điểm thuộc tổ chức khác.", 403);
    }
}

