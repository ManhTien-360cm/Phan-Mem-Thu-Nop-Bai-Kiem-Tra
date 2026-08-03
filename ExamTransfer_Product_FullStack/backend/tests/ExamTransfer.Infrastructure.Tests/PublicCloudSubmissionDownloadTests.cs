using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Diagnostics;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.LocalServer.Controllers;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class PublicCloudSubmissionDownloadTests
{
    [Fact]
    public async Task AuthorizedTeacherDownloadsExactPublicCloudFileWithSafeMetadata()
    {
        await using var fixture = await DownloadFixture.CreateAsync();

        var download = await fixture.OpenAsync();
        var bytes = await ReadAndDisposeAsync(download.Content);

        Assert.Equal(fixture.ExpectedBytes, bytes);
        Assert.Equal("answer.zip", download.DownloadName);
        Assert.Equal("application/zip", download.MimeType);
        Assert.Equal(1, fixture.Cloud.DownloadCount);
        Assert.Equal(
            $"public-submission-archives/{fixture.File.CloudObjectPath}",
            fixture.Cloud.LastObjectPath);
    }

    [Fact]
    public async Task EndpointRequiresTeacherOrAdminAndReturnsRangeEnabledStream()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        var method = typeof(SubmissionsController).GetMethod(
            nameof(SubmissionsController.FileContent),
            BindingFlags.Instance | BindingFlags.Public)!;
        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal("TeacherOrAdmin", authorize.Policy);
        Assert.Equal(
            [typeof(Guid), typeof(Guid), typeof(CancellationToken)],
            method.GetParameters().Select(x => x.ParameterType).ToArray());

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, fixture.Owner.Id.ToString()),
                new Claim(ClaimTypes.Role, UserRole.Teacher.ToString()),
                new Claim("organization_id", fixture.Owner.OrganizationId!)
            ],
            "test");
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            TraceIdentifier = "public-cloud-controller"
        };
        var controller = new SubmissionsController(null!, fixture.Service)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var action = await controller.FileContent(
            fixture.Submission.Id,
            fixture.File.Id,
            CancellationToken.None);

        var result = Assert.IsType<FileStreamResult>(action);
        Assert.True(result.EnableRangeProcessing);
        Assert.Equal("answer.zip", result.FileDownloadName);
        Assert.Equal(fixture.ExpectedBytes, await ReadAndDisposeAsync(result.FileStream));
    }

    [Fact]
    public async Task DifferentOrganizationTeacherIsDenied()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        var stranger = await fixture.AddUserAsync(UserRole.Teacher, Guid.NewGuid().ToString());

        await AssertStatusAsync(403, () => fixture.OpenAsAsync(stranger));
        Assert.Equal(0, fixture.Cloud.DownloadCount);
    }

    [Fact]
    public async Task OrganizationClaimMismatchIsDenied()
    {
        await using var fixture = await DownloadFixture.CreateAsync();

        await AssertStatusAsync(
            403,
            () => fixture.Service.OpenAsync(
                fixture.Submission.Id,
                fixture.File.Id,
                fixture.Owner.Id,
                Guid.NewGuid().ToString(),
                "claim-mismatch",
                CancellationToken.None));
        Assert.Equal(0, fixture.Cloud.DownloadCount);
    }

    [Fact]
    public async Task StudentActorIsDeniedByServiceDefenseInDepth()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        var actor = await fixture.AddUserAsync(UserRole.Student, fixture.Owner.OrganizationId);

        await AssertStatusAsync(403, () => fixture.OpenAsAsync(actor));
        Assert.Equal(0, fixture.Cloud.DownloadCount);
    }

    [Fact]
    public async Task MissingActorIsDeniedLikeAnonymousRequest()
    {
        await using var fixture = await DownloadFixture.CreateAsync();

        await AssertStatusAsync(
            403,
            () => fixture.Service.OpenAsync(
                fixture.Submission.Id,
                fixture.File.Id,
                Guid.Empty,
                null,
                "anonymous",
                CancellationToken.None));
    }

    [Fact]
    public async Task OnlyLanSessionIsRejectedByPublicCloudService()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        fixture.Session.AccessMode = SessionAccessMode.LanOnly;
        await fixture.Db.SaveChangesAsync();

        await AssertStatusAsync(404, () => fixture.OpenAsync());
        Assert.Equal(0, fixture.Cloud.DownloadCount);
    }

    [Fact]
    public async Task ParticipantFromAnotherSessionIsRejected()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        var other = new ExamSession
        {
            ExamId = fixture.Exam.Id,
            RoomCode = $"OTHER-{Guid.NewGuid():N}",
            AccessMode = SessionAccessMode.PublicCloud
        };
        fixture.Db.ExamSessionsSet.Add(other);
        fixture.Participant.SessionId = other.Id;
        await fixture.Db.SaveChangesAsync();

        await AssertStatusAsync(404, () => fixture.OpenAsync());
        Assert.Equal(0, fixture.Cloud.DownloadCount);
    }

    [Fact]
    public async Task FileFromAnotherSubmissionIsRejected()
    {
        await using var fixture = await DownloadFixture.CreateAsync();

        await AssertStatusAsync(
            404,
            () => fixture.Service.OpenAsync(
                fixture.Submission.Id,
                Guid.NewGuid(),
                fixture.Owner.Id,
                fixture.Owner.OrganizationId,
                "wrong-file",
                CancellationToken.None));
        Assert.Equal(0, fixture.Cloud.DownloadCount);
    }

    [Fact]
    public async Task NonOfficialOrIncompleteSubmissionIsRejected()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        fixture.Submission.IsOfficial = false;
        await fixture.Db.SaveChangesAsync();

        await AssertStatusAsync(404, () => fixture.OpenAsync());
        Assert.Equal(0, fixture.Cloud.DownloadCount);
    }

    [Fact]
    public async Task InvalidAuthoritativeObjectNamespaceIsRejectedBeforeCloudAccess()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        fixture.File.CloudObjectPath =
            $"{fixture.Owner.OrganizationId}/public-submissions/{fixture.Participant.UserId}/{Guid.NewGuid()}/{fixture.File.Id}.zip";
        await fixture.Db.SaveChangesAsync();

        await AssertStatusAsync(404, () => fixture.OpenAsync());
        Assert.Equal(0, fixture.Cloud.DownloadCount);
    }

    [Fact]
    public async Task ValidCacheSkipsCloudAndReturnsVerifiedBytes()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        await fixture.SeedCacheAsync(fixture.ExpectedBytes);

        var download = await fixture.OpenAsync();

        Assert.Equal(fixture.ExpectedBytes, await ReadAndDisposeAsync(download.Content));
        Assert.Equal(0, fixture.Cloud.DownloadCount);
    }

    [Fact]
    public async Task WrongSizeCacheIsDeletedAndDownloadedAgain()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        await fixture.SeedCacheAsync([1]);

        var download = await fixture.OpenAsync();

        Assert.Equal(fixture.ExpectedBytes, await ReadAndDisposeAsync(download.Content));
        Assert.Equal(1, fixture.Cloud.DownloadCount);
        Assert.Equal(fixture.ExpectedBytes, await File.ReadAllBytesAsync(fixture.CachePath));
    }

    [Fact]
    public async Task WrongHashCacheIsDeletedAndDownloadedAgain()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        await fixture.SeedCacheAsync(Enumerable.Repeat((byte)0x5A, fixture.ExpectedBytes.Length).ToArray());

        var download = await fixture.OpenAsync();

        Assert.Equal(fixture.ExpectedBytes, await ReadAndDisposeAsync(download.Content));
        Assert.Equal(1, fixture.Cloud.DownloadCount);
    }

    [Fact]
    public async Task MissingCacheDownloadsThroughUniqueTempAndPromotesVerifiedFile()
    {
        await using var fixture = await DownloadFixture.CreateAsync();

        var download = await fixture.OpenAsync();
        await download.Content.DisposeAsync();

        Assert.Contains($"{fixture.File.Id:N}.tmp.", fixture.Cloud.DestinationName);
        Assert.True(File.Exists(fixture.CachePath));
        Assert.Empty(fixture.TempFiles());
    }

    [Fact]
    public async Task MidStreamCloudFailureDeletesTempAndCreatesNoFinalCache()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        fixture.Cloud.FailAfterPartialWrite = true;

        var error = await AssertStatusAsync(502, () => fixture.OpenAsync());

        Assert.Equal(ErrorCodes.CloudUploadFailed, error.Code);
        Assert.False(File.Exists(fixture.CachePath));
        Assert.Empty(fixture.TempFiles());
    }

    [Fact]
    public async Task CancellationDeletesTempAndCreatesNoFinalCache()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        fixture.Cloud.WaitForCancellation = true;
        using var cts = new CancellationTokenSource();
        var operation = fixture.OpenAsync(cts.Token);
        await fixture.Cloud.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(File.Exists(fixture.CachePath));
        Assert.Empty(fixture.TempFiles());
    }

    [Fact]
    public async Task DownloadedSizeMismatchFailsClosedAndDeletesTemp()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        fixture.Cloud.Payload = [1, 2, 3];

        var error = await AssertStatusAsync(502, () => fixture.OpenAsync());

        Assert.Equal(ErrorCodes.HashMismatch, error.Code);
        Assert.False(File.Exists(fixture.CachePath));
        Assert.Empty(fixture.TempFiles());
    }

    [Fact]
    public async Task DownloadedHashMismatchFailsClosedAndDeletesTemp()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        fixture.Cloud.Payload = Enumerable.Repeat((byte)0x44, fixture.ExpectedBytes.Length).ToArray();

        var error = await AssertStatusAsync(502, () => fixture.OpenAsync());

        Assert.Equal(ErrorCodes.HashMismatch, error.Code);
        Assert.False(File.Exists(fixture.CachePath));
        Assert.Empty(fixture.TempFiles());
    }

    [Fact]
    public async Task ConcurrentDownloadsProduceOneVerifiedFinalWithoutTempFiles()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        fixture.Cloud.BlockDownload = true;
        var first = fixture.OpenAsync();
        await fixture.Cloud.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = fixture.OpenAsync();

        fixture.Cloud.ReleaseDownload.TrySetResult();
        var results = await Task.WhenAll(first, second);

        foreach (var result in results)
            Assert.Equal(fixture.ExpectedBytes, await ReadAndDisposeAsync(result.Content));
        Assert.Equal(1, fixture.Cloud.DownloadCount);
        Assert.Equal(fixture.ExpectedBytes, await File.ReadAllBytesAsync(fixture.CachePath));
        Assert.Empty(fixture.TempFiles());
    }

    [Fact]
    public async Task ValidFinalCacheSurvivesLaterCloudFailureMode()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        await fixture.SeedCacheAsync(fixture.ExpectedBytes);
        fixture.Cloud.FailAfterPartialWrite = true;

        var download = await fixture.OpenAsync();

        Assert.Equal(fixture.ExpectedBytes, await ReadAndDisposeAsync(download.Content));
        Assert.Equal(0, fixture.Cloud.DownloadCount);
        Assert.Equal(fixture.ExpectedBytes, await File.ReadAllBytesAsync(fixture.CachePath));
    }

    [Fact]
    public async Task CachePathIsCanonicalAndIgnoresDatabaseOnlyLanRelativePath()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        fixture.File.RelativePath = @"..\..\client-controlled.zip";
        await fixture.Db.SaveChangesAsync();

        var download = await fixture.OpenAsync();
        await download.Content.DisposeAsync();

        Assert.StartsWith(
            Path.GetFullPath(fixture.Paths.RootPath) + Path.DirectorySeparatorChar,
            Path.GetFullPath(fixture.CachePath),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            $"public-submission-archives/{fixture.File.CloudObjectPath}",
            fixture.Cloud.LastObjectPath);
    }

    [Fact]
    public async Task ReparsePointCacheIsRejectedWithoutCloudFallback()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        var cachePath = fixture.CachePath;
        var outsideDirectory = Path.Combine(fixture.Root, "outside-cache");
        Directory.CreateDirectory(outsideDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(outsideDirectory, Path.GetFileName(cachePath)),
            fixture.ExpectedBytes);
        var cacheDirectory = Path.GetDirectoryName(cachePath)!;
        Directory.Delete(cacheDirectory);
        if (!TryCreateDirectoryLink(cacheDirectory, outsideDirectory))
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Test environment cannot create a symbolic link or junction.");
        }

        await AssertStatusAsync(404, () => fixture.OpenAsync());
        Assert.Equal(0, fixture.Cloud.DownloadCount);
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows())
                return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(link);
            startInfo.ArgumentList.Add(target);
            using var process = Process.Start(startInfo);
            process?.WaitForExit();
            return process?.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Fact]
    public async Task DispatcherUsesPublicCloudHandlerAndNeverFallsBackToOnlyLan()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        fixture.Cloud.FailWithSensitiveMessage = true;
        var onlyLan = new RecordingOnlyLanDownloadService();
        var dispatcher = new SubmissionDownloadDispatcher(fixture.Db, onlyLan, fixture.Service);

        var error = await AssertStatusAsync(
            502,
            () => dispatcher.OpenAsync(
                fixture.Submission.Id,
                fixture.File.Id,
                fixture.Owner.Id,
                fixture.Owner.OrganizationId,
                "dispatcher",
                CancellationToken.None));

        Assert.Equal(ErrorCodes.CloudUploadFailed, error.Code);
        Assert.Equal(0, onlyLan.CallCount);
        Assert.DoesNotContain("service_role", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", error.Message, StringComparison.OrdinalIgnoreCase);
        var logs = string.Join(Environment.NewLine, fixture.Logger.Messages);
        Assert.DoesNotContain("service_role", logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", logs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsafeFilenameAndMimeTypeAreSanitizedWithoutChangingBytes()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        fixture.File.OriginalName = "folder/CON\r\nanswer.zip";
        fixture.File.MimeType = "application/zip\r\nX-Injected: yes";
        await fixture.Db.SaveChangesAsync();

        var download = await fixture.OpenAsync();

        Assert.DoesNotContain('\r', download.DownloadName);
        Assert.DoesNotContain('\n', download.DownloadName);
        Assert.DoesNotContain('/', download.DownloadName);
        Assert.Equal("application/octet-stream", download.MimeType);
        Assert.Equal(fixture.ExpectedBytes, await ReadAndDisposeAsync(download.Content));
    }

    private static async Task<byte[]> ReadAndDisposeAsync(Stream stream)
    {
        await using (stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            return buffer.ToArray();
        }
    }

    private static async Task<ApiException> AssertStatusAsync(
        int expectedStatus,
        Func<Task<SubmissionDownloadContent>> action)
    {
        var exception = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(expectedStatus, exception.StatusCode);
        return exception;
    }

    private sealed class DownloadFixture : IAsyncDisposable
    {
        private DownloadFixture(
            string root,
            AppDbContext db,
            TestStoragePaths paths,
            User owner,
            Exam exam,
            ExamSession session,
            SessionParticipant participant,
            Submission submission,
            SubmissionFile file,
            byte[] expectedBytes,
            FakeCloudAdapter cloud,
            ListLogger<PublicCloudSubmissionDownloadService> logger)
        {
            Root = root;
            Db = db;
            Paths = paths;
            Owner = owner;
            Exam = exam;
            Session = session;
            Participant = participant;
            Submission = submission;
            File = file;
            ExpectedBytes = expectedBytes;
            Cloud = cloud;
            Logger = logger;
            Service = new(db, paths, cloud, logger);
        }

        public string Root { get; }
        public AppDbContext Db { get; }
        public TestStoragePaths Paths { get; }
        public User Owner { get; }
        public Exam Exam { get; }
        public ExamSession Session { get; }
        public SessionParticipant Participant { get; }
        public Submission Submission { get; }
        public SubmissionFile File { get; }
        public byte[] ExpectedBytes { get; }
        public FakeCloudAdapter Cloud { get; }
        public ListLogger<PublicCloudSubmissionDownloadService> Logger { get; }
        public PublicCloudSubmissionDownloadService Service { get; }
        public string CachePath => PublicCloudSubmissionDownloadService.GetCacheFilePath(
            Paths,
            Session.Id,
            Participant.Id,
            Submission.Id,
            File.Id);

        public static async Task<DownloadFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "ExamTransfer.Tests",
                "PublicCloudSubmissionDownload",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={Path.Combine(root, "download.db")}")
                    .Options);
            await db.Database.EnsureCreatedAsync();
            var paths = new TestStoragePaths(Path.Combine(root, "storage"));
            Directory.CreateDirectory(paths.RootPath);

            var organizationId = Guid.NewGuid();
            var owner = new User
            {
                Username = $"owner-{Guid.NewGuid():N}",
                DisplayName = "Owner",
                Role = UserRole.Teacher,
                OrganizationId = organizationId.ToString(),
                IsActive = true
            };
            var exam = new Exam
            {
                Title = "PublicCloud download",
                Subject = "Security",
                DurationMinutes = 30,
                DeliveryType = ExamDeliveryType.FileSubmission,
                Status = ExamStatus.Published,
                CreatedBy = owner.Id
            };
            var session = new ExamSession
            {
                ExamId = exam.Id,
                RoomCode = $"PUBLIC-{Guid.NewGuid():N}",
                AccessMode = SessionAccessMode.PublicCloud,
                Status = SessionStatus.Finished
            };
            var participant = new SessionParticipant
            {
                SourceMode = "PublicCloud",
                SessionId = session.Id,
                UserId = Guid.NewGuid(),
                StudentCode = "SV001",
                DisplayName = "Student One",
                DeviceId = "device",
                MachineName = "machine",
                AppVersion = "test",
                Status = ParticipantStatus.Approved
            };
            var submission = new Submission
            {
                SourceMode = "PublicCloud",
                SessionId = session.Id,
                ParticipantId = participant.Id,
                AttemptNumber = 1,
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                Status = SubmissionStatus.Submitted,
                ClientSubmittedAtUtc = DateTimeOffset.UtcNow,
                ServerReceivedAtUtc = DateTimeOffset.UtcNow,
                DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(30),
                IsOfficial = true
            };
            var expectedBytes = new byte[] { 80, 75, 3, 4, 1, 2, 3, 4, 5 };
            var file = new SubmissionFile
            {
                SourceMode = "PublicCloud",
                SubmissionId = submission.Id,
                ClientFileId = "client-file",
                OriginalName = "answer.zip",
                StoredName = $"{Guid.NewGuid():N}.zip",
                MimeType = "application/zip",
                SizeBytes = expectedBytes.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(expectedBytes)).ToLowerInvariant(),
                ChunkSizeBytes = 1024,
                TotalChunks = 1,
                TransferStatus = TransferStatus.Completed,
                SyncStatus = SyncStatus.Synced
            };
            file.CloudObjectPath =
                $"{organizationId}/public-submissions/{participant.UserId}/{submission.Id}/{file.Id}.zip";

            db.AddRange(owner, exam, session, participant, submission, file);
            await db.SaveChangesAsync();
            var cloud = new FakeCloudAdapter(expectedBytes);
            var logger = new ListLogger<PublicCloudSubmissionDownloadService>();
            return new(root, db, paths, owner, exam, session, participant, submission, file, expectedBytes, cloud, logger);
        }

        public Task<SubmissionDownloadContent> OpenAsync(CancellationToken cancellationToken = default) =>
            Service.OpenAsync(
                Submission.Id,
                File.Id,
                Owner.Id,
                Owner.OrganizationId,
                "test-trace",
                cancellationToken);

        public Task<SubmissionDownloadContent> OpenAsAsync(User actor) =>
            Service.OpenAsync(
                Submission.Id,
                File.Id,
                actor.Id,
                actor.OrganizationId,
                "test-trace",
                CancellationToken.None);

        public async Task<User> AddUserAsync(UserRole role, string? organizationId)
        {
            var user = new User
            {
                Username = $"actor-{Guid.NewGuid():N}",
                DisplayName = role.ToString(),
                Role = role,
                OrganizationId = organizationId,
                IsActive = true
            };
            Db.UsersSet.Add(user);
            await Db.SaveChangesAsync();
            return user;
        }

        public async Task SeedCacheAsync(byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await System.IO.File.WriteAllBytesAsync(CachePath, bytes);
        }

        public string[] TempFiles() =>
            Directory.Exists(Path.GetDirectoryName(CachePath)!)
                ? Directory.GetFiles(Path.GetDirectoryName(CachePath)!, $"{File.Id:N}.tmp.*")
                : [];

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Test cleanup is best effort only.
            }
        }
    }

    private sealed class FakeCloudAdapter(byte[] defaultPayload) : ICloudAdapter
    {
        private int downloadCount;

        public bool Enabled => true;
        public bool Configured => true;
        public bool Authenticated => true;
        public bool CanSynchronize => true;
        public CloudLoginResult? CurrentSession => null;
        public int DownloadCount => downloadCount;
        public byte[]? Payload { get; set; }
        public bool FailAfterPartialWrite { get; set; }
        public bool FailWithSensitiveMessage { get; set; }
        public bool WaitForCancellation { get; set; }
        public bool BlockDownload { get; set; }
        public string? LastObjectPath { get; private set; }
        public string DestinationName { get; private set; } = string.Empty;
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDownload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<CloudPreflightResult> PreflightAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudPushResult> PushAsync(SyncQueueItem item, Func<CancellationToken, Task>? checkpoint, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudLoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudLoginResult?> RefreshSessionAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LogoutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<CloudBackupDescriptor>> ListBackupsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DownloadObjectAsync(string cloudObjectPath, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task DownloadObjectToAsync(
            string cloudObjectPath,
            Stream destination,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref downloadCount);
            LastObjectPath = cloudObjectPath;
            DestinationName = Assert.IsType<FileStream>(destination).Name;
            DownloadStarted.TrySetResult();
            if (BlockDownload)
                await ReleaseDownload.Task.WaitAsync(cancellationToken);
            if (WaitForCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (FailWithSensitiveMessage)
            {
                throw new ApiException(
                    ErrorCodes.CloudUploadFailed,
                    "Bearer service_role secret at https://example.invalid/signed?token=secret",
                    502);
            }
            if (FailAfterPartialWrite)
            {
                await destination.WriteAsync(defaultPayload.AsMemory(0, 2), cancellationToken);
                throw new IOException("simulated cloud stream failure");
            }

            var payload = Payload ?? defaultPayload;
            await destination.WriteAsync(payload, cancellationToken);
        }
    }

    private sealed class RecordingOnlyLanDownloadService : IOnlyLanSubmissionDownloadService
    {
        public int CallCount { get; private set; }

        public Task<SubmissionDownloadContent> OpenAsync(
            Guid submissionId,
            Guid fileId,
            Guid actorId,
            string? actorOrganizationId,
            string? traceId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("OnlyLAN fallback must not run.");
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class TestStoragePaths(string root) : IStoragePaths
    {
        public string RootPath { get; } = Path.GetFullPath(root);
        public string DatabasePath => Path.Combine(RootPath, "database", "exam-transfer.db");
        public string BackupRoot => Path.Combine(RootPath, "database", "backups");
        public string ExportRoot => Path.Combine(RootPath, "exports");
        public string TemporaryRoot => Path.Combine(RootPath, "temporary");
        public string ExamVersionRoot(Guid examId, int version) => Path.Combine(RootPath, "exams", examId.ToString("N"), $"v{version}");
        public string SessionRoot(Guid sessionId) => Path.Combine(RootPath, "sessions", sessionId.ToString("N"));
        public string SubmissionRoot(Guid sessionId, string studentCode, Guid submissionId) => Path.Combine(SessionRoot(sessionId), "submissions", studentCode, submissionId.ToString("N"));
        public string ReceiptRoot(Guid sessionId) => Path.Combine(SessionRoot(sessionId), "receipts");
        public void EnsureCreated() => Directory.CreateDirectory(RootPath);
    }
}
