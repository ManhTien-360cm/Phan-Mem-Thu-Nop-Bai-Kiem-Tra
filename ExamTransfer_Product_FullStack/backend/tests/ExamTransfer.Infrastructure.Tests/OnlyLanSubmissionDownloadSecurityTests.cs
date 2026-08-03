using System.Reflection;
using System.Security.Claims;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class OnlyLanSubmissionDownloadSecurityTests
{
    [Fact]
    public async Task OwnerDownloadsExactCompletedFileAsStreamWithSafeMetadata()
    {
        await using var fixture = await DownloadFixture.CreateAsync();

        var download = await fixture.OpenAsAsync(fixture.Owner);
        await using var content = download.Content;
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);

        Assert.Equal(fixture.ExpectedBytes, buffer.ToArray());
        Assert.IsType<FileStream>(content);
        Assert.Equal("answer.zip", download.DownloadName);
        Assert.Equal("application/zip", download.MimeType);
        Assert.DoesNotContain(fixture.Paths.RootPath, download.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EndpointRequiresTeacherOrAdminPolicy()
    {
        var method = typeof(SubmissionsController).GetMethod(
            nameof(SubmissionsController.FileContent),
            BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(method);
        var authorize = Assert.Single(
            method.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal("TeacherOrAdmin", authorize.Policy);
    }

    [Fact]
    public async Task AuthorizedEndpointReturnsStreamingFileResultWithExpectedHeaders()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, fixture.Owner.Id.ToString()),
                new Claim(ClaimTypes.Role, UserRole.Teacher.ToString()),
                new Claim("organization_id", fixture.Owner.OrganizationId!)
            ],
            "test");
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            TraceIdentifier = "controller-test"
        };
        var controller = new SubmissionsController(null!, fixture.Service)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var action = await controller.FileContent(
            fixture.Submission.Id,
            fixture.File.Id,
            CancellationToken.None);

        var result = Assert.IsType<FileStreamResult>(action);
        await using var content = result.FileStream;
        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        Assert.Equal("application/zip", result.ContentType);
        Assert.Equal("answer.zip", result.FileDownloadName);
        Assert.True(result.EnableRangeProcessing);
    }

    [Fact]
    public async Task SameOrganizationTeacherCanDownload()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        var colleague = await fixture.AddUserAsync(UserRole.Teacher, "org-a");

        var download = await fixture.OpenAsAsync(colleague);

        await download.Content.DisposeAsync();
    }

    [Fact]
    public async Task NonOwnerWithoutOrganizationIsDenied()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        var stranger = await fixture.AddUserAsync(UserRole.Teacher, null);

        await AssertStatusAsync(
            403,
            () => fixture.OpenAsAsync(stranger));
    }

    [Fact]
    public async Task TeacherFromDifferentOrganizationIsDenied()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        var stranger = await fixture.AddUserAsync(UserRole.Teacher, "org-b");

        await AssertStatusAsync(
            403,
            () => fixture.OpenAsAsync(stranger));
    }

    [Fact]
    public async Task StudentActorIsDeniedByServiceDefenseInDepth()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        var student = await fixture.AddUserAsync(UserRole.Student, "org-a");

        await AssertStatusAsync(
            403,
            () => fixture.OpenAsAsync(student));
    }

    [Fact]
    public async Task PublicCloudSessionIsNotServedByOnlyLanDownload()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        fixture.Session.AccessMode = SessionAccessMode.PublicCloud;
        await fixture.Db.SaveChangesAsync();

        await AssertStatusAsync(
            404,
            () => fixture.OpenAsAsync(fixture.Owner));
    }

    [Fact]
    public async Task SubmissionWhoseParticipantBelongsToAnotherSessionIsRejected()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        var otherSession = new ExamSession
        {
            ExamId = fixture.Exam.Id,
            RoomCode = $"OTHER-{Guid.NewGuid():N}",
            AccessMode = SessionAccessMode.LanOnly
        };
        fixture.Db.ExamSessionsSet.Add(otherSession);
        fixture.Participant.SessionId = otherSession.Id;
        await fixture.Db.SaveChangesAsync();

        await AssertStatusAsync(
            404,
            () => fixture.OpenAsAsync(fixture.Owner));
    }

    [Fact]
    public async Task ExactSubmissionDoesNotResolveFileFromAnotherAttempt()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        var otherSubmission = new Submission
        {
            SessionId = fixture.Session.Id,
            ParticipantId = fixture.Participant.Id,
            AttemptNumber = 2,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            Status = SubmissionStatus.Submitted,
            ClientSubmittedAtUtc = DateTimeOffset.UtcNow,
            ServerReceivedAtUtc = DateTimeOffset.UtcNow,
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(30),
            IsOfficial = true
        };
        var otherFile = fixture.FileFor(otherSubmission);
        fixture.Db.AddRange(otherSubmission, otherFile);
        await fixture.Db.SaveChangesAsync();

        await AssertStatusAsync(
            404,
            () => fixture.Service.OpenAsync(
                fixture.Submission.Id,
                otherFile.Id,
                fixture.Owner.Id,
                fixture.Owner.OrganizationId,
                "exact-submission",
                CancellationToken.None));
    }

    [Fact]
    public async Task MissingSubmissionAndMissingFileRecordFailClosed()
    {
        await using var fixture = await DownloadFixture.CreateAsync();

        await AssertStatusAsync(
            404,
            () => fixture.Service.OpenAsync(
                Guid.NewGuid(),
                fixture.File.Id,
                fixture.Owner.Id,
                fixture.Owner.OrganizationId,
                "missing-submission",
                CancellationToken.None));
        await AssertStatusAsync(
            404,
            () => fixture.Service.OpenAsync(
                fixture.Submission.Id,
                Guid.NewGuid(),
                fixture.Owner.Id,
                fixture.Owner.OrganizationId,
                "missing-file",
                CancellationToken.None));
    }

    [Fact]
    public async Task MultipleFileRecordsViolateOneArchiveInvariantAndFailClosed()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        fixture.Db.SubmissionFilesSet.Add(fixture.FileFor(fixture.Submission));
        await fixture.Db.SaveChangesAsync();

        await AssertStatusAsync(
            404,
            () => fixture.OpenAsAsync(fixture.Owner));
    }

    [Fact]
    public async Task MissingPhysicalFileReturnsControlledNotFound()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        File.Delete(fixture.PhysicalPath);

        var exception = await AssertStatusAsync(
            404,
            () => fixture.OpenAsAsync(fixture.Owner));

        Assert.DoesNotContain(fixture.Paths.RootPath, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RelativeTraversalOutsideStorageRootIsRejected()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        var outside = Path.Combine(fixture.Paths.RootPath, "..", "outside.zip");
        await File.WriteAllBytesAsync(outside, [1, 2, 3]);
        fixture.File.RelativePath = Path.Combine("..", "outside.zip");
        await fixture.Db.SaveChangesAsync();

        await AssertStatusAsync(
            404,
            () => fixture.OpenAsAsync(fixture.Owner));
    }

    [Fact]
    public async Task AbsolutePathIsRejectedEvenWhenFileExists()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        var outside = Path.Combine(fixture.Root, "absolute.zip");
        await File.WriteAllBytesAsync(outside, [1, 2, 3]);
        fixture.File.RelativePath = Path.GetFullPath(outside);
        await fixture.Db.SaveChangesAsync();

        await AssertStatusAsync(
            404,
            () => fixture.OpenAsAsync(fixture.Owner));
    }

    [Fact]
    public async Task UncPathIsRejectedBeforeAnyNetworkAccess()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        fixture.File.RelativePath = @"\\server\share\answer.zip";
        await fixture.Db.SaveChangesAsync();

        await AssertStatusAsync(
            404,
            () => fixture.OpenAsAsync(fixture.Owner));
    }

    [Fact]
    public async Task PrefixCollisionDirectoryIsNotInsideAllowedRoot()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        var evilRoot = fixture.Paths.RootPath + "-evil";
        Directory.CreateDirectory(evilRoot);
        var outside = Path.Combine(evilRoot, "answer.zip");
        await File.WriteAllBytesAsync(outside, [1, 2, 3]);
        fixture.File.RelativePath = Path.GetRelativePath(fixture.Paths.RootPath, outside);
        await fixture.Db.SaveChangesAsync();

        await AssertStatusAsync(
            404,
            () => fixture.OpenAsAsync(fixture.Owner));
    }

    [Fact]
    public async Task ReparsePointInsideSubmissionRootIsRejectedWhenEnvironmentSupportsIt()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        var outside = Path.Combine(fixture.Root, "reparse-target");
        Directory.CreateDirectory(outside);
        await File.WriteAllBytesAsync(Path.Combine(outside, "answer.zip"), [1, 2, 3]);
        var link = Path.Combine(Path.GetDirectoryName(fixture.PhysicalPath)!, "linked");
        if (!TryCreateDirectoryLink(link, outside))
            throw Xunit.Sdk.SkipException.ForSkip(
                "Test environment cannot create a symbolic link or junction.");

        var linkedFile = Path.Combine(link, "answer.zip");
        fixture.File.RelativePath = Path.GetRelativePath(fixture.Paths.RootPath, linkedFile);
        await fixture.Db.SaveChangesAsync();

        await AssertStatusAsync(
            404,
            () => fixture.OpenAsAsync(fixture.Owner));
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
    public async Task UnsafeFilenameAndMimeTypeAreSanitizedWithoutChangingBytes()
    {
        await using var fixture = await DownloadFixture.CreateAsync();
        fixture.File.OriginalName = "folder/CON\r\nanswer.zip";
        fixture.File.MimeType = "application/zip\r\nX-Injected: yes";
        await fixture.Db.SaveChangesAsync();

        var download = await fixture.OpenAsAsync(fixture.Owner);
        await using var content = download.Content;

        Assert.DoesNotContain('\r', download.DownloadName);
        Assert.DoesNotContain('\n', download.DownloadName);
        Assert.DoesNotContain('/', download.DownloadName);
        Assert.Equal("application/octet-stream", download.MimeType);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        Assert.Equal(fixture.ExpectedBytes, buffer.ToArray());
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
            string physicalPath,
            byte[] expectedBytes)
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
            PhysicalPath = physicalPath;
            ExpectedBytes = expectedBytes;
            Service = new(
                db,
                paths,
                NullLogger<OnlyLanSubmissionDownloadService>.Instance);
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
        public string PhysicalPath { get; }
        public byte[] ExpectedBytes { get; }
        public OnlyLanSubmissionDownloadService Service { get; }

        public static async Task<DownloadFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "ExamTransfer.Tests",
                "OnlyLanSubmissionDownload",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={Path.Combine(root, "download.db")}")
                    .Options);
            await db.Database.EnsureCreatedAsync();
            var paths = new TestStoragePaths(Path.Combine(root, "storage"));
            Directory.CreateDirectory(paths.RootPath);

            var owner = new User
            {
                Username = $"owner-{Guid.NewGuid():N}",
                DisplayName = "Owner",
                Role = UserRole.Teacher,
                OrganizationId = "org-a",
                IsActive = true
            };
            var exam = new Exam
            {
                Title = "OnlyLAN download",
                Subject = "Security",
                DurationMinutes = 30,
                DeliveryType = ExamDeliveryType.FileSubmission,
                Status = ExamStatus.Published,
                CreatedBy = owner.Id
            };
            var session = new ExamSession
            {
                ExamId = exam.Id,
                RoomCode = $"LAN-{Guid.NewGuid():N}",
                AccessMode = SessionAccessMode.LanOnly,
                Status = SessionStatus.Finished
            };
            var participant = new SessionParticipant
            {
                SessionId = session.Id,
                StudentCode = "SV001",
                DisplayName = "Student One",
                DeviceId = "device",
                MachineName = "machine",
                AppVersion = "test",
                Status = ParticipantStatus.Approved
            };
            var submission = new Submission
            {
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
            var file = new SubmissionFile
            {
                SubmissionId = submission.Id,
                ClientFileId = "client-file",
                OriginalName = "answer.zip",
                StoredName = $"{Guid.NewGuid():N}.zip",
                MimeType = "application/zip",
                SizeBytes = 7,
                Sha256 = new string('a', 64),
                ChunkSizeBytes = 1024,
                TotalChunks = 1,
                TransferStatus = TransferStatus.Completed
            };
            var submissionRoot = paths.SubmissionRoot(
                session.Id,
                participant.StudentCode,
                submission.Id);
            Directory.CreateDirectory(submissionRoot);
            var physicalPath = Path.Combine(submissionRoot, file.StoredName);
            var expectedBytes = new byte[] { 80, 75, 3, 4, 1, 2, 3 };
            await System.IO.File.WriteAllBytesAsync(physicalPath, expectedBytes);
            file.RelativePath = Path.GetRelativePath(paths.RootPath, physicalPath);

            db.AddRange(owner, exam, session, participant, submission, file);
            await db.SaveChangesAsync();
            return new(
                root,
                db,
                paths,
                owner,
                exam,
                session,
                participant,
                submission,
                file,
                physicalPath,
                expectedBytes);
        }

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

        public Task<SubmissionDownloadContent> OpenAsAsync(User actor) =>
            Service.OpenAsync(
                Submission.Id,
                File.Id,
                actor.Id,
                actor.OrganizationId,
                "test-trace",
                CancellationToken.None);

        public SubmissionFile FileFor(Submission submission) => new()
        {
            SubmissionId = submission.Id,
            ClientFileId = Guid.NewGuid().ToString("N"),
            OriginalName = "other.zip",
            StoredName = $"{Guid.NewGuid():N}.zip",
            MimeType = "application/zip",
            SizeBytes = 1,
            Sha256 = new string('b', 64),
            ChunkSizeBytes = 1024,
            TotalChunks = 1,
            TransferStatus = TransferStatus.Completed,
            RelativePath = File.RelativePath
        };

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
                // Test cleanup is best effort only.
            }
        }
    }

    private sealed class TestStoragePaths(string root) : IStoragePaths
    {
        public string RootPath { get; } = Path.GetFullPath(root);
        public string DatabasePath => Path.Combine(RootPath, "database", "exam-transfer.db");
        public string BackupRoot => Path.Combine(RootPath, "database", "backups");
        public string ExportRoot => Path.Combine(RootPath, "exports");
        public string TemporaryRoot => Path.Combine(RootPath, "temporary");
        public string ExamVersionRoot(Guid examId, int version) =>
            Path.Combine(RootPath, "exams", examId.ToString("N"), $"v{version}");
        public string SessionRoot(Guid sessionId) =>
            Path.Combine(RootPath, "sessions", sessionId.ToString("N"));
        public string SubmissionRoot(Guid sessionId, string studentCode, Guid submissionId) =>
            Path.Combine(
                SessionRoot(sessionId),
                "submissions",
                studentCode,
                submissionId.ToString("N"));
        public string ReceiptRoot(Guid sessionId) =>
            Path.Combine(SessionRoot(sessionId), "receipts");
        public void EnsureCreated() => Directory.CreateDirectory(RootPath);
    }
}
