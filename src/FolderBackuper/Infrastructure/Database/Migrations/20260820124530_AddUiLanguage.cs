using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolderBackuper.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddUiLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Deliberately left null on the rows that already exist, and deliberately given no
            // default. Null means nobody has chosen a language, which makes the interface follow the
            // Windows installed interface language. An upgraded installation therefore behaves like a
            // fresh one rather than being silently forced to English.
            migrationBuilder.AddColumn<string>(
                name: "UiLanguage",
                table: "ApplicationSettings",
                type: "TEXT",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UiLanguage",
                table: "ApplicationSettings");
        }
    }
}
