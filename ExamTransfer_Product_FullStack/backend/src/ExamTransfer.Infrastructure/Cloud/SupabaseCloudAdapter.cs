using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Cloud;

public sealed class SupabaseCloudAdapter(
    HttpClient httpClient,
    IOptions<ExamTransferOptions> options,
    CloudSessionState sessionState) : ICloudAdapter
{
    private const string TusVersion = "1.0.0";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly CloudOptions cloudOptions = options.Value.Cloud;
    private readonly SemaphoreSlim refreshLock = new(1, 1);

    public bool Enabled => cloudOptions.Enabled;

    public bool Configured => Enabled && GetConfigurationErrors().Count == 0;

    public bool Authenticated => sessionState.Snapshot is not null;

    public bool CanSynchronize =>
        Configured
        && (UsesTrustedServer || HasUsableUserSession);

    public CloudLoginResult? CurrentSession =>
        sessionState.Snapshot is { } snapshot
            ? ToLoginResult(snapshot)
            : null;

    private bool UsesTrustedServer => string.Equals(
        cloudOptions.AccessMode,
        CloudAccessModes.TrustedServer,
        StringComparison.OrdinalIgnoreCase);

    private bool HasUsableUserSession
    {
        get
        {
            var snapshot = sessionState.Snapshot;
            return snapshot is not null
                && Guid.TryParse(snapshot.OrganizationId, out var sessionOrganization)
                && Guid.TryParse(cloudOptions.OrganizationId, out var configuredOrganization)
                && sessionOrganization == configuredOrganization;
        }
    }

    public async Task<bool> CheckHealthAsync(
        CancellationToken cancellationToken)
    {
        if (!CanSynchronize)
            return false;

        try
        {
            using var request = await CreateSyncRequestAsync(
                HttpMethod.Post,
                "/rest/v1/rpc/get_examtransfer_cloud_capabilities",
                cancellationToken);
            request.Content = JsonContent.Create(new { });
            using var response = await httpClient.SendAsync(
                request,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return false;
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1)
                root = root[0];
            if (!root.TryGetProperty("schemaVersion", out var schema)
                || schema.GetInt32() != CloudSchemaCompatibility.RequiredVersion)
                return false;
            if (!root.TryGetProperty("criticalRpcs", out var rpcElement)
                || rpcElement.ValueKind != JsonValueKind.Array)
                return false;
            var rpcs = rpcElement.EnumerateArray()
                .Select(x => x.GetString())
                .Where(x => x is not null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);
            if (!CloudSchemaCompatibility.CriticalRpcs.IsSubsetOf(rpcs))
                return false;
            if (!root.TryGetProperty("buckets", out var bucketElement)
                || bucketElement.ValueKind != JsonValueKind.Array)
                return false;
            var buckets = bucketElement.EnumerateArray()
                .Select(x => x.GetString())
                .Where(x => x is not null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);
            return buckets.Contains("exam-archives")
                && buckets.Contains("public-submission-archives");
        }
        catch
        {
            return false;
        }
    }

    public async Task<CloudPreflightResult> PreflightAsync(
        CancellationToken cancellationToken)
    {
        var errors = GetConfigurationErrors();
        var warnings = new List<string>();
        var secret = ResolveSecretKey();
        var publishable = cloudOptions.EffectivePublishableKey;
        var keyMode = DescribeKeyMode(publishable, secret);
        var session = CurrentSession;

        if (cloudOptions.TusChunkSizeBytes != 6 * 1024 * 1024)
        {
            warnings.Add(
                "Supabase TUS dùng chunk 6 MiB; Backend sẽ chuẩn hóa kích thước khi upload.");
        }

        if (!cloudOptions.UseResumableUploads)
        {
            warnings.Add(
                "Upload resumable đang tắt; file lớn sẽ kém ổn định khi mạng gián đoạn.");
        }

        var configured = Enabled && errors.Count == 0;
        if (!UsesTrustedServer && configured && session is null)
        {
            warnings.Add(
                "Cần đăng nhập tài khoản Supabase Teacher/Admin trước khi đồng bộ.");
        }
        else if (!UsesTrustedServer && configured && !HasUsableUserSession)
        {
            warnings.Add(
                "Phiên Supabase không thuộc OrganizationId đang cấu hình; cần đăng nhập lại.");
        }

        var canSynchronize = configured
            && (UsesTrustedServer || HasUsableUserSession);
        var reachable = canSynchronize
            && await CheckHealthAsync(cancellationToken);

        if (canSynchronize && !reachable)
            warnings.Add(
                "Supabase schema hiện tại không tương thích với phiên bản phần mềm, thiếu capability bắt buộc hoặc không thể kết nối. Hãy chạy migration trước khi bật PublicCloud; LAN vẫn hoạt động bằng SQLite.");

        return new CloudPreflightResult(
            Enabled,
            configured,
            reachable,
            !string.IsNullOrWhiteSpace(secret),
            keyMode,
            cloudOptions.OrganizationId,
            cloudOptions.UseResumableUploads
                ? "TUS resumable for files over 6 MiB"
                : "Standard upload",
            errors,
            warnings,
            UsesTrustedServer
                ? CloudAccessModes.TrustedServer
                : CloudAccessModes.UserSession,
            session is not null,
            session?.Email,
            canSynchronize);
    }

    public async Task<CloudPushResult> PushAsync(
        SyncQueueItem item,
        Func<CancellationToken, Task>? checkpoint,
        CancellationToken cancellationToken)
    {
        EnsureCanSynchronize();
        item.LastAttemptAtUtc = DateTimeOffset.UtcNow;

        var table = MapTable(item.EntityType);
        var operation = item.Operation.Trim().ToLowerInvariant();

        if (operation == "delete")
        {
            await DeleteObjectIfPresentAsync(
                item.EntityType,
                item.EntityId,
                item.PayloadJson,
                item.CloudObjectPath,
                cancellationToken);

            if (table is not null)
            {
                await DeleteMetadataAsync(
                    table,
                    item.EntityId,
                    cancellationToken);
            }

            item.UploadUrl = null;
            item.UploadOffset = 0;
            item.CompletedAtUtc = DateTimeOffset.UtcNow;
            return new CloudPushResult(true, null, "delete", 0);
        }

        string? cloudObjectPath = item.CloudObjectPath;
        var uploadStrategy = "metadata-only";
        long bytesTransferred = 0;

        if (!string.IsNullOrWhiteSpace(item.FilePath))
        {
            var uploadResult = await UploadObjectAsync(
                item,
                checkpoint,
                cancellationToken);
            cloudObjectPath = uploadResult.CloudObjectPath;
            uploadStrategy = uploadResult.UploadStrategy;
            bytesTransferred = uploadResult.BytesTransferred;
            item.CloudObjectPath = cloudObjectPath;
        }

        if (table is not null)
        {
            var payload = BuildPayload(
                item.EntityType,
                item.PayloadJson,
                cloudObjectPath);
            await PushMetadataOptimisticallyAsync(table, item.EntityId, payload, cancellationToken);
        }

        item.UploadUrl = null;
        item.UploadOffset = 0;
        item.CompletedAtUtc = DateTimeOffset.UtcNow;
        return new CloudPushResult(
            false,
            cloudObjectPath,
            uploadStrategy,
            bytesTransferred);
    }

    public async Task<CloudPullPage> PullAsync(
        string entityName,
        CloudPullCursorValue cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        EnsureCanSynchronize();
        var table = CloudEntityOwnershipRegistry.Normalize(entityName);
        if (CloudEntityOwnershipRegistry.GetAuthority(table)
            is not (CloudEntityAuthority.CloudOwned or CloudEntityAuthority.SourceModeDependent))
            throw new ArgumentException($"Entity '{entityName}' is not cloud-owned.", nameof(entityName));

        var keyColumn = table is "public_device_commands" or "public_device_command_results"
            ? "command_id"
            : "id";
        var boundedLimit = Math.Clamp(limit, 1, 500);
        var sourceFilter = CloudEntityOwnershipRegistry.GetAuthority(table) == CloudEntityAuthority.SourceModeDependent
            ? "&source_mode=eq.PublicCloud"
            : string.Empty;
        var organizationId = Uri.EscapeDataString(GetRequiredOrganizationId().ToString());
        var path = $"/rest/v1/{table}?select=*&organization_id=eq.{organizationId}" +
            $"{sourceFilter}&cloud_version=gte.{cursor.CloudVersion}" +
            $"&order=cloud_version.asc,updated_at.asc,{keyColumn}.asc&limit={boundedLimit}";
        using var request = await CreateSyncRequestAsync(HttpMethod.Get, path, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, $"Supabase pull {table}", cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var records = new List<CloudPullRecord>();
        var rawRows = document.RootElement.EnumerateArray().ToList();
        foreach (var row in rawRows)
        {
            var id = row.GetProperty(keyColumn).GetString()
                ?? throw new JsonException($"{table}.{keyColumn} is missing.");
            var cloudVersion = row.GetProperty("cloud_version").GetInt64();
            var updatedAt = row.GetProperty("updated_at").GetDateTimeOffset();
            if (IsAfterCursor(cloudVersion, updatedAt, id, cursor))
                records.Add(new CloudPullRecord(table, id, cloudVersion, updatedAt, row.GetRawText()));
        }
        return new CloudPullPage(records, rawRows.Count == boundedLimit);
    }

    public async Task<CloudParticipantMutationResult> ApprovePublicParticipantAsync(
        Guid sessionId,
        Guid participantId,
        Guid requestId,
        CancellationToken cancellationToken) =>
        ParseParticipantMutation(await InvokeTeacherRpcAsync(
            "approve_public_participant",
            new { p_session_id = sessionId, p_participant_id = participantId, p_request_id = requestId },
            cancellationToken));

    public async Task<CloudParticipantMutationResult> RejectPublicParticipantAsync(
        Guid sessionId,
        Guid participantId,
        string? reason,
        Guid requestId,
        CancellationToken cancellationToken) =>
        ParseParticipantMutation(await InvokeTeacherRpcAsync(
            "reject_public_participant",
            new { p_session_id = sessionId, p_participant_id = participantId, p_reason = reason, p_request_id = requestId },
            cancellationToken));

    public async Task<CloudBulkParticipantMutationResult> BulkApprovePublicParticipantsAsync(
        Guid sessionId,
        IReadOnlyList<Guid> participantIds,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var result = await InvokeTeacherRpcAsync(
            "bulk_approve_public_participants",
            new { p_session_id = sessionId, p_participant_ids = participantIds, p_request_id = requestId },
            cancellationToken);
        var participants = result.GetProperty("participants")
            .EnumerateArray()
            .Select(ParseParticipantMutation)
            .ToList();
        return new(
            result.GetProperty("approvedCount").GetInt32(),
            result.GetProperty("skippedCount").GetInt32(),
            participants);
    }

    public async Task<CloudParticipantMutationResult> AddPublicParticipantExtraTimeAsync(
        Guid sessionId,
        Guid participantId,
        int minutes,
        string reason,
        Guid requestId,
        CancellationToken cancellationToken) =>
        ParseParticipantMutation(await InvokeTeacherRpcAsync(
            "add_public_participant_extra_time",
            new
            {
                p_session_id = sessionId,
                p_participant_id = participantId,
                p_minutes = minutes,
                p_reason = reason,
                p_request_id = requestId
            },
            cancellationToken));

    public async Task<CloudParticipantMutationResult> AllowPublicResubmissionAsync(
        Guid participantId,
        string reason,
        Guid requestId,
        CancellationToken cancellationToken) =>
        ParseParticipantMutation(await InvokeTeacherRpcAsync(
            "allow_public_resubmission",
            new { p_participant_id = participantId, p_reason = reason, p_request_id = requestId },
            cancellationToken));

    public async Task<CloudSubmissionMutationResult> RejectPublicSubmissionAsync(
        Guid submissionId,
        string reason,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var result = await InvokeTeacherRpcAsync(
            "reject_public_submission",
            new { p_submission_id = submissionId, p_reason = reason, p_request_id = requestId },
            cancellationToken);
        return new(
            result.GetProperty("submissionId").GetGuid(),
            result.GetProperty("sessionId").GetGuid(),
            result.GetProperty("participantId").GetGuid(),
            Enum.Parse<SubmissionStatus>(result.GetProperty("status").GetString()!, true),
            OptionalString(result, "teacherRejectReason"),
            result.GetProperty("cloudVersion").GetInt64(),
            result.GetProperty("updatedAt").GetDateTimeOffset());
    }

    public async Task<MessageDto> SendPublicTeacherMessageAsync(
        Guid sessionId,
        Guid? participantId,
        MessageType messageType,
        string content,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var result = await InvokeTeacherRpcAsync(
            "send_public_teacher_message",
            new
            {
                p_session_id = sessionId,
                p_participant_id = participantId,
                p_message_type = messageType.ToString(),
                p_content = content,
                p_request_id = requestId
            },
            cancellationToken);
        return new MessageDto(
            result.GetProperty("id").GetGuid(),
            result.GetProperty("sessionId").GetGuid(),
            result.TryGetProperty("senderId", out var sender) && sender.ValueKind != JsonValueKind.Null
                ? sender.GetGuid()
                : null,
            result.TryGetProperty("receiverId", out var receiver) && receiver.ValueKind != JsonValueKind.Null
                ? receiver.GetGuid()
                : null,
            Enum.Parse<MessageType>(result.GetProperty("type").GetString()!, true),
            result.GetProperty("content").GetString()
                ?? throw new JsonException("PublicCloud teacher message content is missing."),
            result.GetProperty("createdAtUtc").GetDateTimeOffset());
    }

    public async Task<CloudEnrollmentMutationResult> ApprovePublicEnrollmentAsync(
        Guid enrollmentRequestId,
        Guid requestId,
        CancellationToken cancellationToken) =>
        ParseEnrollmentMutation(await InvokeTeacherRpcAsync(
            "approve_public_enrollment_request",
            new { p_enrollment_request_id = enrollmentRequestId, p_request_id = requestId },
            cancellationToken));

    public async Task<CloudEnrollmentMutationResult> RejectPublicEnrollmentAsync(
        Guid enrollmentRequestId,
        string? reason,
        Guid requestId,
        CancellationToken cancellationToken) =>
        ParseEnrollmentMutation(await InvokeTeacherRpcAsync(
            "reject_public_enrollment_request",
            new { p_enrollment_request_id = enrollmentRequestId, p_reason = reason, p_request_id = requestId },
            cancellationToken));

    public async Task<CloudQuizGradeMutationResult> SavePublicQuizGradeAsync(
        Guid attemptId,
        decimal? score,
        string? generalComment,
        long expectedCloudVersion,
        Guid requestId,
        CancellationToken cancellationToken) =>
        ParseQuizGradeMutation(await InvokeTeacherRpcAsync(
            "save_public_quiz_grade",
            new
            {
                p_attempt_id = attemptId,
                p_score = score,
                p_general_comment = generalComment,
                p_expected_cloud_version = expectedCloudVersion,
                p_request_id = requestId
            },
            cancellationToken));

    public async Task<CloudQuizGradeMutationResult> ReturnPublicQuizGradeAsync(
        Guid attemptId,
        string? message,
        long expectedCloudVersion,
        Guid requestId,
        CancellationToken cancellationToken) =>
        ParseQuizGradeMutation(await InvokeTeacherRpcAsync(
            "return_public_quiz_grade",
            new
            {
                p_attempt_id = attemptId,
                p_message = message,
                p_expected_cloud_version = expectedCloudVersion,
                p_request_id = requestId
            },
            cancellationToken));

    public async Task<CloudQuizGradeMutationResult> ReopenPublicQuizGradeAsync(
        Guid attemptId,
        string reason,
        long expectedCloudVersion,
        Guid requestId,
        CancellationToken cancellationToken) =>
        ParseQuizGradeMutation(await InvokeTeacherRpcAsync(
            "reopen_public_quiz_grade",
            new
            {
                p_attempt_id = attemptId,
                p_reason = reason,
                p_expected_cloud_version = expectedCloudVersion,
                p_request_id = requestId
            },
            cancellationToken));

    public async Task<CloudEssayGradeResult> GetPublicEssayGradeAsync(
        Guid submissionId,
        CancellationToken cancellationToken) =>
        ParseEssayGrade(await InvokeTeacherRpcAsync(
            "get_public_essay_grade",
            new { p_submission_id = submissionId },
            cancellationToken));

    public async Task<CloudEssayGradeResult> SavePublicEssayGradeAsync(
        Guid submissionId,
        decimal? score,
        IReadOnlyList<RubricScoreDto> rubricScores,
        string? generalComment,
        long expectedCloudVersion,
        Guid requestId,
        CancellationToken cancellationToken) =>
        ParseEssayGrade(await InvokeTeacherRpcAsync(
            "save_public_essay_grade",
            new
            {
                p_submission_id = submissionId,
                p_score = score,
                p_rubric_scores = rubricScores,
                p_general_comment = generalComment,
                p_expected_cloud_version = expectedCloudVersion,
                p_request_id = requestId
            },
            cancellationToken));

    public async Task<CloudEssayGradeResult> ReturnPublicEssayGradeAsync(
        Guid submissionId,
        string? message,
        long expectedCloudVersion,
        Guid requestId,
        CancellationToken cancellationToken) =>
        ParseEssayGrade(await InvokeTeacherRpcAsync(
            "return_public_essay_grade",
            new
            {
                p_submission_id = submissionId,
                p_message = message,
                p_expected_cloud_version = expectedCloudVersion,
                p_request_id = requestId
            },
            cancellationToken));

    public async Task<CloudEssayGradeResult> ReopenPublicEssayGradeAsync(
        Guid submissionId,
        string reason,
        long expectedCloudVersion,
        Guid requestId,
        CancellationToken cancellationToken) =>
        ParseEssayGrade(await InvokeTeacherRpcAsync(
            "reopen_public_essay_grade",
            new
            {
                p_submission_id = submissionId,
                p_reason = reason,
                p_expected_cloud_version = expectedCloudVersion,
                p_request_id = requestId
            },
            cancellationToken));

    private async Task<JsonElement> InvokeTeacherRpcAsync(
        string rpcName,
        object payload,
        CancellationToken cancellationToken)
    {
        EnsurePublishableConfigured();
        var session = await RefreshSessionAsync(cancellationToken)
            ?? throw new ApiException(
                ErrorCodes.Unauthorized,
                "Cần đăng nhập Supabase bằng tài khoản giáo viên trước khi thao tác dữ liệu PublicCloud.",
                401);
        if (session.Role is not ("Teacher" or "Admin")
            || !Guid.TryParse(session.OrganizationId, out var sessionOrganization)
            || sessionOrganization != GetRequiredOrganizationId())
        {
            throw new ApiException(
                ErrorCodes.Forbidden,
                "Phiên Supabase hiện tại không có quyền giáo viên trong tổ chức đang cấu hình.",
                403);
        }

        using var request = CreateProjectRequest(
            HttpMethod.Post,
            $"/rest/v1/rpc/{rpcName}",
            CloudCredential.Publishable,
            session.AccessToken,
            includeUserToken: false);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var traceId = response.Headers.TryGetValues("x-request-id", out var values)
                ? values.FirstOrDefault()
                : null;
            traceId ??= Guid.NewGuid().ToString("N");
            var concurrencyConflict = content.Contains(
                    "QUIZ_GRADE_VERSION_CONFLICT",
                    StringComparison.OrdinalIgnoreCase)
                || content.Contains(
                    "ESSAY_GRADE_VERSION_CONFLICT",
                    StringComparison.OrdinalIgnoreCase);
            var status = concurrencyConflict
                ? 409
                : response.StatusCode is HttpStatusCode.Unauthorized
                ? 401
                : response.StatusCode is HttpStatusCode.Forbidden
                    ? 403
                    : 502;
            var code = concurrencyConflict
                ? ErrorCodes.ConcurrencyConflict
                : status == 401
                ? ErrorCodes.Unauthorized
                : status == 403
                    ? ErrorCodes.Forbidden
                    : ErrorCodes.CloudUploadFailed;
            throw new ApiException(
                code,
                $"Supabase RPC {rpcName} thất bại.",
                status,
                details: new { traceId, upstreamStatus = (int)response.StatusCode, response = content });
        }

        using var document = JsonDocument.Parse(content);
        return document.RootElement.Clone();
    }

    private static bool IsAfterCursor(
        long cloudVersion,
        DateTimeOffset updatedAt,
        string entityId,
        CloudPullCursorValue cursor)
    {
        if (cloudVersion != cursor.CloudVersion)
            return cloudVersion > cursor.CloudVersion;
        if (!cursor.UpdatedAtUtc.HasValue)
            return true;
        var updatedComparison = updatedAt.CompareTo(cursor.UpdatedAtUtc.Value);
        if (updatedComparison != 0)
            return updatedComparison > 0;
        return string.CompareOrdinal(entityId, cursor.EntityId ?? string.Empty) > 0;
    }

    private static CloudParticipantMutationResult ParseParticipantMutation(JsonElement result) =>
        new(
            result.GetProperty("participantId").GetGuid(),
            result.GetProperty("sessionId").GetGuid(),
            Enum.Parse<ParticipantStatus>(result.GetProperty("status").GetString()!, true),
            OptionalDate(result, "approvedAt"),
            result.GetProperty("extraTimeMinutes").GetInt32(),
            result.GetProperty("resubmitAllowed").GetBoolean(),
            OptionalString(result, "resubmitReason"),
            result.GetProperty("cloudVersion").GetInt64(),
            result.GetProperty("updatedAt").GetDateTimeOffset(),
            OptionalDate(result, "effectiveDeadline"),
            OptionalGuid(result, "attemptId"),
            OptionalString(result, "attemptStatus"),
            OptionalDate(result, "attemptDeadline"),
            OptionalInt64(result, "attemptRevision"),
            OptionalDate(result, "serverNowUtc"),
            OptionalInt64(result, "revision"),
            OptionalGuid(result, "requestId"));

    private static CloudEnrollmentMutationResult ParseEnrollmentMutation(JsonElement result) =>
        new(
            result.GetProperty("enrollmentRequestId").GetGuid(),
            result.GetProperty("classId").GetGuid(),
            result.GetProperty("status").GetString()!,
            OptionalDate(result, "decidedAt"),
            result.GetProperty("cloudVersion").GetInt64(),
            result.GetProperty("updatedAt").GetDateTimeOffset());

    private static CloudQuizGradeMutationResult ParseQuizGradeMutation(JsonElement result) =>
        new(
            result.GetProperty("attemptId").GetGuid(),
            result.GetProperty("sessionId").GetGuid(),
            result.GetProperty("participantId").GetGuid(),
            OptionalDecimal(result, "autoScore"),
            OptionalDecimal(result, "score"),
            result.GetProperty("maxScore").GetDecimal(),
            Enum.Parse<GradingStatus>(
                result.GetProperty("gradingStatus").GetString()!,
                true),
            OptionalString(result, "generalComment"),
            OptionalGuid(result, "graderId"),
            OptionalDate(result, "gradedAt"),
            OptionalDate(result, "returnedAt"),
            result.GetProperty("cloudVersion").GetInt64(),
            result.GetProperty("updatedAt").GetDateTimeOffset());

    private static CloudEssayGradeResult ParseEssayGrade(JsonElement result) =>
        new(
            OptionalGuid(result, "gradeId"),
            result.GetProperty("submissionId").GetGuid(),
            result.GetProperty("sessionId").GetGuid(),
            result.GetProperty("participantId").GetGuid(),
            OptionalDecimal(result, "score"),
            result.GetProperty("maxScore").GetDecimal(),
            Enum.Parse<GradingStatus>(result.GetProperty("status").GetString()!, true),
            OptionalString(result, "generalComment"),
            OptionalGuid(result, "graderId"),
            OptionalDate(result, "gradedAt"),
            OptionalDate(result, "returnedAt"),
            result.GetProperty("revision").GetInt64(),
            result.GetProperty("cloudVersion").GetInt64(),
            result.GetProperty("updatedAt").GetDateTimeOffset(),
            result.GetProperty("rubricScores").EnumerateArray().Select(item => new RubricScoreDto(
                item.GetProperty("criterionKey").GetString()!,
                item.GetProperty("title").GetString()!,
                item.GetProperty("score").GetDecimal(),
                item.GetProperty("maxScore").GetDecimal(),
                OptionalString(item, "comment"),
                item.GetProperty("order").GetInt32())).ToList(),
            result.GetProperty("attachments").EnumerateArray().Select(item => new CloudGradeAttachmentResult(
                item.GetProperty("id").GetGuid(),
                item.GetProperty("name").GetString()!,
                item.GetProperty("sizeBytes").GetInt64(),
                item.GetProperty("sha256").GetString()!,
                OptionalString(item, "mimeType") ?? "application/octet-stream")).ToList());

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
        ? value.GetString()
        : null;

    private static Guid? OptionalGuid(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
        ? value.GetGuid()
        : null;

    private static long? OptionalInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
        ? value.GetInt64()
        : null;

    private static decimal? OptionalDecimal(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
        ? value.GetDecimal()
        : null;

    private static DateTimeOffset? OptionalDate(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.GetDateTimeOffset()
            : null;

    public async Task<CloudLoginResult> LoginAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        EnsurePublishableConfigured();

        using var request = CreateProjectRequest(
            HttpMethod.Post,
            "/auth/v1/token?grant_type=password",
            CloudCredential.Publishable,
            includeUserToken: false);
        request.Content = JsonContent.Create(new { email, password });

        using var response = await httpClient.SendAsync(
            request,
            cancellationToken);
        var content = await response.Content.ReadAsStringAsync(
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(
                ErrorCodes.Unauthorized,
                "Đăng nhập Supabase thất bại.",
                401,
                details: content);
        }

        return await SaveSessionResponseAsync(
            content,
            email,
            cancellationToken);
    }

    public async Task<CloudLoginResult?> RefreshSessionAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = sessionState.Snapshot;
        if (snapshot is null)
            return null;

        if (snapshot.ExpiresAtUtc
            > DateTimeOffset.UtcNow.AddSeconds(
                cloudOptions.AuthRefreshSkewSeconds))
        {
            return ToLoginResult(snapshot);
        }

        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            snapshot = sessionState.Snapshot;
            if (snapshot is null)
                return null;

            if (snapshot.ExpiresAtUtc
                > DateTimeOffset.UtcNow.AddSeconds(
                    cloudOptions.AuthRefreshSkewSeconds))
            {
                return ToLoginResult(snapshot);
            }

            using var request = CreateProjectRequest(
                HttpMethod.Post,
                "/auth/v1/token?grant_type=refresh_token",
                CloudCredential.Publishable,
                includeUserToken: false);
            request.Content = JsonContent.Create(new
            {
                refresh_token = snapshot.RefreshToken
            });

            using var response = await httpClient.SendAsync(
                request,
                cancellationToken);
            var content = await response.Content.ReadAsStringAsync(
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                sessionState.Clear();
                throw new ApiException(
                    ErrorCodes.Unauthorized,
                    "Phiên Supabase đã hết hạn; cần đăng nhập lại.",
                    401,
                    details: content);
            }

            return await SaveSessionResponseAsync(
                content,
                snapshot.Email,
                cancellationToken);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        var snapshot = sessionState.Snapshot;
        try
        {
            if (snapshot is not null)
            {
                using var request = CreateProjectRequest(
                    HttpMethod.Post,
                    "/auth/v1/logout?scope=local",
                    CloudCredential.Publishable,
                    snapshot.AccessToken,
                    includeUserToken: false);
                using var response = await httpClient.SendAsync(
                    request,
                    cancellationToken);
            }
        }
        finally
        {
            sessionState.Clear();
        }
    }

    public async Task<IReadOnlyList<CloudBackupDescriptor>> ListBackupsAsync(
        CancellationToken cancellationToken)
    {
        EnsureCanSynchronize();
        var organizationId = GetRequiredOrganizationId();
        var path =
            "/rest/v1/backups"
            + $"?organization_id=eq.{Uri.EscapeDataString(organizationId.ToString())}"
            + "&select=id,file_name,size_bytes,sha256,schema_version,encrypted,status,cloud_object_path,created_at,updated_at"
            + "&cloud_object_path=not.is.null&order=created_at.desc";

        using var request = await CreateSyncRequestAsync(
            HttpMethod.Get,
            path,
            cancellationToken);
        using var response = await httpClient.SendAsync(
            request,
            cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                $"Không đọc được danh mục backup cloud: {json}",
                502);
        }

        using var document = JsonDocument.Parse(json);
        var result = new List<CloudBackupDescriptor>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var objectPath = element.GetProperty("cloud_object_path").GetString();
            if (string.IsNullOrWhiteSpace(objectPath))
                continue;

            result.Add(new CloudBackupDescriptor(
                element.GetProperty("id").GetGuid(),
                element.GetProperty("file_name").GetString() ?? "backup.zip",
                element.GetProperty("size_bytes").GetInt64(),
                element.GetProperty("sha256").GetString() ?? string.Empty,
                element.GetProperty("schema_version").GetString() ?? "1",
                element.GetProperty("encrypted").GetBoolean(),
                element.GetProperty("status").GetString() ?? "Ready",
                objectPath,
                element.GetProperty("created_at").GetDateTimeOffset(),
                element.GetProperty("updated_at").GetDateTimeOffset()));
        }

        return result;
    }

    public async Task DownloadObjectAsync(
        string cloudObjectPath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporaryPath = destinationPath + $".download.{Guid.NewGuid():N}";
        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await DownloadObjectToAsync(
                    cloudObjectPath,
                    destination,
                    cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task DownloadObjectToAsync(
        string cloudObjectPath,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));

        EnsureCanSynchronize();
        var target = ParseCloudObjectPath(cloudObjectPath);
        var encodedPath = EncodeObjectPath(target.ObjectPath);
        using var request = await CreateSyncRequestAsync(
            HttpMethod.Get,
            $"/storage/v1/object/authenticated/{Uri.EscapeDataString(target.Bucket)}/{encodedPath}",
            cancellationToken);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(
            response,
            "Supabase Storage download",
            cancellationToken);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private async Task<CloudLoginResult> SaveSessionResponseAsync(
        string content,
        string email,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var accessToken = root.GetProperty("access_token").GetString()
            ?? throw new ApiException(
                ErrorCodes.Unauthorized,
                "Supabase không trả access token.",
                401);
        var refreshToken = root.GetProperty("refresh_token").GetString()
            ?? throw new ApiException(
                ErrorCodes.Unauthorized,
                "Supabase không trả refresh token.",
                401);
        var expiresIn = root.TryGetProperty("expires_in", out var expires)
            ? expires.GetInt32()
            : 3600;
        var userId = root.TryGetProperty("user", out var userElement)
            && userElement.TryGetProperty("id", out var userIdElement)
                ? userIdElement.GetString()
                : sessionState.Snapshot?.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ApiException(
                ErrorCodes.Unauthorized,
                "Supabase không trả user id.",
                401);
        }

        var profile = await ReadProfileAsync(
            userId,
            accessToken,
            cancellationToken);
        var configuredOrganization = GetRequiredOrganizationId();
        if (profile.OrganizationId is null
            || profile.OrganizationId.Value != configuredOrganization)
        {
            throw new ApiException(
                ErrorCodes.Unauthorized,
                "Tài khoản không thuộc organization đã cấu hình trên máy giáo viên.",
                403);
        }

        if (!string.Equals(profile.Role, "Admin", StringComparison.Ordinal)
            && !string.Equals(profile.Role, "Teacher", StringComparison.Ordinal))
        {
            throw new ApiException(
                ErrorCodes.Forbidden,
                "Tài khoản Supabase phải có vai trò Admin hoặc Teacher.",
                403);
        }

        var snapshot = new CloudSessionSnapshot(
            accessToken,
            refreshToken,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            userId,
            email,
            profile.OrganizationId?.ToString(),
            profile.Role);

        sessionState.Set(
            snapshot,
            cloudOptions.PersistUserSession);

        return ToLoginResult(snapshot);
    }

    private async Task<(Guid? OrganizationId, string? Role)> ReadProfileAsync(
        string userId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateProjectRequest(
            HttpMethod.Get,
            $"/rest/v1/profiles?id=eq.{Uri.EscapeDataString(userId)}&select=organization_id,role&limit=1",
            CloudCredential.Publishable,
            accessToken,
            includeUserToken: false);
        using var response = await httpClient.SendAsync(
            request,
            cancellationToken);
        var content = await response.Content.ReadAsStringAsync(
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(
                ErrorCodes.Unauthorized,
                "Không đọc được profile Supabase.",
                403,
                details: content);
        }

        using var document = JsonDocument.Parse(content);
        if (document.RootElement.GetArrayLength() == 0)
        {
            throw new ApiException(
                ErrorCodes.Unauthorized,
                "Tài khoản Supabase chưa được cấp profile ExamTransfer.",
                403);
        }

        var profile = document.RootElement[0];
        Guid? organizationId = null;
        if (profile.TryGetProperty("organization_id", out var org)
            && org.ValueKind == JsonValueKind.String
            && Guid.TryParse(org.GetString(), out var parsed))
        {
            organizationId = parsed;
        }

        var role = profile.TryGetProperty("role", out var roleElement)
            ? roleElement.GetString()
            : null;
        return (organizationId, role);
    }

    private async Task DeleteMetadataAsync(
        string table,
        string entityId,
        CancellationToken cancellationToken)
    {
        using var request = await CreateSyncRequestAsync(
            HttpMethod.Delete,
            $"/rest/v1/{table}?id=eq.{Uri.EscapeDataString(entityId)}",
            cancellationToken);
        request.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
        using var response = await httpClient.SendAsync(
            request,
            cancellationToken);
        await EnsureSuccessAsync(
            response,
            "Supabase metadata delete",
            cancellationToken);
    }

    private async Task DeleteObjectIfPresentAsync(
        string entityType,
        string entityId,
        string payloadJson,
        string? queuedCloudObjectPath,
        CancellationToken cancellationToken)
    {
        JsonObject? payload = null;
        try
        {
            payload = JsonNode.Parse(payloadJson)?.AsObject();
        }
        catch (JsonException)
        {
            // Older queue records may not contain a valid JSON payload.
        }

        var explicitPath = queuedCloudObjectPath
            ?? payload?["cloud_object_path"]?.GetValue<string>();
        StorageTarget? target = null;

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            target = ParseCloudObjectPath(explicitPath);
        }
        else
        {
            var fileName = payload?["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(fileName)
                && IsFileBackedEntity(entityType))
            {
                target = ResolveStorageTarget(
                    entityType,
                    entityId,
                    fileName);
            }
        }

        if (target is null)
            return;

        using var request = await CreateSyncRequestAsync(
            HttpMethod.Delete,
            $"/storage/v1/object/{Uri.EscapeDataString(target.Bucket)}/{EncodeObjectPath(target.ObjectPath)}",
            cancellationToken);
        using var response = await httpClient.SendAsync(
            request,
            cancellationToken);
        if (response.IsSuccessStatusCode
            || response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(
            response,
            "Supabase Storage delete",
            cancellationToken);
    }

    private async Task<CloudPushResult> UploadObjectAsync(
        SyncQueueItem item,
        Func<CancellationToken, Task>? checkpoint,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(item.FilePath!);
        if (!File.Exists(fullPath))
        {
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                "File cần đồng bộ cloud không còn tồn tại.",
                409,
                details: new
                {
                    item.EntityType,
                    item.EntityId,
                    item.FilePath
                });
        }

        var target = ResolveStorageTarget(
            item.EntityType,
            item.EntityId,
            Path.GetFileName(fullPath));
        var length = new FileInfo(fullPath).Length;

        if (cloudOptions.UseResumableUploads
            && length > cloudOptions.StandardUploadThresholdBytes)
        {
            return await UploadObjectResumableAsync(
                item,
                target,
                fullPath,
                checkpoint,
                cancellationToken);
        }

        return await UploadObjectStandardAsync(
            target,
            fullPath,
            cancellationToken);
    }

    private async Task<CloudPushResult> UploadObjectStandardAsync(
        StorageTarget target,
        string fullPath,
        CancellationToken cancellationToken)
    {
        using var request = await CreateSyncRequestAsync(
            HttpMethod.Post,
            $"/storage/v1/object/{Uri.EscapeDataString(target.Bucket)}/{EncodeObjectPath(target.ObjectPath)}",
            cancellationToken);
        request.Headers.TryAddWithoutValidation("x-upsert", "true");

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            useAsync: true);
        var content = new StreamContent(stream);
        content.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = stream.Length;
        request.Content = content;

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(
            response,
            "Supabase Storage standard upload",
            cancellationToken);

        return new CloudPushResult(
            false,
            $"{target.Bucket}/{target.ObjectPath}",
            "standard",
            stream.Length);
    }

    private async Task<CloudPushResult> UploadObjectResumableAsync(
        SyncQueueItem item,
        StorageTarget target,
        string fullPath,
        Func<CancellationToken, Task>? checkpoint,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(fullPath);
        var uploadUrl = item.UploadUrl;
        long offset = item.UploadOffset;

        if (!string.IsNullOrWhiteSpace(uploadUrl))
        {
            var resumedOffset = await TryGetTusOffsetAsync(
                uploadUrl,
                cancellationToken);
            if (resumedOffset is null)
            {
                uploadUrl = null;
                offset = 0;
                item.UploadUrl = null;
                item.UploadOffset = 0;
            }
            else
            {
                offset = resumedOffset.Value;
                item.UploadOffset = offset;
            }
        }

        if (string.IsNullOrWhiteSpace(uploadUrl))
        {
            uploadUrl = await CreateTusUploadAsync(
                target,
                fileInfo,
                cancellationToken);
            item.UploadUrl = uploadUrl;
            item.UploadOffset = 0;
            offset = 0;
            if (checkpoint is not null)
                await checkpoint(cancellationToken);
        }

        var chunkSize = 6 * 1024 * 1024;
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            chunkSize,
            useAsync: true);
        stream.Position = Math.Clamp(offset, 0, stream.Length);
        var buffer = new byte[chunkSize];

        while (offset < stream.Length)
        {
            var remaining = stream.Length - offset;
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = 0;
            while (read < requested)
            {
                var current = await stream.ReadAsync(
                    buffer.AsMemory(read, requested - read),
                    cancellationToken);
                if (current == 0)
                    break;
                read += current;
            }

            if (read == 0)
                break;

            using var request = new HttpRequestMessage(
                HttpMethod.Patch,
                uploadUrl);
            await ApplySyncHeadersAsync(
                request,
                cancellationToken);
            request.Headers.TryAddWithoutValidation(
                "Tus-Resumable",
                TusVersion);
            request.Headers.TryAddWithoutValidation(
                "Upload-Offset",
                offset.ToString());
            request.Content = new ByteArrayContent(buffer, 0, read);
            request.Content.Headers.ContentType =
                new MediaTypeHeaderValue(
                    "application/offset+octet-stream");

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            await EnsureSuccessAsync(
                response,
                "Supabase Storage TUS PATCH",
                cancellationToken);

            offset = response.Headers.TryGetValues(
                    "Upload-Offset",
                    out var values)
                && long.TryParse(values.FirstOrDefault(), out var serverOffset)
                    ? serverOffset
                    : offset + read;
            item.UploadOffset = offset;
            item.LastAttemptAtUtc = DateTimeOffset.UtcNow;
            if (checkpoint is not null)
                await checkpoint(cancellationToken);
        }

        if (offset != fileInfo.Length)
        {
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                "TUS upload chưa hoàn tất đủ số byte.",
                502,
                details: new
                {
                    expected = fileInfo.Length,
                    actual = offset
                });
        }

        return new CloudPushResult(
            false,
            $"{target.Bucket}/{target.ObjectPath}",
            "tus-resumable",
            offset);
    }

    private async Task<string> CreateTusUploadAsync(
        StorageTarget target,
        FileInfo fileInfo,
        CancellationToken cancellationToken)
    {
        var endpoint = GetStorageBaseUrl()
            + "/storage/v1/upload/resumable";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            endpoint);
        await ApplySyncHeadersAsync(
            request,
            cancellationToken);
        request.Headers.TryAddWithoutValidation("Tus-Resumable", TusVersion);
        request.Headers.TryAddWithoutValidation(
            "Upload-Length",
            fileInfo.Length.ToString());
        request.Headers.TryAddWithoutValidation("x-upsert", "true");
        request.Headers.TryAddWithoutValidation(
            "Upload-Metadata",
            BuildTusMetadata(target));
        request.Content = new ByteArrayContent(Array.Empty<byte>());

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(
            response,
            "Supabase Storage TUS create",
            cancellationToken);

        var location = response.Headers.Location?.ToString();
        if (string.IsNullOrWhiteSpace(location))
        {
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                "Supabase TUS không trả upload URL.",
                502);
        }

        return Uri.TryCreate(location, UriKind.Absolute, out var absolute)
            ? absolute.ToString()
            : new Uri(new Uri(endpoint), location).ToString();
    }

    private async Task<long?> TryGetTusOffsetAsync(
        string uploadUrl,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Head,
            uploadUrl);
        await ApplySyncHeadersAsync(
            request,
            cancellationToken);
        request.Headers.TryAddWithoutValidation("Tus-Resumable", TusVersion);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.NotFound
            or HttpStatusCode.Gone)
        {
            return null;
        }

        await EnsureSuccessAsync(
            response,
            "Supabase Storage TUS HEAD",
            cancellationToken);
        return response.Headers.TryGetValues(
                "Upload-Offset",
                out var values)
            && long.TryParse(values.FirstOrDefault(), out var offset)
                ? offset
                : 0;
    }

    private async Task PushMetadataOptimisticallyAsync(
        string table,
        string entityId,
        string payload,
        CancellationToken cancellationToken)
    {
        using var insert = await CreateSyncRequestAsync(
            HttpMethod.Post,
            $"/rest/v1/{table}?on_conflict=id",
            cancellationToken);
        insert.Headers.TryAddWithoutValidation(
            "Prefer",
            "resolution=ignore-duplicates,return=representation");
        insert.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var insertResponse = await httpClient.SendAsync(insert, cancellationToken);
        await EnsureSuccessAsync(insertResponse, "Supabase metadata insert", cancellationToken);
        var insertBody = await insertResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(insertBody) && insertBody != "[]")
            return;

        if (string.Equals(table, "audit_logs", StringComparison.OrdinalIgnoreCase))
            return;

        using var payloadDocument = JsonDocument.Parse(payload);
        if (!payloadDocument.RootElement.TryGetProperty("cloud_version", out var versionElement)
            || !versionElement.TryGetInt64(out var expectedVersion))
            throw new JsonException($"Local-owned payload for {table} has no cloud_version.");

        using var update = await CreateSyncRequestAsync(
            HttpMethod.Patch,
            $"/rest/v1/{table}?id=eq.{Uri.EscapeDataString(entityId)}&cloud_version=lt.{expectedVersion}",
            cancellationToken);
        update.Headers.TryAddWithoutValidation("Prefer", "return=representation");
        update.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var updateResponse = await httpClient.SendAsync(update, cancellationToken);
        await EnsureSuccessAsync(updateResponse, "Supabase optimistic metadata update", cancellationToken);
        var updateBody = await updateResponse.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(updateBody) || updateBody == "[]")
        {
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                $"Cloud conflict for local-owned {table}/{entityId}; a newer cloud_version already exists.",
                409);
        }
    }

    private string BuildPayload(
        string entityType,
        string payloadJson,
        string? cloudObjectPath)
    {
        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(payloadJson)?.AsObject()
                ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                "Payload đồng bộ cloud không phải JSON object hợp lệ.",
                500,
                details: ex.Message);
        }

        NormalizeJsonFields(payload);
        payload["organization_id"] =
            GetRequiredOrganizationId().ToString();
        if (CloudEntityOwnershipRegistry.GetAuthority(entityType)
            is CloudEntityAuthority.LocalOwned or CloudEntityAuthority.SourceModeDependent)
        {
            payload["cloud_version"] = ResolveLocalCloudVersion(payload);
        }
        if (!string.IsNullOrWhiteSpace(cloudObjectPath))
            payload["cloud_object_path"] = cloudObjectPath;
        if (payload.ContainsKey("sync_status"))
            payload["sync_status"] = SyncStatus.Synced.ToString();
        return payload.ToJsonString(JsonOptions);
    }

    private static long ResolveLocalCloudVersion(JsonObject payload)
    {
        if (payload["cloud_version"] is JsonValue existing
            && existing.TryGetValue<long>(out var explicitVersion)
            && explicitVersion > 0)
            return explicitVersion;
        foreach (var key in new[] { "updated_at", "updatedAt", "updatedAtUtc" })
        {
            if (payload[key] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && DateTimeOffset.TryParse(text, out var updatedAt))
                return Math.Max(1, updatedAt.UtcTicks);
        }
        return DateTimeOffset.UtcNow.UtcTicks;
    }

    private static void NormalizeJsonFields(JsonObject payload)
    {
        foreach (var key in new[]
                 {
                     "metadata_json",
                     "file_rule_json",
                     "settings_json",
                     "capability_json",
                     "policy_json",
                     "payload_json",
                     "before_json",
                     "after_json",
                     "snapshot_json",
                     "choice_ids"
                 })
        {
            if (payload[key] is not JsonValue value
                || !value.TryGetValue<string>(out var text)
                || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            try
            {
                payload[key] = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                // Preserve old scalar payloads for backward compatibility.
            }
        }
    }

    private async Task<HttpRequestMessage> CreateSyncRequestAsync(
        HttpMethod method,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var token = await GetSyncBearerTokenAsync(cancellationToken);
        return CreateProjectRequest(
            method,
            relativePath,
            UsesTrustedServer
                ? CloudCredential.Secret
                : CloudCredential.Publishable,
            token,
            includeUserToken: false);
    }

    private async Task ApplySyncHeadersAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await GetSyncBearerTokenAsync(cancellationToken);
        ApplyProjectHeaders(
            request,
            UsesTrustedServer
                ? CloudCredential.Secret
                : CloudCredential.Publishable,
            token);
    }

    private async Task<string?> GetSyncBearerTokenAsync(
        CancellationToken cancellationToken)
    {
        if (UsesTrustedServer)
            return null;

        var session = await RefreshSessionAsync(cancellationToken);
        if (session is null)
        {
            throw new ApiException(
                ErrorCodes.Unauthorized,
                "Cần đăng nhập Supabase trước khi đồng bộ cloud.",
                401);
        }

        return session.AccessToken;
    }

    private HttpRequestMessage CreateProjectRequest(
        HttpMethod method,
        string relativePath,
        CloudCredential credential,
        string? bearerToken = null,
        bool includeUserToken = true)
    {
        var request = new HttpRequestMessage(
            method,
            GetProjectBaseUrl() + relativePath);
        if (includeUserToken
            && credential == CloudCredential.Publishable
            && bearerToken is null)
        {
            bearerToken = sessionState.Snapshot?.AccessToken;
        }

        ApplyProjectHeaders(request, credential, bearerToken);

        if (!string.IsNullOrWhiteSpace(cloudOptions.Schema)
            && !string.Equals(
                cloudOptions.Schema,
                "public",
                StringComparison.OrdinalIgnoreCase)
            && relativePath.StartsWith(
                "/rest/v1/",
                StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation(
                "Accept-Profile",
                cloudOptions.Schema);
            request.Headers.TryAddWithoutValidation(
                "Content-Profile",
                cloudOptions.Schema);
        }

        return request;
    }

    private void ApplyProjectHeaders(
        HttpRequestMessage request,
        CloudCredential credential,
        string? bearerToken)
    {
        var key = credential == CloudCredential.Secret
            ? ResolveSecretKey()
            : cloudOptions.EffectivePublishableKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ApiException(
                ErrorCodes.CloudOffline,
                credential == CloudCredential.Secret
                    ? "Thiếu Supabase secret key trong biến môi trường trusted backend."
                    : "Thiếu Supabase publishable key.",
                503);
        }

        request.Headers.TryAddWithoutValidation("apikey", key);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    bearerToken);
        }
        else if (IsLegacyJwtKey(key))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    key);
        }
    }

    private List<string> GetConfigurationErrors()
    {
        var errors = new List<string>();
        if (!Enabled)
            return errors;

        if (!CloudAccessModes.IsValid(cloudOptions.AccessMode))
        {
            errors.Add("Cloud AccessMode phải là UserSession hoặc TrustedServer.");
        }

        if (!Uri.TryCreate(
                cloudOptions.SupabaseUrl,
                UriKind.Absolute,
                out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps
                && !uri.IsLoopback))
        {
            errors.Add("SupabaseUrl phải là URL HTTPS hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(
                cloudOptions.EffectivePublishableKey))
        {
            errors.Add("Thiếu Supabase publishable/anon key.");
        }

        if (UsesTrustedServer
            && string.IsNullOrWhiteSpace(ResolveSecretKey()))
        {
            errors.Add(
                "TrustedServer yêu cầu Supabase secret/service-role key trong biến môi trường.");
        }

        if (!Guid.TryParse(cloudOptions.OrganizationId, out _))
            errors.Add("OrganizationId phải là UUID hợp lệ.");

        foreach (var (name, bucket) in new[]
                 {
                     (nameof(cloudOptions.ExamBucket), cloudOptions.ExamBucket),
                     (nameof(cloudOptions.SubmissionBucket), cloudOptions.SubmissionBucket),
                     (nameof(cloudOptions.ExportBucket), cloudOptions.ExportBucket),
                     (nameof(cloudOptions.BackupBucket), cloudOptions.BackupBucket)
                 })
        {
            if (string.IsNullOrWhiteSpace(bucket))
                errors.Add($"{name} không được để trống.");
        }

        return errors;
    }

    private void EnsureCanSynchronize()
    {
        if (!Enabled)
        {
            throw new ApiException(
                ErrorCodes.CloudOffline,
                "Supabase chưa được bật.",
                503);
        }

        var errors = GetConfigurationErrors();
        if (errors.Count > 0)
        {
            throw new ApiException(
                ErrorCodes.CloudOffline,
                "Cấu hình Supabase chưa hoàn chỉnh.",
                503,
                details: errors);
        }

        if (!UsesTrustedServer && !HasUsableUserSession)
        {
            throw new ApiException(
                ErrorCodes.Unauthorized,
                "Cần đăng nhập lại bằng tài khoản Supabase thuộc OrganizationId đã cấu hình.",
                401);
        }
    }

    private void EnsurePublishableConfigured()
    {
        if (!Enabled
            || string.IsNullOrWhiteSpace(cloudOptions.SupabaseUrl)
            || string.IsNullOrWhiteSpace(
                cloudOptions.EffectivePublishableKey))
        {
            throw new ApiException(
                ErrorCodes.CloudOffline,
                "Thiếu URL hoặc publishable key của Supabase.",
                503);
        }
    }

    private Guid GetRequiredOrganizationId() =>
        Guid.TryParse(cloudOptions.OrganizationId, out var id)
            ? id
            : throw new ApiException(
                ErrorCodes.CloudOffline,
                "OrganizationId chưa được cấu hình hợp lệ.",
                503);

    private string ResolveSecretKey() =>
        cloudOptions.ResolveSecretKey()
        ?? string.Empty;

    private string GetProjectBaseUrl() =>
        cloudOptions.SupabaseUrl!.TrimEnd('/');

    private string GetStorageBaseUrl()
    {
        var uri = new Uri(GetProjectBaseUrl());
        if (uri.Host.EndsWith(
                ".supabase.co",
                StringComparison.OrdinalIgnoreCase))
        {
            var project = uri.Host[..^".supabase.co".Length];
            return $"{uri.Scheme}://{project}.storage.supabase.co";
        }

        return GetProjectBaseUrl();
    }

    private StorageTarget ResolveStorageTarget(
        string entityType,
        string entityId,
        string fileName)
    {
        var organization = SanitizeSegment(
            GetRequiredOrganizationId().ToString());
        var environment = SanitizeSegment(
            cloudOptions.Environment);
        var safeEntityId = SanitizeSegment(entityId);
        var safeFileName = SanitizeFileName(fileName);
        var prefix = $"{organization}/{environment}";

        return entityType.Trim().ToLowerInvariant() switch
        {
            "exam_file" or "exam_files" => new(
                cloudOptions.ExamBucket,
                $"{prefix}/exam-files/{safeEntityId}/{safeFileName}"),
            "quiz_import_source" or "quiz_import_sources" => new(
                cloudOptions.ExamBucket,
                $"{prefix}/quiz-sources/{safeEntityId}/source.bin"),
            "submission_file" or "submission_files" => new(
                cloudOptions.SubmissionBucket,
                $"{prefix}/submission-files/{safeEntityId}/{safeFileName}"),
            "graded_attachment" or "graded_attachments" => new(
                cloudOptions.SubmissionBucket,
                $"{prefix}/graded-attachments/{safeEntityId}/{safeFileName}"),
            "export_job" or "export_jobs" => new(
                cloudOptions.ExportBucket,
                $"{prefix}/exports/{safeEntityId}/{safeFileName}"),
            "backup" or "backups" => new(
                cloudOptions.BackupBucket,
                $"{prefix}/backups/{safeEntityId}/{safeFileName}"),
            _ => new(
                cloudOptions.ExportBucket,
                $"{prefix}/other/{SanitizeSegment(entityType)}/{safeEntityId}/{safeFileName}")
        };
    }

    private static string? MapTable(string entityType) =>
        entityType.Trim().ToLowerInvariant() switch
        {
            "class" or "classes" => "classes",
            "class_member" or "class_members" => "class_members",
            "public_class_assignment" or "public_class_assignments" => "public_class_assignments",
            "exam" or "exams" => "exams",
            "exam_file" or "exam_files" => "exam_files",
            "quiz_import_source" or "quiz_import_sources" => "quiz_import_sources",
            "session" or "exam_session" or "exam_sessions" => "exam_sessions",
            "participant" or "session_participant" or "session_participants" => "session_participants",
            "submission" or "submissions" => "submissions",
            "submission_file" or "submission_files" => "submission_files",
            "grade" or "grades" => "grades",
            "rubric_score" or "rubric_scores" => "rubric_scores",
            "graded_attachment" or "graded_attachments" => "graded_attachments",
            "control_policy" or "control_policies" => "control_policies",
            "violation" or "violations" => "violations",
            "export_job" or "export_jobs" => "export_jobs",
            "backup" or "backups" => "backups",
            "audit" or "audit_log" or "audit_logs" => "audit_logs",
            "quiz_question" or "quiz_questions" => "quiz_questions",
            "quiz_choice" or "quiz_choices" => "quiz_choices",
            "quiz_attempt" or "quiz_attempts" => "quiz_attempts",
            "quiz_answer" or "quiz_answers" => "quiz_answers",
            _ => null
        };

    private static bool IsFileBackedEntity(string entityType) =>
        entityType.Trim().ToLowerInvariant() is
            "exam_file" or "exam_files"
            or "quiz_import_source" or "quiz_import_sources"
            or "submission_file" or "submission_files"
            or "graded_attachment" or "graded_attachments"
            or "export_job" or "export_jobs"
            or "backup" or "backups";

    private static StorageTarget ParseCloudObjectPath(
        string cloudObjectPath)
    {
        var parts = cloudObjectPath.Split(
            '/',
            2,
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                "Cloud object path không hợp lệ.",
                500);
        }

        return new StorageTarget(parts[0], parts[1]);
    }

    private static string BuildTusMetadata(StorageTarget target)
    {
        static string Encode(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

        var contentType = Encode("application/octet-stream");
        var cacheControl = Encode("3600");
        return string.Join(
            ",",
            new[]
            {
                $"bucketName {Encode(target.Bucket)}",
                $"objectName {Encode(target.ObjectPath)}",
                $"contentType {contentType}",
                $"cacheControl {cacheControl}"
            });
    }

    private static string EncodeObjectPath(string objectPath) =>
        string.Join(
            "/",
            objectPath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));

    private static string DescribeKeyMode(
        string? publishable,
        string? secret)
    {
        if (publishable?.StartsWith(
                "sb_publishable_",
                StringComparison.Ordinal) == true
            || secret?.StartsWith(
                "sb_secret_",
                StringComparison.Ordinal) == true)
        {
            return "Supabase publishable/secret keys";
        }

        return IsLegacyJwtKey(publishable)
            || IsLegacyJwtKey(secret)
                ? "Legacy anon/service_role JWT keys"
                : "Unknown";
    }

    private static bool IsLegacyJwtKey(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && key.Count(c => c == '.') == 2;

    private static string SanitizeSegment(string value)
    {
        var sanitized = new string(value
            .Trim()
            .Select(c =>
                char.IsLetterOrDigit(c)
                || c is '-' or '_' or '.'
                    ? c
                    : '-')
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized)
            ? "default"
            : sanitized;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Select(c => invalid.Contains(c) ? '_' : c)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized)
            ? "file.bin"
            : sanitized;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var detail = await response.Content.ReadAsStringAsync(
            cancellationToken);
        if (IsActivePublicRoomCodeConflict(detail))
        {
            throw new ApiException(
                ErrorCodes.RoomCodeConflict,
                "Mã phòng PublicCloud đang hoạt động đã được sử dụng trong tổ chức.",
                409);
        }
        throw new ApiException(
            ErrorCodes.CloudUploadFailed,
            $"{operation} trả về {(int)response.StatusCode}: {detail}",
            502);
    }

    private static bool IsActivePublicRoomCodeConflict(string detail)
    {
        if (!detail.Contains(
                "ux_exam_sessions_active_public_room",
                StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var document = JsonDocument.Parse(detail);
            return document.RootElement.TryGetProperty("code", out var code)
                && string.Equals(code.GetString(), "23505", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static CloudLoginResult ToLoginResult(
        CloudSessionSnapshot snapshot) =>
        new(
            snapshot.AccessToken,
            snapshot.RefreshToken,
            snapshot.ExpiresAtUtc,
            snapshot.UserId,
            snapshot.Email,
            snapshot.OrganizationId,
            snapshot.Role);

    private enum CloudCredential
    {
        Publishable,
        Secret
    }

    private sealed record StorageTarget(
        string Bucket,
        string ObjectPath);
}
