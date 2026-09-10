using Microsoft.Data.Sqlite;

namespace SupportSatchel.Core.Storage;

/// <summary>
/// Ordered, forward-only schema migrations for the local database.
/// The active version is tracked in <c>PRAGMA user_version</c> and every
/// applied migration is recorded in the <c>schema_migration</c> table.
/// See <c>docs/persistence.md</c> for the full strategy.
/// </summary>
public static class Schema
{
    /// <summary>Schema version the current build of the application expects.</summary>
    public const int TargetVersion = 2;

    /// <summary>Single migration step definition.</summary>
    /// <param name="ToVersion">The <c>user_version</c> this migration produces.</param>
    /// <param name="Name">Short human-readable name recorded in <c>schema_migration</c>.</param>
    /// <param name="Up">Forward-only DDL executed inside a transaction.</param>
    public readonly record struct Migration(int ToVersion, string Name, Action<SqliteConnection> Up);

    /// <summary>All migrations in ascending target-version order.</summary>
    public static IReadOnlyList<Migration> Migrations { get; } =
    [
        new Migration(1, "initial-schema", InitialSchema),
        new Migration(2, "run-notes-and-index", RunNotesAndIndex),
    ];

    /// <summary>
    /// Brings a connection up to <see cref="TargetVersion"/>, applying every
    /// pending migration in order. Throws when the database is newer than
    /// this build understands.
    /// </summary>
    public static void MigrateUp(SqliteConnection connection)
    {
        var current = ReadVersion(connection);

        if (current > TargetVersion)
        {
            throw new StoreException(
                $"Database schema version {current} is newer than this build supports ({TargetVersion}).");
        }

        using var transaction = connection.BeginTransaction();
        using (var ensure = connection.CreateCommand())
        {
            ensure.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migration (
                    version    INTEGER PRIMARY KEY,
                    name       TEXT NOT NULL,
                    applied_at TEXT NOT NULL
                );
                """;
            ensure.ExecuteNonQuery();
        }

        foreach (var migration in Migrations)
        {
            if (migration.ToVersion <= current)
            {
                continue;
            }

            migration.Up(connection);

            using var record = connection.CreateCommand();
            record.CommandText = """
                INSERT INTO schema_migration (version, name, applied_at)
                VALUES ($version, $name, $applied_at);
                """;
            record.Parameters.AddWithValue("$version", migration.ToVersion);
            record.Parameters.AddWithValue("$name", migration.Name);
            record.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            record.ExecuteNonQuery();

            SetVersion(connection, migration.ToVersion);
        }

        transaction.Commit();
    }

    /// <summary>Reads the current <c>PRAGMA user_version</c>.</summary>
    public static int ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = command.ExecuteScalar();
        return value is long l ? (int)l : 0;
    }

    private static void SetVersion(SqliteConnection connection, int version)
    {
        // PRAGMA values cannot be parameterized; version is a build constant.
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }

    private static void InitialSchema(SqliteConnection connection)
    {
        Execute(connection, """
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
            """);
    }

    private static void RunNotesAndIndex(SqliteConnection connection)
    {
        Execute(connection, """
            ALTER TABLE runs ADD COLUMN notes TEXT NOT NULL DEFAULT '';

            CREATE INDEX idx_runs_profile_started ON runs (profile_id, started_at);
            """);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
