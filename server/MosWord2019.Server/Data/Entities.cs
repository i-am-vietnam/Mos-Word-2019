namespace MosWord2019.Server.Data;

public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string NormalizedUsername { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }
    public int MaxUses { get; set; }
    public int Used { get; set; }
    public bool Locked { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public Guid CredentialVersion { get; set; } = Guid.NewGuid();
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Session
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Guid LoginRequestId { get; set; }
    public Guid AccountCredentialVersion { get; set; }
    public string MachineId { get; set; } = "";
    public string? Mode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

public sealed class Result
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class Package
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string StoragePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public Guid? AccountId { get; set; }
    public string Action { get; set; } = "";
    public string DetailsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
