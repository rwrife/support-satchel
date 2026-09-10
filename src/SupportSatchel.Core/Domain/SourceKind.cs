namespace SupportSatchel.Core.Domain;

/// <summary>
/// Kind of evidence a <see cref="CaptureSource"/> points at.
/// Collectors for each kind land with issue #3.
/// </summary>
public enum SourceKind
{
    /// <summary>A single file path.</summary>
    File = 0,

    /// <summary>A directory scanned with include/exclude glob patterns.</summary>
    Folder = 1,
}
