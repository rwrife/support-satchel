using System.Text;
using SupportSatchel.Core.Collecting;
using SupportSatchel.Core.Domain;
using SupportSatchel.Core.Redacting;
using Xunit;

namespace SupportSatchel.Core.Tests;

/// <summary>
/// Integration tests for redaction over real collector workspaces: staged
/// copies only, per-artifact preview fidelity, report determinism, and the
/// fail-closed behaviours packaging (issue #5) will rely on.
/// </summary>
public class RedactionEngineWorkspaceTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 3, 5, 8, 15, 0, TimeSpan.Zero);

    private readonly string root;

    public RedactionEngineWorkspaceTests()
    {
        root = Path.Combine(Path.GetTempPath(), "satchel-redaction-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp fixtures.
        }
    }

    private string WriteFixture(string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private string WriteBinaryFixture(string relative, byte[] content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    private CollectionResult Collect(out string sourceFile)
    {
        sourceFile = WriteFixture("logs/app.log", "contact jane.doe@example.com\n");
        var profile = TestProfiles.Valid(sources:
        [
            new CaptureSource
            {
                Id = "src-logs",
                Kind = SourceKind.Folder,
                Path = Path.Combine(root, "logs"),
                Required = true,
            },
        ]);
        return new Collector(new List<IDiagnosticProbe> { new OperatingSystemProbe() })
            .Run(profile, Path.Combine(root, "ws"), new CollectorOptions { Clock = () => FixedNow });
    }

    private static string Staged(CollectionResult result, string stagedPath) =>
        File.ReadAllText(Path.Combine(
            result.WorkspacePath,
            Collector.StagingDirectory,
            stagedPath.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void ApplyRedactsStagedCopyAndLeavesSourceAndProvenanceUntouched()
    {
        var result = Collect(out var sourceFile);
        var engine = new RedactionEngine();
        var sidecarBefore = Directory.GetFiles(
                Path.Combine(result.WorkspacePath, Collector.ProvenanceDirectory))
            .ToDictionary(File.ReadAllText);

        var run = engine.ApplyWorkspace(result, RedactionSettings.CreateDefault(), ReportOptions());

        Assert.True(run.IsSuccessful);
        Assert.Contains("[REDACTED]", Staged(result, "src-logs/app.log"), StringComparison.Ordinal);

        // Original source file is byte-identical.
        Assert.Equal("contact jane.doe@example.com\n", File.ReadAllText(sourceFile));

        // Provenance sidecars are untouched.
        foreach (var file in Directory.GetFiles(Path.Combine(result.WorkspacePath, Collector.ProvenanceDirectory)))
        {
            Assert.Contains(File.ReadAllText(file), sidecarBefore.Keys);
        }

        // Report exists at workspace root and round-trips.
        Assert.Equal(Path.Combine(result.WorkspacePath, RedactionEngine.ReportFileName), run.ReportPath);
        Assert.True(File.Exists(run.ReportPath));
        var report = RedactionReportJson.Deserialize(File.ReadAllText(run.ReportPath));
        Assert.NotNull(report);
        Assert.True(report!.IsSuccessful);
        Assert.Equal("2026-03-05T08:15:00.000Z",
            report.GeneratedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
    }

    [Fact]
    public void PreviewIsReadOnlyAndShowsOriginalVersusRedactedPerArtifact()
    {
        var result = Collect(out _);
        var engine = new RedactionEngine();

        var preview = engine.PreviewWorkspace(result, RedactionSettings.CreateDefault());

        var artifact = Assert.Single(
            preview.Artifacts,
            a => a.StagedPath == "src-logs/app.log");
        Assert.Equal(ArtifactRedactionKind.Text, artifact.Kind);
        Assert.Equal("contact jane.doe@example.com\n", artifact.OriginalText);
        Assert.Equal("contact [REDACTED]\n", artifact.RedactedText);
        Assert.True(artifact.Changed);
        Assert.Single(artifact.Matches);

        // Previewing mutated nothing.
        Assert.Equal("contact jane.doe@example.com\n", Staged(result, "src-logs/app.log"));
        Assert.False(File.Exists(Path.Combine(result.WorkspacePath, RedactionEngine.ReportFileName)));
    }

    [Fact]
    public void BinaryAndBomArtifactsAreClassifiedAndHandledCorrectly()
    {
        WriteBinaryFixture("logs/blob.bin", [0xFF, 0xFE, 0x00, 0x80, 0x41]);
        var bomPath = Path.Combine(root, "logs", "bom.log");
        File.WriteAllText(
            bomPath,
            "user contact: jane.doe@example.com",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var profile = TestProfiles.Valid(sources:
        [
            new CaptureSource
            {
                Id = "src-logs",
                Kind = SourceKind.Folder,
                Path = Path.Combine(root, "logs"),
                Required = true,
            },
        ]);
        var result = new Collector(new List<IDiagnosticProbe> { new OperatingSystemProbe() })
            .Run(profile, Path.Combine(root, "ws"), new CollectorOptions { Clock = () => FixedNow });
        var engine = new RedactionEngine();

        var preview = engine.PreviewWorkspace(result, RedactionSettings.CreateDefault());
        var binary = Assert.Single(preview.Artifacts, a => a.StagedPath == "src-logs/blob.bin");
        Assert.Equal(ArtifactRedactionKind.Binary, binary.Kind);
        Assert.Null(binary.RedactedText);

        var run = engine.ApplyWorkspace(result, RedactionSettings.CreateDefault());
        Assert.True(run.IsSuccessful);

        // Binary stays byte-for-byte identical.
        var stagedBin = Path.Combine(
            result.WorkspacePath, Collector.StagingDirectory, "src-logs", "blob.bin");
        Assert.Equal([0xFF, 0xFE, 0x00, 0x80, 0x41], File.ReadAllBytes(stagedBin));

        // BOM survives redaction of the text copy.
        var stagedBom = Path.Combine(
            result.WorkspacePath, Collector.StagingDirectory, "src-logs", "bom.log");
        var bomBytes = File.ReadAllBytes(stagedBom);
        Assert.Equal(0xEF, bomBytes[0]);
        Assert.Equal(0xBB, bomBytes[1]);
        Assert.Equal(0xBF, bomBytes[2]);
        Assert.Contains("[REDACTED]", File.ReadAllText(stagedBom), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingStagedCopyIsReportedAsMissingAndRunFailsClosed()
    {
        var result = Collect(out _);
        // Simulate a staged copy deleted between collection and redaction.
        File.Delete(Path.Combine(
            result.WorkspacePath, Collector.StagingDirectory, "src-logs", "app.log"));
        var engine = new RedactionEngine();

        var run = engine.ApplyWorkspace(result, RedactionSettings.CreateDefault());

        Assert.False(run.IsSuccessful);
        Assert.Contains(run.Errors, e => e.StartsWith("src-logs/app.log", StringComparison.Ordinal)
            && e.Contains("missing", StringComparison.Ordinal));
        var outcome = Assert.Single(run.Artifacts, a => a.StagedPath == "src-logs/app.log");
        Assert.Equal(ArtifactRedactionKind.Missing, outcome.Kind);
        Assert.False(outcome.Redacted);
    }

    [Fact]
    public void AbortedRuleLeavesArtifactUnredactedAndFailsRun()
    {
        var result = Collect(out _);
        var settings = new RedactionSettings
        {
            Rules =
            [
                new RedactionRule
                {
                    Id = "bad",
                    Name = "Broken regex",
                    Pattern = "([unclosed",
                },
            ],
        };
        var engine = new RedactionEngine();

        var run = engine.ApplyWorkspace(result, settings);

        Assert.False(run.IsSuccessful);
        Assert.Contains(run.Errors, e => e.Contains("aborted", StringComparison.Ordinal));

        // Fail closed: the secret is still in the staged copy, and the run
        // is marked unsuccessful so packaging refuses to export it.
        Assert.Contains("jane.doe@example.com", Staged(result, "src-logs/app.log"), StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyIsIdempotentOnAlreadyRedactedCopies()
    {
        var result = Collect(out _);
        var engine = new RedactionEngine();

        var first = engine.ApplyWorkspace(result, RedactionSettings.CreateDefault());
        var afterFirst = Staged(result, "src-logs/app.log");
        var second = engine.ApplyWorkspace(result, RedactionSettings.CreateDefault());

        Assert.True(first.IsSuccessful);
        Assert.True(second.IsSuccessful);
        Assert.Equal(afterFirst, Staged(result, "src-logs/app.log"));
        Assert.DoesNotContain(
            second.Artifacts,
            a => a.StagedPath == "src-logs/app.log" && a.Redacted);
    }

    [Fact]
    public void TwoIdenticalWorkspacesProduceByteIdenticalReports()
    {
        var engine = new RedactionEngine();

        var r1 = CollectInto("ws1");
        var r2 = CollectInto("ws2");
        var run1 = engine.ApplyWorkspace(r1, RedactionSettings.CreateDefault(), ReportOptions());
        var run2 = engine.ApplyWorkspace(r2, RedactionSettings.CreateDefault(), ReportOptions());

        var report1 = File.ReadAllText(run1.ReportPath);
        var report2 = File.ReadAllText(run2.ReportPath);
        Assert.Equal(report1, report2);

        // The report must not carry absolute workspace paths (it may ship
        // inside the exported bundle).
        Assert.DoesNotContain(r1.WorkspacePath, report1, StringComparison.Ordinal);
        Assert.DoesNotContain(root, report1, StringComparison.Ordinal);
    }

    private CollectionResult CollectInto(string name)
    {
        var fixtureRoot = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(fixtureRoot, "logs"));
        File.WriteAllText(
            Path.Combine(fixtureRoot, "logs", "app.log"),
            "host line: hostname: dev-box\n");
        var profile = TestProfiles.Valid(sources:
        [
            new CaptureSource
            {
                Id = "src-logs",
                Kind = SourceKind.Folder,
                Path = Path.Combine(fixtureRoot, "logs"),
                Required = true,
            },
        ]);
        return new Collector(new List<IDiagnosticProbe> { new OperatingSystemProbe() })
            .Run(profile, Path.Combine(root, name + "-ws"), new CollectorOptions { Clock = () => FixedNow });
    }

    private static RedactionOptions ReportOptions() => new() { Clock = () => FixedNow };
}
