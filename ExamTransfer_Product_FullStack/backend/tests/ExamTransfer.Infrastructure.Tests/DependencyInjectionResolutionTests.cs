using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Execution;
using ExamTransfer.Infrastructure.Execution.Dispatch;
using ExamTransfer.Infrastructure.Execution.OnlyLan;
using ExamTransfer.Infrastructure.Execution.PublicCloud;
using ExamTransfer.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class DependencyInjectionResolutionTests
{
    [Fact]
    public void FacadesAndExtractedBoundariesResolveWithoutCircularDependencies()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ExamTransfer.Tests",
            "DependencyInjection",
            Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:RootPath"] = root
                })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IRealtimePublisher, NoOpRealtimePublisher>();
            services.AddExamTransferInfrastructure(configuration);

            using var provider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                });
            using var scope = provider.CreateScope();
            var scoped = scope.ServiceProvider;

            Assert.IsType<SessionService>(
                scoped.GetRequiredService<ISessionService>());
            Assert.IsType<SubmissionService>(
                scoped.GetRequiredService<ISubmissionService>());
            Assert.IsType<SubmissionDownloadDispatcher>(
                scoped.GetRequiredService<ISubmissionDownloadService>());
            Assert.IsType<OnlyLanSubmissionDownloadService>(
                scoped.GetRequiredService<IOnlyLanSubmissionDownloadService>());
            Assert.IsType<PublicCloudSubmissionDownloadService>(
                scoped.GetRequiredService<IPublicCloudSubmissionDownloadService>());
            Assert.IsType<ControlService>(
                scoped.GetRequiredService<IControlService>());
            Assert.IsType<QuizService>(
                scoped.GetRequiredService<IQuizService>());
            Assert.IsType<SessionParticipantMutationDispatcher>(
                scoped.GetRequiredService<SessionParticipantMutationDispatcher>());
            Assert.IsType<SubmissionMutationDispatcher>(
                scoped.GetRequiredService<SubmissionMutationDispatcher>());
            Assert.IsType<LanParticipantSessionExecution>(
                scoped.GetRequiredService<LanParticipantSessionExecution>());
            Assert.IsType<PublicCloudProjectionExecution>(
                scoped.GetRequiredService<PublicCloudProjectionExecution>());
            Assert.IsType<DeviceStatusReadExecution>(
                scoped.GetRequiredService<DeviceStatusReadExecution>());
            Assert.IsType<QuizProjectionOutbox>(
                scoped.GetRequiredService<QuizProjectionOutbox>());

            var participantHandlers = scoped
                .GetServices<ISessionParticipantMutationHandler>()
                .ToArray();
            Assert.Contains(
                participantHandlers,
                handler => handler is LanSessionParticipantMutationHandler);
            Assert.Contains(
                participantHandlers,
                handler => handler is PublicCloudSessionParticipantMutationHandler);

            var submissionHandlers = scoped
                .GetServices<ISubmissionMutationHandler>()
                .ToArray();
            Assert.Contains(
                submissionHandlers,
                handler => handler is LanSubmissionMutationHandler);
            Assert.Contains(
                submissionHandlers,
                handler => handler is PublicCloudSubmissionMutationHandler);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NoOpRealtimePublisher : IRealtimePublisher
    {
        public Task PublishSessionAsync<T>(
            Guid sessionId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PublishParticipantAsync<T>(
            Guid sessionId,
            Guid participantId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
