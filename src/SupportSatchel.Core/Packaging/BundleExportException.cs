namespace SupportSatchel.Core.Packaging;

/// <summary>
/// Thrown when an export is refused. Export is fail-closed: packaging will
/// not produce a bundle from a workspace whose redaction report is missing,
/// unreadable, or unsuccessful, because such a bundle could silently ship
/// un-redacted content (see <c>docs/redaction.md</c> downstream contract).
/// </summary>
public sealed class BundleExportException : InvalidOperationException
{
    /// <summary>Initializes the exception with a message.</summary>
    public BundleExportException(string message)
        : base(message)
    {
    }
}
