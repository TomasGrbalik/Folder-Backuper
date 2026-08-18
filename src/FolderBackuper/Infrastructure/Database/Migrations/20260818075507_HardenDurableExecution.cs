using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolderBackuper.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class HardenDurableExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DestinationUsername",
                table: "Runs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationVerificationFingerprint",
                table: "Runs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Severity",
                table: "RunProblems",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Error");

            migrationBuilder.AddColumn<Guid>(
                name: "RetentionRequestedByRunId",
                table: "BackupArtifacts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupArtifacts_RetentionRequestedByRunId",
                table: "BackupArtifacts",
                column: "RetentionRequestedByRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BackupArtifacts_RetentionRequestedByRunId",
                table: "BackupArtifacts");

            migrationBuilder.DropColumn(
                name: "DestinationUsername",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "DestinationVerificationFingerprint",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "Severity",
                table: "RunProblems");

            migrationBuilder.DropColumn(
                name: "RetentionRequestedByRunId",
                table: "BackupArtifacts");
        }
    }
}
