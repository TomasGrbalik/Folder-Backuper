using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolderBackuper.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class StoreMessageCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The columns that held finished English sentences are dropped rather than renamed onto the
            // new argument columns. A rename would leave a sentence sitting in a column that now means
            // "the arguments substituted into a message", which nothing can read and which would be
            // misleading to anyone inspecting the database. Losing the text is intended: it is
            // reproducible from the code and its arguments for every row written from here on.
            migrationBuilder.DropColumn(name: "UserMessage", table: "RunProblems");

            // One sentence in the old column was written by a migration rather than by a run: the
            // durable-execution upgrade reconciled duplicate active work and recorded why. That reason
            // is carried forward as a code so it is not lost when the column goes.
            migrationBuilder.AddColumn<string>(
                name: "ErrorMessageKey",
                table: "Runs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE Runs
                SET ErrorMessageKey = 'BackupProblemMessage_DuplicateActiveWorkReconciled'
                WHERE ErrorSummary LIKE 'Duplicate active work%';
                """);

            migrationBuilder.DropColumn(name: "ErrorSummary", table: "Runs");
            migrationBuilder.DropColumn(name: "NotificationErrorSummary", table: "Runs");
            migrationBuilder.DropColumn(name: "LastSafeError", table: "NotificationOutbox");
            migrationBuilder.DropColumn(name: "LastAccessErrorSummary", table: "Destinations");

            // The pipeline operation was free text and is now an enumeration stored by member name, so
            // its budget shrinks to what a member name needs.
            migrationBuilder.AlterColumn<string>(
                name: "Operation",
                table: "RunProblems",
                type: "TEXT",
                maxLength: 60,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<string>(
                name: "MessageKey",
                table: "RunProblems",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MessageArguments",
                table: "RunProblems",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessageArguments",
                table: "Runs",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotificationMessageKey",
                table: "Runs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotificationMessageArguments",
                table: "Runs",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSafeErrorKey",
                table: "NotificationOutbox",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSafeErrorArguments",
                table: "NotificationOutbox",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastAccessMessageKey",
                table: "Destinations",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastAccessMessageArguments",
                table: "Destinations",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "MessageKey", table: "RunProblems");
            migrationBuilder.DropColumn(name: "MessageArguments", table: "RunProblems");
            migrationBuilder.DropColumn(name: "ErrorMessageKey", table: "Runs");
            migrationBuilder.DropColumn(name: "ErrorMessageArguments", table: "Runs");
            migrationBuilder.DropColumn(name: "NotificationMessageKey", table: "Runs");
            migrationBuilder.DropColumn(name: "NotificationMessageArguments", table: "Runs");
            migrationBuilder.DropColumn(name: "LastSafeErrorKey", table: "NotificationOutbox");
            migrationBuilder.DropColumn(name: "LastSafeErrorArguments", table: "NotificationOutbox");
            migrationBuilder.DropColumn(name: "LastAccessMessageKey", table: "Destinations");
            migrationBuilder.DropColumn(name: "LastAccessMessageArguments", table: "Destinations");

            migrationBuilder.AlterColumn<string>(
                name: "Operation",
                table: "RunProblems",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 60);

            migrationBuilder.AddColumn<string>(
                name: "UserMessage",
                table: "RunProblems",
                type: "TEXT",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ErrorSummary", table: "Runs", type: "TEXT", maxLength: 2000, nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "NotificationErrorSummary", table: "Runs", type: "TEXT", maxLength: 2000, nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "LastSafeError", table: "NotificationOutbox", type: "TEXT", maxLength: 2000, nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "LastAccessErrorSummary", table: "Destinations", type: "TEXT", maxLength: 2000, nullable: true);
        }
    }
}
