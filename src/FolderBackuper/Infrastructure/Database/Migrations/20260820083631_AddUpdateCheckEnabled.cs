using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolderBackuper.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddUpdateCheckEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The default is declared here rather than on the model, so that it applies to the rows
            // that already exist while the entity keeps writing its own value on insert. An upgraded
            // installation therefore has the check switched on, matching a fresh one.
            migrationBuilder.AddColumn<bool>(
                name: "UpdateCheckEnabled",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdateCheckEnabled",
                table: "ApplicationSettings");
        }
    }
}
