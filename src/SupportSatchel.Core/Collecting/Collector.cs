using System.Text;
using SupportSatchel.Core.Domain;

namespace SupportSatchel.Core.Collecting;

/// <summary>
/// Collects evidence declared by a <see cref="BundleProfile"/> into a run
/// workspace. The pipeline is intentionally staged *before* redaction and
/// packaging (issues #4/#5): it copies files, never moves, deletes, or
/// modifies sources, and it writes only inside the workspace directory.
/// </summary>
/// <remarks>
/// Determinism contract (tested):
/// <list type="bullet">
/// <item>Directory traversal is ordinal-sorted, so artifact order does not
/// depend on file-system enumeration order.</item>
/// <item>Per-artifact provenance is written as <c>*.json</c> next to the
/// staged copy; all timestamps in manifests come from
/// <see cref="CollectorOptions.Clock"/>, never from wall clock directly.</item>
/// <item>The result lists are sorted by staged path.</item>
/// </list>
/// </remarks>
public sealed class Collector
{
    /// <summary>Workspace-relative directory holding staged evidence copies.</summary>
    public const string StagingDirectory = "staging";

    /// <summary>Workspace-relative directory holding provenance sidecars.</summary>
    public const string ProvenanceDirectory = "provenance";

    /// <summary>
    /// File name reserved for probe outputs under the built-in source bucket.
    /// </summary>
    public const string ProbesSourceId = "built-in-probes";

    private readonly IReadOnlyList<IDiagnosticProbe> probes;

    /// <summary>Creates a collector using the default built-in probe set.</summary>
    public Collector()
        : this(BuiltInProbes.All)
    {
    }

    /// <summary>Creates a collector with an explicit probe set (tests, presets).</summary>
    public Collector(IReadOnlyList<IDiagnosticProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        this.probes = probes;
    }

    /// <summary>
    /// Runs collection for <paramref name="profile"/> into a fresh run
    /// workspace under <paramref name="workspaceRoot"/>.
    /// </summary>
    public CollectionResult Run(
        BundleProfile profile,
        string workspaceRoot,
        CollectorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        options ??= new CollectorOptions();
        var now = options.Clock();

        var workspace = Path.Combine(
            workspaceRoot,
            "run-" + profile.Id.ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, StagingDirectory));
        Directory.CreateDirectory(Path.Combine(workspace, ProvenanceDirectory));

        var artifacts = new List<StagedArtifact>();
        var skipped = new List<SkippedSource>();
        var errors = new List<string>();

        foreach (var source in profile.Sources)
        {
            switch (source.Kind)
            {
                case SourceKind.File:
                    CollectFile(source, workspace, artifacts, skipped, errors);
                    break;
                case SourceKind.Folder:
                    CollectFolder(source, workspace, artifacts, skipped, errors);
                    break;
                default:
                    errors.Add($"{source.Id}: unsupported source kind '{source.Kind}'.");
                    break;
            }
        }

        CollectProbes(probes, workspace, now, artifacts);

