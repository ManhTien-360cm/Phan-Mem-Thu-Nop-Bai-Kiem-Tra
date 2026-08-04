using System.Net;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.LocalServer.Controllers;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class OpenSessionDiscoveryTests
{
    [Fact]
    public async Task OpenSessions_ReturnsOnlyOpenWaitingLanRooms()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var classroom = new ClassRoom { Name = "10A", Code = "10A", SchoolYear = "2026-2027" };
        var teacher = new User { Username = "teacher", DisplayName = "Giáo viên", Role = UserRole.Teacher };
        var exam = new Exam { Class = classroom, Title = "Kiểm tra", Subject = "Tin", DurationMinutes = 45, Status = ExamStatus.Published, CreatedBy = teacher.Id };
        db.AddRange(classroom, teacher, exam);
        var unpublishedExam = new Exam { Title = "Nháp", Subject = "Tin", DurationMinutes = 45, Status = ExamStatus.Draft };
        db.Add(unpublishedExam);
        db.ExamSessionsSet.AddRange(
            new ExamSession { Exam = exam, ClassId = null, AdmissionMode = SessionAdmissionMode.OpenRequest, RoomCode = "OPEN1", Status = SessionStatus.Waiting, AcceptingParticipants = true, AccessMode = SessionAccessMode.LanOnly },
            new ExamSession { Exam = exam, ClassId = classroom.Id, RoomCode = "DRAFT1", Status = SessionStatus.Draft, AcceptingParticipants = true, AccessMode = SessionAccessMode.LanOnly },
            new ExamSession { Exam = exam, ClassId = classroom.Id, RoomCode = "CLOSED1", Status = SessionStatus.Waiting, AcceptingParticipants = false, AccessMode = SessionAccessMode.LanOnly },
            new ExamSession { Exam = exam, ClassId = classroom.Id, RoomCode = "PUBLIC1", Status = SessionStatus.Waiting, AcceptingParticipants = true, AccessMode = SessionAccessMode.PublicCloud },
            new ExamSession { Exam = exam, ClassId = classroom.Id, RoomCode = "STARTED1", Status = SessionStatus.InProgress, AcceptingParticipants = true, AccessMode = SessionAccessMode.LanOnly },
            new ExamSession { Exam = exam, ClassId = classroom.Id, RoomCode = "FINISH1", Status = SessionStatus.Finished, AcceptingParticipants = true, AccessMode = SessionAccessMode.LanOnly },
            new ExamSession { Exam = exam, ClassId = classroom.Id, RoomCode = "ARCHIVE1", Status = SessionStatus.Archived, AcceptingParticipants = true, AccessMode = SessionAccessMode.LanOnly },
            new ExamSession { Exam = unpublishedExam, ClassId = null, AdmissionMode = SessionAdmissionMode.OpenRequest, RoomCode = "UNPUBLISHED1", Status = SessionStatus.Waiting, AcceptingParticipants = true, AccessMode = SessionAccessMode.LanOnly });
        await db.SaveChangesAsync();

        var options = new ExamTransferOptions();
        options.Server.PreferredIp = LanNetworkConfiguration.RunningInContainer
            ? "192.168.10.1"
            : LanNetworkConfiguration.GetActivePhysicalAddresses().First().ToString();
        options.LanAccess.AllowedCidrs.Add($"{options.Server.PreferredIp}/32");
        var controller = new DiscoveryController(
            db,
            new LanAccessPolicy(Options.Create(options)),
            Options.Create(options))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse(options.Server.PreferredIp);
        controller.HttpContext.TraceIdentifier = "discovery-test";

        var action = await controller.OpenSessions(default);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<ApiResponse<IReadOnlyList<OpenSessionDiscoveryDto>>>(ok.Value);
        var room = Assert.Single(response.Data!);
        Assert.Equal("OPEN1", room.RoomCode);
        Assert.Equal(SessionAccessMode.LanOnly, room.AccessMode);
        Assert.Equal(SessionAdmissionMode.OpenRequest, room.AdmissionMode);
        Assert.Null(room.ClassId);
        Assert.Null(room.ClassCode);
        Assert.Null(room.ClassName);
        Assert.Equal(exam.Id, room.ExamId);
        Assert.Equal(exam.Title, room.ExamTitle);
        Assert.Equal("Tin", room.Subject);
        Assert.Equal(45, room.DurationMinutes);
        Assert.Equal(ExamDeliveryType.FileSubmission, room.DeliveryType);
        Assert.DoesNotContain("settings", System.Text.Json.JsonSerializer.Serialize(room), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenSessions_RejectsClientOutsideAllowedLan()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var controller = new DiscoveryController(db, new DenyLanPolicy(), Options.Create(new ExamTransferOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8");

        var error = await Assert.ThrowsAsync<ApiException>(() => controller.OpenSessions(default));
        Assert.Equal(ErrorCodes.LanAccessDenied, error.Code);
        Assert.Equal(403, error.StatusCode);
    }

    [Fact]
    public async Task Identity_LocalLifecycleProbeReturnsAdvertisedLanIdentity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();
        var options = new ExamTransferOptions();
        options.Server.PreferredIp = LanNetworkConfiguration.RunningInContainer
            ? "192.168.10.1"
            : LanNetworkConfiguration.GetActivePhysicalAddresses().First().ToString();
        var controller = new DiscoveryController(
            db,
            new AllowLanPolicy(),
            Options.Create(options))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

        var action = controller.Identity();

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<ApiResponse<LocalServerIdentityDto>>(ok.Value);
        Assert.NotNull(response.Data);
        Assert.Equal("ExamTransfer.LocalServer", response.Data!.Product);
        Assert.Equal(DiscoveryProtocol.ProtocolVersion, response.Data.Protocol);
        Assert.Equal(DiscoveryProtocol.DefaultPort, response.Data.DiscoveryPort);
        Assert.Equal(options.Server.PreferredIp, response.Data.AdvertisedAddress);
    }

    private sealed class AllowLanPolicy : ILanAccessPolicy { public bool IsAllowed(string? remoteAddress) => true; }
    private sealed class DenyLanPolicy : ILanAccessPolicy { public bool IsAllowed(string? remoteAddress) => false; }
}
