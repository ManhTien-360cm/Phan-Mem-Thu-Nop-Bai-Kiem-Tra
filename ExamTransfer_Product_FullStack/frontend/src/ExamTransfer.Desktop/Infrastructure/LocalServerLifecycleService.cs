using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed record LocalServerLifecycleResult(
    string Status,
    bool Started,
    string Message,
    string? ExecutablePath = null,
    int? ExitCode = null,
    string? ServerBuildId = null,
    string? Protocol = null);

public sealed record LocalServerProbeResult(
    string Code,
    LocalServerIdentityDto? Identity = null,
    string? Detail = null)
{
    public bool Ready => Code == "READY" && Identity is not null;
}

public interface ILocalServerRuntime
{
    Task<LocalServerProbeResult> ProbeAsync(CancellationToken cancellationToken);
    Task<bool> IsTcpPortOccupiedAsync(CancellationToken cancellationToken);
    Task<bool> IsUdpPortOccupiedAsync(CancellationToken cancellationToken);
    ILocalServerProcess Start(string executablePath, string workingDirectory);
    Task StopExactAsync(string executablePath, CancellationToken cancellationToken);
}

public interface ILocalServerProcess
{
    bool HasExited { get; }
    int? ExitCode { get; }
    Task StopAsync(CancellationToken cancellationToken);
}

public sealed class LocalServerLifecycleService
{
    private const int ServerPort = 5048;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private readonly ILocalServerRuntime runtime;
    private readonly string baseDirectory;
    private readonly object processGate = new();
    private ILocalServerProcess? ownedProcess;

    public LocalServerLifecycleService(
        ILocalServerRuntime? runtime = null,
        string? baseDirectory = null)
    {
        this.runtime = runtime ?? new LocalServerRuntime(ServerPort);
        this.baseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
    }

    public async Task<LocalServerLifecycleResult> EnsureStartedAsync(
        UserRole authenticatedRole,
        CancellationToken cancellationToken = default)
    {
        if (authenticatedRole is not (UserRole.Teacher or UserRole.Admin))
            return new(
                "ROLE_NOT_AUTHORIZED",
                false,
                "Only an authenticated Teacher or Admin may start Local Server.");

        var executablePath = FindServerExecutable();
        if (executablePath is null)
            return new(
                "SERVER_EXE_MISSING",
                false,
                "Bộ cài ExamTransfer thiếu ExamTransfer.LocalServer.exe. Hãy chạy lại bộ cài đặt.",
                ExpectedServerExecutable());

        using var launchGate = new Semaphore(
            1,
            1,
            @"Local\ExamTransfer.Desktop.LocalServerLauncher");
        if (!launchGate.WaitOne(TimeSpan.FromSeconds(5)))
            return new(
                "SERVER_START_COORDINATION_TIMEOUT",
                false,
                "Không thể phối hợp khởi động Local Server với một phiên ExamTransfer khác.",
                executablePath);
        try
        {
            var existing = await runtime.ProbeAsync(cancellationToken);
            if (existing.Ready)
                return HealthyResult("SERVER_HEALTHY", false, "ExamTransfer Local Server is already healthy.", executablePath, existing.Identity!);
            if (IsPackageMismatch(existing))
                return MismatchResult(existing, executablePath);

            if (await runtime.IsTcpPortOccupiedAsync(cancellationToken))
                return new(
                    "PORT_CONFLICT_TCP_5048",
                    false,
                    "Cổng 5048 đang bị tiến trình khác chiếm nhưng không phải ExamTransfer Local Server.",
                    executablePath);

            if (await runtime.IsUdpPortOccupiedAsync(cancellationToken))
                return new(
                    "PORT_CONFLICT_UDP_40550",
                    false,
                    $"UDP port {DiscoveryProtocol.DefaultPort} is occupied by another process. Close that process before starting ExamTransfer Local Server.",
                    executablePath);

            var workingDirectory = Path.GetDirectoryName(executablePath)!;
            ILocalServerProcess process;
            try
            {
                process = runtime.Start(executablePath, workingDirectory);
                lock (processGate)
                    ownedProcess = process;
                FrontendLogger.LogMessage(
                    $"executable={executablePath}; working_directory={workingDirectory}; phase=started",
                    "LocalServerLifecycle");
            }
            catch (Exception ex)
            {
                FrontendLogger.Log(ex, "LocalServerLifecycle.Start");
                return new(
                    "SERVER_START_FAILED",
                    false,
                    $"Không thể khởi động Local Server: {ex.Message}",
                    executablePath);
            }

            var deadline = DateTimeOffset.UtcNow + StartupTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    FrontendLogger.LogMessage(
                        $"executable={executablePath}; exit_code={process.ExitCode?.ToString() ?? "unknown"}; phase=exited_before_health",
                        "LocalServerLifecycle");
                    return new(
                        "SERVER_EXITED_BEFORE_HEALTH",
                        false,
                        $"Local Server dừng trước khi sẵn sàng (exit code {process.ExitCode?.ToString() ?? "không rõ"}).",
                        executablePath,
                        process.ExitCode);
                }

                var probe = await runtime.ProbeAsync(cancellationToken);
                if (probe.Ready)
                    return HealthyResult(
                        "SERVER_STARTED",
                        true,
                        "ExamTransfer Local Server was started and its identity was verified.",
                        executablePath,
                        probe.Identity!);
                if (IsPackageMismatch(probe))
                    return MismatchResult(probe, executablePath);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }

