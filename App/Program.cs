using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace PaxImageViewer.App;

internal static class Program
{
    public static string? StartPath { get; private set; }
    public static bool StartSlideshow { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // Build step: "--write-icon <file.ico>" renders the app icon and exits.
        if (args is ["--write-icon", var ico])
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            AppIcon.WriteIco(ico);
            return;
        }
        // Off-screen check of a build: "--self-test <image> <report.txt>" decodes, renders and saves settings, no window.
        if (args is ["--self-test", var image, var report])
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            File.WriteAllText(report, SelfTest.Run(image));
            return;
        }
        StartPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        StartSlideshow = args.Contains("--slideshow");
        if (OperatingSystem.IsWindows()) SetCurrentProcessExplicitAppUserModelID("PAX.ImageViewer");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ViewerApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

/// <summary>The standalone app: one viewer window, no XAML (keeps start-up and the AOT build lean).</summary>
internal sealed class ViewerApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());

        // Title-bar buttons: a soft highlight, and red for close.
        Styles.Add(Hover("caption", ":pointerover", "#232A36"));
        Styles.Add(Hover("caption", ":pressed", "#2C3544"));
        Styles.Add(Hover("close", ":pointerover", "#C42B1C"));
        Styles.Add(Hover("close", ":pressed", "#A32416"));
    }

    private static Style Hover(string cls, string state, string color)
    {
        var s = new Style(x => x.OfType<Button>().Class(cls).Class(state).Template().OfType<ContentPresenter>());
        s.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, new SolidColorBrush(Color.Parse(color))));
        return s;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var w = new ViewerWindow(canOpenFiles: true);
            try { w.Icon = AppIcon.CreateWindowIcon(); } catch { }
            desktop.MainWindow = w;
            if (Program.StartPath is { } p && (File.Exists(p) || Directory.Exists(p)))
                w.Viewer.Open(Path.GetFullPath(p), Program.StartSlideshow);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
