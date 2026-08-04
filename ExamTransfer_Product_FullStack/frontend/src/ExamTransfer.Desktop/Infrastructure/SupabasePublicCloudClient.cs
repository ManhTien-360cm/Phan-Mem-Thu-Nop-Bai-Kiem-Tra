using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed record PublicCloudRuntimeOptions(
    Uri? ProjectUri,
    string? PublishableKey,
    string? ErrorCode,
    string Source,
    Guid? OrganizationId = null)
{
    public bool Configured => ErrorCode is null
        && ProjectUri is not null
        && !string.IsNullOrWhiteSpace(PublishableKey);
}

public interface IPublicCloudRuntimeOptionsProvider
{
    PublicCloudRuntimeOptions Get();
}

public sealed class PublicCloudRuntimeOptionsProvider(
    string? configPath = null,
    Func<string, string?>? environment = null) : IPublicCloudRuntimeOptionsProvider
{
    public const string ConfigFileName = "publiccloud.runtime.json";
    private readonly string path = configPath
        ?? Path.Combine(AppContext.BaseDirectory, ConfigFileName);
    private readonly Func<string, string?> getEnvironment =
        environment ?? Environment.GetEnvironmentVariable;

    public PublicCloudRuntimeOptions Get()
    {
        var environmentUrl = getEnvironment("EXAMTRANSFER_SUPABASE_URL");
        var environmentKey = getEnvironment("EXAMTRANSFER_SUPABASE_PUBLISHABLE_KEY");
        var environmentOrganizationId = getEnvironment("EXAMTRANSFER_ORGANIZATION_ID");
        if (!string.IsNullOrWhiteSpace(environmentUrl)
            || !string.IsNullOrWhiteSpace(environmentKey))
            return Validate(
                environmentUrl,
                environmentKey,
                "Environment",
                allowLoopbackHttp: true,
                organizationId: environmentOrganizationId);

        if (!File.Exists(path))
            return new(null, null, "PUBLICCLOUD_NOT_CONFIGURED", "Missing");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var url = root.TryGetProperty("supabaseUrl", out var urlElement)
                ? urlElement.GetString()
                : null;
            var key = root.TryGetProperty("publishableKey", out var keyElement)
                ? keyElement.GetString()
                : null;
            var organizationId = root.TryGetProperty("organizationId", out var organizationElement)
                ? organizationElement.GetString()
                : null;
            return Validate(
                url,
                key,
                "InstalledFile",
                allowLoopbackHttp: false,
                organizationId: organizationId);
        }
        catch (JsonException)
        {
            return new(null, null, "PUBLICCLOUD_NOT_CONFIGURED", "InvalidFile");
        }
        catch (IOException)
        {
            return new(null, null, "PUBLICCLOUD_NOT_CONFIGURED", "UnreadableFile");
        }
    }

    public static PublicCloudRuntimeOptions Validate(
        string? url,
        string? key,
        string source,
        bool allowLoopbackHttp = false,
        bool allowExplicitTestKey = false,
        string? organizationId = null)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
            return new(null, null, "PUBLICCLOUD_NOT_CONFIGURED", source);
        if (!Uri.TryCreate(url.Trim().TrimEnd('/'), UriKind.Absolute, out var projectUri)
            || (projectUri.Scheme != Uri.UriSchemeHttps
                && !(allowLoopbackHttp
                    && projectUri.Scheme == Uri.UriSchemeHttp
                    && projectUri.IsLoopback)))
            return new(null, null, "PUBLICCLOUD_INVALID_URL", source);

        var normalizedKey = key.Trim();
        if (!IsPublishableKey(normalizedKey, allowExplicitTestKey))
            return new(projectUri, null, "PUBLICCLOUD_INVALID_PUBLISHABLE_KEY", source);
        Guid? parsedOrganizationId = null;
        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            if (!Guid.TryParse(organizationId.Trim(), out var organizationGuid)
                || organizationGuid == Guid.Empty)
                return new(projectUri, normalizedKey, "PUBLICCLOUD_INVALID_ORGANIZATION_ID", source);
            parsedOrganizationId = organizationGuid;
        }
        return new(projectUri, normalizedKey, null, source, parsedOrganizationId);
    }

    private static bool IsPublishableKey(string key, bool allowExplicitTestKey)
    {
        if (key.StartsWith("sb_secret_", StringComparison.OrdinalIgnoreCase)
            || key.Contains("service_role", StringComparison.OrdinalIgnoreCase))
            return false;
        if (key.StartsWith("sb_publishable_", StringComparison.Ordinal))
            return key.Length > "sb_publishable_".Length;
        if (TryReadJwtRole(key, out var role))
            return string.Equals(role, "anon", StringComparison.Ordinal);
        return allowExplicitTestKey && key.Length >= 8;
    }

    private static bool TryReadJwtRole(string value, out string? role)
    {
        role = null;
        var segments = value.Split('.');
        if (segments.Length != 3)
            return false;
        try
        {
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            role = document.RootElement.TryGetProperty("role", out var roleElement)
                ? roleElement.GetString()
                : null;
            return !string.IsNullOrWhiteSpace(role);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }
}

public sealed class FixedPublicCloudRuntimeOptionsProvider(
    PublicCloudRuntimeOptions options) : IPublicCloudRuntimeOptionsProvider
{
    public PublicCloudRuntimeOptions Get() => options;
}

public interface ISupabaseAccessTokenProvider
{
    Task<string> GetValidAccessTokenAsync(
        bool forceRefresh,
        CancellationToken cancellationToken);
}

public sealed class PublicCloudApiException(
    string code,
    string message,
    HttpStatusCode statusCode)
    : HttpRequestException(message, null, statusCode)
{
    public string Code { get; } = code;
}

public sealed record SupabaseAuthenticatedAccount(
    CurrentAccountDto Account,
    string AccessToken,
    string? RefreshToken);

public sealed class SupabaseAuthSessionChangedEventArgs(
    string accessToken,
    string? refreshToken,
    DateTimeOffset expiresAtUtc) : EventArgs
{
    public string AccessToken { get; } = accessToken;
    public string? RefreshToken { get; } = refreshToken;
    public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
}

