using Xunit;

// Test collections run sequentially. TemporaryDatabase must clear the SQLite connection pool in its
// teardown so the file handles are released before the temporary directory is deleted, and the only
// API that reliably covers the connections these tests open is SqliteConnection.ClearAllPools, which
// is global. Run two database-backed classes at the same time and one class's teardown can dispose a
// pooled handle another class is still using, which surfaced as intermittent ObjectDisposedException
// failures in unrelated tests once this assembly gained more database-backed classes.
//
// A per-database SqliteConnection.ClearPool was tried first and is not sufficient: it matches on
// connection string, so it leaves behind the handles for databases these tests open directly, such as
// the migration backup files and the deliberately conflicting schema databases.
//
// The whole suite runs in well under a minute, so serializing it costs little and removes the race
// rather than making it rarer.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
