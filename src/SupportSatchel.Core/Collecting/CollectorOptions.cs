namespace SupportSatchel.Core.Collecting;

/// <summary>
/// Tunables for <see cref="Collector.Run"/>. The clock is injectable so
/// tests (and reproducible runs) can pin the timestamp written into probe
/// output and provenance manifests.
/// </summary>
public sealed class CollectorOptions
{
    /// <summary>UTC clock used for run timestamps. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.UtcNow;
}
