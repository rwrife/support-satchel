namespace SupportSatchel.Core.Domain;

/// <summary>
/// A single redaction rule: a regular expression matched against staged
/// text plus the replacement applied on export. Issue #2 only models and
/// validates rules; the redaction engine itself lands in issue #4.
/// </summary>
public sealed record RedactionRule
{
    /// <summary>Identifier unique within the owning <see cref="RedactionSettings"/>.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable label shown in the review UI (issue #4/#6).</summary>
    public required string Name { get; init; }

    /// <summary>
    /// .NET regular expression evaluated against staged artifact text.
    /// Must compile; validated by <c>ProfileValidator</c>.
    /// </summary>
    public required string Pattern { get; init; }

    /// <summary>
    /// Replacement text; may reference capture groups ($1, ${name}).
    /// </summary>
    public string Replacement { get; init; } = "[REDACTED]";

    /// <summary>
    /// When false the rule is kept but skipped by the engine.
    /// </summary>
    public bool Enabled { get; init; } = true;
}
