using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SupportSatchel.Core.Domain;
using SupportSatchel.Core.Storage;

namespace SupportSatchel.App.ViewModels;

/// <summary>
/// Testable state controller for the keyboard-first desktop workflow. It
/// invalidates downstream review/export state whenever its profile or run changes.
/// </summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private const string DirtyDraftMessage =
        "Unsaved profile changes are protected. Save or revert edits before selecting another profile or using New, Duplicate, or Delete.";
    private static readonly string NewDraftConfigurationJson = DefaultConfiguration();
    private readonly IDesktopWorkflow _workflow;
    private readonly AsyncRelayCommand _saveCommand;
    private readonly AsyncRelayCommand _duplicateCommand;
    private readonly AsyncRelayCommand _deleteCommand;
    private readonly AsyncRelayCommand _captureCommand;
    private readonly AsyncRelayCommand _exportCommand;
    private readonly RelayCommand _newCommand;
    private readonly RelayCommand _revertCommand;
    private BundleProfile? _selectedProfile;
    private ReviewArtifact? _selectedReviewArtifact;
    private DesktopRun? _run;
    private string _profileName = string.Empty;
    private string _profileDescription = string.Empty;
    private string _configurationJson = NewDraftConfigurationJson;
    private string _deleteConfirmation = string.Empty;
    private string _exportDirectory = string.Empty;
    private string _statusMessage = "Select or create a profile to begin.";
    private string _errorMessage = string.Empty;
    private string _bundlePath = string.Empty;
    private bool _reviewAcknowledged;
    private string _savedProfileName = string.Empty;
    private string _savedProfileDescription = string.Empty;
    private string _savedConfigurationJson = string.Empty;
    private int _operationActive;

    /// <summary>Creates a view model over the supplied local desktop workflow.</summary>
    public MainWindowViewModel(IDesktopWorkflow workflow)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _newCommand = new RelayCommand(BeginNewProfile, () => CanNavigateProfiles);
        _revertCommand = new RelayCommand(RevertProfile, () => CanRevertProfile);
        _saveCommand = new AsyncRelayCommand(SaveProfileAsync, () => !IsBusy);
        _duplicateCommand = new AsyncRelayCommand(DuplicateProfileAsync, () => CanUseSelectedProfileActions);
        _deleteCommand = new AsyncRelayCommand(DeleteProfileAsync, () => CanDelete);
        _captureCommand = new AsyncRelayCommand(CaptureAsync, () => CanCapture);
        _exportCommand = new AsyncRelayCommand(ExportAsync, () => CanExport);
    }

    /// <summary>Saved profiles displayed in the selection list.</summary>
    public ObservableCollection<BundleProfile> Profiles { get; } = [];

    /// <summary>Artifacts from the current run, cleared on profile changes.</summary>
    public ObservableCollection<ReviewArtifact> ReviewArtifacts { get; } = [];

    /// <summary>Currently selected saved profile.</summary>
    public BundleProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (_selectedProfile?.Id == value?.Id)
            {
                return;
            }

            if (IsProfileDirty)
            {
                ProtectDirtyDraft();
                OnPropertyChanged();
                return;
            }

            _selectedProfile = value;
            OnPropertyChanged();
            DeleteConfirmation = string.Empty;
            InvalidateRun();
            if (value is not null)
            {
                LoadSavedDraft(value);
                StatusMessage = $"Selected {value.Name}. Review its configuration, then capture.";
            }

            NotifyStateChanged();
        }
    }

    /// <summary>Artifact whose original and sanitized previews are displayed.</summary>
    public ReviewArtifact? SelectedReviewArtifact
    {
        get => _selectedReviewArtifact;
        set => SetProperty(ref _selectedReviewArtifact, value);
    }

    /// <summary>Basic editor profile name.</summary>
    public string ProfileName
    {
        get => _profileName;
        set => SetDraftProperty(ref _profileName, value);
    }

    /// <summary>Basic editor profile description.</summary>
    public string ProfileDescription
    {
        get => _profileDescription;
        set => SetDraftProperty(ref _profileDescription, value);
    }

    /// <summary>Validated advanced JSON for sources, redaction rules, and packaging options.</summary>
    public string ConfigurationJson
    {
        get => _configurationJson;
        set => SetDraftProperty(ref _configurationJson, value);
    }

    /// <summary>Typed exact-name confirmation required by delete.</summary>
    public string DeleteConfirmation
    {
        get => _deleteConfirmation;
        set
        {
            if (SetProperty(ref _deleteConfirmation, value))
            {
                NotifyStateChanged();
            }
        }
    }

    /// <summary>Local directory into which the reviewed ZIP will be written.</summary>
    public string ExportDirectory
    {
        get => _exportDirectory;
        set
        {
            if (SetProperty(ref _exportDirectory, value))
            {
                NotifyStateChanged();
            }
        }
    }

    /// <summary>Explicit human acknowledgment for the current review.</summary>
    public bool ReviewAcknowledged
    {
        get => _reviewAcknowledged;
        set
        {
            if (SetProperty(ref _reviewAcknowledged, value))
            {
                BundlePath = string.Empty;
                NotifyStateChanged();
            }
        }
    }

    /// <summary>Non-sensitive operation progress announced by the UI.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Sanitized user-actionable error text; never raw IO exception text.</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>Generated bundle path after a successful export.</summary>
    public string BundlePath
    {
        get => _bundlePath;
        private set => SetProperty(ref _bundlePath, value);
    }

    /// <summary>Whether one asynchronous operation is active.</summary>
    public bool IsBusy => Volatile.Read(ref _operationActive) != 0;

    /// <summary>Whether controls that could alter an active operation are available.</summary>
    public bool CanInteract => !IsBusy;

    /// <summary>Whether editor values differ from the saved profile or blank new-draft defaults.</summary>
    public bool IsProfileDirty => SelectedProfile is null
        ? !string.Equals(ProfileName, string.Empty, StringComparison.Ordinal)
            || !string.Equals(ProfileDescription, string.Empty, StringComparison.Ordinal)
            || !string.Equals(ConfigurationJson, NewDraftConfigurationJson, StringComparison.Ordinal)
        : !string.Equals(ProfileName, _savedProfileName, StringComparison.Ordinal)
            || !string.Equals(ProfileDescription, _savedProfileDescription, StringComparison.Ordinal)
            || !string.Equals(ConfigurationJson, _savedConfigurationJson, StringComparison.Ordinal);

    /// <summary>Whether profile selection and a fresh new draft are safe.</summary>
    public bool CanNavigateProfiles => !IsProfileDirty && !IsBusy;

    /// <summary>Whether a persisted profile is selected.</summary>
    public bool CanEditProfile => SelectedProfile is not null;

    /// <summary>Whether actions that replace or destroy the selected saved profile are safe.</summary>
    public bool CanUseSelectedProfileActions => SelectedProfile is not null && !IsProfileDirty && !IsBusy;

    /// <summary>Whether capture can start.</summary>
    public bool CanCapture => SelectedProfile is not null && !IsProfileDirty && !IsBusy;

    /// <summary>Whether the current draft can be restored or reset to new-profile defaults.</summary>
    public bool CanRevertProfile => IsProfileDirty && !IsBusy;

    /// <summary>Whether typed confirmation exactly authorizes deleting the selected profile.</summary>
    public bool CanDelete =>
        CanUseSelectedProfileActions
        && SelectedProfile is not null
        && string.Equals(DeleteConfirmation, SelectedProfile.Name, StringComparison.Ordinal);

    /// <summary>Whether the exact current review may be exported.</summary>
    public bool CanExport =>
        _run is not null
        && ReviewAcknowledged
        && ReviewArtifacts.Any(static artifact => artifact.IsIncluded)
        && !string.IsNullOrWhiteSpace(ExportDirectory)
        && !IsBusy;

    /// <summary>Starts a blank profile draft without persisting it.</summary>
    public ICommand NewCommand => _newCommand;

    /// <summary>Discards unsaved editor changes and restores the selected profile.</summary>
    public ICommand RevertCommand => _revertCommand;

    /// <summary>Creates or updates the editor profile.</summary>
    public ICommand SaveCommand => _saveCommand;

    /// <summary>Duplicates the selected saved profile.</summary>
    public ICommand DuplicateCommand => _duplicateCommand;

    /// <summary>Deletes the selected profile after exact-name confirmation.</summary>
    public ICommand DeleteCommand => _deleteCommand;

    /// <summary>Captures the selected profile.</summary>
    public ICommand CaptureCommand => _captureCommand;

    /// <summary>Exports the acknowledged current review.</summary>
    public ICommand ExportCommand => _exportCommand;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Loads locally saved profiles.</summary>
    public Task InitializeAsync() => RunGuardedAsync(async () =>
    {
        await ReloadProfilesAsync();
        StatusMessage = Profiles.Count == 0
            ? "No profiles yet. Create one to begin."
            : "Select a profile to begin.";
    });

    /// <summary>Creates or updates the current profile draft.</summary>
    public Task SaveProfileAsync() => RunGuardedAsync(async () =>
    {
        var saved = SelectedProfile is null
            ? await _workflow.CreateProfileAsync(ProfileName, ProfileDescription, ConfigurationJson)
            : await _workflow.UpdateProfileAsync(SelectedProfile.Id, ProfileName, ProfileDescription, ConfigurationJson);
        await ReloadProfilesAsync(saved.Id);
        InvalidateRun();
        StatusMessage = $"Saved {saved.Name}.";
    });

    /// <summary>Duplicates the selected profile using a generated editable name.</summary>
    public Task DuplicateProfileAsync() => RunGuardedAsync(async () =>
    {
        EnsureDraftIsClean();
        var selected = SelectedProfile ?? throw new DesktopInputException("Select a profile to duplicate.");
        var duplicate = await _workflow.DuplicateProfileAsync(selected.Id, UniqueDuplicateName(selected.Name));
        await ReloadProfilesAsync(duplicate.Id);
        InvalidateRun();
        StatusMessage = $"Created {duplicate.Name}.";
    });

    /// <summary>Deletes the selected profile using the typed confirmation.</summary>
    public Task DeleteProfileAsync() => RunGuardedAsync(async () =>
    {
        EnsureDraftIsClean();
        var selected = SelectedProfile ?? throw new DesktopInputException("Select a profile to delete.");
        await _workflow.DeleteProfileAsync(selected.Id, DeleteConfirmation);
        await ReloadProfilesAsync();
        BeginNewProfile();
        StatusMessage = "Profile deleted from local storage.";
    });

    /// <summary>Captures and previews the currently selected profile.</summary>
    public Task CaptureAsync() => RunGuardedAsync(async () =>
    {
        var selected = SelectedProfile ?? throw new DesktopInputException("Select a saved profile before capture.");
        if (IsProfileDirty)
        {
            throw new DesktopInputException("Save or revert profile changes before capture.");
        }

        var selectedId = selected.Id;
        InvalidateRun();
        StatusMessage = "Capturing local evidence and preparing a redaction preview…";
        var run = await _workflow.CaptureAsync(selectedId);
        if (SelectedProfile?.Id != selectedId)
        {
            throw new DesktopInputException("The selected profile changed during capture. Capture again.");
        }

        _run = run;
        foreach (var artifact in run.Artifacts)
        {
            artifact.PropertyChanged += ArtifactChanged;
            ReviewArtifacts.Add(artifact);
        }

        SelectedReviewArtifact = ReviewArtifacts.FirstOrDefault();
        StatusMessage = $"Capture complete. Review {ReviewArtifacts.Count} artifact(s), exclusions, and binary warnings.";
        NotifyStateChanged();
    });

    /// <summary>Applies redactions and exports the current acknowledged review.</summary>
    public Task ExportAsync() => RunGuardedAsync(async () =>
    {
        var run = _run ?? throw new DesktopInputException("Capture and review a profile before export.");
        StatusMessage = "Applying redactions and creating the local bundle…";
        DesktopExportResult result;
        try
        {
            result = await _workflow.ExportAsync(run, ExportDirectory, ReviewAcknowledged);
        }
        finally
        {
            // The workflow may have modified staging even if packaging failed.
            // Remove the consumed review so the UI requires a fresh capture.
            InvalidateRun();
        }

        BundlePath = result.BundlePath;
        StatusMessage = "Export complete. The bundle remains local until you share it manually.";
    });

    /// <summary>Restores a saved profile or resets an unsaved new draft to defaults.</summary>
    public void RevertProfile()
    {
        if (!CanRevertProfile)
        {
            return;
        }

        if (SelectedProfile is null)
        {
            SetDraftValues(string.Empty, string.Empty, NewDraftConfigurationJson);
            StatusMessage = "Discarded the unsaved new profile draft and restored default fields.";
        }
        else
        {
            LoadSavedDraft(SelectedProfile);
            StatusMessage = $"Reverted unsaved changes to {SelectedProfile.Name}.";
        }

        ErrorMessage = string.Empty;
        InvalidateRun();
        NotifyStateChanged();
    }

    private void BeginNewProfile()
    {
        if (IsProfileDirty)
        {
            ProtectDirtyDraft();
            return;
        }

        _selectedProfile = null;
        OnPropertyChanged(nameof(SelectedProfile));
        SetDraftValues(string.Empty, string.Empty, NewDraftConfigurationJson);
        DeleteConfirmation = string.Empty;
        InvalidateRun();
        StatusMessage = "New profile draft. Enter a name and configure sources before saving.";
        NotifyStateChanged();
    }

    private async Task ReloadProfilesAsync(Guid? selectId = null)
    {
        var profiles = await _workflow.ListProfilesAsync();
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(profile);
        }

        if (selectId is not null)
        {
            _selectedProfile = Profiles.First(profile => profile.Id == selectId);
            OnPropertyChanged(nameof(SelectedProfile));
            LoadSavedDraft(_selectedProfile);
        }
    }

    private void LoadSavedDraft(BundleProfile profile)
    {
        _savedProfileName = profile.Name;
        _savedProfileDescription = profile.Description;
        _savedConfigurationJson = ProfileConfigurationJson.Serialize(profile);
        SetDraftValues(_savedProfileName, _savedProfileDescription, _savedConfigurationJson);
        OnPropertyChanged(nameof(IsProfileDirty));
    }

    private void EnsureDraftIsClean()
    {
        if (IsProfileDirty)
        {
            throw new DesktopInputException(DirtyDraftMessage);
        }
    }

    private void ProtectDirtyDraft()
    {
        ErrorMessage = DirtyDraftMessage;
        StatusMessage = "Unsaved profile changes were kept. Save or revert edits to continue.";
        NotifyStateChanged();
    }

    private void SetDraftValues(string name, string description, string configurationJson)
    {
        SetProperty(ref _profileName, name, nameof(ProfileName));
        SetProperty(ref _profileDescription, description, nameof(ProfileDescription));
        SetProperty(ref _configurationJson, configurationJson, nameof(ConfigurationJson));
    }

    private async Task RunGuardedAsync(Func<Task> operation)
    {
        if (Interlocked.CompareExchange(ref _operationActive, 1, 0) != 0)
        {
            ErrorMessage = "Another operation is already in progress.";
            return;
        }

        ErrorMessage = string.Empty;
        NotifyStateChanged();
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            ErrorMessage = SafeMessage(exception);
            StatusMessage = "The operation did not complete.";
        }
        finally
        {
            Interlocked.Exchange(ref _operationActive, 0);
            NotifyStateChanged();
        }
    }

    private void ArtifactChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReviewArtifact.IsIncluded))
        {
            ReviewAcknowledged = false;
            StatusMessage = "Artifact selection changed. Review and acknowledge again before export.";
        }
    }

    private void InvalidateRun()
    {
        foreach (var artifact in ReviewArtifacts)
        {
            artifact.PropertyChanged -= ArtifactChanged;
        }

        _run = null;
        ReviewArtifacts.Clear();
        SelectedReviewArtifact = null;
        _reviewAcknowledged = false;
        OnPropertyChanged(nameof(ReviewAcknowledged));
        BundlePath = string.Empty;
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(IsProfileDirty));
        OnPropertyChanged(nameof(CanNavigateProfiles));
        OnPropertyChanged(nameof(CanEditProfile));
        OnPropertyChanged(nameof(CanUseSelectedProfileActions));
        OnPropertyChanged(nameof(CanCapture));
        OnPropertyChanged(nameof(CanRevertProfile));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanExport));
        _newCommand.NotifyCanExecuteChanged();
        _revertCommand.NotifyCanExecuteChanged();
        _saveCommand.NotifyCanExecuteChanged();
        _duplicateCommand.NotifyCanExecuteChanged();
        _deleteCommand.NotifyCanExecuteChanged();
        _captureCommand.NotifyCanExecuteChanged();
        _exportCommand.NotifyCanExecuteChanged();
    }

    private static string SafeMessage(Exception exception) => exception switch
    {
        DesktopInputException input => input.Message,
        ProfileNameConflictException => "A profile with that name already exists.",
        ProfileNotFoundException => "The selected profile no longer exists. Refresh and select another profile.",
        _ => "The local operation failed. Details were withheld to avoid exposing source paths or private content.",
    };

    private static string DefaultConfiguration() =>
        ProfileConfigurationJson.Serialize([], RedactionSettings.CreateDefault(), new ExportOptions());

    private string UniqueDuplicateName(string sourceName)
    {
        var existingNames = Profiles.Select(static profile => profile.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = sourceName + " copy";
        for (var suffix = 2; existingNames.Contains(candidate); suffix++)
        {
            candidate = $"{sourceName} copy {suffix}";
        }

        return candidate;
    }

    private void SetDraftProperty(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return;
        }

        if (IsProfileDirty)
        {
            InvalidateRun();
            StatusMessage = "Profile has unsaved changes. Save or revert edits before changing profiles or continuing.";
        }

        NotifyStateChanged();
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
