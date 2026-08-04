using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Net;
using System.Text.Json.Serialization;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class BackendClient : IBackendClient
{
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    private readonly HttpClient http;
    private readonly object endpointGate = new();
    private Uri baseAddress;
    private string? accountToken;
    private Uri? accountTokenOrigin;
    private string? participantToken;
    private Uri? participantTokenOrigin;

    public BackendClient(string baseUrl, HttpMessageHandler? handler = null)
    {
        baseAddress = NormalizeBaseAddress(baseUrl, null, allowLoopback: true);
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromMinutes(10);
    }

    public Uri BaseAddress
    {
        get { lock (endpointGate) return baseAddress; }
    }

    public bool HasTrustedAccountToken
    {
        get
        {
            lock (endpointGate)
                return !string.IsNullOrWhiteSpace(accountToken) && SameOrigin(accountTokenOrigin, baseAddress);
        }
    }

    public bool TrySetBaseAddress(string hostOrUrl, int port, out string? error)
    {
        try
        {
            var next = NormalizeBaseAddress(hostOrUrl, port, allowLoopback: false);
            lock (endpointGate)
            {
                if (!SameOrigin(baseAddress, next))
                {
                    accountToken = null;
                    accountTokenOrigin = null;
                    participantToken = null;
                    participantTokenOrigin = null;
                }
                baseAddress = next;
            }
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void SetBearerToken(string? token)
    {
        SetAccountToken(token);
    }

    public void SetAccountToken(string? token)
    {
        lock (endpointGate)
        {
            accountToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
            accountTokenOrigin = accountToken is null ? null : baseAddress;
        }
    }

    public void SetParticipantToken(string? token)
    {
        lock (endpointGate)
        {
            participantToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
            participantTokenOrigin = participantToken is null ? null : baseAddress;
        }
    }

    public Task<ApiResponse<SystemStatusDto>?> GetSystemStatusAsync(CancellationToken ct = default) => GetAsync<SystemStatusDto>("api/v1/system/status", ct);
    public Task<ApiResponse<DashboardSummaryDto>?> GetDashboardAsync(CancellationToken ct = default) => GetAsync<DashboardSummaryDto>("api/v1/dashboard/summary", ct);
    public Task<ApiResponse<PagedResult<ClassSummaryDto>>?> GetClassesAsync(CancellationToken ct = default) => GetAsync<PagedResult<ClassSummaryDto>>("api/v1/classes", ct);
    public Task<ApiResponse<PagedResult<ExamSummaryDto>>?> GetExamsAsync(CancellationToken ct = default) => GetAsync<PagedResult<ExamSummaryDto>>("api/v1/exams", ct);
    public Task<ApiResponse<PagedResult<SessionSummaryDto>>?> GetSessionsAsync(CancellationToken ct = default) => GetAsync<PagedResult<SessionSummaryDto>>("api/v1/sessions", ct);
    public Task<ApiResponse<SessionDetailDto>?> GetSessionAsync(Guid id, CancellationToken ct = default) => GetAsync<SessionDetailDto>($"api/v1/sessions/{id}", ct);
    public Task<ApiResponse<PagedResult<SubmissionSummaryDto>>?> GetSubmissionsAsync(Guid sessionId, CancellationToken ct = default) => GetAsync<PagedResult<SubmissionSummaryDto>>($"api/v1/sessions/{sessionId}/submissions", ct);
    public Task<ApiResponse<CloudSyncStatusDto>?> GetCloudStatusAsync(CancellationToken ct = default) => GetAsync<CloudSyncStatusDto>("api/v1/cloud/sync/status", ct);
    public Task<ApiResponse<SettingsDto>?> GetSettingsAsync(CancellationToken ct = default) => GetAsync<SettingsDto>("api/v1/settings", ct);

    public async Task<ApiResponse<T>?> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return await Read<T>(response, ct);
    }

    public async Task<ApiResponse<TResponse>?> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default)
    {
        using var message = CreateRequest(HttpMethod.Post, path);
        message.Content = JsonContent.Create(request, options: Json);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        return await Read<TResponse>(response, ct);
    }

    public async Task<ApiResponse<TResponse>?> PutAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default)
    {
        using var message = CreateRequest(HttpMethod.Put, path);
        message.Content = JsonContent.Create(request, options: Json);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        return await Read<TResponse>(response, ct);
    }

    public async Task<ApiResponse<TResponse>?> DeleteAsync<TResponse>(string path, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, path);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return await Read<TResponse>(response, ct);
    }

    public async Task<ApiResponse<object>?> UploadChunkAsync(string path, Stream content, long contentLength, string? sha256 = null, CancellationToken ct = default)
    {
        using var streamContent = new StreamContent(content);
        streamContent.Headers.ContentLength = contentLength;
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = CreateRequest(HttpMethod.Put, path);
        request.Content = streamContent;
        if (!string.IsNullOrWhiteSpace(sha256))
        {
            request.Headers.TryAddWithoutValidation("X-Chunk-Sha256", sha256);
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return await Read<object>(response, ct);
    }

    public async Task DownloadFileAsync(string path, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await SaveResponseAsync(response, destinationPath, progress, ct);
    }

    public async Task DownloadVerifiedFileAsync(
        string path,
        string destinationPath,
        string expectedSha256,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var expected = expectedSha256?.Trim().ToLowerInvariant();
        if (expected?.Length != 64 || expected.Any(x => !Uri.IsHexDigit(x)))
            throw new ArgumentException("SHA-256 của file đề không hợp lệ.", nameof(expectedSha256));

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? AppContext.BaseDirectory);
        if (File.Exists(destinationPath) && await HashFileAsync(destinationPath, ct) == expected)
        {
            progress?.Report(100);
            return;
        }

        var partialPath = destinationPath + ".partial";
        for (var verificationAttempt = 0; verificationAttempt < 2; verificationAttempt++)
        {
            var offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (offset > 0 && await HashFileAsync(partialPath, ct) == expected)
            {
                File.Move(partialPath, destinationPath, true);
                progress?.Report(100);
                return;
            }

            using var request = CreateRequest(HttpMethod.Get, path);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var canAppend = offset > 0
                && response.StatusCode == System.Net.HttpStatusCode.PartialContent
                && response.Content.Headers.ContentRange?.From == offset;
            if (!canAppend) offset = 0;
            var total = response.Content.Headers.ContentRange?.Length
                ?? (response.Content.Headers.ContentLength.HasValue ? offset + response.Content.Headers.ContentLength.Value : null);
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(
                partialPath,
                canAppend ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                true))
            {
                var buffer = new byte[81920];
                var received = offset;
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(), ct);
                    if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    if (total is > 0) progress?.Report(received * 100d / total.Value);
                }
            }

            if (await HashFileAsync(partialPath, ct) == expected)
            {
                File.Move(partialPath, destinationPath, true);
                progress?.Report(100);
                return;
            }

            File.Delete(partialPath);
        }

        throw new InvalidDataException("File tải về không khớp SHA-256 sau khi đã tải lại từ đầu.");
    }

    public async Task PostDownloadFileAsync<TRequest>(string path, TRequest request, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var message = CreateRequest(HttpMethod.Post, path);
        message.Content = JsonContent.Create(request, options: Json);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await SaveResponseAsync(response, destinationPath, progress, ct);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        Uri endpoint;
        string? trustedAccountToken;
        string? trustedParticipantToken;
        lock (endpointGate)
        {
            endpoint = new Uri(baseAddress, path.TrimStart('/'));
            trustedAccountToken = SameOrigin(accountTokenOrigin, baseAddress) ? accountToken : null;
            trustedParticipantToken = SameOrigin(participantTokenOrigin, baseAddress) ? participantToken : null;
        }

        var request = new HttpRequestMessage(method, endpoint);
        if (!string.IsNullOrWhiteSpace(trustedAccountToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", trustedAccountToken);
        }

        if (!string.IsNullOrWhiteSpace(trustedParticipantToken))
        {
            request.Headers.TryAddWithoutValidation("X-Exam-Session-Token", trustedParticipantToken);
        }

        return request;
    }

    private static Uri NormalizeBaseAddress(string hostOrUrl, int? port, bool allowLoopback)
    {
        var value = hostOrUrl?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Địa chỉ máy chủ không được để trống.");

        if (!value.Contains("://", StringComparison.Ordinal))
            value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || (parsed.AbsolutePath != "" && parsed.AbsolutePath != "/")
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
            throw new ArgumentException("Địa chỉ máy chủ phải là IP hoặc URL HTTP/HTTPS hợp lệ, không kèm đường dẫn.");

        var effectivePort = port ?? (parsed.IsDefaultPort ? (parsed.Scheme == Uri.UriSchemeHttps ? 443 : 80) : parsed.Port);
        if (effectivePort is <= 0 or > 65535)
            throw new ArgumentException("Cổng máy chủ phải nằm trong khoảng 1-65535.");
        if (!allowLoopback)
        {
            if (!IPAddress.TryParse(parsed.Host, out var address)
                || !DiscoveryProtocol.IsUsableEndpointAddress(address))
                throw new ArgumentException("Endpoint LAN phải là địa chỉ IPv4 hợp lệ, không được là localhost, loopback, unspecified hoặc link-local.");
        }

        return new UriBuilder(parsed.Scheme, parsed.Host, effectivePort).Uri;
    }

    private static bool SameOrigin(Uri? left, Uri? right) => left is not null
        && right is not null
        && string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static async Task SaveResponseAsync(HttpResponseMessage response, string destinationPath, IProgress<double>? progress, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? AppContext.BaseDirectory);
        var partialPath = destinationPath + ".partial";
        long readTotal;
        await using (var target = new FileStream(
            partialPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            true))
        {
            var buffer = new byte[81920];
            readTotal = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;
                if (total is > 0) progress?.Report(readTotal * 100d / total.Value);
            }
            await target.FlushAsync(ct);
        }

        if (total.HasValue && readTotal != total.Value)
            throw new EndOfStreamException(
                $"Download ended after {readTotal} bytes; the server announced {total.Value} bytes.");

        File.Move(partialPath, destinationPath, true);
        progress?.Report(100);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<ApiResponse<T>?> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            if (response.IsSuccessStatusCode) return null;
            return CreateProtocolError<T>(
                response,
                "Máy chủ không trả về nội dung phản hồi.");
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ApiResponse<T>>(body, Json);
            if (parsed is not null)
            {
                if (parsed.Success || parsed.Error is null) return parsed;
                var endpoint = response.RequestMessage?.RequestUri?.AbsolutePath;
                var transport = new BackendTransportDetails(
                    (int)response.StatusCode,
                    endpoint,
                    parsed.Error.Details,
                    !string.IsNullOrWhiteSpace(parsed.TraceId));
                return parsed with { Error = parsed.Error with { Details = transport } };
            }
        }
        catch (JsonException ex)
        {
            // Do not include the raw response body in logs or UI. Login responses
            // can contain access tokens and must never be exposed to the screen.
            FrontendLogger.Log(ex, $"BackendClient.Read<{typeof(T).Name}>");
            return CreateProtocolError<T>(
                response,
                "Máy chủ trả về dữ liệu không đúng định dạng mà ứng dụng hỗ trợ.");
        }

        return CreateProtocolError<T>(
            response,
            "Máy chủ trả về dữ liệu rỗng hoặc không hợp lệ.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        // ASP.NET Core serializes contract enums as strings (for example
        // role: "Admin"). The desktop client must use the same convention.
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static ApiResponse<T> CreateProtocolError<T>(HttpResponseMessage response, string message)
    {
        var statusCode = (int)response.StatusCode;
        var path = response.RequestMessage?.RequestUri?.AbsolutePath;
        var endpoint = string.IsNullOrWhiteSpace(path) ? string.Empty : $" tại {path}";
        var error = new ApiError(
            "INVALID_SERVER_RESPONSE",
            $"{message} (HTTP {statusCode}{endpoint}).",
            Details: new BackendTransportDetails(statusCode, path, null, false));

        return new ApiResponse<T>(
            false,
            default,
            error,
            Guid.NewGuid().ToString("N"),
            ContractInfo.SchemaVersion);
    }
}
