namespace SupportSatchel.Core.Packaging;

/// <summary>
/// Knobs for <see cref="BundleExporter"/>. Mirrors <c>CollectorOptions</c>
/// and <c>RedactionOptions</c>: the run timestamp comes from an injectable
/// clock so exports (and their manifests) are reproducible in tests and CI.
/// </summary>
public sealed class PackagingOptions
{
    /// <summary>
    /// Clock used for the manifest timestamp and the <c>{utc}</c> bundle
    /// name token. Defaults to <see cref="DateTimeOffset.UtcNow"/>.
    /// </summary>
    public Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.UtcNow;
}

/// <summary>Outcome of a successful <see cref="BundleExporter.Export"/>.</summary>
public sealed record ExportResult
{
    /// <summary>Absolute path of the written ZIP bundle.</summary>
    public required string BundlePath { get; init; }

    /// <summary>Absolute path of the manifest sidecar next to the bundle.</summary>
    public required string ManifestPath { get; init; }

    /// <summary>Lowercase hex SHA-256 of the ZIP file itself.</summary>
    public required string BundleSha256 { get; init; }

    /// <summary>The manifest as serialized into the bundle and the sidecar.</summary>
    public required BundleManifest Manifest { get; init; }
}
