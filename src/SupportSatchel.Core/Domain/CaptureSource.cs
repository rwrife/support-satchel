namespace SupportSatchel.Core.Domain;

/// <summary>
/// One source definition inside a <see cref="BundleProfile"/>: a file or a
/// folder plus include/exclude glob patterns. This is data only — the
/// collectors that read these sources are built in issue #3.
/// </summary>
public sealed record CaptureSource
{
    /// <summary>Stable identifier unique within the owning profile.</summary>
    public required string Id { get; init; }

    /// <summary>Whether the source is a single file or a folder scan.</summary>
    public SourceKind Kind { get; init; } = SourceKind.File;

    /// <summary>Path to the file or folder (no glob wildcards; use patterns).</summary>
    public required string Path { get; init; }

    /// <summary>Glob patterns selecting files when <see cref="Kind"/> is Folder.</summary>
    public IReadOnlyList<string> IncludePatterns { get; init; } = [];

    /// <summary>Glob patterns removing files from the selection.</summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];

    /// <summary>
    /// When true, a missing source fails the run; when false it is recorded
    /// as skipped.
    /// </summary>
    public bool Required { get; init; }

    /// <inheritdoc />
    public bool Equals(CaptureSource? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Id == other.Id
            && Kind == other.Kind
            && Path == other.Path
            && Required == other.Required
            && PatternList.Equal(IncludePatterns, other.IncludePatterns)
            && PatternList.Equal(ExcludePatterns, other.ExcludePatterns);
    }

    /// <inheritdoc />
    public override int GetHashCode() =>
        PatternList.Combine(
            StringComparer.Ordinal.GetHashCode(Id),
            Kind.GetHashCode(),
            StringComparer.Ordinal.GetHashCode(Path),
            Required.GetHashCode(),
            PatternList.Hash(IncludePatterns),
            PatternList.Hash(ExcludePatterns));
}
