using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SupportSatchel.App.ViewModels;

namespace SupportSatchel.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SupportSatchel");
            var workflow = new DesktopWorkflow(
                Path.Combine(dataRoot, "profiles.db"),
                Path.Combine(dataRoot, "runs"));
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(workflow),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
