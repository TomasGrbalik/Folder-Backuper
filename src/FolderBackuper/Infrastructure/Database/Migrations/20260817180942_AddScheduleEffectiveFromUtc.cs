using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolderBackuper.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleEffectiveFromUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScheduleEffectiveFromUtc",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("UPDATE Jobs SET ScheduleEffectiveFromUtc = UpdatedAtUtc");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "ScheduleEffectiveFromUtc",
                table: "Jobs",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScheduleEffectiveFromUtc",
                table: "Jobs");
        }
    }
}
