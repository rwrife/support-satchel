using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SupportSatchel.Core.Collecting;
using SupportSatchel.Core.Domain;
using SupportSatchel.Core.Redacting;

namespace SupportSatchel.Core.Packaging;

/// <summary>
/// Packages a redacted run workspace into a portable ZIP bundle with
/// manifest/checksum evidence. This is the final stage of the pipeline
/// collect (#3) → redact (#4) → <c>package</c>:
/// <list type="bullet">
/// <item><description>The export is <b>fail-closed</b>: it refuses to run
/// unless <c>{workspace}/redaction-report.json</c> exists, parses, and
/// reports success, per the downstream contract in <c>docs/redaction.md</c>.
/// A bundle can therefore never silently contain un-redacted text the
/// redaction engine walked away from.</description></item>
/// <item><description>The bundle contains <c>manifest.json</c> (artifact
/// list with sizes and SHA-256 hashes), a copy of the redaction report, and
/// every staged artifact under a <c>staging/</c> prefix. Provenance
/// sidecars are deliberately excluded because they record absolute source
/// paths; the bundle must not leak machine layout.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Determinism contract (tested):
/// <list type="bullet">
/// <item>ZIP entries are written in ordinal path order with fixed (DOS
/// epoch) timestamps when <see cref="ExportOptions.Deterministic"/> is set,
/// so identical staged input produces byte-identical archives.</item>
/// <item>Manifest ordering is ordinal by bundle entry path, independent of
/// file-system enumeration order; the manifest timestamp comes from
/// <see cref="PackagingOptions.Clock"/>, never wall clock directly.</item>
/// <item>SHA-256 values are of the uncompressed staged bytes at export
/// time, so a recipient can verify each entry after extraction.</item>
/// </list>
/// </remarks>
public sealed class BundleExporter
{
    /// <summary>Bundle entry path prefix for staged evidence files.</summary>
    public const string StagingEntryPrefix = "staging/";

    /// <summary>Bundle entry path for the bundled copy of the redaction report.</summary>
    public const string ReportEntryName = RedactionEngine.ReportFileName;

