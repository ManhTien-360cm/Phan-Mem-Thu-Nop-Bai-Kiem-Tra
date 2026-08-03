using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Backup;
using ExamTransfer.Infrastructure.Cloud;
using ExamTransfer.Infrastructure.Execution;
using ExamTransfer.Infrastructure.Execution.Dispatch;
using ExamTransfer.Infrastructure.Execution.OnlyLan;
using ExamTransfer.Infrastructure.Execution.PublicCloud;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Infrastructure.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddExamTransferInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ExamTransferOptions>()
            .Bind(configuration)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ExamTransferOptions>, ExamTransferOptionsValidator>();
        services.AddSingleton<IStoragePaths, StoragePaths>();
        services.AddSingleton<IChunkStorage, ChunkStorage>();
        services.AddSingleton<IReceiptSigner, ReceiptSigner>();
        services.AddSingleton<ISessionTokenService, SessionTokenService>();
        services.AddSingleton<IAccountTokenService, AccountTokenService>();
        services.AddSingleton<ILoginChallengeService, LoginChallengeService>();
        services.AddSingleton<IBackupEngine, SqliteBackupEngine>();
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        var keyDirectory = ResolveDataProtectionKeyDirectory();
        Directory.CreateDirectory(keyDirectory);
        var dataProtection = services
            .AddDataProtection()
            .SetApplicationName("ExamTransfer")
            .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
        if (OperatingSystem.IsWindows())
            dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);

        services.AddSingleton<CloudSessionState>();
        services.AddHttpClient<ICloudAdapter, SupabaseCloudAdapter>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ExamTransfer-Backend/1.2");
        });
        services.AddHttpClient<IExternalIdentityProvider, SupabaseIdentityClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ExamTransfer-Auth/1.2");
        });

        services.AddDbContext<AppDbContext>((sp, builder) =>
        {
            var paths = sp.GetRequiredService<IStoragePaths>(); paths.EnsureCreated();
            builder.UseSqlite($"Data Source={paths.DatabasePath};Cache=Shared");
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IOutboxService, OutboxService>();
        services.AddSingleton<ICloudSyncSignal, CloudSyncSignal>();
        services.AddScoped<IAccountSessionService, AccountSessionService>();
        services.AddScoped<IAccountAuthenticationService, AccountAuthenticationService>();
        services.AddScoped<IClassService, ClassService>();
        services.AddScoped<IExamService, ExamService>();
        services.AddScoped<
            ISessionParticipantMutationHandler,
            LanSessionParticipantMutationHandler>();
        services.AddScoped<
            ISessionParticipantMutationHandler,
            PublicCloudSessionParticipantMutationHandler>();
        services.AddScoped<SessionParticipantMutationDispatcher>();
        services.AddScoped<LanParticipantSessionExecution>();
        services.AddScoped<PublicCloudProjectionExecution>();
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<
            ISubmissionMutationHandler,
            LanSubmissionMutationHandler>();
        services.AddScoped<
            ISubmissionMutationHandler,
            PublicCloudSubmissionMutationHandler>();
        services.AddScoped<SubmissionMutationDispatcher>();
        services.AddScoped<ISubmissionService, SubmissionService>();
        services.AddScoped<IOnlyLanSubmissionDownloadService, OnlyLanSubmissionDownloadService>();
        services.AddScoped<IPublicCloudSubmissionDownloadService, PublicCloudSubmissionDownloadService>();
        services.AddScoped<ISubmissionDownloadService, SubmissionDownloadDispatcher>();
        services.AddScoped<IGradeService, GradeService>();
        services.AddScoped<IStudentResultService, StudentResultService>();
        services.AddScoped<IQuizGradingService, QuizGradingService>();
        services.AddScoped<ISubmissionPreviewService, SubmissionPreviewService>();
        services.AddScoped<DeviceStatusReadExecution>();
        services.AddScoped<IControlService, ControlService>();
        services.AddScoped<IExportService, ExportService>();
        services.AddScoped<IBackupService, BackupService>();
        services.AddScoped<ISystemService, SystemService>();
        services.AddScoped<QuizProjectionOutbox>();
        services.AddScoped<IQuizService, QuizService>();
        services.AddSingleton<ILanAccessPolicy, LanAccessPolicy>();
        return services;
    }

    public static string ResolveDataProtectionKeyDirectory()
    {
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonData))
            commonData = AppContext.BaseDirectory;
        return Path.Combine(commonData, "ExamTransfer", "keys");
    }
}
