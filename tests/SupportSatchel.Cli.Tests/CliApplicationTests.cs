using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using SupportSatchel.Cli;
using SupportSatchel.Core.Domain;
using SupportSatchel.Core.Storage;
using Xunit;

namespace SupportSatchel.Cli.Tests;

public sealed class CliApplicationTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "support-satchel-cli-tests", Guid.NewGuid().ToString("N"));

    public CliApplicationTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temporary fixtures.
        }
    }

    [Fact]
    public void ProfilesListJsonIsOrderedAndDoesNotExposeSourcePaths()
    {
        var database = Path.Combine(root, "profiles.db");
        SaveProfile(database, Profile("Zulu", Path.Combine(root, "secret-zulu.log")));
        SaveProfile(database, Profile("alpha", Path.Combine(root, "secret-alpha.log")));
        var (exitCode, stdout, stderr) = Run(
            "--json", "profiles", "list", "--store", database);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        var profiles = document.RootElement.GetProperty("profiles");
        Assert.Equal(["alpha", "Zulu"], profiles.EnumerateArray()
            .Select(profile => profile.GetProperty("name").GetString()!).ToArray());
        Assert.DoesNotContain("secret-alpha", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-zulu", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfilesListCreatesMissingStoreDirectory()
    {
        var database = Path.Combine(root, "new", "nested", "profiles.db");

        var (exitCode, stdout, stderr) = Run("--json", "profiles", "list", "--store", database);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        Assert.True(File.Exists(database));
        using var document = JsonDocument.Parse(stdout);
        Assert.Empty(document.RootElement.GetProperty("profiles").EnumerateArray());
    }

    [Fact]
    public void VersionHonoursMachineReadableMode()
    {
        var (exitCode, stdout, stderr) = Run("--version", "--json");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("SupportSatchel.Core", document.RootElement.GetProperty("product").GetString());
        var metadataVersion = typeof(CliApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        Assert.Equal(metadataVersion, document.RootElement.GetProperty("version").GetString());
    }

    [Theory]
    [InlineData("--help", "extra")]
    [InlineData("--version", "extra")]
    public void HelpAndVersionRejectExtraneousArguments(string command, string extra)
    {
        var result = Run(command, extra);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void WhitespaceOnlyOptionValueIsRejected()
    {
        var result = Run("profiles", "list", "--store", "   ");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("cannot be empty", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RunJsonCapturesThenRedactsAndPersistsReviewReadyRun()
    {
        var database = Path.Combine(root, "profiles.db");
        var source = Path.Combine(root, "sanitized-sample.log");
        File.WriteAllText(source, "contact fixture.user@example.test\n");
        var profile = Profile("Sample profile", source);
        SaveProfile(database, profile);

        var (exitCode, stdout, stderr) = Run(
            "run", "--profile", profile.Id.ToString("D"),
            "--store", database, "--workspace-root", Path.Combine(root, "runs"), "--json");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("readyForReview", document.RootElement.GetProperty("status").GetString());
        Assert.True(document.RootElement.GetProperty("reviewRequired").GetBoolean());
        Assert.Equal(3, document.RootElement.GetProperty("artifactCount").GetInt32());
        var runId = document.RootElement.GetProperty("runId").GetGuid();

        using var store = new ProfileStore(database);
        var stored = store.GetRun(runId);
        Assert.Equal(RunStatus.Completed, stored.Status);
        Assert.True(File.Exists(Path.Combine(stored.WorkspacePath, "redaction-report.json")));
        Assert.Equal(
            "contact [REDACTED]\n",
            File.ReadAllText(Path.Combine(stored.WorkspacePath, "staging", "sample", "sanitized-sample.log")));
        Assert.Equal("contact fixture.user@example.test\n", File.ReadAllText(source));
    }

    [Fact]
    public void RunFailureIsMachineReadableAndDoesNotEchoSensitiveSourcePath()
    {
        const string SensitiveMarker = "DO_NOT_LEAK_this-customer-secret";
        var database = Path.Combine(root, "profiles.db");
        var profile = Profile("Broken profile", Path.Combine(root, SensitiveMarker, "missing.log"));
        SaveProfile(database, profile);

        var (exitCode, stdout, stderr) = Run(
            "--json", "run", "--profile", "Broken profile",
            "--store", database, "--workspace-root", Path.Combine(root, "runs"));

        Assert.NotEqual(0, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("collection_failed", document.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(SensitiveMarker, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFailureInHumanModeWritesSanitizedErrorToStderr()
    {
        const string SensitiveMarker = "DO_NOT_LEAK_human-mode-secret";
        var database = Path.Combine(root, "profiles.db");
        var profile = Profile("Broken profile", Path.Combine(root, SensitiveMarker, "missing.log"));
        SaveProfile(database, profile);

        var (exitCode, stdout, stderr) = Run(
            "run", "--profile", profile.Id.ToString("D"),
            "--store", database, "--workspace-root", Path.Combine(root, "runs"));

        Assert.NotEqual(0, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("Capture failed", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveMarker, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportJsonUsesCompletedRunAndNeverIncludesProvenance()
    {
        var database = Path.Combine(root, "profiles.db");
        var source = Path.Combine(root, "sample.log");
        File.WriteAllText(source, "token=abcdefghijklmnop\n");
        var profile = Profile("Export sample", source);
        SaveProfile(database, profile);
        var run = Run(
            "--json", "run", "--profile", profile.Name,
            "--store", database, "--workspace-root", Path.Combine(root, "runs"));
        using var runDocument = JsonDocument.Parse(run.Stdout);
        var runId = runDocument.RootElement.GetProperty("runId").GetGuid();

        var exported = Run(
            "export", "--run", runId.ToString("D"), "--store", database,
            "--output", Path.Combine(root, "exports"), "--reviewed", "--json");

        Assert.Equal(0, exported.ExitCode);
        Assert.Empty(exported.Stderr);
        using var exportDocument = JsonDocument.Parse(exported.Stdout);
        var bundlePath = exportDocument.RootElement.GetProperty("bundlePath").GetString();
        Assert.NotNull(bundlePath);
        using var archive = ZipFile.OpenRead(bundlePath!);
        Assert.DoesNotContain(archive.Entries, entry =>
            entry.FullName.StartsWith("provenance", StringComparison.Ordinal));
        Assert.Contains(archive.Entries, entry => entry.FullName == "redaction-report.json");
    }

    [Fact]
    public void ExportRequiresExplicitReviewedAcknowledgment()
    {
        var (database, runId, _) = CreateCompletedRun();

        var exported = Run(
            "export", "--run", runId.ToString("D"), "--store", database,
            "--output", Path.Combine(root, "exports"));

        Assert.Equal(2, exported.ExitCode);
        Assert.Contains("--reviewed", exported.Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, "exports")));
    }

    [Fact]
    public void ExportRejectsSymlinkedArtifact()
    {
        var (database, runId, workspace) = CreateCompletedRun();
        var artifact = Directory.GetFiles(Path.Combine(workspace, "staging", "sample")).Single();
        var outside = Path.Combine(root, "outside-secret.log");
        File.WriteAllText(outside, "same-user secret");
        File.Delete(artifact);
        File.CreateSymbolicLink(artifact, outside);

        var exported = Run(
            "export", "--run", runId.ToString("D"), "--store", database,
            "--output", Path.Combine(root, "exports"), "--reviewed");

        Assert.Equal(5, exported.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(root, "exports")));
    }

    [Fact]
    public void ExportRejectsSymlinkedStagingAncestor()
    {
        var (database, runId, workspace) = CreateCompletedRun();
        var staging = Path.Combine(workspace, "staging");
        var outside = Path.Combine(root, "outside-staging");
        Directory.Move(staging, outside);
        Directory.CreateSymbolicLink(staging, outside);

        var exported = Run(
            "export", "--run", runId.ToString("D"), "--store", database,
            "--output", Path.Combine(root, "exports"), "--reviewed");

        Assert.Equal(5, exported.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(root, "exports")));
    }

    [Fact]
    public void ExportRejectsSymlinkedWorkspace()
    {
        var (database, runId, workspace) = CreateCompletedRun();
        var outside = workspace + "-moved";
        Directory.Move(workspace, outside);
        Directory.CreateSymbolicLink(workspace, outside);

        var exported = Run(
            "export", "--run", runId.ToString("D"), "--store", database,
            "--output", Path.Combine(root, "exports"), "--reviewed");

        Assert.Equal(5, exported.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(root, "exports")));
    }

    [Fact]
    public void ExportAllowsWorkspaceBelowSymlinkedParent()
    {
        var actualParent = Path.Combine(root, "private", "var");
        Directory.CreateDirectory(actualParent);
        var linkedParent = Path.Combine(root, "var");
        Directory.CreateSymbolicLink(linkedParent, actualParent);

        var database = Path.Combine(root, "profiles.db");
        var source = Path.Combine(root, "sample.log");
        File.WriteAllText(source, "contact fixture.user@example.test\n");
        var profile = Profile("Linked parent export", source);
        SaveProfile(database, profile);
        var run = Run(
            "--json", "run", "--profile", profile.Id.ToString("D"),
            "--store", database, "--workspace-root", Path.Combine(linkedParent, "runs"));
        Assert.Equal(0, run.ExitCode);
        using var document = JsonDocument.Parse(run.Stdout);
        var runId = document.RootElement.GetProperty("runId").GetGuid();

        var exported = Run(
            "export", "--run", runId.ToString("D"), "--store", database,
            "--output", Path.Combine(root, "exports"), "--reviewed");

        Assert.Equal(0, exported.ExitCode);
        Assert.Empty(exported.Stderr);
        Assert.True(Directory.Exists(Path.Combine(root, "exports")));
    }

    [Fact]
    public void UnexpectedRunFailureIsPersistedAsFailedWithSanitizedNotes()
    {
        const string SensitiveMarker = "DO_NOT_LEAK_workspace-secret";
        var database = Path.Combine(root, "profiles.db");
        var source = Path.Combine(root, "sample.log");
        File.WriteAllText(source, "safe");
        var profile = Profile("Failure state", source);
        SaveProfile(database, profile);
        var invalidWorkspaceRoot = Path.Combine(root, SensitiveMarker);
        File.WriteAllText(invalidWorkspaceRoot, "not a directory");

        var result = Run(
            "run", "--profile", profile.Id.ToString("D"), "--store", database,
            "--workspace-root", invalidWorkspaceRoot);

        Assert.Equal(5, result.ExitCode);
        using var store = new ProfileStore(database);
        var stored = Assert.Single(store.ListRuns(profile.Id));
        Assert.Equal(RunStatus.Failed, stored.Status);
        Assert.NotNull(stored.FinishedAtUtc);
        Assert.DoesNotContain(SensitiveMarker, stored.Notes, StringComparison.Ordinal);
    }

    private (string Database, Guid RunId, string Workspace) CreateCompletedRun()
    {
        var database = Path.Combine(root, Guid.NewGuid().ToString("N") + ".db");
        var source = Path.Combine(root, Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(source, "contact fixture.user@example.test\n");
        var profile = Profile("Export fixture", source);
        SaveProfile(database, profile);
        var run = Run(
            "--json", "run", "--profile", profile.Id.ToString("D"),
            "--store", database, "--workspace-root", Path.Combine(root, "runs"));
        Assert.Equal(0, run.ExitCode);
        using var document = JsonDocument.Parse(run.Stdout);
        var runId = document.RootElement.GetProperty("runId").GetGuid();
        using var store = new ProfileStore(database);
        return (database, runId, store.GetRun(runId).WorkspacePath);
    }

    private (int ExitCode, string Stdout, string Stderr) Run(params string[] arguments)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = CliApplication.Run(arguments, stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static BundleProfile Profile(string name, string sourcePath) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
        Sources =
        [
            new CaptureSource
            {
                Id = "sample",
                Kind = SourceKind.File,
                Path = sourcePath,
                Required = true,
            },
        ],
    };

    private static void SaveProfile(string database, BundleProfile profile)
    {
        using var store = new ProfileStore(database);
        store.SaveProfile(profile);
    }
}
