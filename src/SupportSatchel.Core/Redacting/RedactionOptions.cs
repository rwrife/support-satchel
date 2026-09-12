namespace SupportSatchel.Core.Redacting;

/// <summary>
/// Knobs for <see cref="RedactionEngine"/>. Mirrors
/// <c>CollectorOptions</c>: time comes from an injectable clock so redaction
/// reports can be produced deterministically in tests and CI.
/// </summary>
public sealed class RedactionOptions
{
    /// <summary>
    /// Clock used for the report timestamp. Defaults to
    /// <see cref="DateTimeOffset.UtcNow"/>.
    /// </summary>
    public Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Per-rule match timeout. Guards against pathological regular
    /// expressions (catastrophic backtracking). A rule that exceeds the
    /// timeout fails the artifact closed: the staged copy is left
    /// un-redacted and the run records an error. Must be positive or
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>.
    /// </summary>
    public TimeSpan PerRuleTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
