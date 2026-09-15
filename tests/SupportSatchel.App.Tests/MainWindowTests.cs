using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using SupportSatchel.App.ViewModels;
using SupportSatchel.Core.Domain;
using Xunit;

namespace SupportSatchel.App.Tests;

public sealed class MainWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "support-satchel-ui-tests-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public async Task BusyOperationDisablesProfileEditorArtifactAndAcknowledgmentControls()
    {
        var workflow = new BlockingWorkflow();
        var viewModel = new MainWindowViewModel(workflow);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        await workflow.Started.Task;

        Assert.False(window.FindControl<ListBox>("ProfileList")!.IsEnabled);
        Assert.False(window.FindControl<StackPanel>("ProfileEditorPanel")!.IsEnabled);
        Assert.False(window.FindControl<ListBox>("ReviewList")!.IsEnabled);
        Assert.False(window.FindControl<CheckBox>("ReviewAcknowledgment")!.IsEnabled);

        workflow.Release.SetResult();
        window.Close();
    }

    [AvaloniaFact]
    public async Task ArtifactInclusionCheckboxNamesContainUniqueStagedPaths()
    {
        Directory.CreateDirectory(_root);
        var firstPath = Path.Combine(_root, "first.log");
        var secondPath = Path.Combine(_root, "second.log");
        await File.WriteAllTextAsync(firstPath, "first");
        await File.WriteAllTextAsync(secondPath, "second");
        var workflow = new DesktopWorkflow(Path.Combine(_root, "profiles.db"), Path.Combine(_root, "runs"));
        var profile = await workflow.CreateProfileAsync(
            "Accessible",
            string.Empty,
            ProfileConfigurationJson.Serialize(
                [
                    new CaptureSource { Id = "first", Path = firstPath, Required = true },
                    new CaptureSource { Id = "second", Path = secondPath, Required = true },
                ],
                RedactionSettings.CreateDefault(),
                new ExportOptions()));
        var viewModel = new MainWindowViewModel(workflow);
        await viewModel.InitializeAsync();
        viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.Id == profile.Id);
        await viewModel.CaptureAsync();
        var window = new MainWindow { DataContext = viewModel };
        window.FindControl<TabControl>("WorkflowTabs")!.SelectedIndex = 1;
        window.Show();
        window.UpdateLayout();

        var checkboxes = window.FindControl<ListBox>("ReviewList")!
            .GetVisualDescendants()
            .OfType<CheckBox>()
            .ToArray();
        var names = checkboxes.Select(AutomationProperties.GetName).ToArray();

        Assert.Equal(viewModel.ReviewArtifacts.Count, names.Length);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        foreach (var artifact in viewModel.ReviewArtifacts)
        {
            Assert.Contains(names, name => name?.Contains(artifact.StagedPath, StringComparison.Ordinal) == true);
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task DirtySavedDraftDisablesBoundProfileNavigationAndDestructiveControls()
    {
        Directory.CreateDirectory(_root);
        var workflow = new DesktopWorkflow(Path.Combine(_root, "profiles.db"), Path.Combine(_root, "runs"));
        var profile = await workflow.CreateProfileAsync(
            "Bound guard",
            "saved",
            ProfileConfigurationJson.Serialize(
                [new CaptureSource { Id = "optional", Path = Path.Combine(_root, "optional.log") }],
                RedactionSettings.CreateDefault(),
                new ExportOptions()));
        var viewModel = new MainWindowViewModel(workflow);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        await WaitForAsync(() => !viewModel.IsBusy && viewModel.Profiles.Count == 1);
        viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.Id == profile.Id);
        viewModel.DeleteConfirmation = profile.Name;
        viewModel.ProfileDescription = "unsaved";
        window.UpdateLayout();

        Assert.False(window.FindControl<ListBox>("ProfileList")!.IsEnabled);
        Assert.False(window.FindControl<Button>("NewProfileButton")!.IsEnabled);
        Assert.True(window.FindControl<Button>("SaveProfileButton")!.IsEnabled);
        Assert.False(window.FindControl<Button>("DuplicateProfileButton")!.IsEnabled);
        Assert.True(window.FindControl<Button>("RevertProfileButton")!.IsEnabled);
        Assert.False(window.FindControl<Button>("DeleteProfileButton")!.IsEnabled);
        Assert.False(window.FindControl<TextBox>("DeleteConfirmationTextBox")!.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DirtyNewDraftKeepsBoundRevertEnabledSoDraftCanBeReset()
    {
        Directory.CreateDirectory(_root);
        var workflow = new DesktopWorkflow(Path.Combine(_root, "profiles.db"), Path.Combine(_root, "runs"));
        await workflow.CreateProfileAsync(
            "Selectable",
            string.Empty,
            ProfileConfigurationJson.Serialize(
                [new CaptureSource { Id = "optional", Path = Path.Combine(_root, "optional.log") }],
                RedactionSettings.CreateDefault(),
                new ExportOptions()));
        var viewModel = new MainWindowViewModel(workflow);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        await WaitForAsync(() => !viewModel.IsBusy && viewModel.Profiles.Count == 1);
        viewModel.ProfileName = "Unsaved new profile";
        window.UpdateLayout();

        Assert.False(window.FindControl<ListBox>("ProfileList")!.IsEnabled);
        Assert.False(window.FindControl<Button>("NewProfileButton")!.IsEnabled);
        Assert.True(window.FindControl<Button>("SaveProfileButton")!.IsEnabled);
        Assert.True(window.FindControl<Button>("RevertProfileButton")!.IsEnabled);
        Assert.True(window.FindControl<TextBlock>("UnsavedProfileWarning")!.IsVisible);
        window.Close();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The headless window did not reach the expected initialized state.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
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
}
