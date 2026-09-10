using Microsoft.Data.Sqlite;
using SupportSatchel.Core.Domain;

namespace SupportSatchel.Core.Storage;

/// <summary>
/// SQLite-backed local store for bundle profiles and run metadata.
///
/// Design notes:
/// <list type="bullet">
/// <item>A profile is stored as one canonical JSON <c>document</c> plus
/// indexed columns (id, name, timestamps). The document is the source of
/// truth; columns exist for lookups and constraints only.</item>
/// <item>Writes validate through <see cref="ProfileValidator"/> before
/// touching the database.</item>
/// <item>The store is single-writer, multi-reader, matching a desktop
/// app; enable WAL for concurrent readers during long runs.</item>
/// </list>
/// </summary>
public sealed class ProfileStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;

    /// <summary>Opens (creating if needed) the database at a file path.</summary>
    public ProfileStore(string databasePath)
        : this(new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
        }.ToString()))
    {
    }

    /// <summary>
    /// Adopts an open connection (used by tests with in-memory databases).
    /// Takes ownership: <see cref="Dispose"/> closes it.
    /// </summary>
    public ProfileStore(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _connection.Open();

        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        Schema.MigrateUp(_connection);
    }

    /// <summary>Current database schema version.</summary>
    public int SchemaVersion => Schema.ReadVersion(_connection);

    /// <summary>Validates and inserts a new profile. Name must be unique.</summary>
    public void SaveProfile(BundleProfile profile)
    {
        ThrowIfDisposed();
        var validation = ProfileValidator.Validate(profile);
        if (!validation.IsValid)
        {
            throw new ProfileValidationException(validation);
        }

        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO profiles (id, name, document, created_at, updated_at)
            VALUES ($id, $name, $document, $created_at, $updated_at);
            """;
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$document", ProfileJson.Serialize(profile));
        command.Parameters.AddWithValue("$created_at", profile.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", profile.UpdatedAtUtc.ToString("O"));

        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (IsUniqueViolation(ex))
        {
            throw new ProfileNameConflictException(profile.Name);
        }
    }

    /// <summary>
    /// Validates and replaces the stored document for <paramref name="profile"/>
    /// (matched by id). Renaming to a name owned by another profile fails.
    /// </summary>
    public void UpdateProfile(BundleProfile profile)
    {
        ThrowIfDisposed();
        var validation = ProfileValidator.Validate(profile);
        if (!validation.IsValid)
        {
            throw new ProfileValidationException(validation);
        }

        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE profiles
            SET name = $name, document = $document, updated_at = $updated_at
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$document", ProfileJson.Serialize(profile));
        command.Parameters.AddWithValue("$updated_at", profile.UpdatedAtUtc.ToString("O"));

        int rows;
        try
        {
            rows = command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (IsUniqueViolation(ex))
        {
            throw new ProfileNameConflictException(profile.Name);
        }

        if (rows == 0)
        {
            throw new ProfileNotFoundException(profile.Id);
        }
    }

    /// <summary>Loads a profile by id, or throws <see cref="ProfileNotFoundException"/>.</summary>
    public BundleProfile GetProfile(Guid id)
    {
        ThrowIfDisposed();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT document FROM profiles WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));

        var document = command.ExecuteScalar() as string
            ?? throw new ProfileNotFoundException(id);

        return ProfileJson.Deserialize(document);
    }

    /// <summary>Loads a profile by exact name, or throws <see cref="ProfileNotFoundException"/>.</summary>
    public BundleProfile GetProfileByName(string name)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT document FROM profiles WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);

        var document = command.ExecuteScalar() as string
            ?? throw new ProfileNotFoundException(Guid.Empty);

        return ProfileJson.Deserialize(document);
    }

    /// <summary>Lists all profiles ordered by name.</summary>
    public IReadOnlyList<BundleProfile> ListProfiles()
    {
        ThrowIfDisposed();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT document FROM profiles ORDER BY name COLLATE NOCASE;";

        var profiles = new List<BundleProfile>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            profiles.Add(ProfileJson.Deserialize(reader.GetString(0)));
        }

        return profiles;
    }

    /// <summary>
    /// Deletes a profile and (via cascade) its run metadata.
    /// Throws <see cref="ProfileNotFoundException"/> when the id is unknown.
    /// </summary>
    public void DeleteProfile(Guid id)
    {
        ThrowIfDisposed();
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM profiles WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));

        if (command.ExecuteNonQuery() == 0)
        {
            throw new ProfileNotFoundException(id);
        }
    }

    /// <summary>Persists a newly started run.</summary>
    public void SaveRun(RunRecord run)
    {
        ThrowIfDisposed();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runs (id, profile_id, profile_name, started_at, finished_at, status, workspace_path, notes)
            VALUES ($id, $profile_id, $profile_name, $started_at, $finished_at, $status, $workspace_path, $notes);
            """;
        command.Parameters.AddWithValue("$id", run.Id.ToString("D"));
        command.Parameters.AddWithValue("$profile_id", run.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$profile_name", run.ProfileName);
        command.Parameters.AddWithValue("$started_at", run.StartedAtUtc.ToString("O"));
        command.Parameters.AddWithValue(
            "$finished_at",
            (object?)run.FinishedAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)run.Status);
        command.Parameters.AddWithValue("$workspace_path", run.WorkspacePath);
        command.Parameters.AddWithValue("$notes", run.Notes);
        command.ExecuteNonQuery();
    }

    /// <summary>Updates the mutable fields of a stored run (status, finish time, notes).</summary>
    public void UpdateRun(RunRecord run)
    {
        ThrowIfDisposed();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE runs
            SET status = $status, finished_at = $finished_at, notes = $notes
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", run.Id.ToString("D"));
        command.Parameters.AddWithValue("$status", (int)run.Status);
        command.Parameters.AddWithValue(
            "$finished_at",
            (object?)run.FinishedAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", run.Notes);

        if (command.ExecuteNonQuery() == 0)
        {
            throw new RunNotFoundException(run.Id);
        }
    }

    /// <summary>Loads a run by id, or throws <see cref="RunNotFoundException"/>.</summary>
    public RunRecord GetRun(Guid id)
    {
        ThrowIfDisposed();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, profile_id, profile_name, started_at, finished_at, status, workspace_path, notes
            FROM runs WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new RunNotFoundException(id);
        }

        return ReadRun(reader);
    }

    /// <summary>Lists runs for one profile, newest first.</summary>
    public IReadOnlyList<RunRecord> ListRuns(Guid profileId)
    {
        ThrowIfDisposed();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, profile_id, profile_name, started_at, finished_at, status, workspace_path, notes
            FROM runs WHERE profile_id = $profile_id ORDER BY started_at DESC;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId.ToString("D"));

        var runs = new List<RunRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            runs.Add(ReadRun(reader));
        }

        return runs;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }

    private static RunRecord ReadRun(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        ProfileId = Guid.Parse(reader.GetString(1)),
        ProfileName = reader.GetString(2),
        StartedAtUtc = DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
        FinishedAtUtc = reader.IsDBNull(4)
            ? null
            : DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
        Status = (RunStatus)reader.GetInt32(5),
        WorkspacePath = reader.GetString(6),
        Notes = reader.GetString(7),
    };

    private static bool IsUniqueViolation(SqliteException ex) =>
        ex.SqliteErrorCode == 19 && ex.SqliteExtendedErrorCode == 2067;

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
