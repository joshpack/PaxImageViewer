using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace pxdImageViewer;

/// <summary>A viewer in its own window, with its own title bar (the image gets every pixel below it).</summary>
public sealed class ViewerWindow : Window
{
    private const string MaximizeGlyph = "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z";
    private const string RestoreGlyph = "M2.5,0.5 L9.5,0.5 L9.5,7.5 M0.5,2.5 L7.5,2.5 L7.5,9.5 L0.5,9.5 Z";

    private WindowState _beforeFullScreen = WindowState.Normal;

    public ViewerControl Viewer { get; } = new();

    /// <summary>The most recently opened viewer window still open (hosts reuse it instead of stacking windows).</summary>
    public static ViewerWindow? Last { get; private set; }

    public ViewerWindow(bool canOpenFiles = false)
    {
        ViewerSettings.EnsureLoaded();
        Title = "pxd Image Viewer";
        Width = 1200;
        Height = 800;
        MinWidth = 420;
        MinHeight = 300;
        Background = ViewerTheme.Back;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = Viewer;

        ExtendClientAreaToDecorationsHint = true;
        WindowDecorations = WindowDecorations.BorderOnly;
        Viewer.CaptionSlot.Children.Add(CaptionButtons());
        MakeDraggable(Viewer.TitleBar);

        Viewer.CloseRequested += Close;
        Viewer.FullScreenRequested += SetFullScreen;
        Viewer.CurrentChanged += p => Title = p is null ? "pxd Image Viewer" : System.IO.Path.GetFileName(p) + " – pxd Image Viewer";
        if (canOpenFiles) Viewer.PickFile = PickAsync;

        RememberPlacement();
        Activated += (_, _) => Last = this;
        Closed += (_, _) =>
        {
            if (Last == this) Last = null;
            ViewerSettings.SaveNow();
        };
        Last = this;
    }

    /// <summary>
    /// Opens <paramref name="path"/> (an image or a folder) in a viewer window: the last one still open when
    /// <paramref name="reuse"/>, otherwise a new one. <paramref name="files"/> overrides the folder's own order.
    /// </summary>
    public static ViewerWindow Show(string path, IReadOnlyList<string>? files = null, bool slideshow = false, bool reuse = true)
    {
        var w = reuse && Last is { } last ? last : new ViewerWindow();
        if (files is { Count: > 0 })
        {
            var i = Math.Max(0, files.ToList().FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)));
            w.Viewer.Open(files, i, slideshow);
        }
        else w.Viewer.Open(path, slideshow);
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        return w;
    }

    private async Task<string?> PickAsync()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ImageLoader.Extensions.Select(e => "*" + e).ToArray() },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    private void SetFullScreen(bool on)
    {
        if (on == (WindowState == WindowState.FullScreen)) return;
        if (on)
        {
            _beforeFullScreen = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            WindowState = WindowState.FullScreen;
        }
        else WindowState = _beforeFullScreen;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty) return;
        Viewer.SetFullScreen(WindowState == WindowState.FullScreen);
        // A maximized borderless window hangs a few pixels off each screen edge; keep the content on screen.
        Padding = WindowState == WindowState.Maximized ? OffScreenMargin : default;
        if (_max?.Content is Avalonia.Controls.Shapes.Path p)
            p.Data = Geometry.Parse(WindowState == WindowState.Maximized ? RestoreGlyph : MaximizeGlyph);
    }

    // ---- Title bar -------------------------------------------------------------------------------------------------

    private Button? _max;

    private StackPanel CaptionButtons()
    {
        _max = Caption(MaximizeGlyph, "Maximize", WindowDecorationsElementRole.MaximizeButton,
            () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                Caption("M0,5.5 L10,5.5", "Minimize", WindowDecorationsElementRole.MinimizeButton, () => WindowState = WindowState.Minimized),
                _max,
                Caption("M0,0 L10,10 M10,0 L0,10", "Close", WindowDecorationsElementRole.CloseButton, Close, close: true),
            },
        };
    }

    private static Button Caption(string glyph, string tip, WindowDecorationsElementRole role, Action click, bool close = false)
    {
        var b = new Button
        {
            Content = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(glyph), Width = 10, Height = 10, Stroke = ViewerTheme.Text, StrokeThickness = 1 },
            Width = 46,
            Height = 36,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Classes.Add("caption");
        if (close) b.Classes.Add("close");
        ToolTip.SetTip(b, tip);
        Avalonia.Controls.Chrome.WindowDecorationProperties.SetElementRole(b, role);
        b.Click += (_, _) => click();
        return b;
    }

    private void MakeDraggable(Control bar)
    {
        bar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed) return;
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else BeginMoveDrag(e);
            e.Handled = true;
        };
    }

    // ---- Placement -------------------------------------------------------------------------------------------------

    private void RememberPlacement()
    {
        var p = ViewerSettings.Current.Window;
        if (p is { Width: > 0, Height: > 0 })
        {
            Width = Math.Max(MinWidth, p.Width);
            Height = Math.Max(MinHeight, p.Height);
            if ((p.X != 0 || p.Y != 0) && OnSomeScreen(p))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint(p.X, p.Y);
            }
            if (p.Maximized) WindowState = WindowState.Maximized;
        }
        var ready = false;
        void Save()
        {
            if (!ready) return;
            var s = ViewerSettings.Current.Window ??= new ViewerSettings.Placement();
            if (WindowState == WindowState.Normal)
            {
                s.X = Position.X;
                s.Y = Position.Y;
                s.Width = ClientSize.Width;
                s.Height = ClientSize.Height;
            }
            if (WindowState is WindowState.Normal or WindowState.Maximized) s.Maximized = WindowState == WindowState.Maximized;
            ViewerSettings.SaveSoon();
        }
        Opened += (_, _) => ready = true;
        PositionChanged += (_, _) => Save();
        Resized += (_, _) => Save();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) Save();
        };
    }

    private bool OnSomeScreen(ViewerSettings.Placement p)
    {
        var screens = Screens?.All;
        if (screens is null || screens.Count == 0) return true;
        var probe = new PixelPoint(p.X + 60, p.Y + 20);
        return screens.Any(s => s.WorkingArea.Contains(probe));
    }
}