        // Stable order regardless of source/probe interleaving.
        artifacts.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.StagedPath, b.StagedPath));
        skipped.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.SourceId, b.SourceId));
        errors.Sort(StringComparer.Ordinal);

        return new CollectionResult
        {
            WorkspacePath = workspace,
            Artifacts = artifacts,
            Skipped = skipped,
            Errors = errors,
        };
    }

    private void CollectFile(
        CaptureSource source,
        string workspace,
        List<StagedArtifact> artifacts,
        List<SkippedSource> skipped,
        List<string> errors)
    {
        if (!File.Exists(source.Path))
        {
            HandleMissing(source, skipped, errors, "source file does not exist");
            return;
        }

        var fileName = Path.GetFileName(source.Path);
        if (string.IsNullOrEmpty(fileName))
        {
            errors.Add($"{source.Id}: could not determine file name for '{source.Path}'.");
            return;
        }

        try
        {
            var stagedRelative = $"{source.Id}/{fileName}";
            StageFileCopy(source.Id, source.Path, source.Path, fileName, stagedRelative, workspace, artifacts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HandleUnreadable(source, skipped, errors, ex);
        }
    }

    private void CollectFolder(
        CaptureSource source,
        string workspace,
        List<StagedArtifact> artifacts,
        List<SkippedSource> skipped,
        List<string> errors)
    {
        if (!Directory.Exists(source.Path))
        {
            HandleMissing(source, skipped, errors, "source folder does not exist");
            return;
        }

        var matches = new List<string>();
        try
        {
            var queue = new Queue<string>();
            queue.Enqueue(source.Path);
            while (queue.Count > 0)
            {
                var dir = queue.Dequeue();

                // Ordinal sort keeps traversal independent of FS order.
                foreach (var file in Directory.EnumerateFiles(dir).OrderBy(static p => p, StringComparer.Ordinal))
                {
                    var relative = GlobMatcher.NormalizePath(Path.GetRelativePath(source.Path, file));
                    if (GlobMatcher.IsSelected(relative, source.IncludePatterns, source.ExcludePatterns))
                    {
                        matches.Add(file);
                    }
                }

                foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(static p => p, StringComparer.Ordinal))
                {
                    queue.Enqueue(sub);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HandleUnreadable(source, skipped, errors, ex);
            return;
        }

        if (matches.Count == 0)
        {
            skipped.Add(new SkippedSource(source.Id, "no files matched include/exclude patterns"));
            return;
        }

        foreach (var file in matches.OrderBy(static p => p, StringComparer.Ordinal))
        {
            var relative = GlobMatcher.NormalizePath(Path.GetRelativePath(source.Path, file));
            try
            {
                var stagedRelative = $"{source.Id}/{relative}";
                StageFileCopy(source.Id, source.Path, file, relative, stagedRelative, workspace, artifacts);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                HandleUnreadable(source, skipped, errors, ex);
            }
        }
    }

    private static void CollectProbes(
        IReadOnlyList<IDiagnosticProbe> probes,
        string workspace,
        DateTimeOffset now,
        List<StagedArtifact> artifacts)
    {
        foreach (var probe in probes)
        {
            var text = probe.Collect(now);
            var stagedRelative = $"{ProbesSourceId}/{probe.Id}.txt";
            var stagedFull = Path.Combine(workspace, StagingDirectory, stagedRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(stagedFull)!);
            File.WriteAllText(stagedFull, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var provenance = new ProbeProvenance(ProbesSourceId, probe.Id, probe.Version);
            WriteProvenance(workspace, stagedRelative, now, provenance);
            artifacts.Add(new StagedArtifact
            {
                StagedPath = stagedRelative,
                SizeBytes = new FileInfo(stagedFull).Length,
                SourceModifiedUtc = now,
                Provenance = provenance,
            });
        }
    }

    private static void StageFileCopy(
        string sourceId,
        string sourceRoot,
        string file,
        string relativeSourcePath,
        string stagedRelative,
        string workspace,
        List<StagedArtifact> artifacts)
    {
        var stagedFull = Path.Combine(workspace, StagingDirectory, stagedRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedFull)!);

        // Capture source facts before copying; never mutate the source.
        var info = new FileInfo(file);
        var modified = info.LastWriteTimeUtc;
        File.Copy(file, stagedFull, overwrite: true);

        var provenance = new FileProvenance(sourceId, sourceRoot, GlobMatcher.NormalizePath(relativeSourcePath));
        WriteProvenance(workspace, stagedRelative, modified, provenance);
        artifacts.Add(new StagedArtifact
        {
            StagedPath = stagedRelative,
            SizeBytes = new FileInfo(stagedFull).Length,
            SourceModifiedUtc = modified,
            Provenance = provenance,
        });
    }

    private static void WriteProvenance(
        string workspace,
        string stagedRelative,
        DateTimeOffset modifiedUtc,
        Provenance provenance)
    {
        // Sidecars must be JSON and must never collide with the payload they
        // describe, regardless of the staged file's own extension.
        var safeSidecar = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(stagedRelative)) + ".json";
        var sidecarPath = Path.Combine(workspace, ProvenanceDirectory, safeSidecar);
        File.WriteAllText(sidecarPath, ProvenanceJson.Serialize(stagedRelative, modifiedUtc, provenance));
    }

    private static void HandleMissing(
        CaptureSource source,
        List<SkippedSource> skipped,
        List<string> errors,
        string reason)
    {
        if (source.Required)
        {
            errors.Add($"{source.Id}: required source missing ({reason})");
        }
        else
        {
            skipped.Add(new SkippedSource(source.Id, reason));
        }
    }

    private static void HandleUnreadable(
        CaptureSource source,
        List<SkippedSource> skipped,
        List<string> errors,
        Exception ex)
    {
        if (source.Required)
        {
            errors.Add($"{source.Id}: required source unreadable ({ex.GetType().Name})");
        }
        else
        {
            skipped.Add(new SkippedSource(source.Id, $"source unreadable ({ex.GetType().Name})"));
        }
    }
}
