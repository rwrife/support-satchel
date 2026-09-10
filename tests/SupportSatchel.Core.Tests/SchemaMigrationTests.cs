using Microsoft.Data.Sqlite;
using SupportSatchel.Core.Storage;
using Xunit;

namespace SupportSatchel.Core.Tests;

/// <summary>
/// Acceptance: migration/version strategy tested for at least one schema
/// version bump. A database fabricated at the *previous* version (1) must
/// upgrade to the current target (2) without losing rows, and a database
/// newer than this build must fail fast.
/// </summary>
public class SchemaMigrationTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public SchemaMigrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>Builds a version-1 database by hand, exactly as shipped first.</summary>
    private void FabricateVersion1Database()
    {
        Execute("""
            CREATE TABLE schema_migration (
                version    INTEGER PRIMARY KEY,
                name       TEXT NOT NULL,
                applied_at TEXT NOT NULL
            );
            CREATE TABLE profiles (
                id         TEXT PRIMARY KEY NOT NULL,
                name       TEXT NOT NULL UNIQUE,
                document   TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE runs (
                id             TEXT PRIMARY KEY NOT NULL,
                profile_id     TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
                profile_name   TEXT NOT NULL,
                started_at     TEXT NOT NULL,
                finished_at    TEXT,
                status         INTEGER NOT NULL,
                workspace_path TEXT NOT NULL
            );
            INSERT INTO schema_migration (version, name, applied_at)
            VALUES (1, 'initial-schema', '2026-01-15T12:00:00.0000000+00:00');
            PRAGMA user_version = 1;
            """);
    }

    [Fact]
    public void FreshStoreMigratesToTargetVersion()
    {
        Schema.MigrateUp(_connection);
        Assert.Equal(Schema.TargetVersion, Schema.ReadVersion(_connection));

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT version, name FROM schema_migration ORDER BY version;";
        using var reader = command.ExecuteReader();
        var applied = new List<(int, string)>();
        while (reader.Read())
        {
            applied.Add((reader.GetInt32(0), reader.GetString(1)));
        }

        Assert.Equal([(1, "initial-schema"), (2, "run-notes-and-index")], applied);
    }

    [Fact]
    public void Version1DatabaseUpgradesToVersion2KeepingRows()
    {
        FabricateVersion1Database();

        // Insert legacy data through raw SQL (schema v1 has no notes column).
        var profileId = Guid.NewGuid();
        using (var insert = _connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO profiles (id, name, document, created_at, updated_at)
                VALUES ($id, 'legacy', '{}', '2026-01-15T12:00:00.0000000+00:00',
                        '2026-01-15T12:00:00.0000000+00:00');
                INSERT INTO runs (id, profile_id, profile_name, started_at, finished_at, status, workspace_path)
                VALUES ($runId, $id, 'legacy', '2026-01-15T12:05:00.0000000+00:00',
                        '2026-01-15T12:06:00.0000000+00:00', 1, '/tmp/legacy-run');
                """;
            insert.Parameters.AddWithValue("$id", profileId.ToString("D"));
            insert.Parameters.AddWithValue("$runId", Guid.NewGuid().ToString("D"));
            insert.ExecuteNonQuery();
        }

        // Open the store on the v1 file: it must migrate to v2 transparently.
        using var store = new ProfileStore(_connection);
        Assert.Equal(Schema.TargetVersion, store.SchemaVersion);

        // The run row survived and gained its defaulted notes column.
        var runs = store.ListRuns(profileId);
        var run = Assert.Single(runs);
        Assert.Equal("/tmp/legacy-run", run.WorkspacePath);
        Assert.Equal(string.Empty, run.Notes);

        // New behaviour from v2 works: updating notes persists.
        store.UpdateRun(run with { Notes = "added after upgrade" });
        Assert.Equal("added after upgrade", store.GetRun(run.Id).Notes);
    }

    [Fact]
    public void NewerDatabaseVersionFailsFast()
    {
        Execute("PRAGMA user_version = 99;");
        var ex = Assert.Throws<StoreException>(() => Schema.MigrateUp(_connection));
        Assert.Contains("newer than this build", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationIsIdempotent()
    {
        Schema.MigrateUp(_connection);
        Schema.MigrateUp(_connection);
        Assert.Equal(Schema.TargetVersion, Schema.ReadVersion(_connection));

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_migration;";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
