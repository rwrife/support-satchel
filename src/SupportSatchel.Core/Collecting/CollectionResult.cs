namespace SupportSatchel.Core.Collecting;

/// <summary>
/// One file staged into a run workspace, plus the provenance describing
/// where it came from. Staged paths are workspace-relative and use forward
/// slashes so results are stable across platforms.
/// </summary>
public sealed record StagedArtifact
{
    /// <summary>Workspace-relative staging path (e.g. <c>staging/src-logs/app.log</c>).</summary>
    public required string StagedPath { get; init; }

    /// <summary>Size of the staged copy in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Last write time of the source file at collection time (UTC). For
    /// probe outputs this is the run time. The staged copy's own timestamps
    /// are never trusted as evidence.
    /// </summary>
    public required DateTimeOffset SourceModifiedUtc { get; init; }

    /// <summary>Where the artifact came from.</summary>
    public required Provenance Provenance { get; init; }
}

/// <summary>A source that produced no artifacts, with the reason.</summary>
/// <param name="SourceId">Id of the profile source that was skipped.</param>
/// <param name="Reason">Machine-readable skip reason.</param>
public sealed record SkippedSource(string SourceId, string Reason);

/// <summary>Outcome of a <see cref="Collector.Run"/> execution.</summary>
public sealed record CollectionResult
{
    /// <summary>Root directory of the run workspace.</summary>
    public required string WorkspacePath { get; init; }

    /// <summary>All staged artifacts, ordered deterministically by staged path.</summary>
    public required IReadOnlyList<StagedArtifact> Artifacts { get; init; }

    /// <summary>Optional sources that were absent or produced no matches.</summary>
    public required IReadOnlyList<SkippedSource> Skipped { get; init; }

    /// <summary>Required sources that were missing or unreadable.</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>True when no required source failed.</summary>
    public bool IsSuccessful => Errors.Count == 0;
}
