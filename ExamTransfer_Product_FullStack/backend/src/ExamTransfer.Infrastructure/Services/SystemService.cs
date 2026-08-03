using System.Reflection;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Backup;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Services;

public sealed class SystemService(AppDbContext db, IStoragePaths paths, ICloudAdapter cloud, IOptions<ExamTransferOptions> options, IRealtimePublisher realtime) : ISystemService
{
    private static readonly SessionStatus[] ActiveSessionStatuses =
    [
        SessionStatus.Waiting,
        SessionStatus.InProgress,
        SessionStatus.Paused,
        SessionStatus.Collecting
    ];

    private readonly ExamTransferOptions _options = options.Value;
    private const string SettingsKey = "runtime.settings";

    public async Task<SystemStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var dbStatus = "Ready";
        try { _ = await db.Database.ExecuteSqlRawAsync("SELECT 1;", cancellationToken); }
        catch (Exception ex) { dbStatus = "Error"; warnings.Add("Database: " + ex.Message); }
        paths.EnsureCreated();
        var drive = new DriveInfo(Path.GetPathRoot(paths.RootPath)!); var free = drive.AvailableFreeSpace;
        var storageStatus = free < _options.Storage.MinFreeBytes ? "LowDisk" : "Ready";
        if (storageStatus == "LowDisk") warnings.Add("Dung lượng ổ đĩa thấp hơn ngưỡng an toàn.");
        var cloudPreflight = await cloud.PreflightAsync(cancellationToken);
        var cloudStatus = !_options.Cloud.Enabled
            ? "Disabled"
            : !cloudPreflight.Configured
                ? "Misconfigured"
                : !cloudPreflight.CanSynchronize
                    ? "AuthenticationRequired"
                    : cloudPreflight.Reachable
                        ? "Online"
                        : "Offline";
        if (_options.Cloud.Enabled && !cloudPreflight.Configured)
            warnings.AddRange(cloudPreflight.Errors);
        else if (_options.Cloud.Enabled
            && cloudStatus == "AuthenticationRequired")
            warnings.Add("Cần đăng nhập Supabase để đồng bộ; luồng LAN vẫn hoạt động.");
        else if (_options.Cloud.Enabled && cloudStatus == "Offline")
            warnings.Add("Cloud đang ngoại tuyến; luồng LAN vẫn hoạt động.");
        return new SystemStatusDto(dbStatus == "Ready" && storageStatus == "Ready", Version(), DateTimeOffset.UtcNow, dbStatus, storageStatus, free, _options.Discovery.Enabled ? "Enabled" : "Disabled", cloudStatus, warnings);
    }

    public Task<SystemStatusDto> PreflightAsync(CancellationToken cancellationToken) => GetStatusAsync(cancellationToken);

    public async Task<object> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        var schema = await db.AppSettingsSet.AsNoTracking().FirstOrDefaultAsync(x => x.Key == "schema.version", cancellationToken);
        return new { status, schemaVersion = schema?.ValueJson, process = new { Environment.ProcessId, Environment.MachineName, Environment.OSVersion, baseDirectory = AppContext.BaseDirectory }, paths = new { root = paths.RootPath, database = paths.DatabasePath }, configuration = new { server = new { _options.Server.Port, _options.Server.UseHttps }, discovery = new { _options.Discovery.Enabled, _options.Discovery.Port }, transfer = new { _options.Transfer.ChunkSizeBytes, _options.Transfer.MaxConcurrentUploads }, cloud = new { _options.Cloud.Enabled, _options.Cloud.Environment, _options.Cloud.SupabaseUrl, _options.Cloud.OrganizationId, publishableKeyConfigured = !string.IsNullOrWhiteSpace(_options.Cloud.EffectivePublishableKey), secretKeyConfigured = !string.IsNullOrWhiteSpace(_options.Cloud.ResolveSecretKey()), _options.Cloud.UseResumableUploads, _options.Cloud.AccessMode, authenticated = cloud.Authenticated, authenticatedEmail = cloud.CurrentSession?.Email, canSynchronize = cloud.CanSynchronize }, backupEncryptionConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(BackupCrypto.KeyEnvironmentVariable)), runtimeSettingsPath = RuntimeConfiguration.ConfigurationPath } };
    }

    public async Task<DashboardSummaryDto> GetDashboardAsync(CancellationToken cancellationToken)
    {
        var classes = await db.ClassesSet.CountAsync(x => x.Status == ClassStatus.Active, cancellationToken);
        var exams = await db.ExamsSet.CountAsync(x => x.Status != ExamStatus.Archived, cancellationToken);
        var active = await db.ExamSessionsSet.CountAsync(x => ActiveSessionStatuses.Contains(x.Status), cancellationToken);
        var pending = await db.SubmissionsSet.CountAsync(x => x.IsOfficial && (x.Status == SubmissionStatus.Submitted || x.Status == SubmissionStatus.LateSubmitted) && !db.GradesSet.Any(g => g.SubmissionId == x.Id), cancellationToken);
        var recentKeys = await db.ExamSessionsSet
            .AsNoTracking()
            .Select(x => new { x.Id, x.Status, x.UpdatedAtUtc })
            .ToListAsync(cancellationToken);
        var orderedSessionKeys = recentKeys
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.Id)
            .ToList();
        var recentIds = orderedSessionKeys
            .Take(5)
            .Select(x => x.Id)
            .ToList();
        var activeSessionId = orderedSessionKeys
            .FirstOrDefault(x => ActiveSessionStatuses.Contains(x.Status))
            ?.Id;
        var dashboardSessionIds = recentIds.ToList();
        if (activeSessionId is { } selectedActiveSessionId
            && !dashboardSessionIds.Contains(selectedActiveSessionId))
        {
            dashboardSessionIds.Add(selectedActiveSessionId);
        }
        var dashboardSessionEntities = dashboardSessionIds.Count == 0
            ? []
            : await db.ExamSessionsSet
                .AsNoTracking()
                .Include(x => x.Exam)
                .Include(x => x.Participants)
                .Where(x => dashboardSessionIds.Contains(x.Id))
                .ToListAsync(cancellationToken);
        var serverNowUtc = DateTimeOffset.UtcNow;
        var summariesById = dashboardSessionEntities.ToDictionary(
            x => x.Id,
            x => ToSessionSummary(x, serverNowUtc, _options.Session.DisconnectAfterSeconds));
        var recent = recentIds
            .Select(id => summariesById[id])
            .ToList();
        var activeSession = activeSessionId is { } id
            ? summariesById[id]
            : null;
        long storage = 0; try { storage = Directory.EnumerateFiles(paths.RootPath, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); } catch { }
        var status = await GetStatusAsync(cancellationToken);
        return new DashboardSummaryDto(classes, exams, active, pending, storage, recent, status.Warnings, activeSession);
    }

    private static SessionSummaryDto ToSessionSummary(
        ExamSession session,
        DateTimeOffset serverNowUtc,
        int disconnectAfterSeconds) =>
        new(
            session.Id,
            session.ExamId,
            session.Exam.Title,
            session.RoomCode,
            session.Status,
            serverNowUtc,
            session.StartedAtUtc,
            session.EndedAtUtc,
            session.StartedAtUtc?.AddMinutes(session.Exam.DurationMinutes),
            new SessionCountsDto(
                session.Participants.Count,
                session.Participants.Count(p => p.Status == ParticipantStatus.PendingApproval),
                session.Participants.Count(p => p.Status == ParticipantStatus.Approved),
                session.Participants.Count(p => p.LastSeenUtc > serverNowUtc.AddSeconds(-disconnectAfterSeconds)),
                session.Participants.Count(p => p.SubmissionStatus is SubmissionStatus.Submitted or SubmissionStatus.LateSubmitted),
                session.Participants.Count(p => p.SubmissionStatus == SubmissionStatus.Uploading),
                session.Participants.Count(p => p.Status == ParticipantStatus.Disconnected)),
            session.Sequence,
            session.RowVersion,
            AdmissionMode: session.AdmissionMode);

    public async Task<SettingsDto> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var stored = await db.AppSettingsSet.AsNoTracking().FirstOrDefaultAsync(x => x.Key == SettingsKey, cancellationToken);
        if (stored is not null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<SettingsDto>(
                    stored.ValueJson,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (parsed is not null)
                {
                    return OverlayCloudRuntime(
                        parsed with { RowVersion = stored.RowVersion });
                }
            }
            catch
            {
                // Fall back to current runtime configuration.
            }
        }
        return Defaults(stored?.RowVersion ?? "new");
    }

    public async Task<SettingsDto> UpdateSettingsAsync(UpdateSettingsRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var cloudIdentityChanged =
            !string.Equals(
                request.SupabaseUrl?.TrimEnd('/'),
                _options.Cloud.SupabaseUrl?.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                request.SupabasePublishableKey,
                _options.Cloud.EffectivePublishableKey,
                StringComparison.Ordinal)
            || !string.Equals(
                request.OrganizationId,
                _options.Cloud.OrganizationId,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                request.CloudAccessMode,
                _options.Cloud.AccessMode,
                StringComparison.OrdinalIgnoreCase);

        if (cloudIdentityChanged && cloud.Authenticated)
            await cloud.LogoutAsync(cancellationToken);

        var active = await db.ExamSessionsSet.AnyAsync(x => x.Status == SessionStatus.Waiting || x.Status == SessionStatus.InProgress || x.Status == SessionStatus.Paused || x.Status == SessionStatus.Collecting, cancellationToken);
        if (active && (request.ServerPort != _options.Server.Port || request.StorageRootPath != paths.RootPath || request.ChunkSizeBytes != _options.Transfer.ChunkSizeBytes)) throw new ApiException(ErrorCodes.Conflict, "Không đổi cổng, đường dẫn hoặc chunk size khi phòng thi đang hoạt động.", 409);
        var entity = await db.AppSettingsSet.FirstOrDefaultAsync(x => x.Key == SettingsKey, cancellationToken);
        if (entity is null) { entity = new AppSetting { Key = SettingsKey }; db.AppSettingsSet.Add(entity); }
        else if (request.RowVersion != "new" && entity.RowVersion != request.RowVersion) throw new ApiException(ErrorCodes.ConcurrencyConflict, "Cài đặt đã thay đổi.", 409);
        var dto = new SettingsDto(
            request.ServerPort,
            request.UseHttps,
            request.DiscoveryEnabled,
            request.DiscoveryPort,
            request.StorageRootPath,
            request.MinFreeBytes,
            request.ChunkSizeBytes,
            request.MaxConcurrentUploads,
            request.HeartbeatSeconds,
            request.DisconnectAfterSeconds,
            request.CloudEnabled,
            request.TemporaryHours,
            request.LogsDays,
            entity.RowVersion,
            request.SupabaseUrl,
            request.SupabasePublishableKey,
            request.OrganizationId,
            request.CloudEnvironment,
            request.CloudUseResumableUploads,
            !string.IsNullOrWhiteSpace(_options.Cloud.ResolveSecretKey()),
            request.CloudEnabled
                ? cloudIdentityChanged
                    && string.Equals(
                        request.CloudAccessMode,
                        CloudAccessModes.UserSession,
                        StringComparison.OrdinalIgnoreCase)
                    ? "AuthenticationRequired"
                    : "PendingPreflight"
                : "Disabled",
            request.CloudAccessMode,
            cloud.Authenticated,
            cloud.CurrentSession?.Email);
        RuntimeConfiguration.Save(request);
        entity.ValueJson = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.SaveChangesAsync(cancellationToken);

        _options.Server.Port = request.ServerPort;
        _options.Server.UseHttps = request.UseHttps;
        _options.Discovery.Enabled = request.DiscoveryEnabled;
        _options.Discovery.Port = DiscoveryProtocol.DefaultPort;
        _options.Storage.RootPath = request.StorageRootPath;
        _options.Storage.MinFreeBytes = request.MinFreeBytes;
        _options.Transfer.ChunkSizeBytes = request.ChunkSizeBytes;
        _options.Transfer.MaxConcurrentUploads = request.MaxConcurrentUploads;
        _options.Session.HeartbeatSeconds = request.HeartbeatSeconds;
        _options.Session.DisconnectAfterSeconds = request.DisconnectAfterSeconds;
        _options.Cloud.Enabled = request.CloudEnabled;
        _options.Cloud.SupabaseUrl = request.SupabaseUrl;
        _options.Cloud.PublishableKey = request.SupabasePublishableKey;
        _options.Cloud.OrganizationId = request.OrganizationId;
        _options.Cloud.Environment = request.CloudEnvironment;
        _options.Cloud.UseResumableUploads = request.CloudUseResumableUploads;
        _options.Cloud.AccessMode = request.CloudAccessMode;
        _options.Retention.TemporaryHours = request.TemporaryHours;
        _options.Retention.LogsDays = request.LogsDays;

        dto = dto with { RowVersion = entity.RowVersion };
        await realtime.PublishSessionAsync(Guid.Empty, RealtimeEvents.SettingsChanged, 0, dto, cancellationToken);
        return dto;
    }

    public async Task<CloudSyncStatusDto> GetCloudStatusAsync(CancellationToken cancellationToken)
    {
        var pending = await db.SyncQueueSet.CountAsync(x => x.Status == SyncStatus.Pending || x.Status == SyncStatus.Failed, cancellationToken);
        var failed = (await db.SyncQueueSet
                .AsNoTracking()
                .Where(x => x.Status == SyncStatus.Failed)
                .ToListAsync(cancellationToken))
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();
        var success = (await db.SyncQueueSet
                .AsNoTracking()
                .Where(x => x.Status == SyncStatus.Synced)
                .Select(x => x.UpdatedAtUtc)
                .ToListAsync(cancellationToken))
            .OrderByDescending(x => x)
            .Cast<DateTimeOffset?>()
            .FirstOrDefault();

        if (!_options.Cloud.Enabled)
        {
            return new CloudSyncStatusDto(
                false,
                SyncStatus.LocalOnly,
                pending,
                success,
                failed?.LastError,
                false,
                _options.Cloud.OrganizationId,
                "LocalOnly",
                _options.Cloud.AccessMode,
                false,
                false);
        }

        CloudPreflightResult preflight;
        try
        {
            preflight = await cloud.PreflightAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CloudSyncStatusDto(
                true,
                SyncStatus.Failed,
                pending,
                success,
                failed?.LastError ?? $"Cloud preflight không khả dụng: {ex.Message}",
                cloud.Configured,
                _options.Cloud.OrganizationId,
                _options.Cloud.UseResumableUploads ? "Resumable" : "Standard",
                _options.Cloud.AccessMode,
                cloud.Authenticated,
                false);
        }

        return new CloudSyncStatusDto(
            _options.Cloud.Enabled,
            !_options.Cloud.Enabled
                ? SyncStatus.LocalOnly
                : !preflight.Configured
                    ? SyncStatus.Failed
                    : !preflight.CanSynchronize
                        ? SyncStatus.Pending
                        : failed is not null
                            ? SyncStatus.Failed
                            : pending > 0
                                ? SyncStatus.Pending
                                : SyncStatus.Synced,
            pending,
            success,
            failed?.LastError
                ?? preflight.Errors.FirstOrDefault()
                ?? (!preflight.CanSynchronize
                    ? "Cần đăng nhập Supabase bằng tài khoản Admin/Teacher thuộc đúng organization."
                    : null),
            preflight.Configured,
            preflight.OrganizationId,
            preflight.UploadStrategy,
            preflight.AccessMode,
            preflight.Authenticated,
            preflight.CanSynchronize);
    }

    public async Task TriggerCloudSyncAsync(CancellationToken cancellationToken)
    {
        if (!_options.Cloud.Enabled)
            throw new ApiException(ErrorCodes.CloudOffline, "Cloud chưa được bật.", 503);
        var preflight = await cloud.PreflightAsync(cancellationToken);
        if (!preflight.CanSynchronize)
            throw new ApiException(
                preflight.Configured
                    ? ErrorCodes.Unauthorized
                    : ErrorCodes.CloudOffline,
                preflight.Configured
                    ? "Cần đăng nhập Supabase trước khi đồng bộ."
                    : "Cấu hình Supabase chưa hoàn chỉnh.",
                preflight.Configured ? 401 : 503,
                details: preflight.Errors.Concat(preflight.Warnings).ToArray());
        var items = await db.SyncQueueSet.Where(x => x.Status == SyncStatus.Failed).ToListAsync(cancellationToken);
        foreach (var item in items) { item.Status = SyncStatus.Pending; item.NextRetryAtUtc = DateTimeOffset.UtcNow; item.LastError = null; }
        await db.SaveChangesAsync(cancellationToken);
    }

    private SettingsDto Defaults(string row) => new(
        _options.Server.Port,
        _options.Server.UseHttps,
        _options.Discovery.Enabled,
        _options.Discovery.Port,
        paths.RootPath,
        _options.Storage.MinFreeBytes,
        _options.Transfer.ChunkSizeBytes,
        _options.Transfer.MaxConcurrentUploads,
        _options.Session.HeartbeatSeconds,
        _options.Session.DisconnectAfterSeconds,
        _options.Cloud.Enabled,
        _options.Retention.TemporaryHours,
        _options.Retention.LogsDays,
        row,
        _options.Cloud.SupabaseUrl,
        _options.Cloud.EffectivePublishableKey,
        _options.Cloud.OrganizationId,
        _options.Cloud.Environment,
        _options.Cloud.UseResumableUploads,
        !string.IsNullOrWhiteSpace(_options.Cloud.ResolveSecretKey()),
        _options.Cloud.Enabled ? "PendingPreflight" : "Disabled",
        _options.Cloud.AccessMode,
        cloud.Authenticated,
        cloud.CurrentSession?.Email);
    private SettingsDto OverlayCloudRuntime(SettingsDto value) =>
        value with
        {
            CloudEnabled = _options.Cloud.Enabled,
            SupabaseUrl = _options.Cloud.SupabaseUrl,
            SupabasePublishableKey = _options.Cloud.EffectivePublishableKey,
            OrganizationId = _options.Cloud.OrganizationId,
            CloudEnvironment = _options.Cloud.Environment,
            CloudUseResumableUploads = _options.Cloud.UseResumableUploads,
            CloudSecretConfigured = !string.IsNullOrWhiteSpace(
                _options.Cloud.ResolveSecretKey()),
            CloudConfigurationStatus = !_options.Cloud.Enabled
                ? "Disabled"
                : cloud.CanSynchronize
                    ? "Ready"
                    : cloud.Configured
                        ? "AuthenticationRequired"
                        : "NotConfigured",
            CloudAccessMode = _options.Cloud.AccessMode,
            CloudAuthenticated = cloud.Authenticated,
            CloudAuthenticatedEmail = cloud.CurrentSession?.Email
        };

    private static void Validate(UpdateSettingsRequest r)
    {
        if (r.ServerPort is < 1024 or > 65535)
            throw new ApiException(ErrorCodes.ValidationFailed, "Cổng HTTP phải nằm trong 1024-65535.");
        if (r.DiscoveryPort != DiscoveryProtocol.DefaultPort)
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                $"ExamTransfer/2 yêu cầu cố định UDP {DiscoveryProtocol.DefaultPort}; không cho phép fallback hoặc đổi cổng.");
        if (r.ChunkSizeBytes is < 1048576 or > 16777216) throw new ApiException(ErrorCodes.ValidationFailed, "Chunk size phải từ 1 MiB đến 16 MiB.");
        if (r.HeartbeatSeconds is < 2 or > 60 || r.DisconnectAfterSeconds <= r.HeartbeatSeconds)
            throw new ApiException(ErrorCodes.ValidationFailed, "Cấu hình heartbeat không hợp lệ.");
        if (r.CloudEnabled)
        {
            if (!CloudAccessModes.IsValid(r.CloudAccessMode))
                throw new ApiException(
                    ErrorCodes.ValidationFailed,
                    "Cloud access mode phải là UserSession hoặc TrustedServer.");
            if (!Uri.TryCreate(r.SupabaseUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
                throw new ApiException(ErrorCodes.ValidationFailed, "Supabase URL phải là HTTPS hợp lệ.");
            if (string.IsNullOrWhiteSpace(r.SupabasePublishableKey))
                throw new ApiException(ErrorCodes.ValidationFailed, "Thiếu Supabase publishable key.");
            if (!Guid.TryParse(r.OrganizationId, out _))
                throw new ApiException(ErrorCodes.ValidationFailed, "Organization ID phải là UUID hợp lệ.");
            if (string.IsNullOrWhiteSpace(r.CloudEnvironment))
                throw new ApiException(ErrorCodes.ValidationFailed, "Cloud environment không được để trống.");
        }
    }
    private static string Version() => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";
}
