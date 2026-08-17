using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolderBackuper.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApplicationSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NotificationProvider = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    NotificationProviderConfiguration = table.Column<string>(type: "TEXT", nullable: true),
                    RecipientList = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedNotificationSecret = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Destinations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RootPath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    SmbUsername = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ProtectedPassword = table.Column<byte[]>(type: "BLOB", nullable: true),
                    VerificationFingerprint = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    VerificationResult = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    VerifiedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Lifecycle = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LastAccessResult = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    LastAccessSource = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    LastAccessedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastAccessErrorSummary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Destinations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    DestinationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DestinationSubfolder = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Weekdays = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ScheduledTime = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    ScheduleRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    RetentionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Lifecycle = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DestinationOwnershipKey = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    ManagedArtifactCount = table.Column<long>(type: "INTEGER", nullable: false),
                    ManagedArtifactBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LatestArtifactBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    StorageConfirmedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.CheckConstraint("CK_Jobs_OwnershipKey", "Lifecycle = 'Archived' OR DestinationOwnershipKey IS NOT NULL");
                    table.CheckConstraint("CK_Jobs_RetentionCount", "RetentionCount >= 1");
                    table.CheckConstraint("CK_Jobs_StorageTotals", "ManagedArtifactCount >= 0 AND ManagedArtifactBytes >= 0 AND (LatestArtifactBytes IS NULL OR LatestArtifactBytes >= 0)");
                    table.ForeignKey(
                        name: "FK_Jobs_Destinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "Destinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    DestinationName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DestinationType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DestinationRootPath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    DestinationSubfolder = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    ScheduledWeekdays = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ScheduledTime = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    RetentionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RegionalCulture = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    QueuedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    CancellationRequestedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FinalCommitStartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FinalCommittedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FileCount = table.Column<long>(type: "INTEGER", nullable: false),
                    DirectoryCount = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ArchiveBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CompressionDuration = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    TransferDuration = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    ErrorSummary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    NotificationState = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    NotificationErrorSummary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                    table.CheckConstraint("CK_Runs_CompletedOutcome", "(Outcome IS NULL AND CompletedAtUtc IS NULL) OR (Outcome IS NOT NULL AND CompletedAtUtc IS NOT NULL)");
                    table.CheckConstraint("CK_Runs_Counts", "FileCount >= 0 AND DirectoryCount >= 0 AND SourceBytes >= 0 AND ArchiveBytes >= 0");
                    table.ForeignKey(
                        name: "FK_Runs_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DestinationName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DestinationRootPath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    EffectivePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    FinalFileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    OwnershipRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnershipExpectedLength = table.Column<long>(type: "INTEGER", nullable: false),
                    OwnershipCreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    OwnershipFileSystemIdentity = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FinalizationState = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    RetentionState = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    StateChangedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupArtifacts", x => x.Id);
                    table.CheckConstraint("CK_BackupArtifacts_Size", "Size >= 0 AND OwnershipExpectedLength >= 0");
                    table.ForeignKey(
                        name: "FK_BackupArtifacts_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunOutcome = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PayloadSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SendingAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeliveredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FailedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSafeError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationOutbox", x => x.Id);
                    table.CheckConstraint("CK_NotificationOutbox_AttemptCount", "AttemptCount >= 0");
                    table.CheckConstraint("CK_NotificationOutbox_NotCancelled", "RunOutcome <> 'Cancelled'");
                    table.ForeignKey(
                        name: "FK_NotificationOutbox_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RunProblems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ErrorCategory = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NativeErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    UserMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    DiagnosticDetail = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunProblems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunProblems_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledOccurrences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScheduleRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ScheduledLocalDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ScheduledLocalTime = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    UtcOffsetMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledOccurrences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledOccurrences_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduledOccurrences_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationSettings_InstallationId",
                table: "ApplicationSettings",
                column: "InstallationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupArtifacts_RunId",
                table: "BackupArtifacts",
                column: "RunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Destinations_Name",
                table: "Destinations",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_DestinationId",
                table: "Jobs",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_DestinationOwnershipKey",
                table: "Jobs",
                column: "DestinationOwnershipKey",
                unique: true,
                filter: "Lifecycle IN ('Active', 'Paused')");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Name",
                table: "Jobs",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_RunId",
                table: "NotificationOutbox",
                column: "RunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_State_CreatedAtUtc",
                table: "NotificationOutbox",
                columns: new[] { "State", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RunProblems_RunId",
                table: "RunProblems",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_JobId_Outcome",
                table: "Runs",
                columns: new[] { "JobId", "Outcome" });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_QueuedAtUtc_Id",
                table: "Runs",
                columns: new[] { "QueuedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledOccurrences_JobId_ScheduleRevision_ScheduledLocalDate",
                table: "ScheduledOccurrences",
                columns: new[] { "JobId", "ScheduleRevision", "ScheduledLocalDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledOccurrences_RunId",
                table: "ScheduledOccurrences",
                column: "RunId",
                unique: true,
                filter: "RunId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationSettings");

            migrationBuilder.DropTable(
                name: "BackupArtifacts");

            migrationBuilder.DropTable(
                name: "NotificationOutbox");

            migrationBuilder.DropTable(
                name: "RunProblems");

            migrationBuilder.DropTable(
                name: "ScheduledOccurrences");

            migrationBuilder.DropTable(
                name: "Runs");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "Destinations");
        }
    }
}
