using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace PaxImageViewer;

/// <summary>
/// The viewer's colours. It's always dark (photos look best on near-black); the accent follows the host app
/// (PAX Commander passes its own) and is recoloured in place, so a change reaches open windows.
/// </summary>
public static class ViewerTheme
{
    public static readonly SolidColorBrush Back = new(Color.Parse("#0A0C10"));
    public static readonly SolidColorBrush Bar = new(Color.Parse("#0F1218"));
    public static readonly SolidColorBrush Pill = new(Color.Parse("#E6151922"));
    public static readonly SolidColorBrush PillBorder = new(Color.Parse("#2A3140"));
    public static readonly SolidColorBrush Text = new(Color.Parse("#E8EAF0"));
    public static readonly SolidColorBrush Muted = new(Color.Parse("#8A92A6"));
    public static readonly SolidColorBrush Hover = new(Color.Parse("#232A36"));
    public static readonly SolidColorBrush Pressed = new(Color.Parse("#2C3544"));
    public static readonly SolidColorBrush Bad = new(Color.Parse("#F2666A"));
    public static readonly SolidColorBrush Accent = new(Color.Parse("#89CFF0"));
    public static readonly SolidColorBrush AccentSoft = new(Color.Parse("#2389CFF0"));
    public static readonly SolidColorBrush CheckA = new(Color.Parse("#1A1E26"));
    public static readonly SolidColorBrush CheckB = new(Color.Parse("#232833"));

    public static void SetAccent(Color c)
    {
        Accent.Color = c;
        AccentSoft.Color = Color.FromArgb(0x30, c.R, c.G, c.B);
    }
}

/// <summary>24×24 line icons, drawn as strokes.</summary>
internal static class Glyphs
{
    public const string Prev = "M15 5 L8 12 L15 19";
    public const string Next = "M9 5 L16 12 L9 19";
    public const string Play = "M8 5 L19 12 L8 19 Z";
    public const string Pause = "M8 5 V19 M16 5 V19";
    public const string ZoomIn = "M10.5 4 A6.5 6.5 0 1 1 10.49 4 Z M15.3 15.3 L20 20 M10.5 7.5 V13.5 M7.5 10.5 H13.5";
    public const string ZoomOut = "M10.5 4 A6.5 6.5 0 1 1 10.49 4 Z M15.3 15.3 L20 20 M7.5 10.5 H13.5";
    public const string Fit = "M3 8 V3 H8 M16 3 H21 V8 M21 16 V21 H16 M8 21 H3 V16 M8 8 H16 V16 H8 Z";
    public const string FullScreen = "M3 9 V3 H9 M15 3 H21 V9 M21 15 V21 H15 M9 21 H3 V15";
    public const string ExitFullScreen = "M9 3 V9 H3 M15 3 V9 H21 M21 15 H15 V21 M3 15 H9 V21";
    public const string RotateRight = "M19 12 A7 7 0 1 1 15.5 5.9 M15.5 2 V6 H19.5";
    public const string RotateLeft = "M5 12 A7 7 0 1 0 8.5 5.9 M8.5 2 V6 H4.5";
    public const string Lock = "M8 11 V7.5 A4 4 0 0 1 16 7.5 V11 M5.5 11 H18.5 V20.5 H5.5 Z";
    public const string Shuffle = "M3 7 H7 L15 17 H21 M3 17 H7 L9.5 14 M14.5 10 L15 7 H21 M18 4 L21 7 L18 10 M18 14 L21 17 L18 20";
    public const string Open = "M3 6 H10 L12 8 H21 V19 H3 Z";
    public const string Info = "M12 3 A9 9 0 1 1 11.99 3 Z M12 11 V17 M12 7.2 V7.8";
    public const string Grid = "M4 4 H20 V20 H4 Z M9.33 4 V20 M14.66 4 V20 M4 9.33 H20 M4 14.66 H20";
    public const string Minus = "M6 12 H18";
    public const string Plus = "M6 12 H18 M12 6 V18";
    public const string Clock = "M12 3 A9 9 0 1 1 11.99 3 Z M12 7 V12 L15.5 14";
}

/// <summary>A flat icon button that looks the same whatever theme the host app uses.</summary>
internal sealed class GlyphButton : Border
{
    private readonly Path _path;
    private bool _on;
    private bool _over;

    public GlyphButton(string glyph, string tip, Action click, double size = 34)
    {
        _path = new Path
        {
            Data = Geometry.Parse(glyph),
            Stroke = ViewerTheme.Text,
            StrokeThickness = 2.1,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 24,
            Height = 24,
        };
        // Drawn on the 24-unit grid and scaled as a whole, so every icon keeps its proportions (a lone "−" too).
        Child = new Viewbox { Child = _path, Width = 18, Height = 18 };
        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(8);
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, tip);
        PointerEntered += (_, _) => { _over = true; Paint(); };
        PointerExited += (_, _) => { _over = false; Paint(); };
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            Background = ViewerTheme.Pressed;
            e.Handled = true;
        };
        PointerReleased += (_, e) =>
        {
            Paint();
            e.Handled = true;
            if (_over) click();
        };
    }

    public void SetGlyph(string glyph) => _path.Data = Geometry.Parse(glyph);

    public void SetFilled(bool filled)
    {
        _path.Fill = filled ? _path.Stroke : null;
    }

    /// <summary>A toggle that's on shows in the accent colour.</summary>
    public bool IsOn
    {
        get => _on;
        set
        {
            _on = value;
            Paint();
        }
    }

    public void SetTip(string tip) => ToolTip.SetTip(this, tip);

    private void Paint()
    {
        Background = _on ? ViewerTheme.AccentSoft : _over ? ViewerTheme.Hover : Brushes.Transparent;
        _path.Stroke = _on ? ViewerTheme.Accent : ViewerTheme.Text;
        if (_path.Fill is not null) _path.Fill = _path.Stroke;
    }
}

/// <summary>A small text button ("1:1", "−", "+").</summary>
internal sealed class TextButton : Border
{
    public TextButton(string text, string tip, Action click, double width = 34)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = ViewerTheme.Text,
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = label;
        Width = width;
        Height = 34;
        CornerRadius = new CornerRadius(8);
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, tip);
        var over = false;
        PointerEntered += (_, _) => { over = true; Background = ViewerTheme.Hover; };
        PointerExited += (_, _) => { over = false; Background = Brushes.Transparent; };
        PointerPressed += (_, e) => { Background = ViewerTheme.Pressed; e.Handled = true; };
        PointerReleased += (_, e) =>
        {
            Background = over ? ViewerTheme.Hover : Brushes.Transparent;
            e.Handled = true;
            if (over) click();
        };
    }
}
