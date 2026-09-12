using SupportSatchel.Core.Collecting;

namespace SupportSatchel.Core.Redacting;

/// <summary>
/// Machine-readable record of a redaction pass over a run workspace,
/// written as <c>redaction-report.json</c> at the workspace root. Packaging
/// (issue #5) reads the report to verify the staged tree was sanitized
/// before zipping; the review UI (issue #6) can render it without re-running
/// rules.
/// </summary>
public sealed record RedactionReport
{
    /// <summary>Wire format version of the report document.</summary>
    public const int ReportSchema = 1;

    /// <summary>Report timestamp taken from <see cref="RedactionOptions.Clock"/>.</summary>
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>Per-artifact outcomes, ordered by staged path.</summary>
    public required IReadOnlyList<RedactedArtifact> Artifacts { get; init; }

    /// <summary>Errors recorded during the run, ordinal-sorted.</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>True when every artifact was processed cleanly.</summary>
    public bool IsSuccessful { get; init; }
}
