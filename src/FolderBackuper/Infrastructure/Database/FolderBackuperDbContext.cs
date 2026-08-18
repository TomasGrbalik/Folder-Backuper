using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Notifications;
using FolderBackuper.Features.Settings;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Infrastructure.Database;

public sealed class FolderBackuperDbContext(DbContextOptions<FolderBackuperDbContext> options)
    : DbContext(options)
{
    public DbSet<Destination> Destinations => Set<Destination>();
    public DbSet<BackupJob> Jobs => Set<BackupJob>();
    public DbSet<BackupRun> Runs => Set<BackupRun>();
    public DbSet<ScheduledOccurrence> ScheduledOccurrences => Set<ScheduledOccurrence>();
    public DbSet<RunProblem> RunProblems => Set<RunProblem>();
    public DbSet<BackupArtifact> BackupArtifacts => Set<BackupArtifact>();
    public DbSet<NotificationOutboxItem> NotificationOutbox => Set<NotificationOutboxItem>();
    public DbSet<ApplicationSettings> ApplicationSettings => Set<ApplicationSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureDestination(modelBuilder);
        ConfigureJob(modelBuilder);
        ConfigureRun(modelBuilder);
        ConfigureOccurrence(modelBuilder);
        ConfigureRunProblem(modelBuilder);
        ConfigureArtifact(modelBuilder);
        ConfigureNotification(modelBuilder);
        ConfigureSettings(modelBuilder);
    }

    private static void ConfigureDestination(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Destination>();
        entity.ToTable("Destinations");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).HasMaxLength(200).UseCollation("NOCASE");
        entity.HasIndex(x => x.Name).IsUnique();
        entity.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        entity.Property(x => x.RootPath).HasMaxLength(2048);
        entity.Property(x => x.SmbUsername).HasMaxLength(256);
        entity.Property(x => x.VerificationFingerprint).HasMaxLength(256);
        entity.Property(x => x.VerificationResult).HasConversion<string>().HasMaxLength(30);
        entity.Property(x => x.Lifecycle).HasConversion<string>().HasMaxLength(20);
        entity.Property(x => x.LastAccessResult).HasConversion<string>().HasMaxLength(30);
        entity.Property(x => x.LastAccessSource).HasConversion<string>().HasMaxLength(30);
        entity.Property(x => x.LastAccessErrorSummary).HasMaxLength(2000);
    }

    private static void ConfigureJob(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BackupJob>();
        entity.ToTable("Jobs", table =>
        {
            table.HasCheckConstraint("CK_Jobs_RetentionCount", "RetentionCount >= 1");
            table.HasCheckConstraint("CK_Jobs_OwnershipKey", "Lifecycle = 'Archived' OR DestinationOwnershipKey IS NOT NULL");
            table.HasCheckConstraint("CK_Jobs_StorageTotals", "ManagedArtifactCount >= 0 AND ManagedArtifactBytes >= 0 AND (LatestArtifactBytes IS NULL OR LatestArtifactBytes >= 0)");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).HasMaxLength(200).UseCollation("NOCASE");
        entity.HasIndex(x => x.Name).IsUnique();
        entity.Property(x => x.SourcePath).HasMaxLength(2048);
        entity.Property(x => x.DestinationSubfolder).HasMaxLength(2048);
        entity.Property(x => x.Weekdays).HasConversion<string>().HasMaxLength(100);
        entity.Property(x => x.ScheduleEffectiveFromUtc).IsRequired();
        entity.Property(x => x.Lifecycle).HasConversion<string>().HasMaxLength(20);
        entity.Property(x => x.DestinationOwnershipKey)
            .HasConversion(key => key.ToUpperInvariant(), key => key)
            .HasMaxLength(2048);
        entity.HasIndex(x => x.DestinationOwnershipKey)
            .IsUnique()
            .HasFilter("Lifecycle IN ('Active', 'Paused')");
        entity.HasOne(x => x.Destination)
            .WithMany()
            .HasForeignKey(x => x.DestinationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureRun(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BackupRun>();
        entity.ToTable("Runs", table =>
        {
            table.HasCheckConstraint("CK_Runs_Counts", "FileCount >= 0 AND DirectoryCount >= 0 AND SourceBytes >= 0 AND ArchiveBytes >= 0");
            table.HasCheckConstraint("CK_Runs_CompletedOutcome", "(Outcome IS NULL AND CompletedAtUtc IS NULL) OR (Outcome IS NOT NULL AND CompletedAtUtc IS NOT NULL)");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.JobName).HasMaxLength(200);
        entity.Property(x => x.DestinationId).IsRequired();
        entity.Property(x => x.SourcePath).HasMaxLength(2048);
        entity.Property(x => x.DestinationName).HasMaxLength(200);
        entity.Property(x => x.DestinationType).HasConversion<string>().HasMaxLength(20);
        entity.Property(x => x.DestinationRootPath).HasMaxLength(2048);
        entity.Property(x => x.DestinationSubfolder).HasMaxLength(2048);
        entity.Property(x => x.ScheduledWeekdays).HasConversion<string>().HasMaxLength(100);
        entity.Property(x => x.RegionalCulture).HasMaxLength(100);
        entity.Property(x => x.TimeZoneId).HasMaxLength(200);
        entity.Property(x => x.Trigger).HasConversion<string>().HasMaxLength(20);
        entity.Property(x => x.Phase).HasConversion<string>().HasMaxLength(30);
        entity.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(40);
        entity.Property(x => x.NotificationState).HasConversion<string>().HasMaxLength(40);
        entity.Property(x => x.ErrorSummary).HasMaxLength(2000);
        entity.Property(x => x.NotificationErrorSummary).HasMaxLength(2000);
        entity.HasIndex(x => new { x.JobId, x.Outcome });
        entity.HasIndex(x => new { x.QueuedAtUtc, x.Id });
        entity.Property(x => x.StagingPath).HasMaxLength(2048);
        entity.Property(x => x.DestinationPartialPath).HasMaxLength(2048);
        entity.HasIndex(x => new { x.Phase, x.QueuedAtUtc, x.Id })
            .HasFilter("Outcome IS NULL AND Phase <> 'Planned'");
        entity.HasIndex(x => new { x.JobId, x.Phase, x.QueuedAtUtc, x.Id })
            .HasFilter("Outcome IS NULL AND Phase <> 'Planned'");
        entity.HasIndex(x => new { x.Phase, x.Id })
            .HasFilter("Outcome IS NULL");
        entity.HasIndex(x => x.JobId)
            .IsUnique()
            .HasFilter("Outcome IS NULL AND Phase <> 'Planned'");
        entity.HasOne(x => x.Job)
            .WithMany()
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureOccurrence(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ScheduledOccurrence>();
        entity.ToTable("ScheduledOccurrences");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.TimeZoneId).HasMaxLength(200);
        entity.HasIndex(x => new { x.JobId, x.ScheduleRevision, x.ScheduledLocalDate }).IsUnique();
        entity.HasIndex(x => x.RunId).IsUnique().HasFilter("RunId IS NOT NULL");
        entity.HasOne(x => x.Job)
            .WithMany()
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Run)
            .WithOne(x => x.Occurrence)
            .HasForeignKey<ScheduledOccurrence>(x => x.RunId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureRunProblem(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<RunProblem>();
        entity.ToTable("RunProblems");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Path).HasMaxLength(2048);
        entity.Property(x => x.Phase).HasConversion<string>().HasMaxLength(30);
        entity.Property(x => x.Operation).HasMaxLength(200);
        entity.Property(x => x.ErrorCategory).HasMaxLength(100);
        entity.Property(x => x.NativeErrorCode).HasMaxLength(100);
        entity.Property(x => x.UserMessage).HasMaxLength(2000);
        entity.HasIndex(x => x.RunId);
        entity.HasOne(x => x.Run)
            .WithMany(x => x.Problems)
            .HasForeignKey(x => x.RunId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureArtifact(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BackupArtifact>();
        entity.ToTable("BackupArtifacts", table => table.HasCheckConstraint("CK_BackupArtifacts_Size", "Size >= 0 AND OwnershipExpectedLength >= 0"));
        entity.HasKey(x => x.Id);
        entity.Property(x => x.DestinationName).HasMaxLength(200);
        entity.Property(x => x.DestinationRootPath).HasMaxLength(2048);
        entity.Property(x => x.EffectivePath).HasMaxLength(2048);
        entity.Property(x => x.FinalFileName).HasMaxLength(260);
        entity.Property(x => x.State).HasConversion<string>().HasMaxLength(40);
        entity.Property(x => x.FinalizationState).HasConversion<string>().HasMaxLength(30);
        entity.Property(x => x.RetentionState).HasConversion<string>().HasMaxLength(30);
        entity.Property(x => x.OwnershipFileSystemIdentity).HasMaxLength(500);
        entity.HasIndex(x => x.RunId).IsUnique();
        entity.HasOne(x => x.Run)
            .WithOne(x => x.Artifact)
            .HasForeignKey<BackupArtifact>(x => x.RunId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureNotification(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<NotificationOutboxItem>();
        entity.ToTable("NotificationOutbox", table =>
        {
            table.HasCheckConstraint("CK_NotificationOutbox_AttemptCount", "AttemptCount >= 0");
            table.HasCheckConstraint("CK_NotificationOutbox_NotCancelled", "RunOutcome <> 'Cancelled'");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.RunOutcome).HasConversion<string>().HasMaxLength(40);
        entity.Property(x => x.State).HasConversion<string>().HasMaxLength(40);
        entity.Property(x => x.LastSafeError).HasMaxLength(2000);
        entity.HasIndex(x => x.RunId).IsUnique();
        entity.HasIndex(x => new { x.State, x.CreatedAtUtc });
        entity.HasOne(x => x.Run)
            .WithOne(x => x.Notification)
            .HasForeignKey<NotificationOutboxItem>(x => x.RunId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSettings(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApplicationSettings>();
        entity.ToTable("ApplicationSettings");
        entity.HasKey(x => x.Id);
        entity.HasIndex(x => x.InstallationId).IsUnique();
        entity.Property(x => x.NotificationProvider).HasMaxLength(100);
    }
}
