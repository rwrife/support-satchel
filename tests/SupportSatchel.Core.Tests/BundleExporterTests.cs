using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SupportSatchel.Core.Collecting;
using SupportSatchel.Core.Domain;
using SupportSatchel.Core.Packaging;
using SupportSatchel.Core.Redacting;
using Xunit;

namespace SupportSatchel.Core.Tests;

/// <summary>
/// Tests for deterministic bundle packaging (issue #5): the redaction gate,
/// manifest/checksum correctness, archive integrity, and byte-stable output
/// for identical staged input.
/// </summary>
public class BundleExporterTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 4, 20, 9, 30, 15, TimeSpan.Zero);

    private readonly string root;

    public BundleExporterTests()
    {
        root = Path.Combine(Path.GetTempPath(), "satchel-bundle-tests", Guid.NewGuid().ToString("N"));
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

    private static BundleProfile ProfileWith(params CaptureSource[] sources) =>
        TestProfiles.Valid(sources: sources);

    private static CollectorOptions Options() => new() { Clock = () => FixedNow };

    private static RedactionOptions ReportOptions() => new() { Clock = () => FixedNow };

    /// <summary>Collects and redacts a small two-file workspace.</summary>
    private CollectionResult CollectAndRedact(BundleProfile profile, string workspaceRoot)
    {
        var collected = new Collector(new List<IDiagnosticProbe> { new OperatingSystemProbe() })
            .Run(profile, workspaceRoot, Options());
        var run = new RedactionEngine().ApplyWorkspace(
            collected, profile.Redaction, ReportOptions());
        Assert.True(run.IsSuccessful);
        return collected;
    }

    private static Dictionary<string, byte[]> ReadZip(string bundlePath)
    {
        using var archive = ZipFile.OpenRead(bundlePath);
        return archive.Entries.ToDictionary(
            static e => e.FullName,
            static e =>
            {
                using var stream = e.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            });
    }

    [Fact]
    public void ExportProducesBundleAndManifestWithArtifactChecksums()
    {
        WriteFixture("logs/app.log", "contact jane.doe@example.com\n");
        WriteFixture("logs/other.log", "plain text\n");
        var profile = ProfileWith(new CaptureSource
        {
            Id = "src-logs",
            Kind = SourceKind.Folder,
            Path = Path.Combine(root, "logs"),
            Required = true,
        });
        var workspace = CollectAndRedact(profile, Path.Combine(root, "ws"));

        var result = new BundleExporter().Export(
            profile, workspace, Path.Combine(root, "out"), new PackagingOptions { Clock = () => FixedNow });

        Assert.True(File.Exists(result.BundlePath));
        Assert.True(File.Exists(result.ManifestPath));
        Assert.EndsWith(".zip", result.BundlePath, StringComparison.Ordinal);

        var entries = ReadZip(result.BundlePath);

        // Manifest, report copy, and every staged artifact are present.
        Assert.Contains(BundleManifest.ManifestEntryName, entries.Keys);
        Assert.Contains(RedactionEngine.ReportFileName, entries.Keys);
        Assert.Contains("staging/src-logs/app.log", entries.Keys);
        Assert.Contains("staging/src-logs/other.log", entries.Keys);
        Assert.Contains("staging/built-in-probes/os-info.txt", entries.Keys);

        // Provenance sidecars (absolute source paths) must never ship.
        Assert.DoesNotContain(entries.Keys, k => k.StartsWith("provenance", StringComparison.Ordinal));

        // Every manifest checksum/size matches the actual archive content.
        var manifest = BundleManifestJson.Deserialize(File.ReadAllText(result.ManifestPath));
        Assert.NotNull(manifest);
        foreach (var artifact in manifest!.Artifacts)
        {
            var content = entries[artifact.Path];
            Assert.Equal(content.Length, artifact.SizeBytes);
            var actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            Assert.Equal(actual, artifact.Sha256);
        }

        // Manifest total and result bundle hash are consistent.
        Assert.Equal(manifest.Artifacts.Sum(a => a.SizeBytes), manifest.TotalBytes);
        var bundleBytes = File.ReadAllBytes(result.BundlePath);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(bundleBytes)).ToLowerInvariant(),
            result.BundleSha256);

        // The shipped redacted text actually has the secret masked.
        Assert.Contains(
            "[REDACTED]",
            Encoding.UTF8.GetString(entries["staging/src-logs/app.log"]),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExportSanitizesReportSnippetsAndNoArchiveEntryContainsFixtureSecret()
    {
        const string Secret = "archive.fixture@example.test";
        WriteFixture("logs/app.log", $"contact {Secret}\n");
        var profile = ProfileWith(new CaptureSource
        {
            Id = "src-logs",
            Kind = SourceKind.Folder,
            Path = Path.Combine(root, "logs"),
        });
        var collected = new Collector(new List<IDiagnosticProbe>())
            .Run(profile, Path.Combine(root, "ws"), Options());
        var redaction = new RedactionEngine().ApplyWorkspace(collected, profile.Redaction, ReportOptions());

        Assert.Contains(Secret, Assert.Single(Assert.Single(redaction.Artifacts).Matches).Snippet, StringComparison.Ordinal);

        var result = new BundleExporter().Export(
            profile, collected, Path.Combine(root, "out"), new PackagingOptions { Clock = () => FixedNow });
        var entries = ReadZip(result.BundlePath);

        foreach (var (entryName, bytes) in entries)
        {
            Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        }

        var exportedReport = RedactionReportJson.Deserialize(
            Encoding.UTF8.GetString(entries[RedactionEngine.ReportFileName]));
        Assert.NotNull(exportedReport);
        Assert.Equal("[REDACTED]", Assert.Single(Assert.Single(exportedReport!.Artifacts).Matches).Snippet);
    }

    [Fact]
    public void IdenticalInputProducesByteStableBundleAndManifest()
    {
        WriteFixture("logs/a.log", "alpha token:abcdefghijklmno\n");
        WriteFixture("logs/b.log", "beta\n");
        var profile = ProfileWith(new CaptureSource
        {
            Id = "src-logs",
            Kind = SourceKind.Folder,
            Path = Path.Combine(root, "logs"),
            IncludePatterns = ["*.log"],
        });

        var w1 = CollectAndRedact(profile, Path.Combine(root, "ws1"));
        var w2 = CollectAndRedact(profile, Path.Combine(root, "ws2"));

        var exporter = new BundleExporter();
        var r1 = exporter.Export(profile, w1, Path.Combine(root, "out1"), new PackagingOptions { Clock = () => FixedNow });
        var r2 = exporter.Export(profile, w2, Path.Combine(root, "out2"), new PackagingOptions { Clock = () => FixedNow });

        Assert.Equal(
            File.ReadAllBytes(r1.BundlePath),
            File.ReadAllBytes(r2.BundlePath));
        Assert.Equal(r1.BundleSha256, r2.BundleSha256);
        Assert.Equal(File.ReadAllText(r1.ManifestPath), File.ReadAllText(r2.ManifestPath));
    }

    [Fact]
    public void ManifestIsOrdinalOrderedAndCarriesProfileIdentityAndUtcTimestamp()
    {
        WriteFixture("logs/b.log", "second\n");
        WriteFixture("logs/a.log", "first\n");
        var profile = ProfileWith(new CaptureSource
        {
            Id = "src-logs",
            Kind = SourceKind.Folder,
            Path = Path.Combine(root, "logs"),
            IncludePatterns = ["*.log"],
        });
        var workspace = CollectAndRedact(profile, Path.Combine(root, "ws"));

        var result = new BundleExporter().Export(
            profile, workspace, Path.Combine(root, "out"), new PackagingOptions { Clock = () => FixedNow });

        var manifest = result.Manifest;
        Assert.Equal(BundleManifest.ManifestSchema, manifest.Schema);
        Assert.Equal(profile.Id, manifest.ProfileId);
        Assert.Equal(profile.Name, manifest.ProfileName);
        Assert.Equal(BundleProfile.CurrentDocumentSchema, manifest.ProfileVersion);
        Assert.Equal("2026-04-20T09:30:15.000Z", manifest.GeneratedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));

        var paths = manifest.Artifacts.Select(a => a.Path).ToList();
        Assert.Equal(paths.OrderBy(p => p, StringComparer.Ordinal).ToList(), paths);

        // Archive entry order matches the manifest order, manifest.json first.
        using var archive = ZipFile.OpenRead(result.BundlePath);
        var entryNames = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Equal(
            new[] { BundleManifest.ManifestEntryName }.Concat(paths).ToList(),
            entryNames);

        // Deterministic mode stamps the DOS epoch on every entry.
        foreach (var entry in archive.Entries)
        {
            Assert.Equal(BundleExporter.FixedEntryTime.DateTime, entry.LastWriteTime.DateTime);
        }
    }

    [Fact]
    public void ExportRefusedWhenRedactionReportMissing()
    {
        WriteFixture("logs/app.log", "hello\n");
        var profile = ProfileWith(new CaptureSource
        {
            Id = "src-logs",
            Kind = SourceKind.Folder,
            Path = Path.Combine(root, "logs"),
        });
        var collected = new Collector(new List<IDiagnosticProbe>())
            .Run(profile, Path.Combine(root, "ws"), Options());

        var ex = Assert.Throws<BundleExportException>(() => new BundleExporter().Export(
            profile, collected, Path.Combine(root, "out")));
        Assert.Contains("redaction-report.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportRefusedWhenRedactionRunUnsuccessful()
    {
        WriteFixture("logs/app.log", "hello\n");
        var brokenSettings = new RedactionSettings
        {
            Rules = [new RedactionRule { Id = "bad", Name = "Broken", Pattern = "[unclosed(" }],
        };
        var profile = ProfileWith(new CaptureSource
        {
            Id = "src-logs",
            Kind = SourceKind.Folder,
            Path = Path.Combine(root, "logs"),
        }) with
        {
            Redaction = brokenSettings,
        };

        var collected = new Collector(new List<IDiagnosticProbe>())
            .Run(profile, Path.Combine(root, "ws"), Options());
        var run = new RedactionEngine().ApplyWorkspace(collected, brokenSettings, ReportOptions());
        Assert.False(run.IsSuccessful);

        var ex = Assert.Throws<BundleExportException>(() => new BundleExporter().Export(
            profile, collected, Path.Combine(root, "out")));
        Assert.Contains("not successful", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportRefusedWhenRedactionReportCorrupt()
    {
        WriteFixture("logs/app.log", "hello\n");
        var profile = ProfileWith(new CaptureSource
        {
            Id = "src-logs",
            Kind = SourceKind.Folder,
            Path = Path.Combine(root, "logs"),
        });
        var workspace = CollectAndRedact(profile, Path.Combine(root, "ws"));

        File.WriteAllText(
            Path.Combine(workspace.WorkspacePath, RedactionEngine.ReportFileName),
            "{ not json");

        Assert.Throws<BundleExportException>(() => new BundleExporter().Export(
            profile, workspace, Path.Combine(root, "out")));
    }

    [Fact]
    public void ExportNameTemplateRendersProfileVersionAndUtcTokens()
    {
        var name = BundleExporter.RenderBundleFileName(
            "Nightly / Acme: Beta", "{profile}-{version}-{utc}.zip", FixedNow);
        Assert.Equal("Nightly-Acme-Beta-1-20260420T093015Z.zip", name);
    }

    [Fact]
    public void ExportHonoursCustomNameTemplateAndDeterministicFlag()
    {
        WriteFixture("logs/app.log", "hello\n");
        var profile = ProfileWith(new CaptureSource
        {
            Id = "src-logs",
            Kind = SourceKind.Folder,
            Path = Path.Combine(root, "logs"),
        }) with
        {
            Export = new ExportOptions { BundleNameTemplate = "bundle-{profile}.zip" },
        };
        var workspace = CollectAndRedact(profile, Path.Combine(root, "ws"));

        var result = new BundleExporter().Export(
            profile, workspace, Path.Combine(root, "out"), new PackagingOptions { Clock = () => FixedNow });

        Assert.Equal("bundle-Test-Profile.zip", Path.GetFileName(result.BundlePath));
        var entries = ReadZip(result.BundlePath);
        Assert.Contains(BundleManifest.ManifestEntryName, entries.Keys);
    }

    [Fact]
    public void ManifestSidecarMatchesManifestEntryInsideBundle()
    {
        WriteFixture("logs/app.log", "hello token:abcdefghijklmno\n");
        var profile = ProfileWith(new CaptureSource
        {
            Id = "src-logs",
            Kind = SourceKind.Folder,
            Path = Path.Combine(root, "logs"),
        });
        var workspace = CollectAndRedact(profile, Path.Combine(root, "ws"));

        var result = new BundleExporter().Export(
            profile, workspace, Path.Combine(root, "out"), new PackagingOptions { Clock = () => FixedNow });

        var entries = ReadZip(result.BundlePath);
        var inner = Encoding.UTF8.GetString(entries[BundleManifest.ManifestEntryName]);
        Assert.Equal(File.ReadAllText(result.ManifestPath), inner);
    }
}
