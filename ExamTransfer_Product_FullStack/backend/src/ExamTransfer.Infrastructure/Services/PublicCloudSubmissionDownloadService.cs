using System.Net.Http.Headers;
using System.Security.Cryptography;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExamTransfer.Infrastructure.Services;

public sealed class SubmissionDownloadDispatcher(
    AppDbContext db,
    IOnlyLanSubmissionDownloadService onlyLan,
    IPublicCloudSubmissionDownloadService publicCloud) : ISubmissionDownloadService
{
    public async Task<SubmissionDownloadContent> OpenAsync(
        Guid submissionId,
        Guid fileId,
        Guid actorId,
        string? actorOrganizationId,
        string? traceId,
        CancellationToken cancellationToken)
    {
        var mode = await db.SubmissionsSet.AsNoTracking()
            .Where(x => x.Id == submissionId)
            .Select(x => (SessionAccessMode?)x.Session.AccessMode)
            .SingleOrDefaultAsync(cancellationToken);

        return mode switch
        {
            SessionAccessMode.LanOnly => await onlyLan.OpenAsync(
                submissionId,
                fileId,
                actorId,
                actorOrganizationId,
                traceId,
                cancellationToken),
            SessionAccessMode.PublicCloud => await publicCloud.OpenAsync(
                submissionId,
                fileId,
                actorId,
                actorOrganizationId,
                traceId,
                cancellationToken),
            null => throw new ApiException(
                ErrorCodes.NotFound,
                "Không tìm thấy file bài nộp hợp lệ.",
                404),
            _ => throw new ApiException(
                ErrorCodes.InvalidStateTransition,
                "Chế độ lưu trữ bài nộp không được hỗ trợ.",
                409)
        };
    }
}

