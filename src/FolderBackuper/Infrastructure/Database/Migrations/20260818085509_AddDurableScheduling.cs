using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolderBackuper.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Runs_JobId_Phase_QueuedAtUtc_Id",
                table: "Runs");

            migrationBuilder.DropIndex(
                name: "IX_Runs_Phase_QueuedAtUtc_Id",
                table: "Runs");

            migrationBuilder.DropIndex(
                name: "IX_Runs_QueuedAtUtc_Id",
                table: "Runs");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OccursAtUtc",
                table: "ScheduledOccurrences",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DueAtUtc",
                table: "Runs",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<long>(
                name: "LastSatisfiedScheduleRevision",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LastSatisfiedScheduledLocalDate",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextOccurrenceAtUtc",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "NextOccurrenceLocalDate",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NextOccurrenceTimeZoneId",
                table: "Jobs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NextOccurrenceUtcOffsetMinutes",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScheduleEvaluatedThroughUtc",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("UPDATE Runs SET DueAtUtc = QueuedAtUtc;");
            migrationBuilder.Sql("""
                UPDATE ScheduledOccurrences
                SET OccursAtUtc = COALESCE(
                    (SELECT DueAtUtc FROM Runs WHERE Runs.Id = ScheduledOccurrences.RunId),
                    '0001-01-01 00:00:00+00:00');
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Runs_DueAtUtc_QueuedAtUtc_Id",
                table: "Runs",
                columns: new[] { "DueAtUtc", "QueuedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_JobId_Phase_DueAtUtc_QueuedAtUtc_Id",
                table: "Runs",
                columns: new[] { "JobId", "Phase", "DueAtUtc", "QueuedAtUtc", "Id" },
                filter: "Outcome IS NULL AND Phase <> 'Planned'");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_Phase_DueAtUtc_QueuedAtUtc_Id",
                table: "Runs",
                columns: new[] { "Phase", "DueAtUtc", "QueuedAtUtc", "Id" },
                filter: "Outcome IS NULL AND Phase <> 'Planned'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Runs_DueAtUtc_QueuedAtUtc_Id",
                table: "Runs");

            migrationBuilder.DropIndex(
                name: "IX_Runs_JobId_Phase_DueAtUtc_QueuedAtUtc_Id",
                table: "Runs");

            migrationBuilder.DropIndex(
                name: "IX_Runs_Phase_DueAtUtc_QueuedAtUtc_Id",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "OccursAtUtc",
                table: "ScheduledOccurrences");

            migrationBuilder.DropColumn(
                name: "DueAtUtc",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "LastSatisfiedScheduleRevision",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "LastSatisfiedScheduledLocalDate",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "NextOccurrenceAtUtc",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "NextOccurrenceLocalDate",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "NextOccurrenceTimeZoneId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "NextOccurrenceUtcOffsetMinutes",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ScheduleEvaluatedThroughUtc",
                table: "Jobs");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_JobId_Phase_QueuedAtUtc_Id",
                table: "Runs",
                columns: new[] { "JobId", "Phase", "QueuedAtUtc", "Id" },
                filter: "Outcome IS NULL AND Phase <> 'Planned'");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_Phase_QueuedAtUtc_Id",
                table: "Runs",
                columns: new[] { "Phase", "QueuedAtUtc", "Id" },
                filter: "Outcome IS NULL AND Phase <> 'Planned'");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_QueuedAtUtc_Id",
                table: "Runs",
                columns: new[] { "QueuedAtUtc", "Id" });
        }
    }
}
