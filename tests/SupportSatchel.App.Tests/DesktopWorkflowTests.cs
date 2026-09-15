using SupportSatchel.App.ViewModels;
using SupportSatchel.Core.Domain;
using SupportSatchel.Core.Storage;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace SupportSatchel.App.Tests;

public sealed class DesktopWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "support-satchel-app-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProfileCrudPersistsCreateEditDuplicateAndDeleteRoundTrip()
    {
        Directory.CreateDirectory(_root);
        var database = Path.Combine(_root, "profiles.db");
        var workflow = new DesktopWorkflow(database, Path.Combine(_root, "runs"));
        var configuration = ProfileConfigurationJson.Serialize(
            [new CaptureSource { Id = "log", Kind = SourceKind.File, Path = "/tmp/app.log", Required = true }],
            RedactionSettings.CreateDefault(),
            new ExportOptions { BundleNameTemplate = "{profile}-evidence-{utc}.zip" });

        var created = await workflow.CreateProfileAsync("Crash report", "first", configuration);
        var edited = await workflow.UpdateProfileAsync(created.Id, "Crash report", "edited", configuration);
        var duplicate = await workflow.DuplicateProfileAsync(created.Id, "Crash report copy");
        await workflow.DeleteProfileAsync(created.Id, "Crash report");

        using var reopened = new ProfileStore(database);
        var persisted = Assert.Single(reopened.ListProfiles());
        Assert.Equal(duplicate.Id, persisted.Id);
        Assert.Equal("Crash report copy", persisted.Name);
        Assert.Equal("edited", edited.Description);
        Assert.Equal("{profile}-evidence-{utc}.zip", persisted.Export.BundleNameTemplate);
        Assert.Single(persisted.Sources);
    }

    [Fact]
    public async Task CaptureReviewAcknowledgmentAndExportUseRealPipelineWithoutSecretLeakage()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "diagnostic.log");
        const string secret = "person@example.com";
        await File.WriteAllTextAsync(sourcePath, "contact=" + secret);
        var workflow = new DesktopWorkflow(Path.Combine(_root, "profiles.db"), Path.Combine(_root, "runs"));
        var configuration = ProfileConfigurationJson.Serialize(
            [new CaptureSource { Id = "log", Kind = SourceKind.File, Path = sourcePath, Required = true }],
            RedactionSettings.CreateDefault(),
            new ExportOptions());
        var profile = await workflow.CreateProfileAsync("Privacy check", string.Empty, configuration);

        var run = await workflow.CaptureAsync(profile.Id);

        var review = Assert.Single(run.Artifacts, artifact => artifact.StagedPath.EndsWith("diagnostic.log", StringComparison.Ordinal));
        Assert.Contains(secret, review.OriginalText, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, review.RedactedText, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", review.RedactedText, StringComparison.Ordinal);
        await Assert.ThrowsAsync<DesktopInputException>(() =>
            workflow.ExportAsync(run, Path.Combine(_root, "exports"), reviewAcknowledged: false));

        var exported = await workflow.ExportAsync(
            run,
            Path.Combine(_root, "exports"),
            reviewAcknowledged: true);

        Assert.True(File.Exists(exported.BundlePath));
        using var archive = ZipFile.OpenRead(exported.BundlePath);
        var artifact = Assert.Single(archive.Entries, entry => entry.FullName.EndsWith("diagnostic.log", StringComparison.Ordinal));
        using var reader = new StreamReader(artifact.Open(), Encoding.UTF8);
        var bundledText = await reader.ReadToEndAsync();
        Assert.DoesNotContain(secret, bundledText, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", bundledText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidInputAndCaptureFailureAreFailClosedAndSanitized()
    {
        Directory.CreateDirectory(_root);
        var missingSecretPath = Path.Combine(_root, "secret-customer-name", "missing.log");
        var workflow = new DesktopWorkflow(Path.Combine(_root, "profiles.db"), Path.Combine(_root, "runs"));
        await Assert.ThrowsAsync<DesktopInputException>(() =>
            workflow.CreateProfileAsync("Bad", string.Empty, "{ not-json }"));
        var configuration = ProfileConfigurationJson.Serialize(
            [new CaptureSource { Id = "required", Kind = SourceKind.File, Path = missingSecretPath, Required = true }],
            RedactionSettings.CreateDefault(),
            new ExportOptions());
        var profile = await workflow.CreateProfileAsync("Failure", string.Empty, configuration);

        var error = await Assert.ThrowsAsync<DesktopInputException>(() => workflow.CaptureAsync(profile.Id));

        Assert.DoesNotContain(missingSecretPath, error.Message, StringComparison.Ordinal);
        Assert.Contains("export", error.Message, StringComparison.Ordinal);
        using var store = new ProfileStore(Path.Combine(_root, "profiles.db"));
        Assert.Equal(RunStatus.Failed, Assert.Single(store.ListRuns(profile.Id)).Status);
    }

    [Fact]
    public async Task ViewModelInvalidatesReviewAndExportWheneverSelectionOrInclusionChanges()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "app.log");
        await File.WriteAllTextAsync(sourcePath, "email=person@example.com");
        var workflow = new DesktopWorkflow(Path.Combine(_root, "profiles.db"), Path.Combine(_root, "runs"));
        var configuration = ProfileConfigurationJson.Serialize(
            [new CaptureSource { Id = "log", Kind = SourceKind.File, Path = sourcePath, Required = true }],
            RedactionSettings.CreateDefault(),
            new ExportOptions());
        var first = await workflow.CreateProfileAsync("First", string.Empty, configuration);
        var second = await workflow.CreateProfileAsync("Second", string.Empty, configuration);
        var viewModel = new MainWindowViewModel(workflow);
        await viewModel.InitializeAsync();
        viewModel.SelectedProfile = viewModel.Profiles.Single(profile => profile.Id == first.Id);
        await viewModel.CaptureAsync();
        viewModel.ReviewAcknowledged = true;

        viewModel.ReviewArtifacts[0].IsIncluded = false;
        Assert.False(viewModel.ReviewAcknowledged);
        Assert.False(viewModel.CanExport);

        viewModel.ReviewAcknowledged = true;
        viewModel.SelectedProfile = viewModel.Profiles.Single(profile => profile.Id == second.Id);
        Assert.False(viewModel.ReviewAcknowledged);
        Assert.Empty(viewModel.ReviewArtifacts);
        Assert.False(viewModel.CanExport);
    }

    [Fact]
    public async Task DeleteRequiresExactCurrentNameAndLeavesProfileWhenConfirmationIsStale()
    {
        Directory.CreateDirectory(_root);
        var workflow = new DesktopWorkflow(Path.Combine(_root, "profiles.db"), Path.Combine(_root, "runs"));
        var profile = await workflow.CreateProfileAsync(
            "Keep me",
            string.Empty,
            ProfileConfigurationJson.Serialize(
                [new CaptureSource { Id = "optional", Path = Path.Combine(_root, "optional.log") }],
                RedactionSettings.CreateDefault(),
                new ExportOptions()));

        await Assert.ThrowsAsync<DesktopInputException>(() => workflow.DeleteProfileAsync(profile.Id, "Keep Me"));

        Assert.Single(await workflow.ListProfilesAsync());
    }

    [Fact]
    public async Task ViewModelEnablesDeleteOnlyForExactSelectedName()
    {
        Directory.CreateDirectory(_root);
        var workflow = new DesktopWorkflow(Path.Combine(_root, "profiles.db"), Path.Combine(_root, "runs"));
        var profile = await workflow.CreateProfileAsync(
            "Exact name",
            string.Empty,
            ProfileConfigurationJson.Serialize(
                [new CaptureSource { Id = "optional", Path = Path.Combine(_root, "optional.log") }],
                RedactionSettings.CreateDefault(),
                new ExportOptions()));
        var viewModel = new MainWindowViewModel(workflow);
        await viewModel.InitializeAsync();
        viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.Id == profile.Id);

        viewModel.DeleteConfirmation = "exact name";
        Assert.False(viewModel.CanDelete);
        viewModel.DeleteConfirmation = "Exact name";
        Assert.True(viewModel.CanDelete);
    }

    [Fact]
    public async Task ViewModelRejectsReentrantOperationWhileFirstOperationIsBusy()
    {
        var workflow = new BlockingWorkflow();
        var viewModel = new MainWindowViewModel(workflow);

        var first = viewModel.InitializeAsync();
        await workflow.Started.Task;
        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsBusy);
        Assert.Equal("Another operation is already in progress.", viewModel.ErrorMessage);
        workflow.Release.SetResult();
        await first;
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task ExportRejectsProfileEditedAfterCapture()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "app.log");
        await File.WriteAllTextAsync(sourcePath, "safe text");
        var workflow = new DesktopWorkflow(Path.Combine(_root, "profiles.db"), Path.Combine(_root, "runs"));
        var configuration = ProfileConfigurationJson.Serialize(
            [new CaptureSource { Id = "log", Path = sourcePath, Required = true }],
            RedactionSettings.CreateDefault(),
            new ExportOptions());
        var profile = await workflow.CreateProfileAsync("Mutable", string.Empty, configuration);
        var run = await workflow.CaptureAsync(profile.Id);
        await workflow.UpdateProfileAsync(profile.Id, profile.Name, "changed", configuration);

        var error = await Assert.ThrowsAsync<DesktopInputException>(() =>
            workflow.ExportAsync(run, Path.Combine(_root, "exports"), reviewAcknowledged: true));

        Assert.Contains("changed after capture", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, "exports")));
    }

    [Fact]
    public async Task ExcludedArtifactIsNotRedactedOrPackaged()
    {
        Directory.CreateDirectory(_root);
        var firstPath = Path.Combine(_root, "first.log");
        var secondPath = Path.Combine(_root, "second.log");
        await File.WriteAllTextAsync(firstPath, "first@example.com");
        await File.WriteAllTextAsync(secondPath, "second@example.com");
        var workflow = new DesktopWorkflow(Path.Combine(_root, "profiles.db"), Path.Combine(_root, "runs"));
        var configuration = ProfileConfigurationJson.Serialize(
            [
                new CaptureSource { Id = "first", Path = firstPath, Required = true },
                new CaptureSource { Id = "second", Path = secondPath, Required = true },
            ],
            RedactionSettings.CreateDefault(),
            new ExportOptions());
        var profile = await workflow.CreateProfileAsync("Exclude", string.Empty, configuration);
        var run = await workflow.CaptureAsync(profile.Id);
        run.Artifacts.Single(artifact => artifact.StagedPath.EndsWith("second.log", StringComparison.Ordinal)).IsIncluded = false;

        var result = await workflow.ExportAsync(run, Path.Combine(_root, "exports"), reviewAcknowledged: true);

        using var archive = ZipFile.OpenRead(result.BundlePath);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("second.log", StringComparison.Ordinal));
        Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("first.log", StringComparison.Ordinal));
        var excludedStaged = Path.Combine(run.WorkspacePath, "staging", "second", "second.log");
        Assert.Equal("second@example.com", await File.ReadAllTextAsync(excludedStaged));
    }

    [Fact]
    public async Task ExportSnapshotsIncludedArtifactsBeforeBackgroundWorkStarts()
    {
        Directory.CreateDirectory(_root);
        var firstPath = Path.Combine(_root, "first.log");
        var secondPath = Path.Combine(_root, "second.log");
        await File.WriteAllTextAsync(firstPath, "first@example.com");
        await File.WriteAllTextAsync(secondPath, "second@example.com");
        var databasePath = Path.Combine(_root, "profiles.db");
        var workflow = new DesktopWorkflow(databasePath, Path.Combine(_root, "runs"));
        var profile = await workflow.CreateProfileAsync(
            "Snapshot",
            string.Empty,
            ProfileConfigurationJson.Serialize(
                [
                    new CaptureSource { Id = "first", Path = firstPath, Required = true },
                    new CaptureSource { Id = "second", Path = secondPath, Required = true },
                ],
                RedactionSettings.CreateDefault(),
                new ExportOptions()));
        var run = await workflow.CaptureAsync(profile.Id);

        var export = workflow.ExportAsync(run, Path.Combine(_root, "exports"), reviewAcknowledged: true);
        run.Artifacts.Single(artifact => artifact.StagedPath.EndsWith("second.log", StringComparison.Ordinal)).IsIncluded = false;
        var result = await export;

        using var archive = ZipFile.OpenRead(result.BundlePath);
        Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("first.log", StringComparison.Ordinal));
        Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("second.log", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedExportAfterRedactionConsumesRunAndRequiresRecapture()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "non-idempotent.log");
        await File.WriteAllTextAsync(sourcePath, "a");
        var workflow = new DesktopWorkflow(Path.Combine(_root, "profiles.db"), Path.Combine(_root, "runs"));
        var redaction = new RedactionSettings
        {
            Rules =
            [
                new RedactionRule
                {
                    Id = "expand",
                    Name = "Non-idempotent expansion",
                    Pattern = "a",
                    Replacement = "aa",
                },
            ],
        };
        var profile = await workflow.CreateProfileAsync(
            "Fail closed",
            string.Empty,
            ProfileConfigurationJson.Serialize(
                [new CaptureSource { Id = "log", Path = sourcePath, Required = true }],
                redaction,
                new ExportOptions()));
        var run = await workflow.CaptureAsync(profile.Id);
        var unusableOutputPath = Path.Combine(_root, "not-a-directory");
        await File.WriteAllTextAsync(unusableOutputPath, "occupied");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            workflow.ExportAsync(run, unusableOutputPath, reviewAcknowledged: true));

        var stagedPath = Path.Combine(run.WorkspacePath, "staging", "log", "non-idempotent.log");
        Assert.Equal("aa", await File.ReadAllTextAsync(stagedPath));
        var retryError = await Assert.ThrowsAsync<DesktopInputException>(() =>
            workflow.ExportAsync(run, Path.Combine(_root, "exports"), reviewAcknowledged: true));
        Assert.Contains("already", retryError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_root, "exports")));
    }

    [Fact]
    public async Task ViewModelRemovesConsumedReviewAfterFailedExportAttempt()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "app.log");
        await File.WriteAllTextAsync(sourcePath, "person@example.com");
        var workflow = new DesktopWorkflow(Path.Combine(_root, "profiles.db"), Path.Combine(_root, "runs"));
        var profile = await workflow.CreateProfileAsync(
            "Consumed UI run",
            string.Empty,
            ProfileConfigurationJson.Serialize(
                [new CaptureSource { Id = "log", Path = sourcePath, Required = true }],
                RedactionSettings.CreateDefault(),
                new ExportOptions()));
        var viewModel = new MainWindowViewModel(workflow);
        await viewModel.InitializeAsync();
        viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.Id == profile.Id);
        await viewModel.CaptureAsync();
        viewModel.ReviewAcknowledged = true;
        var unusableOutputPath = Path.Combine(_root, "not-a-directory");
        await File.WriteAllTextAsync(unusableOutputPath, "occupied");
        viewModel.ExportDirectory = unusableOutputPath;

        await viewModel.ExportAsync();

        Assert.Empty(viewModel.ReviewArtifacts);
        Assert.False(viewModel.ReviewAcknowledged);
        Assert.False(viewModel.CanExport);
        Assert.NotEmpty(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task EditingProfileDraftInvalidatesReviewAndRequiresSaveOrRevertBeforeCapture()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "app.log");
        await File.WriteAllTextAsync(sourcePath, "person@example.com");
        var databasePath = Path.Combine(_root, "profiles.db");
        var workflow = new DesktopWorkflow(databasePath, Path.Combine(_root, "runs"));
        var profile = await workflow.CreateProfileAsync(
            "Draft guard",
            "saved",
            ProfileConfigurationJson.Serialize(
                [new CaptureSource { Id = "log", Path = sourcePath, Required = true }],
                RedactionSettings.CreateDefault(),
                new ExportOptions()));
        var viewModel = new MainWindowViewModel(workflow);
        await viewModel.InitializeAsync();
        viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.Id == profile.Id);
        await viewModel.CaptureAsync();
        viewModel.ReviewAcknowledged = true;
        viewModel.ExportDirectory = Path.Combine(_root, "exports");

        viewModel.ConfigurationJson += " ";

        Assert.True(viewModel.IsProfileDirty);
        Assert.Empty(viewModel.ReviewArtifacts);
        Assert.False(viewModel.ReviewAcknowledged);
        Assert.False(viewModel.CanCapture);
        Assert.False(viewModel.CanExport);
        await viewModel.CaptureAsync();
        Assert.Contains("save or revert", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        using (var store = new ProfileStore(databasePath))
        {
            Assert.Single(store.ListRuns(profile.Id));
        }

        viewModel.RevertProfile();

        Assert.False(viewModel.IsProfileDirty);
        Assert.Equal(profile.Description, viewModel.ProfileDescription);
        Assert.True(viewModel.CanCapture);

        viewModel.ProfileName += " edited";
        Assert.True(viewModel.IsProfileDirty);
        Assert.False(viewModel.CanCapture);
        viewModel.RevertProfile();
        viewModel.ProfileDescription += " edited";
        Assert.True(viewModel.IsProfileDirty);
        Assert.False(viewModel.CanCapture);

        await viewModel.SaveProfileAsync();

        Assert.False(viewModel.IsProfileDirty);
        Assert.True(viewModel.CanCapture);
    }

    [Fact]
    public async Task DirtySavedDraftBlocksProfileNavigationAndDestructiveActionsUntilReverted()
    {
        Directory.CreateDirectory(_root);
        var workflow = new DesktopWorkflow(Path.Combine(_root, "profiles.db"), Path.Combine(_root, "runs"));
        var configuration = ProfileConfigurationJson.Serialize(
            [new CaptureSource { Id = "optional", Path = Path.Combine(_root, "optional.log") }],
            RedactionSettings.CreateDefault(),
            new ExportOptions());
        var first = await workflow.CreateProfileAsync("First draft", "saved", configuration);
        var second = await workflow.CreateProfileAsync("Second draft", string.Empty, configuration);
        var viewModel = new MainWindowViewModel(workflow);
        await viewModel.InitializeAsync();
        viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.Id == first.Id);
        viewModel.DeleteConfirmation = first.Name;
        viewModel.ProfileDescription = "unsaved change";

        viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.Id == second.Id);
        viewModel.NewCommand.Execute(null);
        await viewModel.DuplicateProfileAsync();
        await viewModel.DeleteProfileAsync();

        Assert.Equal(first.Id, viewModel.SelectedProfile?.Id);
        Assert.Equal("unsaved change", viewModel.ProfileDescription);
        Assert.True(viewModel.IsProfileDirty);
        Assert.False(viewModel.NewCommand.CanExecute(null));
        Assert.False(viewModel.DuplicateCommand.CanExecute(null));
        Assert.False(viewModel.DeleteCommand.CanExecute(null));
        Assert.Equal(2, (await workflow.ListProfilesAsync()).Count);
        Assert.Contains("save or revert", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        viewModel.RevertProfile();
        viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.Id == second.Id);

        Assert.Equal(second.Id, viewModel.SelectedProfile?.Id);
        Assert.Equal(second.Description, viewModel.ProfileDescription);
        Assert.False(viewModel.IsProfileDirty);
    }

    [Fact]
    public async Task DirtyNewDraftBlocksProfileSelectionAndCanBeExplicitlyReset()
    {
        Directory.CreateDirectory(_root);
        var workflow = new DesktopWorkflow(Path.Combine(_root, "profiles.db"), Path.Combine(_root, "runs"));
        var profile = await workflow.CreateProfileAsync(
            "Saved profile",
            string.Empty,
            ProfileConfigurationJson.Serialize(
                [new CaptureSource { Id = "optional", Path = Path.Combine(_root, "optional.log") }],
                RedactionSettings.CreateDefault(),
                new ExportOptions()));
        var viewModel = new MainWindowViewModel(workflow);
        await viewModel.InitializeAsync();
        var defaultConfiguration = viewModel.ConfigurationJson;
        viewModel.ProfileName = "Unsaved new profile";
        viewModel.ProfileDescription = "unsaved description";
        viewModel.ConfigurationJson += " ";

        viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.Id == profile.Id);

        Assert.Null(viewModel.SelectedProfile);
        Assert.Equal("Unsaved new profile", viewModel.ProfileName);
        Assert.True(viewModel.IsProfileDirty);
        Assert.False(viewModel.NewCommand.CanExecute(null));
        Assert.True(viewModel.RevertCommand.CanExecute(null));
        Assert.Contains("save or revert", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        viewModel.RevertProfile();

        Assert.Null(viewModel.SelectedProfile);
        Assert.Equal(string.Empty, viewModel.ProfileName);
        Assert.Equal(string.Empty, viewModel.ProfileDescription);
        Assert.Equal(defaultConfiguration, viewModel.ConfigurationJson);
        Assert.False(viewModel.IsProfileDirty);
        Assert.False(viewModel.RevertCommand.CanExecute(null));
        viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.Id == profile.Id);
        Assert.Equal(profile.Id, viewModel.SelectedProfile?.Id);
    }

    [Fact]
    public async Task RepeatedDuplicationOfSameProfileGeneratesUniqueNames()
    {
        Directory.CreateDirectory(_root);
        var workflow = new DesktopWorkflow(Path.Combine(_root, "profiles.db"), Path.Combine(_root, "runs"));
        var profile = await workflow.CreateProfileAsync(
            "Original",
            string.Empty,
            ProfileConfigurationJson.Serialize(
                [new CaptureSource { Id = "optional", Path = Path.Combine(_root, "optional.log") }],
                RedactionSettings.CreateDefault(),
                new ExportOptions()));
        var viewModel = new MainWindowViewModel(workflow);
        await viewModel.InitializeAsync();

        viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.Id == profile.Id);
        await viewModel.DuplicateProfileAsync();
        viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.Id == profile.Id);
        await viewModel.DuplicateProfileAsync();

        Assert.Contains(viewModel.Profiles, item => item.Name == "Original copy");
        Assert.Contains(viewModel.Profiles, item => item.Name == "Original copy 2");
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
    }

    [Fact]
    public async Task ExportRefusesStagedSymbolicLinkWithoutTouchingExternalTarget()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "source.log");
        var externalPath = Path.Combine(_root, "external-secret.log");
        const string externalSecret = "external@example.com";
        await File.WriteAllTextAsync(sourcePath, "captured@example.com");
        await File.WriteAllTextAsync(externalPath, externalSecret);
        var workflow = new DesktopWorkflow(Path.Combine(_root, "profiles.db"), Path.Combine(_root, "runs"));
        var configuration = ProfileConfigurationJson.Serialize(
            [new CaptureSource { Id = "log", Path = sourcePath, Required = true }],
            RedactionSettings.CreateDefault(),
            new ExportOptions());
        var profile = await workflow.CreateProfileAsync("Link guard", string.Empty, configuration);
        var run = await workflow.CaptureAsync(profile.Id);
        var stagedPath = Path.Combine(run.WorkspacePath, "staging", "log", "source.log");
        File.Delete(stagedPath);
        File.CreateSymbolicLink(stagedPath, externalPath);

        var error = await Assert.ThrowsAsync<DesktopInputException>(() =>
            workflow.ExportAsync(run, Path.Combine(_root, "exports"), reviewAcknowledged: true));

        Assert.Contains("link", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(externalSecret, await File.ReadAllTextAsync(externalPath));
        Assert.False(Directory.Exists(Path.Combine(_root, "exports")));
    }

    private sealed class BlockingWorkflow : IDesktopWorkflow
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<BundleProfile>> ListProfilesAsync()
        {
            Started.SetResult();
            await Release.Task;
            return [];
        }

        public Task<BundleProfile> CreateProfileAsync(string name, string description, string configurationJson) =>
            throw new NotSupportedException();

        public Task<BundleProfile> UpdateProfileAsync(Guid id, string name, string description, string configurationJson) =>
            throw new NotSupportedException();

        public Task<BundleProfile> DuplicateProfileAsync(Guid id, string newName) => throw new NotSupportedException();

        public Task DeleteProfileAsync(Guid id, string confirmationName) => throw new NotSupportedException();

        public Task<DesktopRun> CaptureAsync(Guid profileId) => throw new NotSupportedException();

        public Task<DesktopExportResult> ExportAsync(DesktopRun run, string outputDirectory, bool reviewAcknowledged) =>
            throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
