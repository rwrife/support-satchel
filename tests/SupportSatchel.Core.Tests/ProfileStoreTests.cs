using Microsoft.Data.Sqlite;
using SupportSatchel.Core.Domain;
using SupportSatchel.Core.Storage;
using Xunit;

namespace SupportSatchel.Core.Tests;

/// <summary>Acceptance: local persistence stores/retrieves profiles and run metadata.</summary>
public class ProfileStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ProfileStore _store;

    public ProfileStoreTests()
    {
        // Shared in-memory database kept alive by this connection.
        _connection = new SqliteConnection("Data Source=:memory:");
        _store = new ProfileStore(_connection);
    }

    public void Dispose()
    {
        _store.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void SaveAndRetrieveProfileRoundTrips()
    {
        var profile = TestProfiles.Valid();
        _store.SaveProfile(profile);

        var restored = _store.GetProfile(profile.Id);
        Assert.Equal(profile, restored);
    }

    [Fact]
    public void RetrieveByNameWorks()
    {
        var profile = TestProfiles.Valid(name: "My App");
        _store.SaveProfile(profile);

        Assert.Equal(profile, _store.GetProfileByName("My App"));
    }

    [Fact]
    public void ListProfilesOrdersByNameCaseInsensitively()
    {
        _store.SaveProfile(TestProfiles.Valid(id: Guid.NewGuid(), name: "beta"));
        _store.SaveProfile(TestProfiles.Valid(id: Guid.NewGuid(), name: "Alpha"));
        _store.SaveProfile(TestProfiles.Valid(id: Guid.NewGuid(), name: "gamma"));

        var names = _store.ListProfiles().Select(p => p.Name).ToList();
        Assert.Equal(["Alpha", "beta", "gamma"], names);
    }

    [Fact]
    public void DuplicateNameIsRejectedWithTypedException()
    {
        _store.SaveProfile(TestProfiles.Valid(id: Guid.NewGuid(), name: "dup"));
        var ex = Assert.Throws<ProfileNameConflictException>(
            () => _store.SaveProfile(TestProfiles.Valid(id: Guid.NewGuid(), name: "dup")));
        Assert.Contains("dup", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidProfileIsNeverPersisted()
    {
        var invalid = TestProfiles.Valid(name: "   ") with { Id = Guid.NewGuid() };
        Assert.Throws<ProfileValidationException>(() => _store.SaveProfile(invalid));
        Assert.Empty(_store.ListProfiles());
    }

    [Fact]
    public void GetMissingProfileThrows()
    {
        Assert.Throws<ProfileNotFoundException>(() => _store.GetProfile(Guid.NewGuid()));
    }

    [Fact]
    public void UpdateProfileReplacesDocument()
    {
        var profile = TestProfiles.Valid(id: Guid.NewGuid(), name: "before");
        _store.SaveProfile(profile);

        var updated = profile with
        {
            Name = "after",
            Description = "edited",
            UpdatedAtUtc = TestProfiles.BaseTime.AddDays(1),
        };
        _store.UpdateProfile(updated);

        Assert.Equal(updated, _store.GetProfile(profile.Id));
    }

    [Fact]
    public void UpdateUnknownProfileThrows()
    {
        Assert.Throws<ProfileNotFoundException>(
            () => _store.UpdateProfile(TestProfiles.Valid(id: Guid.NewGuid())));
    }

    [Fact]
    public void UpdateRenameOntoExistingNameThrows()
    {
        _store.SaveProfile(TestProfiles.Valid(id: Guid.NewGuid(), name: "taken"));
        var other = TestProfiles.Valid(id: Guid.NewGuid(), name: "free");
        _store.SaveProfile(other);

        Assert.Throws<ProfileNameConflictException>(
            () => _store.UpdateProfile(other with { Name = "taken" }));
    }

    [Fact]
    public void DeleteRemovesProfileAndCascadesRuns()
    {
        var profile = TestProfiles.Valid(id: Guid.NewGuid());
        _store.SaveProfile(profile);
        var run = TestProfiles.ValidRun(profile.Id);
        _store.SaveRun(run);

        _store.DeleteProfile(profile.Id);

        Assert.Throws<ProfileNotFoundException>(() => _store.GetProfile(profile.Id));
        Assert.Empty(_store.ListRuns(profile.Id));
    }

    [Fact]
    public void RunLifecyclePersistsStatusAndFinishTime()
    {
        var profile = TestProfiles.Valid(id: Guid.NewGuid());
        _store.SaveProfile(profile);
        var run = TestProfiles.ValidRun(profile.Id);
        _store.SaveRun(run);

        var finished = run with
        {
            Status = RunStatus.Completed,
            FinishedAtUtc = TestProfiles.BaseTime.AddMinutes(3),
            Notes = "collected 42 artifacts",
        };
        _store.UpdateRun(finished);

        var restored = _store.GetRun(run.Id);
        Assert.Equal(RunStatus.Completed, restored.Status);
        Assert.Equal(finished.FinishedAtUtc, restored.FinishedAtUtc);
        Assert.Equal("collected 42 artifacts", restored.Notes);
    }

    [Fact]
    public void ListRunsIsNewestFirst()
    {
        var profile = TestProfiles.Valid(id: Guid.NewGuid());
        _store.SaveProfile(profile);

        var first = TestProfiles.ValidRun(profile.Id, Guid.NewGuid()) with
        {
            StartedAtUtc = TestProfiles.BaseTime,
        };
        var second = first with
        {
            Id = Guid.NewGuid(),
            StartedAtUtc = TestProfiles.BaseTime.AddHours(1),
        };
        _store.SaveRun(first);
        _store.SaveRun(second);

        var runs = _store.ListRuns(profile.Id);
        Assert.Equal([second.Id, first.Id], runs.Select(r => r.Id).ToList());
    }

    [Fact]
    public void SaveRunForUnknownProfileIsRejected()
    {
        var orphan = TestProfiles.ValidRun(Guid.NewGuid());
        Assert.Throws<SqliteException>(() => _store.SaveRun(orphan));
    }

    [Fact]
    public void UpdateUnknownRunThrows()
    {
        Assert.Throws<RunNotFoundException>(
            () => _store.UpdateRun(TestProfiles.ValidRun(Guid.NewGuid())));
    }

    [Fact]
    public void StoreSurvivesReopenOfSameFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"satchel-test-{Guid.NewGuid():N}.db");
        try
        {
            var profile = TestProfiles.Valid(id: Guid.NewGuid(), name: "durable");
            using (var store = new ProfileStore(path))
            {
                store.SaveProfile(profile);
            }

            using var reopened = new ProfileStore(path);
            Assert.Equal(profile, reopened.GetProfile(profile.Id));
            Assert.Equal(Schema.TargetVersion, reopened.SchemaVersion);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