    /// <summary>
    /// Fixed timestamp stamped on every ZIP entry in deterministic mode:
    /// the DOS epoch, the earliest value the format represents exactly.
    /// </summary>
    public static readonly DateTimeOffset FixedEntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Exports the redacted workspace described by <paramref name="collection"/>
    /// into <paramref name="outputDirectory"/>, honouring the profile's
    /// <see cref="ExportOptions"/>.
    /// </summary>
    /// <exception cref="BundleExportException">
    /// The redaction gate refuses the export (missing, unreadable, or
    /// unsuccessful <c>redaction-report.json</c>), a staged copy listed by
    /// the collection is absent, or the output directory cannot be written.
    /// </exception>
    public ExportResult Export(
        BundleProfile profile,
        CollectionResult collection,
        string outputDirectory,
        PackagingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        options ??= new PackagingOptions();
        var now = options.Clock();

        var report = LoadRedactionGate(collection.WorkspacePath);
        var exportedReport = report with
        {
            Artifacts = report.Artifacts.Select(artifact => artifact with
            {
                Matches = artifact.Matches.Select(match => match with { Snippet = "[REDACTED]" }).ToArray(),
            }).ToArray(),
        };

        var payload = new List<(string EntryPath, byte[] Bytes)>
        {
            (ReportEntryName, NoBom.GetBytes(RedactionReportJson.Serialize(exportedReport))),
        };
        foreach (var artifact in collection.Artifacts)
        {
            var full = StagedFullPath(collection.WorkspacePath, artifact.StagedPath);
            if (!File.Exists(full))
            {
                throw new BundleExportException(
                    $"staged copy listed by the collection is missing on disk: {artifact.StagedPath}");
            }

            payload.Add(($"{StagingEntryPrefix}{artifact.StagedPath}", File.ReadAllBytes(full)));
        }

        // Ordinal entry order regardless of artifact/report interleaving.
        payload.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.EntryPath, b.EntryPath));

        var artifactManifests = payload
            .Select(e => new BundleManifestArtifact
            {
                Path = e.EntryPath,
                SizeBytes = e.Bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(e.Bytes)).ToLowerInvariant(),
            })
            .ToList();

        var exportOptions = profile.Export;
        var bundleFileName = RenderBundleFileName(profile.Name, exportOptions.BundleNameTemplate, now);

        var manifest = new BundleManifest
        {
            Schema = BundleManifest.ManifestSchema,
            GeneratedAtUtc = now,
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            ProfileVersion = profile.DocumentSchema,
            BundleFileName = bundleFileName,
            Artifacts = artifactManifests,
            TotalBytes = artifactManifests.Sum(static a => a.SizeBytes),
        };

        var manifestJson = exportOptions.IncludeManifest ? BundleManifestJson.Serialize(manifest) : null;
        var manifestBytes = manifestJson is null ? null : NoBom.GetBytes(manifestJson);

        // manifest.json sorts before every report/staging entry (m < r < s),
        // so ordinal entry order is preserved by prepending it.
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (manifestBytes is not null)
            {
                WriteEntry(
                    archive,
                    BundleManifest.ManifestEntryName,
                    manifestBytes,
                    timestamp: exportOptions.Deterministic ? FixedEntryTime : now);
            }

            foreach (var (entryPath, bytes) in payload)
            {
                // In deterministic mode every entry carries the same fixed
                // timestamp so archive bytes depend only on content.
                WriteEntry(
                    archive,
                    entryPath,
                    bytes,
                    timestamp: exportOptions.Deterministic ? FixedEntryTime : now);
            }
        }

        var bundleBytes = zipStream.ToArray();
        Directory.CreateDirectory(outputDirectory);

        var bundlePath = Path.Combine(outputDirectory, bundleFileName);
        File.WriteAllBytes(bundlePath, bundleBytes);

        string? manifestPath = null;
        if (manifestJson is not null)
        {
            manifestPath = bundlePath + ".manifest.json";
            File.WriteAllText(manifestPath, manifestJson, NoBom);
        }

        return new ExportResult
        {
            BundlePath = bundlePath,
            ManifestPath = manifestPath ?? bundlePath,
            BundleSha256 = Convert.ToHexString(SHA256.HashData(bundleBytes)).ToLowerInvariant(),
            Manifest = manifest,
        };
    }

    /// <summary>
    /// Verifies the redaction gate: reads
    /// <c>{workspace}/{RedactionEngine.ReportFileName}</c> and refuses to
    /// proceed unless it parses and reports success.
    /// </summary>
    private static RedactionReport LoadRedactionGate(string workspacePath)
    {
        var reportPath = Path.Combine(workspacePath, RedactionEngine.ReportFileName);
        if (!File.Exists(reportPath))
        {
            throw new BundleExportException(
                $"refusing to export: {RedactionEngine.ReportFileName} is missing — "
                + "run the redaction engine over the workspace first.");
        }

        RedactionReport? report;
        try
        {
            report = RedactionReportJson.Deserialize(File.ReadAllText(reportPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BundleExportException(
                $"refusing to export: {RedactionEngine.ReportFileName} unreadable ({ex.GetType().Name}).");
        }

        if (report is null)
        {
            throw new BundleExportException(
                $"refusing to export: {RedactionEngine.ReportFileName} is not a valid report document.");
        }

        if (!report.IsSuccessful)
        {
            throw new BundleExportException(
                $"refusing to export: redaction run was not successful ({report.Errors.Count} error(s)) — "
                + "staged copies may contain un-redacted content.");
        }

        return report;
    }

    private static void WriteEntry(
        ZipArchive archive,
        string entryPath,
        byte[] content,
        DateTimeOffset timestamp)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        entry.LastWriteTime = timestamp;
        using var stream = entry.Open();
        stream.Write(content);
    }

    /// <summary>
    /// Renders <see cref="ExportOptions.BundleNameTemplate"/> for one export:
    /// <c>{profile}</c> becomes the sanitized profile name, <c>{version}</c>
    /// the profile document schema, and <c>{utc}</c> the export timestamp as
    /// <c>yyyyMMdd'T'HHmmss'Z'</c>.
    /// </summary>
    public static string RenderBundleFileName(string profileName, string template, DateTimeOffset utc)
    {
        return template
            .Replace("{profile}", SanitizeNameComponent(profileName), StringComparison.Ordinal)
            .Replace("{version}", BundleProfile.CurrentDocumentSchema.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{utc}", utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>
    /// Characters never allowed in a rendered bundle name. Deliberately a
    /// fixed list rather than <see cref="Path.GetInvalidFileNameChars()"/>,
    /// whose contents differ between Windows and Unix and would make export
    /// names platform-dependent.
    /// </summary>
    private static readonly char[] UnsafeNameChars =
    [
        '\\', '/', ':', '*', '?', '"', '<', '>', '|', ' ', '\t', '\n', '\r',
    ];

    /// <summary>
    /// Maps a human profile name onto a safe single file-name component:
    /// unsafe characters (including whitespace) become dashes and runs of
    /// dashes collapse.
    /// </summary>
    private static string SanitizeNameComponent(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            var safe = Array.IndexOf(UnsafeNameChars, c) >= 0 || char.IsControl(c) ? '-' : c;
            if (safe == '-' && sb.Length > 0 && sb[^1] == '-')
            {
                continue;
            }

            sb.Append(safe);
        }

        var result = sb.ToString().Trim('-');
        return result.Length == 0 ? "profile" : result;
    }

    private static string StagedFullPath(string workspacePath, string stagedPath) =>
        Path.Combine(
            workspacePath,
            Collector.StagingDirectory,
            stagedPath.Replace('/', Path.DirectorySeparatorChar));
}
