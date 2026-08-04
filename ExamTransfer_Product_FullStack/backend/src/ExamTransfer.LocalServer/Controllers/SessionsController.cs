using ExamTransfer.Application;
using ExamTransfer.LocalServer.Auth;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1/sessions")]
[Authorize(AuthenticationSchemes = ExamTransferAuthSchemes.Account + "," + ExamTransferAuthSchemes.ExamParticipant)]
public sealed class SessionsController(ISessionService service) : ApiControllerBase
{
    [HttpGet][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<PagedResult<SessionSummaryDto>>>> List([FromQuery] SessionStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) => Data(await service.ListAsync(status, page, pageSize, ct));

    [HttpPost][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<SessionDetailDto>>> Create(CreateSessionRequest request, CancellationToken ct) => Data(await service.CreateAsync(request, Environment.MachineName, ct));

    [HttpPost("create-and-open")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<SessionDetailDto>>> CreateAndOpen(CreateSessionRequest request, CancellationToken ct) =>
        Data(await service.CreateAndOpenAsync(request, Environment.MachineName, ct));

    [HttpGet("{id:guid}/cloud-projection")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<CloudProjectionReadiness>>> CloudProjection(Guid id, CancellationToken ct) =>
        Data(await service.GetProjectionReadinessAsync(id, ct));

    [HttpPost("{id:guid}/cloud-projection/retry")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<CloudProjectionReadiness>>> RetryCloudProjection(Guid id, CancellationToken ct) =>
        Data(await service.RetryProjectionAsync(id, ct));

    [HttpPut("{id:guid}/room-code")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<SessionDetailDto>>> ChangePublicCloudRoomCode(
        Guid id,
        ChangePublicCloudRoomCodeRequest request,
        CancellationToken ct) =>
        Data(await service.ChangePublicCloudRoomCodeAsync(id, request, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<SessionDetailDto>>> Get(Guid id, CancellationToken ct)
    {
        var detail = await service.GetAsync(id, ct);
        if (IsStudent)
        {
            var participantId = RequiredGuidClaim("participant_id");
            EnsureStudentScope(id, participantId);
            detail = detail with { Participants = detail.Participants.Where(x => x.Id == participantId).ToList() };
        }
        return Data(detail);
    }

    [HttpPut("{id:guid}")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<SessionDetailDto>>> Update(Guid id, UpdateSessionRequest request, CancellationToken ct) => Data(await service.UpdateAsync(id, request, ct));

    [HttpPost("{id:guid}/open")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<SessionDetailDto>>> Open(Guid id, CancellationToken ct) => Data(await service.TransitionAsync(id, SessionStatus.Waiting, null, ct));
    [HttpPost("{id:guid}/distribute")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<SessionDetailDto>>> Distribute(Guid id, CancellationToken ct) => Data(await service.TransitionAsync(id, SessionStatus.Distributing, null, ct));
    [HttpPost("{id:guid}/start")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<SessionDetailDto>>> Start(Guid id, CancellationToken ct) => Data(await service.TransitionAsync(id, SessionStatus.InProgress, null, ct));
    [HttpPost("{id:guid}/pause")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<SessionDetailDto>>> Pause(Guid id, CancellationToken ct) => Data(await service.TransitionAsync(id, SessionStatus.Paused, null, ct));
    [HttpPost("{id:guid}/resume")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<SessionDetailDto>>> Resume(Guid id, CancellationToken ct) => Data(await service.TransitionAsync(id, SessionStatus.InProgress, null, ct));
    [HttpPost("{id:guid}/collect")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<SessionDetailDto>>> Collect(Guid id, CancellationToken ct) => Data(await service.TransitionAsync(id, SessionStatus.Collecting, null, ct));
    [HttpPost("{id:guid}/end")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<SessionDetailDto>>> End(Guid id, EndSessionRequest request, CancellationToken ct) => Data(await service.TransitionAsync(id, SessionStatus.Finished, request, ct));
    [HttpPost("{id:guid}/cancel")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<SessionDetailDto>>> Cancel(Guid id, EndSessionRequest request, CancellationToken ct) => Data(await service.TransitionAsync(id, SessionStatus.Cancelled, request, ct));
    [HttpPost("bulk-archive")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<BulkArchiveResultDto>>> BulkArchive(BulkArchiveRequest request, CancellationToken ct) =>
        Data(await service.BulkArchiveAsync(request, ct));

    [HttpPost("join")][Authorize(Policy = "Student")]
    public async Task<ActionResult<ApiResponse<JoinSessionResponse>>> Join(JoinSessionRequest request, CancellationToken ct)
    {
        var userId = RequiredGuidClaim("sub");
        var studentCode = User.FindFirst("student_code")?.Value
            ?? throw new ApiException(ErrorCodes.StudentIdentityMismatch, "Tài khoản sinh viên chưa có mã sinh viên.", 422);
        var displayName = User.Identity?.Name
            ?? throw new ApiException(ErrorCodes.StudentIdentityMismatch, "Tài khoản sinh viên chưa có họ tên.", 422);
        return Data(await service.JoinAsync(request, userId, studentCode, displayName, HttpContext.Connection.RemoteIpAddress?.ToString(), ct));
    }

    [HttpGet("{id:guid}/participants/{participantId:guid}")]
    public async Task<ActionResult<ApiResponse<ParticipantDto>>> Participant(Guid id, Guid participantId, CancellationToken ct)
    {
        EnsureStudentScope(id, participantId);
        return Data(await service.GetParticipantAsync(id, participantId, ct));
    }

    [HttpPost("{id:guid}/participants/{participantId:guid}/approve")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<ParticipantDto>>> Approve(Guid id, Guid participantId, TeacherMutationRequest mutation, CancellationToken ct) =>
        Data(await service.ApproveAsync(id, participantId, mutation.MutationRequestId, ct));
    [HttpPost("{id:guid}/participants/{participantId:guid}/reject")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<object>>> Reject(Guid id, Guid participantId, ReasonedTeacherMutationRequest mutation, CancellationToken ct) { await service.RejectAsync(id, participantId, mutation.Reason, mutation.MutationRequestId, ct); return EmptyData(); }
    [HttpPost("{id:guid}/participants/bulk-approve")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ParticipantDto>>>> BulkApprove(Guid id, BulkApproveRequest request, CancellationToken ct) => Data(await service.BulkApproveAsync(id, request, ct));
    [HttpPost("{id:guid}/participants/{participantId:guid}/extra-time")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<ParticipantDto>>> ExtraTime(Guid id, Guid participantId, ExtraTimeRequest request, CancellationToken ct) => Data(await service.AddExtraTimeAsync(id, participantId, request, ct));
    [HttpPost("{id:guid}/messages")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<MessageDto>>> Message(Guid id, SendMessageRequest request, CancellationToken ct) => Data(await service.SendMessageAsync(id, request, ct));
    [HttpPost("{id:guid}/participants/{participantId:guid}/heartbeat")][Authorize(Policy = "StudentParticipant")]
    public async Task<ActionResult<ApiResponse<HeartbeatResponse>>> Heartbeat(Guid id, Guid participantId, HeartbeatRequest request, CancellationToken ct)
    {
        EnsureStudentScope(id, participantId);
        var deviceId = User.FindFirst("device_id")?.Value ?? throw new ApiException(ErrorCodes.Unauthorized, "Token thiếu device_id.", 401);
        return Data(await service.HeartbeatAsync(id, participantId, deviceId, request, ct));
    }
}
