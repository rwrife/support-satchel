using Avalonia.Controls;
using SupportSatchel.App.ViewModels;

namespace SupportSatchel.App;

public partial class MainWindow : Window
{
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        Opened += InitializeViewModel;
    }

    private async void InitializeViewModel(object? sender, EventArgs e)
    {
        if (_initialized || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        _initialized = true;
        await viewModel.InitializeAsync();
    }
}
