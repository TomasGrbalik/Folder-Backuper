# Milestone 2 Acceptance Checklist

Run these checks from an elevated PowerShell session on Windows. Use a disposable data root so validation never touches production data.

## Automated Verification

```powershell
dotnet restore FolderBackuper.slnx
dotnet build FolderBackuper.slnx -c Release --no-restore
dotnet test FolderBackuper.slnx -c Release --no-build
dotnet publish src/FolderBackuper/FolderBackuper.csproj -c Release -r win-x64 --self-contained true --no-build
```

The persistence tests use real temporary SQLite files. They cover schema constraints, state transitions, concurrent occurrence identity insertion, WAL-mode online backup, backup retention, and migration failure with a validated pre-migration backup.

## Fresh Database

1. Choose an unused root such as `C:\Temp\FolderBackuper-M2` and remove it if it exists from an earlier disposable test.
2. Run `dotnet run --project src/FolderBackuper/FolderBackuper.csproj -- --FolderBackuper:DataRoot=C:\Temp\FolderBackuper-M2 --FolderBackuper:Port=5180`.
3. Confirm the application starts and `C:\Temp\FolderBackuper-M2\data\folder-backuper.db` exists.
4. Stop and restart the application with the same arguments. Confirm startup is idempotent and the UI remains available.
5. Confirm logs report migration application only on the first startup and do not contain connection strings or protected values.

## Database Inspection

Open the disposable database with a SQLite inspection tool and verify:

```sql
PRAGMA foreign_keys;
PRAGMA journal_mode;
PRAGMA busy_timeout;
PRAGMA quick_check;
SELECT MigrationId FROM __EFMigrationsHistory;
```

Expected results:

- Foreign keys are `1` for application connections.
- Journal mode is `wal` on a compatible local filesystem. A warning and the compatible fallback mode are acceptable where WAL is unavailable.
- Application connections use a `5000` millisecond busy timeout.
- `quick_check` returns `ok`.
- The initial persistence migration is recorded.

Confirm the schema contains destinations, jobs, runs, scheduled occurrences, run problems, backup artifacts, notification outbox work, and application settings. Inspect indexes and check constraints for unique names, destination ownership, retention count, occurrence identity, and one-to-one run records.

## Migration Backup

Automated tests create committed data in a WAL database, invoke SQLite's online backup API, validate the backup, and open it independently. They also prove that only the three newest validated migration backups are retained.

For manual evidence against a future pending migration:

1. Start from a disposable database created by the current release.
2. Run a build containing the pending migration against that root.
3. Confirm a timestamped database appears under `data\migrations` before migration application.
4. Open that backup independently and confirm `PRAGMA quick_check` returns `ok` and pre-migration data is present.

Do not simulate migration failure against production data. The automated conflicting-schema test verifies that migration failure propagates out of database initialization, prevents normal host startup, and leaves a validated backup containing the pre-migration evidence row. The working database is deliberately not assumed to be unchanged.

## Windows Service Regression

Repeat the temporary service, single-instance, loopback binding, security-header, and browser reconnect checks from the [Milestone 1 checklist](milestone-1-acceptance.md). Confirm the service reaches `Running` only after database initialization succeeds.

Remove only the disposable test root after reviewing its database, migration backups, and logs.