            return new(
                "SERVER_HEALTH_TIMEOUT",
                false,
                "Local Server đã khởi động nhưng không sẵn sàng trong thời gian cho phép.",
                executablePath,
                process.ExitCode);
        }
        finally
        {
            launchGate.Release();
        }
    }

    public async Task StopOwnedAsync(CancellationToken cancellationToken = default)
    {
        ILocalServerProcess? process;
        lock (processGate)
        {
            process = ownedProcess;
            ownedProcess = null;
        }
        if (process is not null && !process.HasExited)
            await process.StopAsync(cancellationToken);
    }

    public async Task StopExistingPackagedServerAsync(
        UserRole authenticatedRole,
        CancellationToken cancellationToken = default)
    {
        if (authenticatedRole is not (UserRole.Teacher or UserRole.Admin))
            throw new UnauthorizedAccessException(
                "Only an authenticated Teacher or Admin may stop Local Server.");

        await StopOwnedAsync(cancellationToken);

        // This explicit, role-authorized path also handles a verified packaged
        // server that predates the current Teacher/Admin desktop process.
        await runtime.StopExactAsync(ExpectedServerExecutable(), cancellationToken);
    }

    private static bool IsPackageMismatch(LocalServerProbeResult probe) =>
        probe.Code is DiscoveryProtocol.ProtocolMismatch
            or DiscoveryProtocol.PortMismatch
            or DiscoveryProtocol.BuildMismatch
            or "SERVER_IDENTITY_MISMATCH";

    private static LocalServerLifecycleResult MismatchResult(
        LocalServerProbeResult probe,
        string executablePath) =>
        new(
            probe.Code,
            false,
            probe.Detail ?? "Local Server không tương thích với ứng dụng. Hãy cập nhật và khởi động lại từ cùng bộ cài.",
            executablePath,
            ServerBuildId: probe.Identity?.BuildId,
            Protocol: probe.Identity?.Protocol);

    private static LocalServerLifecycleResult HealthyResult(
        string status,
        bool started,
        string message,
        string executablePath,
        LocalServerIdentityDto identity) =>
        new(
            status,
            started,
            message,
            executablePath,
            ServerBuildId: identity.BuildId,
            Protocol: identity.Protocol);

    private string? FindServerExecutable()
    {
        var expected = ExpectedServerExecutable();
        return File.Exists(expected) ? expected : null;
    }

    private string ExpectedServerExecutable() =>
        Path.GetFullPath(Path.Combine(baseDirectory, "..", "Server", "ExamTransfer.LocalServer.exe"));

}

