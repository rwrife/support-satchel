namespace SupportSatchel.Core.Domain;

/// <summary>
/// The root aggregate for support-bundle definitions. A profile is a pure
/// value object: metadata, capture sources, redaction settings, and export
/// options. It is serialized to canonical JSON and persisted by
/// <c>ProfileStore</c>; collectors (issue #3), the redaction engine
/// (issue #4), and packaging (issue #5) consume it without mutation.
/// </summary>
public sealed record BundleProfile
{
    /// <summary>Schema version of the profile document itself (not the DB schema).</summary>
    public const int CurrentDocumentSchema = 1;

    /// <summary>Document schema version; readers reject versions above their own.</summary>
    public int DocumentSchema { get; init; } = CurrentDocumentSchema;

    /// <summary>Globally unique, stable identity (survives duplicate/edit).</summary>
    public required Guid Id { get; init; }

    /// <summary>Human-facing name; unique per local store.</summary>
    public required string Name { get; init; }

    /// <summary>Optional free-form description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Creation time (must be UTC; enforced by validation).</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Last edit time (must be UTC; enforced by validation).</summary>
    public required DateTimeOffset UpdatedAtUtc { get; init; }

    /// <summary>Ordered evidence sources to collect.</summary>
    public IReadOnlyList<CaptureSource> Sources { get; init; } = [];

    /// <summary>Redaction configuration applied to staged copies.</summary>
    public RedactionSettings Redaction { get; init; } = RedactionSettings.CreateDefault();

    /// <summary>Export behaviour for the packaged bundle.</summary>
    public ExportOptions Export { get; init; } = new();

    /// <inheritdoc />
    public bool Equals(BundleProfile? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return DocumentSchema == other.DocumentSchema
            && Id == other.Id
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && string.Equals(Description, other.Description, StringComparison.Ordinal)
            && CreatedAtUtc == other.CreatedAtUtc
            && UpdatedAtUtc == other.UpdatedAtUtc
            && PatternList.Equal(Sources, other.Sources)
            && Equals(Redaction, other.Redaction)
            && Equals(Export, other.Export);
    }

    /// <inheritdoc />
    public override int GetHashCode() =>
        PatternList.Combine(
            DocumentSchema,
            Id.GetHashCode(),
            StringComparer.Ordinal.GetHashCode(Name),
            StringComparer.Ordinal.GetHashCode(Description),
            CreatedAtUtc.GetHashCode(),
            UpdatedAtUtc.GetHashCode(),
            PatternList.HashOf(Sources),
            Redaction.GetHashCode(),
            Export.GetHashCode());
}
