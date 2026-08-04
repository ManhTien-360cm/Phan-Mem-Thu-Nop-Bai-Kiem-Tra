using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Infrastructure;

public sealed class ExamTransferOptions
{
    public ServerOptions Server { get; set; } = new();
    public DiscoveryOptions Discovery { get; set; } = new();
    public LanAccessOptions LanAccess { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public TransferOptions Transfer { get; set; } = new();
    public SessionOptions Session { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public CloudOptions Cloud { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
}

public sealed class ServerOptions
{
    public int Port { get; set; } = 5048;
    public bool UseHttps { get; set; }
    public string? PreferredIp { get; set; }
    public string? DevelopmentTeacherToken { get; set; }
}

public sealed class DiscoveryOptions
{
    public bool Enabled { get; set; } = true;
    public string Protocol { get; set; } = "UdpBroadcast";
    public int Port { get; set; } = DiscoveryProtocol.DefaultPort;
    public List<string> AdditionalAllowedCidrs { get; set; } = [];
}

public sealed class LanAccessOptions
{
    public List<string> AllowedCidrs { get; set; } = [];
    public bool TrustDockerDesktopNat { get; set; }
    public List<string> TrustedDockerGatewayCidrs { get; set; } = [];
}

public sealed class StorageOptions
{
    public string RootPath { get; set; } = "%ProgramData%/ExamTransfer";
    public long MinFreeBytes { get; set; } = 5L * 1024 * 1024 * 1024;
}

public sealed class TransferOptions
{
    public int ChunkSizeBytes { get; set; } = 4 * 1024 * 1024;
    public int MaxConcurrentUploads { get; set; } = 8;
    public int RetryLimit { get; set; } = 5;
    public int MaxChunkSizeBytes { get; set; } = 16 * 1024 * 1024;
}

public sealed class SessionOptions
{
    public int HeartbeatSeconds { get; set; } = 5;
    public int DisconnectAfterSeconds { get; set; } = 20;
}

public sealed class SecurityOptions
{
    public int RoomCodeLength { get; set; } = 6;
    public int TokenMinutes { get; set; } = 30;
    public bool RateLimitEnabled { get; set; } = true;
    public string? TokenSigningKey { get; set; }
    public string? ReceiptSigningKey { get; set; }
}

public sealed class AuthOptions
{
    public bool AllowDevelopmentToken { get; set; }
    public int AccountTokenMinutes { get; set; } = 8 * 60;
    public int HeartbeatSeconds { get; set; } = 30;
    public int LeaseSeconds { get; set; } = 120;
    public int ChallengeMinutes { get; set; } = 5;
    public string StudentEmailDomain { get; set; } = "students.examtransfer.local";
}

public sealed class CloudOptions
{
    public bool Enabled { get; set; }
    public string AccessMode { get; set; } = CloudAccessModes.UserSession;
    public string Environment { get; set; } = "Development";
    public string? SupabaseUrl { get; set; }

    // PublishableKey is the preferred name for sb_publishable_* keys. AnonKey
    // is retained as a migration alias for older runtime configuration files.
    public string? PublishableKey { get; set; }
    public string? SupabasePublishableKey { get; set; }
    public string? AnonKey { get; set; }
    public string? OrganizationId { get; set; }

    public string SecretKeyEnvironmentVariable { get; set; } = "EXAMTRANSFER_SUPABASE_SECRET_KEY";
    public string ServiceRoleEnvironmentVariable { get; set; } = "EXAMTRANSFER_SUPABASE_SERVICE_KEY";
    public string Schema { get; set; } = "public";
    public string ExamBucket { get; set; } = "exam-archives";
    public string SubmissionBucket { get; set; } = "submission-archives";
    public string ExportBucket { get; set; } = "report-exports";
    public string BackupBucket { get; set; } = "backup-archives";

    public bool UseResumableUploads { get; set; } = true;
    public int StandardUploadThresholdBytes { get; set; } = 6 * 1024 * 1024;
    public int TusChunkSizeBytes { get; set; } = 6 * 1024 * 1024;
    public int WorkerBatchSize { get; set; } = 20;
    public int WorkerIntervalSeconds { get; set; } = 10;
    public int LeaseMinutes { get; set; } = 10;
    public int AuthRefreshSkewSeconds { get; set; } = 120;
    public bool PersistUserSession { get; set; } = true;

    public string? EffectivePublishableKey =>
        !string.IsNullOrWhiteSpace(PublishableKey)
            ? PublishableKey
            : !string.IsNullOrWhiteSpace(SupabasePublishableKey)
                ? SupabasePublishableKey
                : AnonKey;

    public string? ResolveSecretKey() =>
        System.Environment.GetEnvironmentVariable(SecretKeyEnvironmentVariable)
        ?? System.Environment.GetEnvironmentVariable(ServiceRoleEnvironmentVariable);
}

public sealed class RetentionOptions
{
    public int TemporaryHours { get; set; } = 24;
    public int LogsDays { get; set; } = 30;
}
