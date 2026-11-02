using Microsoft.EntityFrameworkCore;

namespace MosWord2019.Server.Data;

public sealed class ServerDbContext(DbContextOptions<ServerDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Result> Results => Set<Result>();
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<SessionHeartbeat> Heartbeats => Set<SessionHeartbeat>();
    public DbSet<SessionOfflineGap> OfflineGaps => Set<SessionOfflineGap>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var accounts = model.Entity<Account>();
        accounts.ToTable("Accounts", t =>
        {
            t.HasCheckConstraint("CK_Accounts_Uses", "\"Used\" >= 0 AND \"MaxUses\" >= \"Used\"");
            t.HasCheckConstraint("CK_Accounts_Failures", "\"FailedLoginCount\" BETWEEN 0 AND 10");
        });
        accounts.HasKey(x => x.Id);
        accounts.Property(x => x.Username).HasMaxLength(64).IsRequired();
        accounts.Property(x => x.NormalizedUsername).HasMaxLength(64).IsRequired();
        accounts.HasIndex(x => x.NormalizedUsername).IsUnique();
        accounts.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
        accounts.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        accounts.Property(x => x.CredentialVersion).HasDefaultValueSql("gen_random_uuid()");

        var sessions = model.Entity<Session>();
        sessions.ToTable("Sessions", t =>
        {
            t.HasCheckConstraint("CK_Sessions_Mode", "\"Mode\" IS NULL OR \"Mode\" IN ('Training', 'Testing')");
            t.HasCheckConstraint("CK_Sessions_End", "\"EndedAt\" IS NULL OR \"EndedAt\" >= \"StartedAt\"");
            t.HasCheckConstraint("CK_Sessions_Machine", "\"MachineId\" ~ '^[0-9a-f]{64}$'");
        });
        sessions.HasKey(x => x.Id);
        sessions.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        sessions.HasIndex(x => new { x.AccountId, x.LoginRequestId }).IsUnique();
        sessions.Property(x => x.MachineId).HasMaxLength(64).IsRequired();
        sessions.Property(x => x.Mode).HasMaxLength(16);
        sessions.Property(x => x.EndReason).HasMaxLength(16);
        sessions.Property(x => x.ExamDefinitionHash).HasMaxLength(64);
        sessions.Property(x => x.ReportedDefinitionHash).HasMaxLength(64);
        sessions.Property(x => x.ExamSnapshotJson).HasColumnType("jsonb");
        sessions.Property(x => x.StartedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

        var heartbeats = model.Entity<SessionHeartbeat>();
        heartbeats.ToTable("Heartbeats", t => t.HasCheckConstraint("CK_Heartbeats_Elapsed",
            "\"ClientElapsedMilliseconds\" IS NULL OR \"ClientElapsedMilliseconds\" BETWEEN 0 AND 86400000"));
        heartbeats.HasKey(x => x.Id);
        heartbeats.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        heartbeats.HasIndex(x => new { x.SessionId, x.ReceivedAt });

        var gaps = model.Entity<SessionOfflineGap>();
        gaps.ToTable("OfflineGaps", t =>
        {
            t.HasCheckConstraint("CK_OfflineGaps_Time", "\"EndedAt\" >= \"StartedAt\"");
            t.HasCheckConstraint("CK_OfflineGaps_Kind", "\"Kind\" IN ('Network', 'Unobserved')");
        });
        gaps.HasKey(x => x.Id);
        gaps.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        gaps.Property(x => x.Kind).HasMaxLength(16).IsRequired();
        gaps.HasIndex(x => new { x.SessionId, x.StartedAt });

        var results = model.Entity<Result>();
        results.ToTable("Results");
        results.HasKey(x => x.Id);
        results.HasOne<Session>().WithOne().HasForeignKey<Result>(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        results.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        results.Property(x => x.ReceivedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

        var packages = model.Entity<Package>();
        packages.ToTable("Packages", t =>
        {
            t.HasCheckConstraint("CK_Packages_Size", "\"SizeBytes\" >= 0");
            t.HasCheckConstraint("CK_Packages_Hash", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
        });
        packages.HasKey(x => x.Id);
        packages.Property(x => x.Code).HasMaxLength(64).IsRequired();
        packages.Property(x => x.Version).HasMaxLength(64).IsRequired();
        packages.HasIndex(x => new { x.Code, x.Version }).IsUnique();
        packages.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        packages.Property(x => x.ZipSha256).HasMaxLength(64).IsRequired();
        packages.Property(x => x.MetadataJson).HasColumnType("jsonb");
        packages.Property(x => x.TasksJson).HasColumnType("jsonb");
        packages.Property(x => x.CatalogEntryJson).HasColumnType("jsonb");
        packages.Property(x => x.StoragePath).HasMaxLength(1024).IsRequired();
        packages.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

        var audit = model.Entity<AuditEntry>();
        audit.ToTable("AuditLog");
        audit.HasKey(x => x.Id);
        audit.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        audit.Property(x => x.Action).HasMaxLength(128).IsRequired();
        audit.Property(x => x.DetailsJson).HasColumnType("jsonb").IsRequired();
        audit.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        audit.HasIndex(x => x.CreatedAt);
    }
}