public sealed class LocalServerRuntime(int port) : ILocalServerRuntime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<LocalServerProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var health = await http.GetAsync(
                $"http://127.0.0.1:{port}/health",
                cancellationToken);
            if (!health.IsSuccessStatusCode)
                return new("NOT_RUNNING", Detail: "Local Server health endpoint is unavailable.");
            using var identityResponse = await http.GetAsync(
                $"http://127.0.0.1:{port}/api/v1/discovery/identity",
                cancellationToken);
            if (identityResponse.StatusCode == HttpStatusCode.NotFound)
                return new(
                    DiscoveryProtocol.ProtocolMismatch,
                    Detail: "Local Server không có identity endpoint V2. Hãy cập nhật và khởi động lại.");
            if (!identityResponse.IsSuccessStatusCode)
                return new("SERVER_IDENTITY_MISMATCH", Detail: "Local Server identity endpoint failed.");
            var wrapper = JsonSerializer.Deserialize<ApiResponse<LocalServerIdentityDto>>(
                await identityResponse.Content.ReadAsStringAsync(cancellationToken),
                Json);
            if (wrapper?.Success != true || wrapper.Data is null)
                return new("SERVER_IDENTITY_MISMATCH", Detail: "Local Server identity response is invalid.");
            var identity = wrapper.Data;
            if (!identity.Protocol.Equals(DiscoveryProtocol.ProtocolVersion, StringComparison.Ordinal))
                return new(
                    DiscoveryProtocol.ProtocolMismatch,
                    identity,
                    "Local Server dùng discovery protocol không tương thích. Hãy cập nhật và khởi động lại.");
            if (identity.DiscoveryPort != DiscoveryProtocol.DefaultPort)
                return new(
                    DiscoveryProtocol.PortMismatch,
                    identity,
                    $"Local Server phải dùng UDP {DiscoveryProtocol.DefaultPort}; không cho phép fallback.");
            if (!identity.BuildId.Equals(ReleaseIdentity.BuildId, StringComparison.Ordinal))
                return new(
                    DiscoveryProtocol.BuildMismatch,
                    identity,
                    "App và Local Server không cùng BuildId. Hãy cài lại cùng bộ cài ExamTransfer.");
            if (!identity.Product.Equals("ExamTransfer.LocalServer", StringComparison.Ordinal)
                || identity.ServerPort != port
                || string.IsNullOrWhiteSpace(identity.ServerId))
                return new("SERVER_IDENTITY_MISMATCH", identity, "Local Server identity does not match the expected endpoint.");
            return new("READY", identity);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or TaskCanceledException
                                   or JsonException)
        {
            return new("NOT_RUNNING", Detail: ex.Message);
        }
    }

    public async Task<bool> IsTcpPortOccupiedAsync(CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await tcp.ConnectAsync("127.0.0.1", port, cancellationToken);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public Task<bool> IsUdpPortOccupiedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var udp = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        udp.ExclusiveAddressUse = true;
        try
        {
            udp.Bind(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.DefaultPort));
            return Task.FromResult(false);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse
                                              or SocketError.AccessDenied)
        {
            return Task.FromResult(true);
        }
    }

    public ILocalServerProcess Start(string executablePath, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        ApplyPublicCloudEnvironment(startInfo);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start did not return a Local Server process.");
        return new LocalServerProcess(process);
    }

    public static void ApplyPublicCloudEnvironment(
        ProcessStartInfo startInfo,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var read = environment ?? Environment.GetEnvironmentVariable;
        var url = read("EXAMTRANSFER_SUPABASE_URL")?.Trim();
        var publishableKey = read("EXAMTRANSFER_SUPABASE_PUBLISHABLE_KEY")?.Trim();
        var organizationId = read("EXAMTRANSFER_ORGANIZATION_ID")?.Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var projectUri)
            || (projectUri.Scheme != Uri.UriSchemeHttps && !projectUri.IsLoopback)
            || string.IsNullOrWhiteSpace(publishableKey)
            || publishableKey.StartsWith("sb_secret_", StringComparison.OrdinalIgnoreCase)
            || publishableKey.Contains("service_role", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(organizationId, out var organization)
            || organization == Guid.Empty)
        {
            return;
        }

        startInfo.Environment["Cloud__Enabled"] = "true";
        startInfo.Environment["Cloud__SupabaseUrl"] = projectUri
            .ToString()
            .TrimEnd('/');
        startInfo.Environment["Cloud__PublishableKey"] = publishableKey;
        startInfo.Environment["Cloud__OrganizationId"] = organization.ToString();
        startInfo.Environment["Cloud__AccessMode"] = CloudAccessModes.UserSession;
    }

    public async Task StopExactAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        var expectedPath = Path.GetFullPath(executablePath);
        var processName = Path.GetFileNameWithoutExtension(expectedPath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.HasExited
                        || !Path.GetFullPath(process.MainModule?.FileName ?? string.Empty)
                            .Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between enumeration and inspection.
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    FrontendLogger.Log(ex, "LocalServerLifecycle.StopExact");
                    throw;
                }
            }
        }
    }

    private sealed class LocalServerProcess(Process process) : ILocalServerProcess
    {
        public bool HasExited => process.HasExited;
        public int? ExitCode => process.HasExited ? process.ExitCode : null;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
        }
    }
}
