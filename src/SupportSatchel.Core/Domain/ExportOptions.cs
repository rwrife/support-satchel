namespace SupportSatchel.Core.Domain;

/// <summary>
/// Export-time options attached to a <see cref="BundleProfile"/>. The
/// packaging code that honours these options lands in issue #5.
/// </summary>
public sealed record ExportOptions
{
    /// <summary>
    /// Filename template for exported bundles. Supports the tokens
    /// <c>{profile}</c>, <c>{version}</c> and <c>{utc}</c>.
    /// </summary>
    public string BundleNameTemplate { get; init; } = "{profile}-{utc}.zip";

    /// <summary>
    /// When true, exported artifacts are stored in the ZIP with zero
    /// timestamps and fixed ordering so identical inputs produce a
    /// byte-stable archive (verified in issue #5).
    /// </summary>
    public bool Deterministic { get; init; } = true;

    /// <summary>
    /// When true, the export writes a manifest with artifact list, sizes
    /// and SHA-256 hashes next to the ZIP.
    /// </summary>
    public bool IncludeManifest { get; init; } = true;
}
