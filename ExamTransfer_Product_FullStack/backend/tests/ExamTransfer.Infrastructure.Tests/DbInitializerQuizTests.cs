using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class DbInitializerQuizTests
{
    [Fact]
    public async Task InitializeAsync_CreatesQuizSchemaAndIsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "ExamTransfer.DbInitTests", Guid.NewGuid().ToString("N"));
        var paths = new Paths(root);
        try
        {
            await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={paths.DatabasePath}").Options))
            {
                await DbInitializer.InitializeAsync(db, paths);
                await DbInitializer.InitializeAsync(db, paths);

                Assert.Equal(7, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name LIKE 'quiz_%'").SingleAsync());
                Assert.Equal("\"12\"", (await db.AppSettingsSet.SingleAsync(x => x.Key == "schema.version")).ValueJson);

                var classroom = new ClassRoom
                {
                    Name = "Legacy class",
                    Code = "LEGACY",
                    SchoolYear = "2026-2027"
                };
                var admissionExam = new Exam
                {
                    Class = classroom,
                    Title = "Admission backfill",
                    Subject = "Upgrade",
                    DurationMinutes = 30
                };
                var classSession = new ExamSession
                {
                    Exam = admissionExam,
                    ClassId = classroom.Id,
                    RoomCode = "CLASS01",
                    AdmissionMode = SessionAdmissionMode.ClassMembersOnly
                };
                var classlessSession = new ExamSession
                {
                    Exam = admissionExam,
                    ClassId = null,
                    RoomCode = "OPEN01",
                    AdmissionMode = SessionAdmissionMode.ClassMembersOnly
                };
                db.AddRange(classroom, admissionExam, classSession, classlessSession);
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();

                await DbInitializer.InitializeAsync(db, paths);
                Assert.Equal(
                    SessionAdmissionMode.ClassMembersOnly,
                    await db.ExamSessionsSet.Where(x => x.Id == classSession.Id).Select(x => x.AdmissionMode).SingleAsync());
                Assert.Equal(
                    SessionAdmissionMode.OpenRequest,
                    await db.ExamSessionsSet.Where(x => x.Id == classlessSession.Id).Select(x => x.AdmissionMode).SingleAsync());

                var exam = new Exam
                {
                    Title = "Legacy duplicate source",
                    Subject = "Upgrade",
                    DurationMinutes = 30,
                    DeliveryType = ExamDeliveryType.MultipleChoice,
                    SupervisionMode = SupervisionMode.Standard
                };
                db.ExamsSet.Add(exam);
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlRawAsync(
                    "DROP INDEX \"IX_quiz_import_sources_ExamId_ExamVersion\"");
                var keeperId = Guid.NewGuid();
                db.QuizImportSourcesSet.AddRange(
                    new QuizImportSource
                    {
                        Id = Guid.NewGuid(),
                        ExamId = exam.Id,
                        ExamVersion = 1,
                        OriginalName = "old.docx",
                        RelativePath = "old.docx",
                        Status = "Failed",
                        ImportedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
                    },
                    new QuizImportSource
                    {
                        Id = keeperId,
                        ExamId = exam.Id,
                        ExamVersion = 1,
                        OriginalName = "current.docx",
                        RelativePath = "current.docx",
                        Status = "Committed",
                        ImportedAtUtc = DateTimeOffset.UtcNow
                    });
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();

                await DbInitializer.InitializeAsync(db, paths);

                var retained = await db.QuizImportSourcesSet.AsNoTracking()
                    .Where(x => x.ExamId == exam.Id && x.ExamVersion == 1)
                    .SingleAsync();
                Assert.Equal(keeperId, retained.Id);
                Assert.Equal("Committed", retained.Status);
                Assert.Equal(
                    1,
                    await db.Database.SqlQueryRaw<int>(
                        "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='index' AND name='IX_quiz_import_sources_ExamId_ExamVersion'")
                        .SingleAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class Paths(string root) : IStoragePaths
    {
        public string RootPath { get; } = root;
        public string DatabasePath => Path.Combine(RootPath, "database", "exam-transfer.db");
        public string BackupRoot => Path.Combine(RootPath, "backups");
        public string ExportRoot => Path.Combine(RootPath, "exports");
        public string TemporaryRoot => Path.Combine(RootPath, "temporary");
        public string ExamVersionRoot(Guid examId, int version) => Path.Combine(RootPath, "exams", examId.ToString("N"), $"v{version}");
        public string SessionRoot(Guid sessionId) => Path.Combine(RootPath, "sessions", sessionId.ToString("N"));
        public string SubmissionRoot(Guid sessionId, string studentCode, Guid submissionId) => Path.Combine(SessionRoot(sessionId), studentCode, submissionId.ToString("N"));
        public string ReceiptRoot(Guid sessionId) => Path.Combine(SessionRoot(sessionId), "receipts");
        public void EnsureCreated() { Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!); Directory.CreateDirectory(BackupRoot); Directory.CreateDirectory(ExportRoot); Directory.CreateDirectory(TemporaryRoot); }
    }
}