public sealed class SupabasePublicCloudClient : ISupabaseAccessTokenProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions NotificationJson = CreateNotificationJson();
    private readonly HttpClient http;
    private readonly IServerClock serverClock;
    private readonly IPublicCloudRuntimeOptionsProvider optionsProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private CancellationTokenSource authLifetime = new();
    private string? accessToken;
    private string? refreshToken;
    private string? providerUserId;
    private string? authenticatedEmail;
    private DateTimeOffset expiresAtUtc;

    public SupabasePublicCloudClient(
        HttpClient? http = null,
        IServerClock? serverClock = null,
        string? supabaseUrl = null,
        string? publishableKey = null,
        IPublicCloudRuntimeOptionsProvider? optionsProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        this.serverClock = serverClock ?? new ServerClock();
        this.optionsProvider = optionsProvider
            ?? (supabaseUrl is not null || publishableKey is not null
                ? new FixedPublicCloudRuntimeOptionsProvider(
                    PublicCloudRuntimeOptionsProvider.Validate(
                        supabaseUrl,
                        publishableKey,
                        "Explicit",
                        allowLoopbackHttp: true,
                        allowExplicitTestKey: true))
                : new PublicCloudRuntimeOptionsProvider());
        this.delay = delay ?? Task.Delay;
    }

    public PublicCloudRuntimeOptions RuntimeOptions => optionsProvider.Get();
    public bool Configured => RuntimeOptions.Configured;
    public bool Authenticated => !string.IsNullOrWhiteSpace(accessToken);
    public string? AccessToken => accessToken;
    public DateTimeOffset ExpiresAtUtc => expiresAtUtc;
    public string? ConfigurationErrorCode => RuntimeOptions.ErrorCode;
    public event EventHandler<SupabaseAuthSessionChangedEventArgs>? SessionChanged;

    public async Task LoginAsync(string account, string password, CancellationToken cancellationToken)
    {
        Logout();
        EnsureConfigured();
        var domain = Environment.GetEnvironmentVariable("EXAMTRANSFER_STUDENT_EMAIL_DOMAIN")
            ?? "students.examtransfer.local";
        var email = account.Contains('@') ? account.Trim() : $"{account.Trim()}@{domain.Trim().TrimStart('@')}";
        using var request = ProjectRequest(HttpMethod.Post, "/auth/v1/token?grant_type=password", false);
        request.Content = JsonContent.Create(new { email = email.ToLowerInvariant(), password });
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Supabase Auth", cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            authLifetime.Cancel();
            authLifetime = new();
            ApplyAuthResponse(document.RootElement);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public async Task<PublicEnrollmentState> RequestEnrollmentAsync(string enrollmentCode, string studentCode, CancellationToken cancellationToken)
    {
        var requestId = await RpcAsync<Guid>("request_public_class_enrollment", new
        {
            p_enrollment_code = enrollmentCode,
            p_student_code = studentCode
        }, cancellationToken);
        using var request = ProjectRequest(HttpMethod.Get,
            $"/rest/v1/class_enrollment_requests?select=id,status&id=eq.{requestId}&limit=1");
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "PublicCloud enrollment status", cancellationToken);
        var rows = JsonSerializer.Deserialize<List<EnrollmentRow>>(
            await response.Content.ReadAsStringAsync(cancellationToken), Json) ?? [];
        if (rows.Count != 1) throw new InvalidDataException("PublicCloud enrollment status was not found.");
        return new PublicEnrollmentState(rows[0].Id, rows[0].Status);
    }

    public async Task<PublicCloudJoinResult> JoinByRoomCodeAsync(
        string roomCode,
        string deviceId,
        string machineName,
        string appVersion,
        CancellationToken cancellationToken,
        Action? projectionDelayed = null)
    {
        await EnsureSchemaCompatibleAsync(cancellationToken);
        OpenPublicJoinRpcResult result;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                result = await RpcAsync<OpenPublicJoinRpcResult>(
                    "join_open_public_session_by_room_code",
                    new
                    {
                        p_room_code = roomCode,
                        p_device_id = deviceId,
                        p_machine_name = machineName,
                        p_app_version = appVersion,
                        p_capability_json = new { platform = Environment.OSVersion.Platform.ToString() }
                    },
                    cancellationToken);
                break;
            }
            catch (PublicCloudApiException ex) when (
                ex.Code == "OPEN_PUBLIC_SESSION_NOT_FOUND"
                && attempt < 3)
            {
                projectionDelayed?.Invoke();
                await delay(
                    TimeSpan.FromMilliseconds(300 * (attempt + 1)),
                    cancellationToken);
            }
        }
        if (!Enum.TryParse<ParticipantStatus>(result.ParticipantStatus, true, out var participantStatus)
            || !Enum.TryParse<SessionStatus>(result.SessionStatus, true, out var sessionStatus)
            || !Enum.TryParse<ExamDeliveryType>(result.DeliveryType, true, out var deliveryType)
            || !Enum.TryParse<SupervisionMode>(result.SupervisionMode, true, out var supervisionMode)
            || !Enum.TryParse<QuizResultPolicy>(result.QuizResultPolicy, true, out var resultPolicy))
            throw new InvalidDataException("PublicCloud open-session join returned invalid typed metadata.");
        return new(
            result.SessionId,
            result.ExamId,
            result.ParticipantId,
            participantStatus,
            sessionStatus,
            result.RoomCode,
            result.ExamTitle,
            result.Subject,
            result.DurationMinutes,
            deliveryType,
            supervisionMode,
            resultPolicy,
            result.PlannedStartUtc,
            result.Capacity,
            result.CurrentParticipantCount,
            accessToken!);
    }

    public async Task<ParticipantStatus> GetParticipantStatusAsync(Guid participantId, CancellationToken cancellationToken)
    {
        await EnsureFreshSessionAsync(cancellationToken);
        using var request = ProjectRequest(HttpMethod.Get,
            $"/rest/v1/session_participants?select=status&id=eq.{participantId}&limit=1");
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "PublicCloud participant snapshot", cancellationToken);
        var rows = JsonSerializer.Deserialize<List<ParticipantStatusRow>>(
            await response.Content.ReadAsStringAsync(cancellationToken), Json) ?? [];
        if (rows.Count != 1 || !Enum.TryParse<ParticipantStatus>(rows[0].Status, true, out var status))
            throw new InvalidDataException("PublicCloud participant snapshot is invalid.");
        return status;
    }

    public async Task<PublicExamFileUrl> GetExamFileUrlAsync(Guid sessionId, Guid fileId, CancellationToken cancellationToken)
    {
        await EnsureFreshSessionAsync(cancellationToken);
        using var request = ProjectRequest(HttpMethod.Post, "/functions/v1/get-public-exam-file-url");
        request.Content = JsonContent.Create(new { sessionId, fileId });
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "PublicCloud signed exam URL", cancellationToken);
        return (await response.Content.ReadFromJsonAsync<PublicExamFileUrl>(Json, cancellationToken))
            ?? throw new InvalidDataException("Signed URL response is empty.");
    }

    public async Task<IReadOnlyList<FileDescriptorDto>> ListExamFilesAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var rows = await RpcAsync<List<ExamFileRow>>(
            "get_public_exam_manifest",
            new { p_session_id = sessionId },
            cancellationToken);
        return rows.Select(x => new FileDescriptorDto(x.Id, x.Name, x.SizeBytes, x.Sha256,
            x.MimeType ?? "application/octet-stream")).ToList();
    }

    public async Task<PublicSubmissionPlan> InitSubmissionAsync(
        Guid sessionId,
        string idempotencyKey,
        string fileName,
        long sizeBytes,
        string sha256,
        CancellationToken cancellationToken)
    {
        var submissionId = await RpcAsync<Guid>("init_public_submission", new
        {
            p_session_id = sessionId,
            p_idempotency_key = idempotencyKey,
            p_file_name = fileName,
            p_size_bytes = sizeBytes,
            p_sha256 = sha256
        }, cancellationToken);
        using var request = ProjectRequest(HttpMethod.Get,
            $"/rest/v1/submission_files?select=id,cloud_object_path&submission_id=eq.{submissionId}&source_mode=eq.PublicCloud&limit=2");
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "PublicCloud submission file plan", cancellationToken);
        var rows = JsonSerializer.Deserialize<List<SubmissionFilePlanRow>>(
            await response.Content.ReadAsStringAsync(cancellationToken), Json) ?? [];
        if (rows.Count != 1 || string.IsNullOrWhiteSpace(rows[0].CloudObjectPath))
            throw new InvalidDataException("PublicCloud did not return exactly one immutable archive plan.");
        return new PublicSubmissionPlan(submissionId, rows[0].Id, rows[0].CloudObjectPath);
    }

    public async Task UploadSubmissionArchiveAsync(PublicSubmissionPlan plan, string filePath, CancellationToken cancellationToken)
    {
        await EnsureFreshSessionAsync(cancellationToken);
        var encodedPath = plan.CloudObjectPath.Split('/').Select(Uri.EscapeDataString);
        using var request = ProjectRequest(HttpMethod.Post,
            "/storage/v1/object/public-submission-archives/" + string.Join('/', encodedPath));
        request.Headers.TryAddWithoutValidation("x-upsert", "false");
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = stream.Length;
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        // A retry after an uncertain response may find the immutable object
        // already present. Verification below remains the source of truth.
        if (response.StatusCode != HttpStatusCode.Conflict)
            await EnsureSuccessAsync(response, "PublicCloud archive upload", cancellationToken);
    }

    public async Task<ReceiptDto> VerifyAndFinalizeSubmissionAsync(
        PublicSubmissionPlan plan,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await EnsureFreshSessionAsync(cancellationToken);
        using var request = ProjectRequest(HttpMethod.Post, "/functions/v1/verify-public-submission-archive");
        request.Content = JsonContent.Create(new { submissionId = plan.SubmissionId, idempotencyKey });
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "PublicCloud archive verification", cancellationToken);

        using var snapshotRequest = ProjectRequest(HttpMethod.Get,
            $"/rest/v1/submissions?select=id,receipt_code,receipt_signature,server_received_at,is_late,submission_files(id,name,size_bytes,sha256,mime_type)&id=eq.{plan.SubmissionId}&limit=1");
        using var snapshotResponse = await SendAsync(snapshotRequest, cancellationToken);
        await EnsureSuccessAsync(snapshotResponse, "PublicCloud receipt snapshot", cancellationToken);
        var rows = JsonSerializer.Deserialize<List<ReceiptRow>>(
            await snapshotResponse.Content.ReadAsStringAsync(cancellationToken), Json) ?? [];
        if (rows.Count != 1 || string.IsNullOrWhiteSpace(rows[0].ReceiptCode)
            || string.IsNullOrWhiteSpace(rows[0].ReceiptSignature) || rows[0].ServerReceivedAt is null)
            throw new InvalidDataException("PublicCloud finalize succeeded without a complete receipt snapshot.");
        var complete = rows[0];
        return new ReceiptDto(complete.Id, complete.ReceiptCode!, complete.ReceiptSignature!,
            complete.ServerReceivedAt!.Value, complete.IsLate,
            complete.SubmissionFiles.Select(x => new FileDescriptorDto(x.Id, x.Name, x.SizeBytes, x.Sha256,
                x.MimeType ?? "application/octet-stream")).ToList());
    }

    public async Task<QuizAttemptDto> StartQuizAttemptAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var attemptId = await RpcAsync<Guid>("start_public_quiz_attempt", new
        {
            p_session_id = sessionId,
            p_idempotency_key = $"start-{sessionId:N}"
        }, cancellationToken);
        var attempt = await GetQuizAttemptAsync(attemptId, cancellationToken);
        var timeline = await GetStudentTimelineAsync(sessionId, cancellationToken);
        return ApplyTimeline(attempt, timeline);
    }

    public async Task<SyncQuizAnswersResultDto> SaveQuizAnswersAsync(
        Guid sessionId,
        Guid attemptId,
        IReadOnlyList<QuizAnswerDto> answers,
        CancellationToken cancellationToken)
    {
        var accepted = new List<QuizAnswerDto>(answers.Count);
        foreach (var answer in answers.OrderBy(x => x.QuestionId))
        {
            var revision = await RpcAsync<long>("save_public_quiz_answers", new
            {
                p_attempt_id = attemptId,
                p_question_id = answer.QuestionId,
                p_choice_ids = answer.ChoiceIds,
                p_revision = answer.Revision,
                p_client_updated_at = answer.ClientUpdatedAtUtc
            }, cancellationToken);
            accepted.Add(answer with { Revision = revision });
        }
        var timeline = await GetStudentTimelineAsync(sessionId, cancellationToken);
        if (timeline.AttemptId != attemptId)
            throw new InvalidDataException("PublicCloud timeline does not match the active quiz attempt.");
        return new SyncQuizAnswersResultDto(attemptId, accepted, timeline.ServerNowUtc);
    }

    public async Task<QuizAttemptDto> FinalizeQuizAttemptAsync(
        Guid attemptId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var snapshot = await RpcAsync<PublicQuizAttemptSnapshot>("finalize_public_quiz_attempt", new
        {
            p_attempt_id = attemptId,
            p_idempotency_key = idempotencyKey
        }, cancellationToken);
        var attempt = ToQuizAttempt(snapshot);
        var timeline = await GetStudentTimelineAsync(attempt.SessionId, cancellationToken);
        return ApplyTimeline(attempt, timeline);
    }

    public async Task<PublicStudentTimeline> GetStudentTimelineAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var timeline = await RpcAsync<PublicStudentTimeline>(
            "get_public_student_timeline",
            new { p_session_id = sessionId },
            cancellationToken);
        if (timeline.SessionId != sessionId
            || timeline.ParticipantId == Guid.Empty
            || timeline.Revision <= 0
            || timeline.ServerNowUtc == default)
            throw new InvalidDataException("PublicCloud student timeline is invalid.");
        serverClock.Synchronize(timeline.ServerNowUtc);
        return timeline;
    }

    public async Task<IReadOnlyList<StudentNotificationEventDto>> GetStudentNotificationEventsAsync(
        Guid sessionId,
        long afterRevision,
        Guid? afterEventId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty || afterRevision < 0 || limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit), "PublicCloud notification cursor is invalid.");
        var rows = await RpcAsync<JsonElement>(
            "get_public_student_notification_events",
            new
            {
                p_session_id = sessionId,
                p_after_revision = afterRevision,
                p_after_event_id = afterEventId,
                p_limit = limit
            },
            cancellationToken);
        if (rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("PublicCloud notification catch-up is not an array.");
        var result = new List<StudentNotificationEventDto>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            var notification = row.Deserialize<StudentNotificationEventDto>(NotificationJson)
                ?? throw new InvalidDataException("PublicCloud notification payload is empty.");
            if (notification.SessionId != sessionId
                || StudentNotificationEventValidator.Validate(notification).Count != 0)
                throw new InvalidDataException("PublicCloud notification payload is invalid.");
            result.Add(notification);
        }
        return result;
    }

    public async Task<QuizAttemptDto> GetQuizAttemptAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var snapshot = await RpcAsync<PublicQuizAttemptSnapshot>(
            "get_public_quiz_attempt",
            new { p_attempt_id = attemptId },
            cancellationToken);
        return ToQuizAttempt(snapshot);
    }

    public async Task<StudentQuizReviewDto> GetQuizAttemptReviewAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        var snapshot = await RpcAsync<PublicQuizReviewSnapshot>(
            "get_public_quiz_attempt_review",
            new { p_attempt_id = attemptId },
            cancellationToken);
        if (snapshot.AttemptId != attemptId)
            throw new InvalidDataException("PublicCloud quiz review does not match the requested attempt.");

        var answers = snapshot.Answers.ToDictionary(
            answer => answer.QuestionId,
            answer => answer.ChoiceIds.ToHashSet());
        var questions = snapshot.Questions
            .OrderBy(question => question.SortOrder)
            .Select(question =>
            {
                answers.TryGetValue(question.Id, out var selected);
                selected ??= [];
                var choices = question.Choices
                    .OrderBy(choice => choice.SortOrder)
                    .Select(choice => new QuizChoiceReviewDto(
                        choice.Id,
                        choice.ChoiceText,
                        choice.SortOrder,
                        selected.Contains(choice.Id),
                        snapshot.CorrectAnswersVisible ? choice.Correct : null))
                    .ToList();
                decimal? earnedPoints = null;
                if (snapshot.CorrectAnswersVisible)
                {
                    var correct = choices
                        .Where(choice => choice.Correct == true)
                        .Select(choice => choice.Id)
                        .ToHashSet();
                    earnedPoints = selected.SetEquals(correct) ? question.Points : 0m;
                }
                return new QuizQuestionReviewDto(
                    question.Id,
                    question.QuestionText,
                    question.SortOrder,
                    question.Points,
                    earnedPoints,
                    choices);
            })
            .ToList();
        return new(
            snapshot.AttemptId,
            snapshot.ScoreVisible ? snapshot.Score : null,
            snapshot.MaxScore,
            snapshot.ScoreVisible,
            snapshot.CorrectAnswersVisible,
            snapshot.CorrectAnswersVisible ? snapshot.GeneralComment : null,
            questions);
    }

    public async Task<StudentResultPageDto> GetStudentResultsAsync(
        int pageSize,
        StudentResultCursorDto? cursor,
        CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > StudentResultPageValidator.MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        var payload = await RpcAsync<JsonElement>(
            "get_student_results",
            new
            {
                p_page_size = pageSize,
                p_cursor_returned_at = cursor?.ReturnedAtUtc,
                p_cursor_result_type = cursor?.ResultType.ToString(),
                p_cursor_result_id = cursor?.ResultId
            },
            cancellationToken);
        var page = payload.Deserialize<StudentResultPageDto>(NotificationJson)
            ?? throw new InvalidDataException("PublicCloud student result page is empty.");
        StudentResultPageValidator.EnsureValid(page);
        return page;
    }

    private static QuizAttemptDto ToQuizAttempt(PublicQuizAttemptSnapshot row) =>
        new(row.Id, row.SessionId, row.ParticipantId,
            Enum.Parse<QuizAttemptStatus>(row.Status, true), row.ExamVersion,
            row.StartedAtUtc, row.DeadlineUtc, row.FinalizedAtUtc,
            row.ScoreVisible ? row.Score : null, row.MaxScore,
            row.Questions.Select(q => new QuizQuestionDto(q.Id, q.QuestionText, q.SortOrder, q.Points, q.Multiple,
                q.Choices.Select(c => new QuizChoiceDto(c.Id, c.ChoiceText, c.SortOrder)).ToList())).ToList(),
            row.Answers.Select(a => new QuizAnswerDto(a.QuestionId, a.ChoiceIds, a.Revision, a.ClientUpdatedAtUtc)).ToList(),
            row.ScoreVisible,
            Enum.TryParse<QuizResultPolicy>(row.ResultPolicy, true, out var policy) ? policy : QuizResultPolicy.Hidden);

    private static QuizAttemptDto ApplyTimeline(
        QuizAttemptDto attempt,
        PublicStudentTimeline timeline)
    {
        if (timeline.SessionId != attempt.SessionId
            || timeline.ParticipantId != attempt.ParticipantId
            || timeline.AttemptId != attempt.Id
            || !timeline.AttemptDeadlineUtc.HasValue)
            throw new InvalidDataException("PublicCloud quiz snapshot and timeline do not match.");
        var status = Enum.TryParse<QuizAttemptStatus>(
            timeline.AttemptStatus,
            true,
            out var parsed)
            ? parsed
            : attempt.Status;
        return attempt with
        {
            Status = status,
            DeadlineUtc = timeline.AttemptDeadlineUtc.Value
        };
    }

    public async Task DownloadVerifiedAsync(PublicExamFileUrl file, string destinationPath, CancellationToken cancellationToken)
    {
        var partial = destinationPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (offset > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            File.Delete(partial);
            offset = 0;
        }
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append,
                         FileAccess.Write, FileShare.None, 128 * 1024, true))
            await input.CopyToAsync(output, cancellationToken);
        if (new FileInfo(partial).Length != file.SizeBytes)
            throw new InvalidDataException("Downloaded exam size does not match metadata.");
        await using var verify = File.OpenRead(partial);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken)).ToLowerInvariant();
        if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Downloaded exam SHA-256 does not match metadata.");
        File.Move(partial, destinationPath, true);
    }

    public async Task<T> RpcAsync<T>(string name, object payload, CancellationToken cancellationToken)
    {
        await EnsureFreshSessionAsync(cancellationToken);
        using var request = ProjectRequest(HttpMethod.Post, $"/rest/v1/rpc/{name}");
        request.Content = JsonContent.Create(payload, options: Json);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, $"PublicCloud RPC {name}", cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(content, Json)
            ?? throw new InvalidDataException($"RPC {name} returned an empty response.");
    }

    public async Task EnsureSchemaCompatibleAsync(CancellationToken cancellationToken)
    {
        var capabilities = await RpcAsync<CloudCapabilities>(
            "get_examtransfer_cloud_capabilities",
            new { },
            cancellationToken);
        if (capabilities.SchemaVersion < 27
            || capabilities.CriticalRpcs is null
            || !capabilities.CriticalRpcs.Contains(
                "get_public_student_notification_events",
                StringComparer.Ordinal)
            || !capabilities.CriticalRpcs.Contains(
                "send_public_teacher_message",
                StringComparer.Ordinal)
            || !capabilities.CriticalRpcs.Contains(
                "get_student_results",
                StringComparer.Ordinal)
            || !capabilities.CriticalRpcs.Contains(
                "set_public_submission_late_override",
                StringComparer.Ordinal))
            throw new PublicCloudApiException(
                "PUBLICCLOUD_SCHEMA_INCOMPATIBLE",
                "PublicCloud schema is incompatible with this ExamTransfer build.",
                HttpStatusCode.Conflict);
    }

    private static JsonSerializerOptions CreateNotificationJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public async Task<string> GetValidAccessTokenAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var tokenObservedBeforeGate = accessToken;
        var lifetime = authLifetime;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime.Token);
        await refreshGate.WaitAsync(linked.Token);
        try
        {
            if (forceRefresh
                && !string.Equals(
                    tokenObservedBeforeGate,
                    accessToken,
                    StringComparison.Ordinal)
                && expiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2)
                && !string.IsNullOrWhiteSpace(accessToken))
                return accessToken;
            if (!forceRefresh
                && expiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2)
                && !string.IsNullOrWhiteSpace(accessToken))
                return accessToken;
            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new PublicCloudApiException(
                    "PUBLICCLOUD_AUTH_EXPIRED",
                    "Phiên PublicCloud đã hết hạn; hãy đăng nhập lại.",
                    HttpStatusCode.Unauthorized);

            var tokenBeforeRefresh = refreshToken;
            using var request = ProjectRequest(
                HttpMethod.Post,
                "/auth/v1/token?grant_type=refresh_token",
                false);
            request.Content = JsonContent.Create(new { refresh_token = tokenBeforeRefresh });
            using var response = await SendAsync(request, linked.Token);
            await EnsureSuccessAsync(response, "Supabase refresh", linked.Token);
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(linked.Token));
            linked.Token.ThrowIfCancellationRequested();
            ApplyAuthResponse(document.RootElement);
            return accessToken
                ?? throw new PublicCloudApiException(
                    "PUBLICCLOUD_AUTH_INVALID",
                    "Supabase refresh did not return an access token.",
                    HttpStatusCode.Unauthorized);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public async Task<SupabaseAuthenticatedAccount> AuthenticateAccountAsync(
        string account,
        string password,
        string deviceId,
        CancellationToken cancellationToken)
    {
        await LoginAsync(account, password, cancellationToken);
        try
        {
            var current = await ReadAuthoritativeAccountAsync(deviceId, cancellationToken);
            return new(
                current,
                accessToken
                    ?? throw new PublicCloudApiException(
                        "PUBLICCLOUD_AUTH_INVALID",
                        "Supabase authentication returned no access token.",
                        HttpStatusCode.Unauthorized),
                refreshToken);
        }
        catch
        {
            Logout();
            throw;
        }
    }

    public async Task<SupabaseAuthenticatedAccount> ChangeOwnPasswordAsync(
        string account,
        string currentPassword,
        string newPassword,
        string confirmPassword,
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal)
            || string.Equals(currentPassword, newPassword, StringComparison.Ordinal)
            || newPassword.Length is < 8 or > 72
            || !newPassword.Any(char.IsUpper)
            || !newPassword.Any(char.IsLower)
            || !newPassword.Any(char.IsDigit)
            || !newPassword.Any(ch => !char.IsLetterOrDigit(ch)))
        {
            throw new PublicCloudApiException(
                "PASSWORD_POLICY_REJECTED",
                "Mật khẩu mới không hợp lệ hoặc xác nhận không khớp.",
                HttpStatusCode.UnprocessableEntity);
        }

        await LoginAsync(account, currentPassword, cancellationToken);
        using var update = ProjectRequest(HttpMethod.Put, "/auth/v1/user");
        update.Content = JsonContent.Create(new { password = newPassword });
        using var updateResponse = await SendAsync(update, cancellationToken);
        await EnsureSuccessAsync(
            updateResponse,
            "Supabase password change",
            cancellationToken);

        var completed = await RpcAsync<bool>(
            "complete_own_password_change",
            new { },
            cancellationToken);
        if (!completed)
        {
            Logout();
            throw new PublicCloudApiException(
                "PASSWORD_CHANGE_FAILED",
                "Supabase profile did not confirm the password change.",
                HttpStatusCode.ServiceUnavailable);
        }

        var current = await ReadAuthoritativeAccountAsync(
            deviceId,
            cancellationToken);
        return new(
            current,
            accessToken
                ?? throw new PublicCloudApiException(
                    "PUBLICCLOUD_AUTH_INVALID",
                    "Supabase password change returned no access token.",
                    HttpStatusCode.Unauthorized),
            refreshToken);
    }

    public bool TryRestoreAccessToken(
        string token,
        string expectedProviderUserId,
        DateTimeOffset expiresAt) =>
        TryRestoreSession(token, null, expectedProviderUserId, expiresAt);

    public bool TryRestoreSession(
        string token,
        string? storedRefreshToken,
        string expectedProviderUserId,
        DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(token)
            || string.IsNullOrWhiteSpace(expectedProviderUserId)
            || !TryReadJwtIdentity(token, out var subject, out var jwtExpiresAt)
            || !string.Equals(subject, expectedProviderUserId, StringComparison.OrdinalIgnoreCase)
            || ((expiresAt <= DateTimeOffset.UtcNow
                    || jwtExpiresAt <= DateTimeOffset.UtcNow)
                && string.IsNullOrWhiteSpace(storedRefreshToken)))
            return false;

        Logout();
        accessToken = token;
        refreshToken = string.IsNullOrWhiteSpace(storedRefreshToken)
            ? null
            : storedRefreshToken;
        providerUserId = subject;
        expiresAtUtc = expiresAt < jwtExpiresAt ? expiresAt : jwtExpiresAt;
        return true;
    }

    public async Task<SupabaseAuthenticatedAccount> RestoreAuthenticatedAccountAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        await EnsureFreshSessionAsync(cancellationToken);
        var current = await ReadAuthoritativeAccountAsync(deviceId, cancellationToken);
        return new(
            current,
            accessToken
                ?? throw new PublicCloudApiException(
                    "PUBLICCLOUD_AUTH_INVALID",
                    "Supabase session restoration returned no access token.",
                    HttpStatusCode.Unauthorized),
            refreshToken);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || !Configured)
        {
            Logout();
            return;
        }

        try
        {
            using var request = ProjectRequest(HttpMethod.Post, "/auth/v1/logout");
            using var response = await SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, "Supabase logout", cancellationToken);
        }
        finally
        {
            Logout();
        }
    }

    public void Logout()
    {
        authLifetime.Cancel();
        authLifetime = new();
        accessToken = null;
        refreshToken = null;
        providerUserId = null;
        authenticatedEmail = null;
        expiresAtUtc = default;
    }

    private Task EnsureFreshSessionAsync(CancellationToken cancellationToken) =>
        GetValidAccessTokenAsync(false, cancellationToken);

    private void ApplyAuthResponse(JsonElement root)
    {
        var nextAccessToken = root.GetProperty("access_token").GetString();
        var nextRefreshToken = root.TryGetProperty("refresh_token", out var refresh)
            ? refresh.GetString()
            : refreshToken;
        if (string.IsNullOrWhiteSpace(nextAccessToken))
            throw new InvalidDataException("Supabase authentication returned an empty access token.");
        accessToken = nextAccessToken;
        refreshToken = nextRefreshToken;
        if (root.TryGetProperty("user", out var user)
            && user.ValueKind == JsonValueKind.Object)
        {
            providerUserId = user.TryGetProperty("id", out var userId)
                && userId.ValueKind == JsonValueKind.String
                    ? userId.GetString()
                    : providerUserId;
            authenticatedEmail = user.TryGetProperty("email", out var email)
                && email.ValueKind == JsonValueKind.String
                    ? email.GetString()
                    : authenticatedEmail;
        }
        expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(
            root.TryGetProperty("expires_in", out var expiry)
                ? expiry.GetInt32()
                : 3600);
        SessionChanged?.Invoke(
            this,
            new SupabaseAuthSessionChangedEventArgs(
                accessToken,
                refreshToken,
                expiresAtUtc));
    }

    private async Task<CurrentAccountDto> ReadAuthoritativeAccountAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(providerUserId, out var userId) || userId == Guid.Empty)
            throw InvalidAuthenticatedRole("Supabase user id is invalid.");
        if (string.IsNullOrWhiteSpace(accessToken)
            || !TryReadJwtIdentity(accessToken, out var jwtSubject, out var jwtExpiresAt)
            || !Guid.TryParse(jwtSubject, out var jwtUserId)
            || jwtUserId != userId
            || jwtExpiresAt <= DateTimeOffset.UtcNow)
            throw InvalidAuthenticatedRole("Supabase access token subject or expiry is invalid.");
        if (expiresAtUtc == default || jwtExpiresAt < expiresAtUtc)
            expiresAtUtc = jwtExpiresAt;

        var options = RuntimeOptions;
        if (options.OrganizationId is null)
        {
            throw new PublicCloudApiException(
                "PUBLICCLOUD_INVALID_ORGANIZATION_ID",
                "PublicCloud organization id is required for account authorization.",
                HttpStatusCode.ServiceUnavailable);
        }

        var encodedId = Uri.EscapeDataString(providerUserId);
        using var request = ProjectRequest(
            HttpMethod.Get,
            $"/rest/v1/profiles?select=id,organization_id,username,display_name,student_code,date_of_birth,must_change_password,role,is_active&id=eq.{encodedId}&limit=2");
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Supabase application profile", cancellationToken);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() != 1)
            throw InvalidAuthenticatedRole("Authenticated account has no unique application profile.");

        var row = document.RootElement[0];
        var profileId = RequiredString(row, "id");
        var organizationId = RequiredString(row, "organization_id");
        var displayName = RequiredString(row, "display_name");
        var roleValue = RequiredString(row, "role");
        if (!Guid.TryParse(profileId, out var profileUserId)
            || profileUserId != userId
            || !Guid.TryParse(organizationId, out var profileOrganizationId)
            || profileOrganizationId != options.OrganizationId.Value
            || !Enum.TryParse<UserRole>(roleValue, true, out var role)
            || !Enum.IsDefined(role)
            || role is not (UserRole.Admin or UserRole.Teacher or UserRole.Student)
            || !row.TryGetProperty("is_active", out var active)
            || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !active.GetBoolean()
            || !row.TryGetProperty("must_change_password", out var mustChange)
            || mustChange.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw InvalidAuthenticatedRole("Authenticated application profile is inactive or malformed.");

        var username = OptionalString(row, "username");
        var studentCode = OptionalString(row, "student_code");
        var dateOfBirth = OptionalDateOnly(row, "date_of_birth");
        string accountIdentifier;
        if (role == UserRole.Student)
        {
            if (string.IsNullOrWhiteSpace(username)
                || string.IsNullOrWhiteSpace(studentCode)
                || dateOfBirth is null
                || !string.Equals(
                    username.Trim(),
                    studentCode.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                throw InvalidAuthenticatedRole("Authenticated student profile is incomplete or inconsistent.");
            accountIdentifier = username.Trim();
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(studentCode))
                throw InvalidAuthenticatedRole("Non-student profile contains a student code.");
            if (string.IsNullOrWhiteSpace(authenticatedEmail))
                throw InvalidAuthenticatedRole("Supabase authenticated email is missing.");
            accountIdentifier = authenticatedEmail.Trim();
        }

        var loginSessionId = TryReadJwtSessionId(accessToken, out var sessionId)
            ? sessionId
            : Guid.Empty;
        return new CurrentAccountDto(
            userId,
            accountIdentifier,
            authenticatedEmail,
            displayName,
            studentCode,
            role,
            profileOrganizationId.ToString("D"),
            loginSessionId,
            deviceId,
            expiresAtUtc,
            dateOfBirth,
            mustChange.GetBoolean(),
            providerUserId);
    }

    private static PublicCloudApiException InvalidAuthenticatedRole(string message) =>
        new(
            ErrorCodes.AuthenticatedRoleInvalid,
            $"{ErrorCodes.AuthenticatedRoleInvalid}: {message}",
            HttpStatusCode.Forbidden);

    private static string RequiredString(JsonElement element, string name)
    {
        var value = OptionalString(element, name);
        if (string.IsNullOrWhiteSpace(value))
            throw InvalidAuthenticatedRole($"Profile field {name} is missing.");
        return value;
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateOnly? OptionalDateOnly(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind == JsonValueKind.Null)
            return null;
        if (property.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(
                property.GetString(),
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var value))
            throw InvalidAuthenticatedRole($"Profile field {name} is invalid.");
        return value;
    }

    private static bool TryReadJwtIdentity(
        string token,
        out string? subject,
        out DateTimeOffset expiresAt)
    {
        subject = null;
        expiresAt = default;
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return false;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            subject = document.RootElement.TryGetProperty("sub", out var sub)
                ? sub.GetString()
                : null;
            expiresAt = document.RootElement.TryGetProperty("exp", out var exp)
                && exp.TryGetInt64(out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : default;
            return !string.IsNullOrWhiteSpace(subject) && expiresAt != default;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryReadJwtSessionId(string? token, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return false;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("session_id", out var session)
                && Guid.TryParse(session.GetString(), out sessionId);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    private HttpRequestMessage ProjectRequest(HttpMethod method, string path, bool userToken = true)
    {
        EnsureConfigured();
        var options = RuntimeOptions;
        var request = new HttpRequestMessage(method, new Uri(options.ProjectUri!, path));
        request.Headers.TryAddWithoutValidation("apikey", options.PublishableKey);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            userToken ? accessToken : options.PublishableKey);
        return request;
    }

    private void EnsureConfigured()
    {
        var options = RuntimeOptions;
        if (!options.Configured)
            throw new PublicCloudApiException(
                options.ErrorCode ?? "PUBLICCLOUD_NOT_CONFIGURED",
                "PublicCloud runtime configuration is missing or invalid.",
                HttpStatusCode.ServiceUnavailable);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return await http.SendAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        return await http.SendAsync(request, completionOption, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        var code = ExtractErrorCode(detail, response.StatusCode);
        throw new PublicCloudApiException(
            code,
            $"{operation} failed ({(int)response.StatusCode}; code={code}).",
            response.StatusCode);
    }

    private static string ExtractErrorCode(string detail, HttpStatusCode statusCode)
    {
        foreach (var known in new[]
                 {
                     "OPEN_PUBLIC_SESSION_NOT_FOUND",
                     "AUTHENTICATION_REQUIRED",
                     "PUBLIC_SESSION_CAPACITY_REACHED",
                     "PUBLIC_SESSION_NOT_JOINABLE",
                     "PUBLICCLOUD_SCHEMA_INCOMPATIBLE"
                 })
        {
            if (detail.Contains(known, StringComparison.Ordinal))
                return known;
        }
        try
        {
            using var document = JsonDocument.Parse(detail);
            foreach (var name in new[] { "code", "message" })
            {
                if (document.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString()!.Length <= 80
                        ? value.GetString()!
                        : $"PUBLICCLOUD_HTTP_{(int)statusCode}";
            }
        }
        catch (JsonException)
        {
        }
        return $"PUBLICCLOUD_HTTP_{(int)statusCode}";
    }

    private sealed record OpenPublicJoinRpcResult(
        Guid SessionId,
        Guid ExamId,
        Guid ParticipantId,
        string ParticipantStatus,
        string SessionStatus,
        string RoomCode,
        string ExamTitle,
        string Subject,
        int DurationMinutes,
        string DeliveryType,
        string SupervisionMode,
        string QuizResultPolicy,
        DateTimeOffset? PlannedStartUtc,
        int? Capacity,
        int CurrentParticipantCount);
    private sealed record CloudCapabilities(
        int SchemaVersion,
        IReadOnlyList<string>? CriticalRpcs = null);
    private sealed record ParticipantStatusRow(string Status);
    private sealed record EnrollmentRow(Guid Id, string Status);
    private sealed record SubmissionFilePlanRow(Guid Id,
        [property: JsonPropertyName("cloud_object_path")] string CloudObjectPath);
    private sealed record ExamFileRow(Guid Id, string Name,
        [property: JsonPropertyName("size_bytes")] long SizeBytes,
        string Sha256,
        [property: JsonPropertyName("mime_type")] string? MimeType);
    private sealed record ReceiptFileRow(Guid Id, string Name,
        [property: JsonPropertyName("size_bytes")] long SizeBytes,
        string Sha256,
        [property: JsonPropertyName("mime_type")] string? MimeType);
    private sealed record ReceiptRow(Guid Id,
        [property: JsonPropertyName("receipt_code")] string? ReceiptCode,
        [property: JsonPropertyName("receipt_signature")] string? ReceiptSignature,
        [property: JsonPropertyName("server_received_at")] DateTimeOffset? ServerReceivedAt,
        [property: JsonPropertyName("is_late")] bool IsLate,
        [property: JsonPropertyName("submission_files")] IReadOnlyList<ReceiptFileRow> SubmissionFiles);
    private sealed record QuizSnapshotChoice(Guid Id, int SortOrder, string ChoiceText);
    private sealed record QuizSnapshotQuestion(Guid Id, int SortOrder, string QuestionText,
        decimal Points, bool Multiple, IReadOnlyList<QuizSnapshotChoice> Choices);
    private sealed record PublicQuizAnswerSnapshot(
        Guid QuestionId,
        IReadOnlyList<Guid> ChoiceIds,
        long Revision,
        DateTimeOffset ClientUpdatedAtUtc);
    private sealed record PublicQuizAttemptSnapshot(
        Guid Id,
        Guid SessionId,
        Guid ParticipantId,
        string Status,
        int ExamVersion,
        string ResultPolicy,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset DeadlineUtc,
        DateTimeOffset? FinalizedAtUtc,
        bool ScoreVisible,
        decimal? Score,
        decimal MaxScore,
        IReadOnlyList<QuizSnapshotQuestion> Questions,
        IReadOnlyList<PublicQuizAnswerSnapshot> Answers);
    private sealed record PublicQuizReviewChoice(
        Guid Id,
        int SortOrder,
        string ChoiceText,
        bool? Correct);
    private sealed record PublicQuizReviewQuestion(
        Guid Id,
        int SortOrder,
        string QuestionText,
        decimal Points,
        bool Multiple,
        IReadOnlyList<PublicQuizReviewChoice> Choices);
    private sealed record PublicQuizReviewSnapshot(
        Guid AttemptId,
        decimal? Score,
        decimal MaxScore,
        bool ScoreVisible,
        bool CorrectAnswersVisible,
        string? GeneralComment,
        IReadOnlyList<PublicQuizReviewQuestion> Questions,
        IReadOnlyList<PublicQuizAnswerSnapshot> Answers);
}

public sealed record PublicCloudJoinResult(
    Guid SessionId,
    Guid ExamId,
    Guid ParticipantId,
    ParticipantStatus ParticipantStatus,
    SessionStatus SessionStatus,
    string RoomCode,
    string ExamTitle,
    string Subject,
    int DurationMinutes,
    ExamDeliveryType DeliveryType,
    SupervisionMode SupervisionMode,
    QuizResultPolicy QuizResultPolicy,
    DateTimeOffset? PlannedStartUtc,
    int? Capacity,
    int CurrentParticipantCount,
    string AccessToken);
public sealed record PublicExamFileUrl(Uri Url, int ExpiresIn, string FileName, long SizeBytes, string Sha256);
public sealed record PublicSubmissionPlan(Guid SubmissionId, Guid FileId, string CloudObjectPath);
public sealed record PublicEnrollmentState(Guid RequestId, string Status);
public sealed record PublicStudentTimeline(
    Guid SessionId,
    Guid ParticipantId,
    string SessionStatus,
    DateTimeOffset? StartedAtUtc,
    int DurationMinutes,
    int ExtraTimeMinutes,
    DateTimeOffset? EffectiveDeadlineUtc,
    Guid? AttemptId,
    string? AttemptStatus,
    DateTimeOffset? AttemptDeadlineUtc,
    DateTimeOffset ServerNowUtc,
    long Revision,
    DateTimeOffset UpdatedAtUtc,
    string? ParticipantStatus = null,
    Guid? ExamId = null,
    int ExamVersion = 1,
    string DeliveryType = "FileSubmission",
    string SupervisionMode = "None",
    string ResultPolicy = "Hidden",
    bool ScoreVisible = false,
    decimal? Score = null,
    decimal? MaxScore = null,
    string SubmissionStatus = "NotStarted",
    string AdmissionMode = "ClassMembersOnly",
    string? ExamTitle = null,
    string? Subject = null,
    bool ResubmitAllowed = false);
