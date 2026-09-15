using SupportSatchel.Core.Collecting;
using SupportSatchel.Core.Domain;
using SupportSatchel.Core.Packaging;
using SupportSatchel.Core.Redacting;

namespace SupportSatchel.App.ViewModels;

/// <summary>One captured artifact presented for explicit privacy review.</summary>
public sealed class ReviewArtifact : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isIncluded = true;

    /// <summary>Creates a review row from a production redaction preview.</summary>
    internal ReviewArtifact(ArtifactRedactionPreview preview)
    {
        StagedPath = preview.StagedPath;
        Kind = preview.Kind;
        OriginalText = preview.OriginalText;
        RedactedText = preview.RedactedText;
        MatchCount = preview.Matches.Count;
    }

    /// <summary>Workspace-relative artifact path.</summary>
    public string StagedPath { get; }

    /// <summary>Whether the artifact is text, binary, or missing.</summary>
    public ArtifactRedactionKind Kind { get; }

    /// <summary>Original staged text for deliberate side-by-side review; null for binary data.</summary>
    public string? OriginalText { get; }

    /// <summary>Sanitized preview that will be exported; null when redaction cannot complete.</summary>
    public string? RedactedText { get; }

    /// <summary>Number of redaction matches without exposing secret-bearing match snippets.</summary>
    public int MatchCount { get; }

    /// <summary>Whether this reviewed artifact should be included in the export.</summary>
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (_isIncluded == value)
            {
                return;
            }

            _isIncluded = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsIncluded)));
        }
    }

    /// <inheritdoc />
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// In-memory desktop run token tying a review to the exact profile snapshot
/// and collection that produced it. It cannot be reconstructed from arbitrary UI input.
/// </summary>
public sealed class DesktopRun
{
    private int _exportState;

    internal DesktopRun(
        Guid id,
        BundleProfile profile,
        CollectionResult collection,
        IReadOnlyList<ReviewArtifact> artifacts)
    {
        Id = id;
        ProfileId = profile.Id;
        ProfileName = profile.Name;
        ProfileUpdatedAtUtc = profile.UpdatedAtUtc;
        Profile = profile;
        Collection = collection;
        Artifacts = artifacts;
    }

    /// <summary>Persisted run identifier.</summary>
    public Guid Id { get; }

    /// <summary>Profile identifier captured at run start.</summary>
    public Guid ProfileId { get; }

    /// <summary>Profile name captured at run start.</summary>
    public string ProfileName { get; }

    /// <summary>Profile revision timestamp captured at run start.</summary>
    public DateTimeOffset ProfileUpdatedAtUtc { get; }

    /// <summary>Local workspace containing staged review copies for this run.</summary>
    public string WorkspacePath => Collection.WorkspacePath;

    /// <summary>Artifacts available for review and inclusion decisions.</summary>
    public IReadOnlyList<ReviewArtifact> Artifacts { get; }

    internal BundleProfile Profile { get; }

    internal CollectionResult Collection { get; }

    internal bool TryBeginExport() => Interlocked.CompareExchange(ref _exportState, 1, 0) == 0;
}

/// <summary>Successful reviewed export information displayed by the desktop UI.</summary>
public sealed record DesktopExportResult
{
    /// <summary>Absolute path of the generated ZIP.</summary>
    public required string BundlePath { get; init; }

    /// <summary>Absolute path of the manifest sidecar.</summary>
    public required string ManifestPath { get; init; }

    /// <summary>SHA-256 checksum of the generated ZIP.</summary>
    public required string BundleSha256 { get; init; }

    internal static DesktopExportResult From(ExportResult result) => new()
    {
        BundlePath = result.BundlePath,
        ManifestPath = result.ManifestPath,
        BundleSha256 = result.BundleSha256,
    };
}
