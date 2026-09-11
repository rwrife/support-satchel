using SupportSatchel.Core.Collecting;
using SupportSatchel.Core.Domain;
using Xunit;

namespace SupportSatchel.Core.Tests;

/// <summary>
/// Integration tests for the collector pipeline using sanitized fixtures
/// built on disk in a temporary directory. Tests assert the stable output
/// structure and that sources are never mutated.
/// </summary>
public class CollectorTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 2, 1, 10, 30, 0, TimeSpan.Zero);

    private readonly string root;

    public CollectorTests()
    {
        root = Path.Combine(Path.GetTempPath(), "satchel-collector-tests", Guid.NewGuid().ToString("N"));
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

    private BundleProfile ProfileWith(params CaptureSource[] sources) =>
        TestProfiles.Valid(sources: sources);

    private CollectorOptions Options() => new() { Clock = () => FixedNow };

    [Fact]
    public void CollectsFileAndFolderSourcesWithDeterministicStagingLayout()
    {
        WriteFixture("logs/app.log", "hello log");
        WriteFixture("logs/nested/app2.log", "nested log");
        WriteFixture("logs/app.log.bak", "ignore me");
        WriteFixture("config.json", "{\"a\":1}");

        var profile = ProfileWith(
            new CaptureSource
            {
                Id = "src-logs",
                Kind = SourceKind.Folder,
                Path = Path.Combine(root, "logs"),
                IncludePatterns = ["*.log"],
                ExcludePatterns = ["*.bak"],
                Required = true,
            },
            new CaptureSource
            {
                Id = "src-config",
                Kind = SourceKind.File,
                Path = Path.Combine(root, "config.json"),
            });

        var workspaceRoot = Path.Combine(root, "workspaces");
        var result = new Collector().Run(profile, workspaceRoot, Options());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.Skipped);

        var stagedPaths = result.Artifacts.Select(a => a.StagedPath).ToList();
        Assert.Contains("src-config/config.json", stagedPaths);
        Assert.Contains("src-logs/app.log", stagedPaths);
        Assert.Contains("src-logs/nested/app2.log", stagedPaths);
        Assert.DoesNotContain(stagedPaths, p => p.EndsWith(".bak", StringComparison.Ordinal));

        // Probe outputs are always staged.
        Assert.Contains("built-in-probes/os-info.txt", stagedPaths);
        Assert.Contains("built-in-probes/runtime-info.txt", stagedPaths);

        // Order is stable (ordinal by staged path).
        Assert.Equal(stagedPaths.OrderBy(p => p, StringComparer.Ordinal), stagedPaths);

        // Staged content is a faithful copy.
        var stagedCopy = Path.Combine(result.WorkspacePath, Collector.StagingDirectory, "src-logs/app.log");
        Assert.Equal("hello log", File.ReadAllText(stagedCopy));
    }

    [Fact]
    public void TwoRunsWithIdenticalInputYieldIdenticalLayoutAndOrdering()
    {
        WriteFixture("logs/a.log", "one");
        WriteFixture("logs/b.log", "two");
        var profile = ProfileWith(new CaptureSource
        {
            Id = "src-logs",
            Kind = SourceKind.Folder,
            Path = Path.Combine(root, "logs"),
            IncludePatterns = ["*.log"],
        });

        var r1 = new Collector().Run(profile, Path.Combine(root, "ws1"), Options());
        var r2 = new Collector().Run(profile, Path.Combine(root, "ws2"), Options());

        Assert.Equal(
            r1.Artifacts.Select(a => a.StagedPath),
            r2.Artifacts.Select(a => a.StagedPath));

        // Every provenance sidecar for file sources must serialize identically.
        var sidecars1 = Directory.GetFiles(Path.Combine(r1.WorkspacePath, Collector.ProvenanceDirectory))
            .Select(File.ReadAllText).ToList();
        var sidecars2 = Directory.GetFiles(Path.Combine(r2.WorkspacePath, Collector.ProvenanceDirectory))
            .Select(File.ReadAllText).ToList();

        // Probe sidecars carry the fixed clock; file sidecars carry source
        // mtimes which are stable between runs. Compare sorted sets.
        Assert.Equal(sidecars1.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            sidecars2.OrderBy(s => s, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void SourcesAreNeverMutated()
    {
        var file = WriteFixture("logs/app.log", "original content");
        var profile = ProfileWith(new CaptureSource
        {
            Id = "src-logs",
            Kind = SourceKind.Folder,
            Path = Path.Combine(root, "logs"),
        });

        var before = File.ReadAllText(file);
        var result = new Collector().Run(profile, Path.Combine(root, "ws"), Options());

        Assert.True(result.IsSuccessful);
        Assert.Equal(before, File.ReadAllText(file));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void MissingOptionalSourceIsSkippedAndRequiredSourceErrors()
    {
        WriteFixture("logs/app.log", "x");
        var profile = ProfileWith(
            new CaptureSource
            {
                Id = "src-optional",
                Kind = SourceKind.File,
                Path = Path.Combine(root, "does-not-exist.txt"),
            },
            new CaptureSource
            {
                Id = "src-required",
                Kind = SourceKind.Folder,
                Path = Path.Combine(root, "no-such-dir"),
                Required = true,
            },
            new CaptureSource
            {
                Id = "src-empty",
                Kind = SourceKind.Folder,
                Path = Path.Combine(root, "logs"),
                IncludePatterns = ["*.never"],
            });

        var result = new Collector().Run(profile, Path.Combine(root, "ws"), Options());

        Assert.False(result.IsSuccessful);
        Assert.Contains(result.Errors, e => e.StartsWith("src-required", StringComparison.Ordinal));
        Assert.Contains(result.Skipped, s => s.SourceId == "src-optional");
        Assert.Contains(result.Skipped, s => s.SourceId == "src-empty"
            && s.Reason.Contains("no files matched", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryArtifactHasProvenanceSidecarWithExpectedFields()
    {
        WriteFixture("logs/app.log", "content");
        var profile = ProfileWith(new CaptureSource
        {
            Id = "src-logs",
            Kind = SourceKind.Folder,
            Path = Path.Combine(root, "logs"),
        });

        var result = new Collector().Run(profile, Path.Combine(root, "ws"), Options());

        Assert.Equal(
            result.Artifacts.Count,
            Directory.GetFiles(Path.Combine(result.WorkspacePath, Collector.ProvenanceDirectory)).Length);

        foreach (var artifact in result.Artifacts)
        {
            var sidecarName = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(artifact.StagedPath)) + ".json";
            var sidecarPath = Path.Combine(result.WorkspacePath, Collector.ProvenanceDirectory, sidecarName);
            Assert.True(File.Exists(sidecarPath), $"missing sidecar for {artifact.StagedPath}");

            var json = File.ReadAllText(sidecarPath);
            Assert.Contains("\"stagedPath\"", json);
            Assert.Contains(artifact.StagedPath.Replace("\\", "\\\\"), json);
            Assert.Contains("\"sourceId\"", json);

            switch (artifact.Provenance)
            {
                case FileProvenance file:
                    Assert.Contains("\"kind\": \"file\"", json);
                    Assert.Equal(GlobMatcher.NormalizePath(Path.GetRelativePath(file.SourceRoot, Path.Combine(file.SourceRoot, file.RelativeSourcePath))),
                        file.RelativeSourcePath);
                    break;
                case ProbeProvenance probe:
                    Assert.Contains("\"kind\": \"probe\"", json);
                    Assert.Contains(probe.ProbeId, json);
                    break;
            }
        }
    }

    [Fact]
    public void ProbeOutputsAreDeterministicUnderFixedClock()
    {
        var profile = ProfileWith(new CaptureSource
        {
            Id = "src-logs",
            Kind = SourceKind.File,
            Path = WriteFixture("logs/app.log", "x"),
        });

        var r1 = new Collector().Run(profile, Path.Combine(root, "ws1"), Options());
        var r2 = new Collector().Run(profile, Path.Combine(root, "ws2"), Options());

        string Read(CollectionResult r, string staged) =>
            File.ReadAllText(Path.Combine(r.WorkspacePath, Collector.StagingDirectory, staged));

        Assert.Equal(Read(r1, "built-in-probes/os-info.txt"), Read(r2, "built-in-probes/os-info.txt"));
        Assert.Contains("capturedUtc=2026-02-01T10:30:00.000Z", Read(r1, "built-in-probes/os-info.txt"));
    }
}
