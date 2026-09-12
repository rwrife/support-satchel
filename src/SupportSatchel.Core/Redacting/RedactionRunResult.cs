namespace SupportSatchel.Core.Redacting;

/// <summary>What happened to one staged artifact during an apply run.</summary>
public sealed record RedactedArtifact
{
    /// <summary>Workspace-relative staged path (forward slashes).</summary>
    public required string StagedPath { get; init; }

    /// <summary>Artifact classification.</summary>
    public required ArtifactRedactionKind Kind { get; init; }

    /// <summary>True when the staged copy was rewritten with redacted text.</summary>
    public bool Redacted { get; init; }

    /// <summary>All matches grouped by rule application order.</summary>
    public required IReadOnlyList<RedactionMatch> Matches { get; init; }
}

/// <summary>
/// Outcome of <see cref="RedactionEngine.ApplyWorkspace"/>. Packaging
/// (issue #5) must treat an unsuccessful run as an export blocker: an error
/// means at least one staged copy is known to contain un-redacted content.
/// </summary>
public sealed record RedactionRunResult
{
    /// <summary>Root directory of the run workspace.</summary>
    public required string WorkspacePath { get; init; }

    /// <summary>Absolute path of the written <c>redaction-report.json</c>.</summary>
    public required string ReportPath { get; init; }

    /// <summary>Per-artifact outcomes, ordered by staged path.</summary>
    public required IReadOnlyList<RedactedArtifact> Artifacts { get; init; }

    /// <summary>
    /// Errors recorded during the run (missing staged copies, rule
    /// timeouts), ordinal-sorted.
    /// </summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>True when every artifact was processed cleanly.</summary>
    public bool IsSuccessful => Errors.Count == 0;
}
