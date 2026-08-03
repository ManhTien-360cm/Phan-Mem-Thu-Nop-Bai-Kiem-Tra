using ExamTransfer.Application;
using ExamTransfer.LocalServer.Auth;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1")]
[Authorize(AuthenticationSchemes = ExamTransferAuthSchemes.Account + "," + ExamTransferAuthSchemes.ExamParticipant)]
public sealed class SubmissionsController(
    ISubmissionService service,
    ISubmissionDownloadService downloads) : ApiControllerBase
{
    [HttpPost("submissions/init")][Authorize(Policy = "StudentParticipant")]
    public async Task<ActionResult<ApiResponse<InitSubmissionResponse>>> Init(InitSubmissionRequest request, CancellationToken ct)
    {
        EnsureStudentScope(request.SessionId, request.ParticipantId);
        return Data(await service.InitAsync(request, ct));
    }

    [HttpPut("submissions/{id:guid}/files/{fileId:guid}/chunks/{index:int}")][Authorize(Policy = "StudentParticipant")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<object>>> Chunk(Guid id, Guid fileId, int index, CancellationToken ct)
    {
        await EnsureSubmissionScopeAsync(id, ct);
        await service.UploadChunkAsync(id, fileId, index, Request.Body, Request.ContentLength ?? -1, Request.Headers["X-Chunk-Sha256"].FirstOrDefault(), ct);
        return EmptyData();
    }

    [HttpGet("submissions/{id:guid}/status")]
    public async Task<ActionResult<ApiResponse<SubmissionSummaryDto>>> Status(Guid id, CancellationToken ct)
    {
        var submission = await EnsureSubmissionScopeAsync(id, ct);
        return Data(submission);
    }

    [HttpPost("submissions/{id:guid}/finalize")][Authorize(Policy = "StudentParticipant")]
    public async Task<ActionResult<ApiResponse<FinalizeSubmissionResponse>>> Finalize(Guid id, FinalizeSubmissionRequest request, CancellationToken ct)
    {
        await EnsureSubmissionScopeAsync(id, ct);
        return Data(await service.FinalizeAsync(id, request, ct));
    }

    [HttpGet("submissions/{id:guid}/receipt")]
    public async Task<ActionResult<ApiResponse<ReceiptDto>>> Receipt(Guid id, CancellationToken ct)
    {
        await EnsureSubmissionScopeAsync(id, ct);
        return Data(await service.GetReceiptAsync(id, ct));
    }

    [HttpGet("sessions/{sessionId:guid}/submissions")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<PagedResult<SubmissionSummaryDto>>>> List(Guid sessionId, [FromQuery] SubmissionStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default) => Data(await service.ListForSessionAsync(sessionId, status, page, pageSize, ct));

    [HttpGet("submissions/{id:guid}")]
    public async Task<ActionResult<ApiResponse<SubmissionSummaryDto>>> Get(Guid id, CancellationToken ct) => Data(await EnsureSubmissionScopeAsync(id, ct));

    [HttpPost("submissions/{id:guid}/reject")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<object>>> Reject(Guid id, RejectSubmissionRequest request, CancellationToken ct) { await service.RejectAsync(id, request, ct); return EmptyData(); }

    [HttpPost("participants/{participantId:guid}/allow-resubmit")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<object>>> Resubmit(Guid participantId, AllowResubmitRequest request, CancellationToken ct) { await service.AllowResubmitAsync(participantId, request, ct); return EmptyData(); }

    [HttpGet("submissions/{id:guid}/files/{fileId:guid}/content")]
    [Authorize(Policy = "TeacherOrAdmin")]
    public async Task<IActionResult> FileContent(Guid id, Guid fileId, CancellationToken ct)
    {
        var download = await downloads.OpenAsync(
            id,
            fileId,
            RequiredGuidClaim(ClaimTypes.NameIdentifier),
            User.FindFirst("organization_id")?.Value,
            HttpContext.TraceIdentifier,
            ct);
        return File(
            download.Content,
            download.MimeType,
            download.DownloadName,
            enableRangeProcessing: true);
    }

    private async Task<SubmissionSummaryDto> EnsureSubmissionScopeAsync(Guid submissionId, CancellationToken ct)
    {
        var submission = await service.GetAsync(submissionId, ct);
        EnsureStudentScope(submission.SessionId, submission.ParticipantId);
        return submission;
    }
}
