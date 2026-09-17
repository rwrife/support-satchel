using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(SupportSatchel.App.Tests.AvaloniaTestApplication))]

namespace SupportSatchel.App.Tests;

public static class AvaloniaTestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<SupportSatchel.App.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
