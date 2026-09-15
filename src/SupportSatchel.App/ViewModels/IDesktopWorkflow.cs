using SupportSatchel.Core.Domain;

namespace SupportSatchel.App.ViewModels;

/// <summary>Asynchronous local operations consumed by the desktop state controller.</summary>
public interface IDesktopWorkflow
{
    /// <summary>Lists saved profiles.</summary>
    Task<IReadOnlyList<BundleProfile>> ListProfilesAsync();

    /// <summary>Creates a validated profile.</summary>
    Task<BundleProfile> CreateProfileAsync(string name, string description, string configurationJson);

    /// <summary>Updates a validated profile.</summary>
    Task<BundleProfile> UpdateProfileAsync(Guid id, string name, string description, string configurationJson);

    /// <summary>Duplicates a profile under a new name.</summary>
    Task<BundleProfile> DuplicateProfileAsync(Guid id, string newName);

    /// <summary>Deletes a profile after exact-name confirmation.</summary>
    Task DeleteProfileAsync(Guid id, string confirmationName);

    /// <summary>Captures and prepares a redaction review.</summary>
    Task<DesktopRun> CaptureAsync(Guid profileId);

    /// <summary>Exports an explicitly acknowledged review.</summary>
    Task<DesktopExportResult> ExportAsync(DesktopRun run, string outputDirectory, bool reviewAcknowledged);
}