public sealed class PublicCloudSubmissionDownloadService(
    AppDbContext db,
    IStoragePaths paths,
    ICloudAdapter cloud,
    ILogger<PublicCloudSubmissionDownloadService> logger)
    : IPublicCloudSubmissionDownloadService
{
    private const string PublicSubmissionBucket = "public-submission-archives";
    private const string PublicSubmissionNamespace = "public-submissions";
    private const int BufferSize = 128 * 1024;
    private static readonly object CacheLocksSync = new();
    private static readonly Dictionary<string, CacheLockEntry> CacheLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<SubmissionDownloadContent> OpenAsync(
        Guid submissionId,
        Guid fileId,
        Guid actorId,
        string? actorOrganizationId,
        string? traceId,
        CancellationToken cancellationToken)
    {
        var actor = await db.UsersSet.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == actorId, cancellationToken);
        if (actor is null
            || !actor.IsActive
            || actor.Role is not (UserRole.Teacher or UserRole.Admin))
        {
            throw Denied(actorId, null, submissionId, traceId);
        }

        var submission = await db.SubmissionsSet.AsNoTracking()
            .Include(x => x.Files)
            .Include(x => x.Participant)
            .Include(x => x.Session)
            .ThenInclude(x => x.Exam)
            .SingleOrDefaultAsync(x => x.Id == submissionId, cancellationToken);
        if (submission is null)
            throw NotFound(actorId, null, submissionId, traceId);

        var sessionId = submission.SessionId;
        if (submission.Session.AccessMode != SessionAccessMode.PublicCloud
            || submission.Session.Id != sessionId
            || submission.ParticipantId != submission.Participant.Id
            || submission.Participant.SessionId != sessionId
            || !submission.IsOfficial
            || !SubmissionStatePolicy.IsCompletedSubmissionStatus(submission.Status)
            || !string.Equals(submission.SourceMode, "PublicCloud", StringComparison.OrdinalIgnoreCase)
            || submission.Files.Count != StudentSubmissionPolicy.MaxFileCount)
        {
            throw NotFound(actorId, sessionId, submissionId, traceId);
        }

        var file = submission.Files.Single();
        if (file.Id != fileId
            || file.SubmissionId != submissionId
            || file.TransferStatus != TransferStatus.Completed
            || !string.Equals(file.SourceMode, "PublicCloud", StringComparison.OrdinalIgnoreCase)
            || file.SizeBytes <= 0
            || !IsSha256(file.Sha256))
        {
            throw NotFound(actorId, sessionId, submissionId, traceId);
        }

        var organizationId = await ResolveAuthorizedOrganizationAsync(
            actor,
            actorOrganizationId,
            submission.Session.Exam.CreatedBy,
            sessionId,
            submissionId,
            traceId,
            cancellationToken);
        var cloudObjectPath = ResolveAuthoritativeCloudObjectPath(
            file.CloudObjectPath,
            organizationId,
            submission.Participant.UserId,
            submissionId,
            fileId,
            actorId,
            sessionId,
            traceId);

        string cacheRoot;
        string cachePath;
        try
        {
            (cacheRoot, cachePath) = PrepareCachePath(
                paths,
                sessionId,
                submission.ParticipantId,
                submissionId,
                fileId);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            logger.LogWarning(
                "PublicCloud submission download CachePathRejected. ActorId={ActorId}; SessionId={SessionId}; SubmissionId={SubmissionId}; TraceId={TraceId}",
                actorId,
                sessionId,
                submissionId,
                traceId);
            throw NotFound(actorId, sessionId, submissionId, traceId);
        }

        using var cacheLock = await AcquireCacheLockAsync(cachePath, cancellationToken);
        try
        {
            var cached = await TryOpenVerifiedAsync(
                cacheRoot,
                cachePath,
                file.SizeBytes,
                file.Sha256,
                cancellationToken);
            if (cached is not null)
            {
                logger.LogInformation(
                    "PublicCloud submission download CacheHit. ActorId={ActorId}; SessionId={SessionId}; SubmissionId={SubmissionId}; TraceId={TraceId}",
                    actorId,
                    sessionId,
                    submissionId,
                    traceId);
                return BuildContent(cached, file, submissionId);
            }

            DeleteInvalidCache(cacheRoot, cachePath);
            var tempPath = Path.Combine(
                Path.GetDirectoryName(cachePath)!,
                $"{fileId:N}.tmp.{Guid.NewGuid():N}");
            try
            {
                await DownloadToTempAsync(
                    cloudObjectPath,
                    tempPath,
                    actorId,
                    sessionId,
                    submissionId,
                    traceId,
                    cancellationToken);

                await using (var verifiedTemp = await TryOpenVerifiedAsync(
                    cacheRoot,
                    tempPath,
                    file.SizeBytes,
                    file.Sha256,
                    cancellationToken))
                {
                    if (verifiedTemp is null)
                    {
                        logger.LogError(
                            "PublicCloud submission download IntegrityMismatch. Code={Code}; ActorId={ActorId}; SessionId={SessionId}; SubmissionId={SubmissionId}; TraceId={TraceId}",
                            ErrorCodes.HashMismatch,
                            actorId,
                            sessionId,
                            submissionId,
                            traceId);
                        throw new ApiException(
                            ErrorCodes.HashMismatch,
                            "File tải từ PublicCloud không khớp metadata xác thực.",
                            502);
                    }
                }

                FileStream? finalStream = null;
                try
                {
                    File.Move(tempPath, cachePath, overwrite: false);
                }
                catch (IOException) when (File.Exists(cachePath))
                {
                    finalStream = await TryOpenVerifiedAsync(
                        cacheRoot,
                        cachePath,
                        file.SizeBytes,
                        file.Sha256,
                        cancellationToken);
                    if (finalStream is null)
                        File.Move(tempPath, cachePath, overwrite: true);
                }

                finalStream ??= await TryOpenVerifiedAsync(
                    cacheRoot,
                    cachePath,
                    file.SizeBytes,
                    file.Sha256,
                    cancellationToken);
                if (finalStream is null)
                    throw new IOException("Verified cache promotion did not produce a readable final file.");

                logger.LogInformation(
                    "PublicCloud submission download Success. ActorId={ActorId}; SessionId={SessionId}; SubmissionId={SubmissionId}; TraceId={TraceId}",
                    actorId,
                    sessionId,
                    submissionId,
                    traceId);
                return BuildContent(finalStream, file, submissionId);
            }
            finally
            {
                DeleteTempFile(tempPath, actorId, sessionId, submissionId, traceId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            logger.LogError(
                "PublicCloud submission download CacheFailure. Code={Code}; ActorId={ActorId}; SessionId={SessionId}; SubmissionId={SubmissionId}; TraceId={TraceId}",
                ErrorCodes.CloudUploadFailed,
                actorId,
                sessionId,
                submissionId,
                traceId);
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                "Không thể chuẩn bị file tải PublicCloud.",
                502);
        }
    }

    internal static string GetCacheFilePath(
        IStoragePaths storagePaths,
        Guid sessionId,
        Guid participantId,
        Guid submissionId,
        Guid fileId) =>
        PrepareCachePath(storagePaths, sessionId, participantId, submissionId, fileId).CachePath;

    private async Task DownloadToTempAsync(
        string cloudObjectPath,
        string tempPath,
        Guid actorId,
        Guid sessionId,
        Guid submissionId,
        string? traceId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var destination = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await cloud.DownloadObjectToAsync(
                cloudObjectPath,
                destination,
                cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ApiException
                or HttpRequestException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            logger.LogWarning(
                "PublicCloud submission download CloudFailure. Code={Code}; ActorId={ActorId}; SessionId={SessionId}; SubmissionId={SubmissionId}; TraceId={TraceId}",
                ErrorCodes.CloudUploadFailed,
                actorId,
                sessionId,
                submissionId,
                traceId);
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                "Không thể tải file bài nộp từ PublicCloud.",
                502);
        }
    }

    private async Task<string> ResolveAuthorizedOrganizationAsync(
        User actor,
        string? actorOrganizationId,
        Guid? ownerId,
        Guid sessionId,
        Guid submissionId,
        string? traceId,
        CancellationToken cancellationToken)
    {
        if (!ownerId.HasValue)
            throw Denied(actor.Id, sessionId, submissionId, traceId);

        var ownerOrganizationId = await db.UsersSet.AsNoTracking()
            .Where(x => x.Id == ownerId.Value && x.IsActive)
            .Select(x => x.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(actorOrganizationId)
            || string.IsNullOrWhiteSpace(actor.OrganizationId)
            || !string.Equals(actor.OrganizationId, actorOrganizationId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(ownerOrganizationId)
            || !string.Equals(ownerOrganizationId, actorOrganizationId, StringComparison.Ordinal))
        {
            throw Denied(actor.Id, sessionId, submissionId, traceId);
        }

        return actorOrganizationId;
    }

    private string ResolveAuthoritativeCloudObjectPath(
        string? objectKey,
        string organizationId,
        Guid? participantUserId,
        Guid submissionId,
        Guid fileId,
        Guid actorId,
        Guid sessionId,
        string? traceId)
    {
        var parts = objectKey?.Split('/', StringSplitOptions.None) ?? [];
        if (!Guid.TryParse(organizationId, out var expectedOrganizationId)
            || !participantUserId.HasValue
            || parts.Length != 5
            || !Guid.TryParse(parts[0], out var objectOrganizationId)
            || objectOrganizationId != expectedOrganizationId
            || !string.Equals(parts[1], PublicSubmissionNamespace, StringComparison.Ordinal)
            || !Guid.TryParse(parts[2], out var objectUserId)
            || objectUserId != participantUserId.Value
            || !Guid.TryParse(parts[3], out var objectSubmissionId)
            || objectSubmissionId != submissionId
            || !IsExpectedObjectFileName(parts[4], fileId)
            || objectKey!.Contains('\0')
            || objectKey.Contains('\\')
            || objectKey.Contains("..", StringComparison.Ordinal))
        {
            logger.LogWarning(
                "PublicCloud submission download ObjectKeyRejected. ActorId={ActorId}; SessionId={SessionId}; SubmissionId={SubmissionId}; TraceId={TraceId}",
                actorId,
                sessionId,
                submissionId,
                traceId);
            throw NotFound(actorId, sessionId, submissionId, traceId);
        }

        return $"{PublicSubmissionBucket}/{objectKey}";
    }

    private static bool IsExpectedObjectFileName(string value, Guid fileId)
    {
        var extension = Path.GetExtension(value);
        return extension.Length is >= 2 and <= 16
            && extension[1..].All(char.IsAsciiLetterOrDigit)
            && Guid.TryParse(Path.GetFileNameWithoutExtension(value), out var parsed)
            && parsed == fileId;
    }

    private static (string CacheRoot, string CachePath) PrepareCachePath(
        IStoragePaths storagePaths,
        Guid sessionId,
        Guid participantId,
        Guid submissionId,
        Guid fileId)
    {
        var storageRoot = Path.GetFullPath(storagePaths.RootPath);
        var cacheRoot = Path.GetFullPath(Path.Combine(
            storageRoot,
            "cloud-cache",
            "public-submissions"));
        var cacheDirectory = Path.GetFullPath(Path.Combine(
            cacheRoot,
            sessionId.ToString("N"),
            participantId.ToString("N"),
            submissionId.ToString("N")));
        var cachePath = Path.GetFullPath(Path.Combine(
            cacheDirectory,
            $"{fileId:N}.cache"));

        if (!IsStrictDescendant(storageRoot, cacheRoot)
            || !IsStrictDescendant(cacheRoot, cacheDirectory)
            || !IsStrictDescendant(cacheRoot, cachePath))
        {
            throw new IOException("PublicCloud cache path escaped its configured root.");
        }

        Directory.CreateDirectory(cacheDirectory);
        SubmissionDownloadPathResolver.RejectReparsePoints(storageRoot, cacheDirectory);
        if (File.Exists(cachePath))
            SubmissionDownloadPathResolver.RejectReparsePoints(cacheRoot, cachePath);
        return (cacheRoot, cachePath);
    }

    private static async Task<FileStream?> TryOpenVerifiedAsync(
        string cacheRoot,
        string path,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;
        SubmissionDownloadPathResolver.RejectReparsePoints(cacheRoot, path);

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != expectedSize)
            {
                await stream.DisposeAsync();
                return null;
            }

            using var sha256 = SHA256.Create();
            var actualHash = await sha256.ComputeHashAsync(stream, cancellationToken);
            var expectedHash = Convert.FromHexString(expectedSha256);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                await stream.DisposeAsync();
                return null;
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            if (stream is not null)
                await stream.DisposeAsync();
            throw;
        }
    }

    private static void DeleteInvalidCache(string cacheRoot, string cachePath)
    {
        if (!File.Exists(cachePath))
            return;
        SubmissionDownloadPathResolver.RejectReparsePoints(cacheRoot, cachePath);
        File.Delete(cachePath);
    }

    private void DeleteTempFile(
        string tempPath,
        Guid actorId,
        Guid sessionId,
        Guid submissionId,
        string? traceId)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            logger.LogError(
                "PublicCloud submission download TempCleanupFailed. ActorId={ActorId}; SessionId={SessionId}; SubmissionId={SubmissionId}; TraceId={TraceId}",
                actorId,
                sessionId,
                submissionId,
                traceId);
        }
    }

    private static SubmissionDownloadContent BuildContent(
        FileStream stream,
        SubmissionFile file,
        Guid submissionId) =>
        new(
            stream,
            SafeContentType(file.MimeType),
            OnlyLanSubmissionDownloadService.SanitizeDownloadName(
                file.OriginalName,
                submissionId,
                file.StoredName));

    private static string SafeContentType(string? mimeType) =>
        MediaTypeHeaderValue.TryParse(mimeType, out var parsed)
            ? parsed.ToString()
            : "application/octet-stream";

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsStrictDescendant(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase)
            && normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException;

    private ApiException Denied(
        Guid actorId,
        Guid? sessionId,
        Guid submissionId,
        string? traceId)
    {
        logger.LogWarning(
            "PublicCloud submission download Denied. ActorId={ActorId}; SessionId={SessionId}; SubmissionId={SubmissionId}; TraceId={TraceId}",
            actorId,
            sessionId,
            submissionId,
            traceId);
        return new ApiException(
            ErrorCodes.Forbidden,
            "Không được tải file bài nộp này.",
            403);
    }

    private ApiException NotFound(
        Guid actorId,
        Guid? sessionId,
        Guid submissionId,
        string? traceId)
    {
        logger.LogWarning(
            "PublicCloud submission download NotFound. ActorId={ActorId}; SessionId={SessionId}; SubmissionId={SubmissionId}; TraceId={TraceId}",
            actorId,
            sessionId,
            submissionId,
            traceId);
        return new ApiException(
            ErrorCodes.NotFound,
            "Không tìm thấy file bài nộp hợp lệ.",
            404);
    }

    private static async Task<CacheLockLease> AcquireCacheLockAsync(
        string cachePath,
        CancellationToken cancellationToken)
    {
        CacheLockEntry entry;
        lock (CacheLocksSync)
        {
            if (!CacheLocks.TryGetValue(cachePath, out entry!))
            {
                entry = new CacheLockEntry();
                CacheLocks.Add(cachePath, entry);
            }
            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
            return new CacheLockLease(cachePath, entry);
        }
        catch
        {
            ReleaseCacheLockReference(cachePath, entry);
            throw;
        }
    }

    private static void ReleaseCacheLockReference(string cachePath, CacheLockEntry entry)
    {
        lock (CacheLocksSync)
        {
            entry.References--;
            if (entry.References == 0)
            {
                CacheLocks.Remove(cachePath);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class CacheLockEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    private sealed class CacheLockLease(string cachePath, CacheLockEntry entry) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            entry.Semaphore.Release();
            ReleaseCacheLockReference(cachePath, entry);
        }
    }
}
