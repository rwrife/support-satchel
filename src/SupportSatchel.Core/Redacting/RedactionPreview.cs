namespace SupportSatchel.Core.Redacting;

/// <summary>
/// One rule hit recorded against a text. <see cref="Index"/> and
/// <see cref="Length"/> refer to the text as it existed when that rule was
/// applied, i.e. after all earlier rules ran (rules are applied
/// sequentially; see <see cref="RedactionEngine"/>).
/// </summary>
/// <param name="RuleId">Id of the rule that matched.</param>
/// <param name="Index">Zero-based start index of the match.</param>
/// <param name="Length">Length of the matched span in UTF-16 chars.</param>
/// <param name="Snippet">
/// Matched text truncated to <c>MaxSnippetChars</c> plus an ellipsis, for
/// compact display in review UIs and reports.
/// </param>
public sealed record RedactionMatch(string RuleId, int Index, int Length, string Snippet);

/// <summary>Result of redacting one text blob without touching any file.</summary>
public sealed record TextRedactionPreview
{
    /// <summary>The text as supplied.</summary>
    public required string OriginalText { get; init; }

    /// <summary>The text after all enabled rules ran in order.</summary>
    public required string RedactedText { get; init; }

    /// <summary>All matches, grouped by rule application order.</summary>
    public required IReadOnlyList<RedactionMatch> Matches { get; init; }

    /// <summary>True when at least one replacement was applied.</summary>
    public bool Changed => !string.Equals(OriginalText, RedactedText, StringComparison.Ordinal);
}

/// <summary>How the engine classified a staged artifact.</summary>
public enum ArtifactRedactionKind
{
    /// <summary>Decoded as valid UTF-8 text (with or without BOM); redactable.</summary>
    Text,

    /// <summary>Not valid UTF-8 text; left byte-for-byte unchanged.</summary>
    Binary,

    /// <summary>Declared by the collection result but absent on disk.</summary>
    Missing,
}

/// <summary>
/// Per-artifact preview used by review UIs (issue #6): the original staged
/// text side by side with what the export would contain.
/// </summary>
public sealed record ArtifactRedactionPreview
{
    /// <summary>Workspace-relative staged path (forward slashes).</summary>
    public required string StagedPath { get; init; }

    /// <summary>Artifact classification.</summary>
    public required ArtifactRedactionKind Kind { get; init; }

    /// <summary>Original staged text; null for binary/missing artifacts.</summary>
    public string? OriginalText { get; init; }

    /// <summary>
    /// Text the export would contain; null for binary/missing artifacts or
    /// when redaction aborted (timeout) on this artifact.
    /// </summary>
    public string? RedactedText { get; init; }

    /// <summary>All matches grouped by rule application order.</summary>
    public required IReadOnlyList<RedactionMatch> Matches { get; init; }

    /// <summary>True when the export content differs from the staged text.</summary>
    public bool Changed =>
        OriginalText is not null
        && RedactedText is not null
        && !string.Equals(OriginalText, RedactedText, StringComparison.Ordinal);
}

/// <summary>Outcome of a read-only <see cref="RedactionEngine.PreviewWorkspace"/> run.</summary>
public sealed record WorkspaceRedactionPreview
{
    /// <summary>Per-artifact previews, ordered by staged path.</summary>
    public required IReadOnlyList<ArtifactRedactionPreview> Artifacts { get; init; }

    /// <summary>Non-fatal problems (missing staged files, aborted rules).</summary>
    public required IReadOnlyList<string> Errors { get; init; }
}
