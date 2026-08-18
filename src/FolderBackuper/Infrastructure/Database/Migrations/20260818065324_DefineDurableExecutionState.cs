using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolderBackuper.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class DefineDurableExecutionState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DestinationId",
                table: "Runs",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "DestinationPartialPath",
                table: "Runs",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StagingPath",
                table: "Runs",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.Sql("UPDATE Runs SET DestinationId = (SELECT DestinationId FROM Jobs WHERE Jobs.Id = Runs.JobId) WHERE EXISTS (SELECT 1 FROM Jobs WHERE Jobs.Id = Runs.JobId);");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_JobId",
                table: "Runs",
                column: "JobId",
                unique: true,
                filter: "Outcome IS NULL AND Phase <> 'Planned'");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_JobId_Phase_QueuedAtUtc_Id",
                table: "Runs",
                columns: new[] { "JobId", "Phase", "QueuedAtUtc", "Id" },
                filter: "Outcome IS NULL AND Phase <> 'Planned'");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_Phase_Id",
                table: "Runs",
                columns: new[] { "Phase", "Id" },
                filter: "Outcome IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_Phase_QueuedAtUtc_Id",
                table: "Runs",
                columns: new[] { "Phase", "QueuedAtUtc", "Id" },
                filter: "Outcome IS NULL AND Phase <> 'Planned'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Runs_JobId",
                table: "Runs");

            migrationBuilder.DropIndex(
                name: "IX_Runs_JobId_Phase_QueuedAtUtc_Id",
                table: "Runs");

            migrationBuilder.DropIndex(
                name: "IX_Runs_Phase_Id",
                table: "Runs");

            migrationBuilder.DropIndex(
                name: "IX_Runs_Phase_QueuedAtUtc_Id",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "DestinationId",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "DestinationPartialPath",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "StagingPath",
                table: "Runs");
        }
    }
}
