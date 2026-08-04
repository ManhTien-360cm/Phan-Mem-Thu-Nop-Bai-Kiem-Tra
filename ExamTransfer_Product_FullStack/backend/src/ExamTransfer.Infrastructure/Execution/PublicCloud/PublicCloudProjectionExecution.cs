using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Execution.PublicCloud;

public sealed class PublicCloudProjectionExecution(
    AppDbContext db,
    ICloudSyncSignal? cloudSyncSignal = null)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<CloudProjectionReadiness> GetProjectionReadinessAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var session = await db.ExamSessionsSet
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.Id, x.AccessMode })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
        if (session.AccessMode != SessionAccessMode.PublicCloud)
            return new(
                id,
                false,
                true,
                SyncStatus.LocalOnly,
                "LAN_ONLY",
                "Phiên LAN không cần PublicCloud projection.",
                0);

        var projectionItems = await db.SyncQueueSet
            .AsNoTracking()
            .Where(x => x.EntityType == "exam_sessions" && x.EntityId == id.ToString())
            .ToListAsync(cancellationToken);
        var item = projectionItems
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault();
        if (item is null)
            return new(
                id,
                true,
                false,
                SyncStatus.Pending,
                "PUBLICCLOUD_PROJECTION_PENDING",
                "Phòng đang chờ đồng bộ PublicCloud.",
                0);

        if (IsRoomCodeConflict(item))
            return new(
                id,
                true,
                false,
                SyncStatus.Conflict,
                ErrorCodes.RoomCodeConflict,
                "Mã phòng PublicCloud đang được sử dụng trong tổ chức. Hãy nhập mã khác hoặc để trống để sinh mã mới.",
                item.RetryCount);

        var failure = ParseFailure(item.LastError);
        return item.Status switch
        {
            SyncStatus.Synced => new(
                id,
                true,
                true,
                item.Status,
                "PUBLICCLOUD_PROJECTION_READY",
                "Sẵn sàng — có thể chia sẻ mã phòng.",
                item.RetryCount),
            SyncStatus.Failed or SyncStatus.Conflict => new(
                id,
                true,
                false,
                item.Status,
                failure?.Code ?? "PUBLICCLOUD_PROJECTION_FAILED",
                failure is null
                    ? "Đồng bộ PublicCloud thất bại — dữ liệu cục bộ vẫn được giữ. Hãy thử lại."
                    : $"Đồng bộ PublicCloud thất bại ({failure.Code}) — dữ liệu cục bộ vẫn được giữ.",
                item.RetryCount),
            _ => new(
                id,
                true,
                false,
                item.Status,
                "PUBLICCLOUD_PROJECTION_SYNCING",
                "Đang đồng bộ PublicCloud.",
                item.RetryCount)
        };
    }

    public async Task<CloudProjectionReadiness> RetryProjectionAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var session = await db.ExamSessionsSet
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.AccessMode })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
        if (session.AccessMode != SessionAccessMode.PublicCloud)
            return await GetProjectionReadinessAsync(id, cancellationToken);

        var projectionItems = await db.SyncQueueSet
            .Where(x => x.EntityType == "exam_sessions" && x.EntityId == id.ToString())
            .ToListAsync(cancellationToken);
        var item = projectionItems
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault()
            ?? throw new ApiException(
                ErrorCodes.Conflict,
                "Không tìm thấy outbox PublicCloud của phòng thi; dữ liệu cục bộ không bị thay đổi.",
                409);
        if (IsRoomCodeConflict(item))
            return await GetProjectionReadinessAsync(id, cancellationToken);
        if (item.Status != SyncStatus.Synced)
        {
            item.Status = SyncStatus.Pending;
            item.LastError = null;
            item.LeaseUntilUtc = null;
            item.NextRetryAtUtc = DateTimeOffset.UtcNow;
            item.CompletedAtUtc = null;
            await db.SaveChangesAsync(cancellationToken);
            cloudSyncSignal?.Pulse();
        }
        return await GetProjectionReadinessAsync(id, cancellationToken);
    }

    internal static bool IsRoomCodeConflict(SyncQueueItem item) =>
        item.Status == SyncStatus.Conflict
        && string.Equals(
            ParseFailure(item.LastError)?.Code,
            ErrorCodes.RoomCodeConflict,
            StringComparison.Ordinal);

    private static CloudSyncFailure? ParseFailure(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return JsonSerializer.Deserialize<CloudSyncFailure>(value, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
