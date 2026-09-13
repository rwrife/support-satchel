namespace SupportSatchel.Core.Packaging;

/// <summary>
/// Machine-readable evidence document describing one exported bundle: the
/// profile it came from, the run timestamp, and the exact artifact list
/// with sizes and SHA-256 hashes. Written as <c>manifest.json</c> both
/// beside the ZIP and as the first entry inside it, so a recipient can
/// verify integrity without any side channel.
/// </summary>
public sealed record BundleManifest
{
    /// <summary>Wire format version of the manifest document.</summary>
    public const int ManifestSchema = 1;

    /// <summary>Workspace-relative name of the manifest inside the bundle.</summary>
    public const string ManifestEntryName = "manifest.json";

    /// <summary>Manifest document schema version.</summary>
    public required int Schema { get; init; }

    /// <summary>Manifest timestamp taken from <see cref="PackagingOptions.Clock"/>.</summary>
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>Id of the profile that produced the bundle.</summary>
    public required Guid ProfileId { get; init; }

    /// <summary>Profile name captured at export time.</summary>
    public required string ProfileName { get; init; }

    /// <summary>Profile document version (<see cref="Domain.BundleProfile.DocumentSchema"/>).</summary>
    public required int ProfileVersion { get; init; }

    /// <summary>File name of the produced ZIP bundle.</summary>
    public required string BundleFileName { get; init; }

    /// <summary>Exported artifacts, ordered by bundle entry path.</summary>
    public required IReadOnlyList<BundleManifestArtifact> Artifacts { get; init; }

    /// <summary>Total payload size in bytes across all artifacts.</summary>
    public required long TotalBytes { get; init; }
}

/// <summary>One artifact entry inside a <see cref="BundleManifest"/>.</summary>
public sealed record BundleManifestArtifact
{
    /// <summary>ZIP entry path (forward slashes), e.g. <c>staging/src-logs/app.log</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Uncompressed size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Lowercase hex SHA-256 of the uncompressed content.</summary>
    public required string Sha256 { get; init; }
}
